using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ETaskManager.UI;

/// <summary>
/// Very cheap ring-buffer line graph (0..100). Rendered directly in OnRender with frozen
/// geometry, so dozens of them can update every second without a charting library.
/// </summary>
public sealed class UsageGraph : FrameworkElement
{
    public const int Capacity = 60;

    private readonly double[] _values = new double[Capacity];
    private int _head;
    private int _count;

    private static readonly Brush BackgroundBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x16, 0x19, 0x20)));
    private static readonly Pen BorderPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0x34, 0x40))), 1));
    private static readonly Pen GridPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x90, 0xA0))), 1));
    private static readonly Brush TitleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB2)));
    private static readonly Brush ValueBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xF0)));
    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static readonly (Brush stroke, Brush fill) Blue = MakePalette(0x3F, 0xA9, 0xF5);
    private static readonly (Brush stroke, Brush fill) Amber = MakePalette(0xF5, 0xB0, 0x3F);
    private static readonly (Brush stroke, Brush fill) Red = MakePalette(0xF5, 0x4F, 0x4F);

    private (Brush stroke, Brush fill) _palette = Blue;
    private Pen _linePen = Freeze(new Pen(Blue.stroke, 1.5));

    public string Title { get; set; } = "";
    public string ValueText { get; set; } = "";
    public double LabelFontSize { get; set; } = 11;

    /// <summary>When true the line turns amber / red as the latest value gets high.</summary>
    public bool ColorByValue { get; set; }

    private Color _lineColor = Color.FromRgb(0x3F, 0xA9, 0xF5);

    public Color LineColor
    {
        get => _lineColor;
        set
        {
            _lineColor = value;
            _palette = MakePalette(value.R, value.G, value.B);
            _linePen = Freeze(new Pen(_palette.stroke, 1.5));
        }
    }

    public double Latest => _count == 0 ? 0 : _values[(_head - 1 + Capacity) % Capacity];

    public void Push(double value, string? valueText = null)
    {
        _values[_head] = Math.Clamp(value, 0, 100);
        _head = (_head + 1) % Capacity;
        if (_count < Capacity) _count++;
        if (valueText != null) ValueText = valueText;

        if (ColorByValue)
        {
            var pal = value >= 90 ? Red : value >= 60 ? Amber : Blue;
            if (!ReferenceEquals(pal.stroke, _palette.stroke))
            {
                _palette = pal;
                _linePen = Freeze(new Pen(pal.stroke, 1.5));
            }
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;

        var rect = new Rect(0.5, 0.5, w - 1, h - 1);
        dc.DrawRectangle(BackgroundBrush, BorderPen, rect);

        for (int i = 1; i < 4; i++)
        {
            double y = Math.Round(h * i / 4) + 0.5;
            dc.DrawLine(GridPen, new Point(1, y), new Point(w - 1, y));
        }

        if (_count >= 2)
        {
            double step = (w - 2) / (Capacity - 1);
            int firstSlot = Capacity - _count; // right-align the data
            var line = new StreamGeometry();
            var fill = new StreamGeometry();
            using (var lc = line.Open())
            using (var fc = fill.Open())
            {
                for (int i = 0; i < _count; i++)
                {
                    int idx = (_head - _count + i + Capacity) % Capacity;
                    double x = 1 + (firstSlot + i) * step;
                    double y = 1 + (h - 2) * (1 - _values[idx] / 100.0);
                    var pt = new Point(x, y);
                    if (i == 0)
                    {
                        lc.BeginFigure(pt, false, false);
                        fc.BeginFigure(new Point(x, h - 1), true, true);
                        fc.LineTo(pt, false, false);
                    }
                    else
                    {
                        lc.LineTo(pt, true, false);
                        fc.LineTo(pt, false, false);
                    }
                }
                fc.LineTo(new Point(w - 1, h - 1), false, false);
            }
            line.Freeze();
            fill.Freeze();
            dc.PushClip(new RectangleGeometry(new Rect(1, 1, w - 2, h - 2)));
            dc.DrawGeometry(_palette.fill, null, fill);
            dc.DrawGeometry(null, _linePen, line);
            dc.Pop();
        }

        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (!string.IsNullOrEmpty(Title))
        {
            var ft = new FormattedText(Title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, LabelFontSize, TitleBrush, dip);
            dc.DrawText(ft, new Point(5, 3));
        }
        if (!string.IsNullOrEmpty(ValueText))
        {
            var ft = new FormattedText(ValueText, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, LabelFontSize, ValueBrush, dip);
            dc.DrawText(ft, new Point(Math.Max(5, w - ft.Width - 5), 3));
        }
    }

    private static (Brush, Brush) MakePalette(byte r, byte g, byte b) =>
        (Freeze(new SolidColorBrush(Color.FromRgb(r, g, b))), Freeze(new SolidColorBrush(Color.FromArgb(0x45, r, g, b))));

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
