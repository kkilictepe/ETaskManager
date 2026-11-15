using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ETaskManager.Mcp;
using ETaskManager.Monitoring;
using ETaskManager.UI;

namespace ETaskManager;

public partial class MainWindow : Window
{
    private readonly SystemSampler _sampler = new();
    private readonly SnapshotHistory _history = new();
    private readonly McpServer? _mcp;
    private readonly ProcessListModel _model = new();
    private UsageGraph[] _coreGraphs = Array.Empty<UsageGraph>();

    // Latest-wins handoff from the sampler thread to the UI thread.
    private Snapshot? _pending;
    private int _applyScheduled;

    private ProcKey? _selectedKey;
    private bool _updatingList;
    private bool _paused;
    private bool _killInProgress;

    private string? _statusOverride;
    private DateTime _statusOverrideUntil;
    private Snapshot? _last;

    public MainWindow()
    {
        InitializeComponent();

        ProcGrid.ItemsSource = _model.Visible;

        bool admin = ProcessKiller.IsElevated;
        Title = admin ? "ETaskManager (Administrator)" : "ETaskManager";
        AdminText.Text = admin ? "Running as administrator" : "Not elevated: elevated processes can't be ended";
        ElevateButton.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;

        _sampler.SnapshotReady += OnSnapshotFromSampler;
        _sampler.SnapshotReady += _history.Add;
        _sampler.SampleFailed += ex => Dispatcher.BeginInvoke(() => ShowStatus("Sampling error: " + ex.Message));

        if (McpServer.ConfiguredPort() is { } port)
        {
            _mcp = new McpServer(new ResourceTools(_history), port);
            _mcp.StateChanged += () => Dispatcher.BeginInvoke(UpdateMcpButton);
        }
        UpdateMcpButton();

        Loaded += (_, _) =>
        {
            ApplySort(SortKey.Cpu, true);
            _sampler.Start();
            _mcp?.Start();
        };
        Closed += (_, _) =>
        {
            _mcp?.Dispose();
            _sampler.Dispose();
        };
    }

    // ------------------------------------------------------------------ MCP server

    private string McpAddCommand => $"claude mcp add --transport http etaskmanager {_mcp!.Url}";

    private void UpdateMcpButton()
    {
        if (_mcp == null)
        {
            McpButton.Content = "MCP: off";
            McpButton.ToolTip = "MCP server disabled (ETM_MCP=0).";
            return;
        }
        switch (_mcp.State)
        {
            case McpState.Running:
                McpButton.Content = $"MCP: localhost:{_mcp.Port}";
                McpButton.ToolTip = $"MCP server for coding agents is running at {_mcp.Url}\nClick to copy:\n{McpAddCommand}";
                break;
            case McpState.Failed:
                McpButton.Content = "MCP: unavailable";
                McpButton.ToolTip = $"Could not listen on {_mcp.Url}: {_mcp.Error}\nIs another ETaskManager running? Set ETM_MCP_PORT to use a different port.";
                break;
            default:
                McpButton.Content = "MCP: starting…";
                McpButton.ToolTip = $"Starting the MCP server on {_mcp.Url}";
                break;
        }
    }

    private void OnMcpClick(object sender, RoutedEventArgs e)
    {
        if (_mcp == null) { ShowStatus("MCP server is disabled. Unset ETM_MCP to enable it."); return; }
        if (_mcp.State == McpState.Failed) { ShowStatus($"MCP server unavailable: {_mcp.Error}"); return; }
        Copy(McpAddCommand);
    }

    // ------------------------------------------------------------------ sampling → UI

    /// <summary>Runs on the sampler thread.</summary>
    private void OnSnapshotFromSampler(Snapshot snap)
    {
        Interlocked.Exchange(ref _pending, snap);
        if (Interlocked.Exchange(ref _applyScheduled, 1) == 0)
        {
            // Background priority: input and rendering always win over data updates.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, ApplyPending);
        }
    }

