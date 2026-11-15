using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ETaskManager.Mcp;

public enum McpState { Disabled, Starting, Running, Failed }

/// <summary>
/// Minimal Model Context Protocol server (Streamable HTTP transport, JSON responses, no SSE)
/// listening on http://localhost:PORT/mcp only. Exposes read-only <see cref="ResourceTools"/>.
///
/// Runs on its own above-normal-priority thread and never touches the UI dispatcher, so it keeps
/// answering while the machine is pegged at 100% CPU.
/// </summary>
public sealed class McpServer : IDisposable
{
    public const int DefaultPort = 7457;
    private const string ServerVersion = "1.1.0";
    private static readonly string[] SupportedProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    private readonly ResourceTools _tools;
    private readonly HttpListener _listener = new();
    private readonly Thread _thread;
    private volatile bool _stop;

    /// <summary>Compact, and without \u0022-style escaping of quotes/apostrophes, so text stays readable for agents.</summary>
    internal static readonly JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public int Port { get; }
    public string Url => $"http://localhost:{Port}/mcp";
    public McpState State { get; private set; } = McpState.Starting;
    public string? Error { get; private set; }

    /// <summary>Raised on the server thread when <see cref="State"/> changes.</summary>
    public event Action? StateChanged;

    public McpServer(ResourceTools tools, int port)
    {
        _tools = tools;
        Port = port;
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.IgnoreWriteExceptions = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "ETaskManager MCP server", Priority = ThreadPriority.AboveNormal };
    }

    /// <summary>Reads ETM_MCP (0/off disables) and ETM_MCP_PORT. Returns null when disabled.</summary>
    public static int? ConfiguredPort()
    {
        var enabled = Environment.GetEnvironmentVariable("ETM_MCP");
        if (enabled is "0" || string.Equals(enabled, "off", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase))
            return null;
        return int.TryParse(Environment.GetEnvironmentVariable("ETM_MCP_PORT"), out int p) && p is > 0 and < 65536 ? p : DefaultPort;
    }

    public void Start() => _thread.Start();

    private void SetState(McpState state, string? error = null)
    {
        State = state;
        Error = error;
        StateChanged?.Invoke();
    }

    private void Run()
    {
        // After "Restart as administrator" the old instance may still hold the port for a moment: retry.
        for (int attempt = 0; ; attempt++)
        {
            if (_stop) return;
            try { _listener.Start(); break; }
            catch (HttpListenerException ex)
            {
                if (attempt >= 20) { SetState(McpState.Failed, ex.Message); return; }
                Thread.Sleep(500);
            }
            catch (Exception ex) { SetState(McpState.Failed, ex.Message); return; }
        }
        SetState(McpState.Running);

        while (!_stop)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch when (_stop) { return; }
            catch (HttpListenerException) { if (!_listener.IsListening) return; continue; }
            catch (ObjectDisposedException) { return; }

            try { Handle(ctx); }
            catch { try { ctx.Response.Abort(); } catch { } }
        }
    }

    // ------------------------------------------------------------------ HTTP

    private void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;

        // DNS-rebinding protection: browsers send Origin; only accept local pages.
        var origin = req.Headers["Origin"];
        if (!string.IsNullOrEmpty(origin) && !IsLocalOrigin(origin))
        {
            WriteText(res, 403, "Forbidden origin");
            return;
        }

        string path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";
        if (path != "/mcp")
        {
            WriteText(res, 404, $"ETaskManager MCP server. Endpoint: {Url}");
            return;
        }

        switch (req.HttpMethod)
        {
            case "POST":
                break;
            case "DELETE": // session termination; we're stateless
                res.StatusCode = 200;
                res.Close();
                return;
            default: // GET (SSE stream) is not offered
                res.AddHeader("Allow", "POST, DELETE");
                WriteText(res, 405, "Method not allowed");
                return;
        }

        string body;
        using (var reader = new StreamReader(req.InputStream, Encoding.UTF8))
            body = reader.ReadToEnd();

        JsonNode? reply;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                var arr = new JsonArray();
                foreach (var msg in root.EnumerateArray())
                    if (HandleMessage(msg) is { } r) arr.Add(r);
                reply = arr.Count > 0 ? arr : null;
            }
            else
            {
                reply = HandleMessage(root);
            }
        }
        catch (JsonException)
        {
            reply = RpcError(null, -32700, "Parse error");
        }

        if (reply == null)
        {
            // Only notifications / responses were received.
            res.StatusCode = 202;
            res.Close();
            return;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(reply.ToJsonString(JsonOptions));
        res.StatusCode = 200;
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes);
        res.Close();
    }

    private static bool IsLocalOrigin(string origin)
    {
        if (origin == "null") return false;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        return uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteText(HttpListenerResponse res, int status, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        res.StatusCode = status;
        res.ContentType = "text/plain; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes);
        res.Close();
    }

    // ------------------------------------------------------------------ JSON-RPC

    /// <summary>Returns the response object, or null for notifications and client responses.</summary>
    private JsonObject? HandleMessage(JsonElement msg)
    {
        if (msg.ValueKind != JsonValueKind.Object)
            return RpcError(null, -32600, "Invalid request");

        bool hasId = msg.TryGetProperty("id", out var idEl);
        JsonNode? id = hasId ? JsonNode.Parse(idEl.GetRawText()) : null;

        if (!msg.TryGetProperty("method", out var methodEl) || methodEl.ValueKind != JsonValueKind.String)
            return hasId && !msg.TryGetProperty("result", out _) && !msg.TryGetProperty("error", out _)
                ? RpcError(id, -32600, "Invalid request")
                : null; // a response from the client: nothing to do

        string method = methodEl.GetString()!;
        msg.TryGetProperty("params", out var prms);

        if (!hasId) return null; // notification (e.g. notifications/initialized)

        try
        {
            return method switch
            {
                "initialize" => Result(id, Initialize(prms)),
                "ping" => Result(id, new JsonObject()),
                "tools/list" => Result(id, new JsonObject { ["tools"] = _tools.ListTools() }),
                "tools/call" => CallTool(id, prms),
                _ => RpcError(id, -32601, $"Method not found: {method}"),
            };
        }
        catch (Exception ex)
        {
            return RpcError(id, -32603, "Internal error: " + ex.Message);
        }
    }

    private static JsonObject Initialize(JsonElement prms)
    {
        string? requested = prms.ValueKind == JsonValueKind.Object && prms.TryGetProperty("protocolVersion", out var v) &&
                            v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string version = requested != null && SupportedProtocolVersions.Contains(requested) ? requested : SupportedProtocolVersions[0];

        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "etaskmanager", ["title"] = "ETaskManager", ["version"] = ServerVersion },
            ["instructions"] =
                "Live CPU and memory data from ETaskManager running on the user's Windows machine. " +
                "Use get_system_overview first, get_top_processes to find what is using resources " +
                "(group_by_name=true to see apps with many processes), and get_process_details to identify a PID. " +
                "CPU percentages are a share of the whole machine (all cores = 100%). These tools are read-only.",
        };
    }

    private JsonObject CallTool(JsonNode? id, JsonElement prms)
    {
        if (prms.ValueKind != JsonValueKind.Object || !prms.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
            return RpcError(id, -32602, "Missing tool name");
        string name = nameEl.GetString()!;
        if (!_tools.HasTool(name))
            return RpcError(id, -32602, $"Unknown tool: {name}");
        prms.TryGetProperty("arguments", out var args);

        string text;
        bool isError = false;
        try { text = _tools.Call(name, args); }
        catch (ResourceTools.ToolException ex) { text = ex.Message; isError = true; }

        return Result(id, new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError,
        });
    }

    private static JsonObject Result(JsonNode? id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject RpcError(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    public void Dispose()
    {
        _stop = true;
        try { _listener.Close(); } catch { }
    }
}