    private void ApplyPending()
    {
        Interlocked.Exchange(ref _applyScheduled, 0);
        var snap = Interlocked.Exchange(ref _pending, null);
        if (snap == null) return;
        _last = snap;
        long t0 = Stopwatch.GetTimestamp();
        ApplySystem(snap.System);
        long t1 = Stopwatch.GetTimestamp();
        if (!_paused) ApplyProcesses(snap);
        long t2 = Stopwatch.GetTimestamp();
        UpdateStatus();
        if (DiagLog != null)
        {
            long t3 = Stopwatch.GetTimestamp();
            UpdateLayout();
            long t4 = Stopwatch.GetTimestamp();
            DiagLog.WriteLine($"{DateTime.Now:HH:mm:ss.fff} sys={Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds:0.0} " +
                              $"procs={Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds:0.0} layout={Stopwatch.GetElapsedTime(t3, t4).TotalMilliseconds:0.0} " +
                              $"sample={_sampler.LastSampleCostMs:0.0} rowsGenerated={CountRows()} " +
                              $"total={snap.System.TotalPercent:0.0} sumOfProcesses={snap.Processes.Sum(p => p.CpuPercent):0.0}");
            DiagLog.Flush();
        }
    }

    private int CountRows()
    {
        int n = 0;
        for (int i = 0; i < ProcGrid.Items.Count; i++)
            if (ProcGrid.ItemContainerGenerator.ContainerFromIndex(i) != null) n++;
        return n;
    }

    private static readonly System.IO.StreamWriter? DiagLog =
        Environment.GetEnvironmentVariable("ETM_DIAG") is { Length: > 0 } path ? new System.IO.StreamWriter(path, append: false) : null;

    private void ApplySystem(SystemSample s)
    {
        EnsureCoreGraphs(s.CorePercents.Length);
        for (int i = 0; i < s.CorePercents.Length; i++)
            _coreGraphs[i].Push(s.CorePercents[i], $"{s.CorePercents[i]:0}%");

        CpuTotalGraph.Push(s.TotalPercent);
        CpuTotalText.Text = $"{s.TotalPercent:0}%";
        CpuDetailText.Text =
            $"{s.CorePercents.Length} logical processors · Interrupts/DPC {s.InterruptDpcPercent:0.0}% · " +
            $"{s.ProcessCount} processes · {s.ThreadCount:N0} threads · {s.HandleCount:N0} handles";

        ulong used = s.TotalPhys - s.AvailPhys;
        double memPct = s.TotalPhys > 0 ? 100.0 * used / s.TotalPhys : 0;
        MemGraph.Push(memPct);
        MemTotalText.Text = $"{memPct:0}%";
        MemDetailText.Text =
            $"{Gb(used)} / {Gb(s.TotalPhys)} GB in use · Available {Gb(s.AvailPhys)} GB · " +
            $"Cached {Gb(s.Cached)} GB · Committed {Gb(s.CommitTotal)} / {Gb(s.CommitLimit)} GB";
    }

    private static string Gb(ulong bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0.0");

    private void EnsureCoreGraphs(int n)
    {
        if (_coreGraphs.Length == n) return;
        CoreGrid.Children.Clear();
        CoreGrid.Columns = n <= 8 ? Math.Max(1, n) : n <= 16 ? (n + 1) / 2 : n <= 32 ? 8 : 16;
        _coreGraphs = new UsageGraph[n];
        for (int i = 0; i < n; i++)
        {
            var g = new UsageGraph
            {
                Title = $"CPU {i}",
                Height = 64,
                Margin = new Thickness(3),
                ColorByValue = true,
                LabelFontSize = 11,
            };
            _coreGraphs[i] = g;
            CoreGrid.Children.Add(g);
        }
        CoresToggle.Content = $"Logical processors ({n})";
    }

    private void ApplyProcesses(Snapshot snap)
    {
        _updatingList = true;
        try
        {
            _model.Update(snap.Processes);
        }
        finally
        {
            _updatingList = false;
        }
        RestoreSelection();
    }

    private void RestoreSelection()
    {
        // The selection follows the *process* (PID + start time), not the grid line,
        // so a re-sort can never leave a different process selected.
        int idx = _selectedKey is { } key ? _model.IndexOf(key) : -1;
        if (idx < 0)
        {
            if (_selectedKey != null && _model.Find(_selectedKey.Value) == null)
                _selectedKey = null; // process exited
            if (ProcGrid.SelectedIndex != -1)
            {
                _updatingList = true;
                try { ProcGrid.SelectedIndex = -1; }
                finally { _updatingList = false; }
            }
            UpdateKillButtons();
            return;
        }
        if (ProcGrid.SelectedIndex != idx)
        {
            _updatingList = true;
            try { ProcGrid.SelectedIndex = idx; }
            finally { _updatingList = false; }
        }
    }

    // ------------------------------------------------------------------ status

    public void ShowStatus(string message, int seconds = 8)
    {
        _statusOverride = message;
        _statusOverrideUntil = DateTime.UtcNow.AddSeconds(seconds);
        StatusText.Text = message;
    }

    private void UpdateStatus()
    {
        if (_statusOverride != null && DateTime.UtcNow < _statusOverrideUntil) return;
        _statusOverride = null;

        string sort = _model.SortKey switch
        {
            SortKey.Cpu => "CPU",
            SortKey.PrivateWs => "memory",
            SortKey.Commit => "commit",
            SortKey.CpuTime => "CPU time",
            SortKey.Pid => "PID",
            _ => _model.SortKey.ToString().ToLowerInvariant(),
        };
        string interval = _last != null ? $" · interval {_last.IntervalSeconds:0.00} s · sample {_sampler.LastSampleCostMs:0} ms" : "";
        string paused = _paused ? " · LIST PAUSED" : "";
        StatusText.Text = $"{_model.Visible.Count} of {_model.Count} rows · sorted by {sort}{interval}{paused}";
    }

    // ------------------------------------------------------------------ sorting / filtering

    private void OnSortCpu(object sender, RoutedEventArgs e) => ApplySort(SortKey.Cpu, true);
    private void OnSortMem(object sender, RoutedEventArgs e) => ApplySort(SortKey.PrivateWs, true);

    private void OnGridSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (!Enum.TryParse<SortKey>(e.Column.SortMemberPath, out var key)) return;
        bool desc = _model.SortKey == key
            ? !_model.Descending
            : key is not (SortKey.Name or SortKey.Pid or SortKey.Parent);
        ApplySort(key, desc);
    }

    private void ApplySort(SortKey key, bool descending)
    {
        _updatingList = true;
        try { _model.SetSort(key, descending); }
        finally { _updatingList = false; }

        foreach (var col in ProcGrid.Columns)
        {
            col.SortDirection = col.SortMemberPath == key.ToString()
                ? (descending ? ListSortDirection.Descending : ListSortDirection.Ascending)
                : null;
        }
        SortCpuButton.IsChecked = key == SortKey.Cpu;
        SortMemButton.IsChecked = key == SortKey.PrivateWs;
        RestoreSelection();
        UpdateStatus();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _updatingList = true;
        try { _model.Filter = SearchBox.Text; }
        finally { _updatingList = false; }
        RestoreSelection();
        UpdateStatus();
    }

    private void OnFindCommand(object sender, ExecutedRoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnPauseToggle(object sender, RoutedEventArgs e)
    {
        _paused = PauseButton.IsChecked == true;
        PauseButton.Content = _paused ? "Resume list" : "Pause list";
        UpdateStatus();
    }

    private void OnRateClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out int ms))
            _sampler.IntervalMs = ms;
    }

    // ------------------------------------------------------------------ selection

    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingList) return;
        _selectedKey = (ProcGrid.SelectedItem as RowSlot)?.Key;
        UpdateKillButtons();
    }

    private ProcessRow? SelectedRow => _selectedKey is { } k ? _model.Find(k) : null;

    private void UpdateKillButtons()
    {
        var row = SelectedRow;
        bool can = row != null && !row.IsPseudo && !_killInProgress;
        KillButton.IsEnabled = can;
        KillTreeButton.IsEnabled = can;
        KillButton.Content = row != null && !row.IsPseudo ? $"End task: {Shorten(row.Name)} ({row.Pid})" : "End task";
    }

    private static string Shorten(string s) => s.Length <= 28 ? s : s[..26] + "…";

    private void OnGridPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            e.Handled = true;
            _ = KillSelectedAsync(tree: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
        }
    }

    /// <summary>Right-click selects the row under the mouse so the context menu acts on it.</summary>
    private void OnGridPreviewRightDown(object sender, MouseButtonEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject;
        while (d != null && d is not DataGridRow)
            d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        if (d is DataGridRow row)
        {
            row.IsSelected = true;
            row.Focus();
        }
    }

    // ------------------------------------------------------------------ killing

    private void OnKillClick(object sender, RoutedEventArgs e) => _ = KillSelectedAsync(tree: false);
    private void OnKillTreeClick(object sender, RoutedEventArgs e) => _ = KillSelectedAsync(tree: true);

    private async Task KillSelectedAsync(bool tree)
    {
        if (_killInProgress) return;
        var row = SelectedRow;
        if (row == null) return;

        // Capture identity NOW; the list keeps updating while the dialog is open.
        var target = new KillTarget(row.Pid, row.CreateTime, row.Name);
        double cpu = row.CpuPercent;
        double mem = row.PrivateWsMb;

        if (ProcessKiller.GetBlockReason(target.Pid, target.Name) is { } reason)
        {
            MessageBox.Show(this, reason, "Can't end this process", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var children = tree
            ? ProcessKiller.FindDescendants(_model.LastProcesses, target)
                .Where(c => ProcessKiller.GetBlockReason(c.Pid, c.Name) == null).ToList()
            : new List<KillTarget>();

        string msg;
        if (tree)
        {
            string list = string.Join("\n", children.Take(12).Select(c => $"    • {c.Name} (PID {c.Pid})"));
            if (children.Count > 12) list += $"\n    … and {children.Count - 12} more";
            msg = $"End the process tree of:\n\n    {target.Name}\n    PID {target.Pid}\n\n" +
                  (children.Count == 0 ? "It has no child processes.\n\n" : $"This also ends {children.Count} child process(es):\n{list}\n\n") +
                  "Unsaved data in these processes will be lost.";
        }
        else
        {
            msg = $"End this process?\n\n    {target.Name}\n    PID {target.Pid}\n    CPU {cpu:0.0}%   Memory {mem:N1} MB\n\n" +
                  "Unsaved data in this process will be lost.";
        }

        var answer = MessageBox.Show(this, msg, tree ? "End process tree?" : "End task?",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        _killInProgress = true;
        UpdateKillButtons();
        ShowStatus($"Ending {target.Name} (PID {target.Pid})…");
        try
        {
            var targets = new List<KillTarget>(children) { target }; // children first, root last
            var results = await Task.Run(() => targets.Select(ProcessKiller.Kill).ToList());
            var root = results[^1];

            if (root.Status is KillStatus.Success or KillStatus.AlreadyExited)
            {
                int killedKids = results.Take(results.Count - 1).Count(r => r.Status == KillStatus.Success);
                ShowStatus(ProcessKiller.Describe(root) + (tree ? $" Also ended {killedKids} child process(es)." : ""));
            }
            else if (root.Status == KillStatus.AccessDenied && !ProcessKiller.IsElevated)
            {
                ShowStatus(ProcessKiller.Describe(root));
                var r = MessageBox.Show(this,
                    ProcessKiller.Describe(root) + "\n\nRestart ETaskManager as administrator now?",
                    "Access denied", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes);
                if (r == MessageBoxResult.Yes) RestartElevated();
            }
            else
            {
                ShowStatus(ProcessKiller.Describe(root));
                MessageBox.Show(this, ProcessKiller.Describe(root), "Could not end process", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _killInProgress = false;
            UpdateKillButtons();
        }
    }

    // ------------------------------------------------------------------ misc

    private void OnElevateClick(object sender, RoutedEventArgs e) => RestartElevated();

    private void RestartElevated()
    {
        try
        {
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            ShowStatus("Elevation was cancelled.");
        }
        catch (Exception ex)
        {
            ShowStatus("Could not restart as administrator: " + ex.Message);
        }
    }

    private void OnCopyName(object sender, RoutedEventArgs e) => Copy(SelectedRow?.Name);
    private void OnCopyPid(object sender, RoutedEventArgs e) => Copy(SelectedRow is { IsPseudo: false } r ? r.Pid.ToString() : null);

    private void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); ShowStatus($"Copied \"{text}\"."); }
        catch (Exception ex) { ShowStatus("Clipboard busy: " + ex.Message); }
    }
}
