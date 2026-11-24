using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VfxStudio.Re4Preview;

/// <summary>One request from the AI control pipe. The window thread owns every field except
/// <see cref="Reply"/>, which completes the waiting pipe connection.</summary>
internal sealed class PreviewControlRequest(string id, string action, JsonObject arguments)
{
    public string Id { get; } = id;
    public string Action { get; } = action;
    public JsonObject Arguments { get; } = arguments;
    public TaskCompletionSource<JsonObject> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public DateTime Deadline { get; set; } = DateTime.UtcNow.AddSeconds(90);
    // Progress kept by the window thread between updates.
    public int Phase { get; set; }
    public Task<VfxStudio.Re4Render.PreviewEditorState>? EditTask { get; set; }
    public JsonObject? BackendReply { get; set; }
    public int? Tick { get; set; }
    public byte[]? Pixels { get; set; }
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }
    public Task<JsonObject>? Encoding { get; set; }

    public void Succeed(JsonObject result) => Reply.TrySetResult(new JsonObject { ["protocol"] = PreviewControlServer.Protocol, ["id"] = Id, ["ok"] = true, ["result"] = result });
    public void Fail(string code, string message, JsonNode? details = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (details != null) error["details"] = details;
        Reply.TrySetResult(new JsonObject { ["protocol"] = PreviewControlServer.Protocol, ["id"] = Id, ["ok"] = false, ["error"] = error });
    }
}

/// <summary>One answered pipe connection, for the window's MCP status panel.</summary>
internal sealed record PreviewControlLogEntry(DateTime Time, int? ClientPid, string? Action, bool Ok, string? Code, string? Message, double Milliseconds);

/// <summary>Named pipe through which the MCP server drives this window. One connection carries one
/// JSON line each way. The pipe accepts only the current Windows user; requests run on the window
/// thread, one at a time, through the same backend session as the edit panel.</summary>
internal sealed class PreviewControlServer : IDisposable
{
    public const string DefaultPipe = "MotionVfx.Re4Preview.Control";
    public const int Protocol = 1;
    private const int MaximumRequestBytes = 1024 * 1024;
    private readonly ConcurrentQueue<PreviewControlRequest> queue = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private readonly NamedPipeServerStream? server;

    private readonly object logLock = new();
    private readonly List<PreviewControlLogEntry> log = new();
    private const int LogLimit = 100;

    public string PipeName { get; }
    /// <summary>Why the pipe is unavailable, or null while it is listening.</summary>
    public string? Error { get; private set; }
    /// <summary>The window runs elevated: its pipe then only admits elevated clients (see Error).</summary>
    public bool Elevated { get; } = IsElevated();
    public DateTime StartedAt { get; } = DateTime.Now;
    public int Connections { get; private set; }
    /// <summary>A client is connected and waiting for its reply.</summary>
    public bool Connected { get; private set; }

    /// <summary>The newest answered connections, oldest first.</summary>
    public PreviewControlLogEntry[] Log() { lock (logLock) return log.ToArray(); }

    public PreviewControlServer(string pipeName)
    {
        PipeName = pipeName;
        try
        {
            // A single instance holds the name for the window's lifetime, so a second window
            // cannot take over the channel between connections.
            server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }
        catch (UnauthorizedAccessException ex)
        {
            // The name exists with a security descriptor this process may not join: a window at another
            // elevation level, another user, or another program.
            Error = $"AI 控制通道 {pipeName} 已被其他进程占用且拒绝访问（{ex.Message}）。" + OtherWindows() +
                "通常是另一个预览窗口以不同的权限（管理员/非管理员）运行；关闭它后重开本窗口。";
        }
        catch (IOException ex)
        {
            Error = $"AI 控制通道 {pipeName} 已被另一个预览窗口占用（{ex.Message}）。" + OtherWindows() + "关闭它后重开本窗口，或用 --ai-pipe 换一个名字。";
        }
        loop = server == null ? Task.CompletedTask : Task.Run(Run);
    }

    public int Queued => queue.Count;
    public bool TryDequeue(out PreviewControlRequest request) => queue.TryDequeue(out request!);

    private async Task Run()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await server!.WaitForConnectionAsync(stop.Token);
                await Serve(server);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            // A request that did not arrive within its 10 s read timeout cancels only that connection; treating it as
            // shutdown ended this loop and left the channel dead for the rest of the window's life.
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
            // Also when IsConnected is already false: a client that closed first leaves the instance broken, and only
            // Disconnect() lets WaitForConnectionAsync listen again (otherwise it throws at once, forever).
            try { server!.Disconnect(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        }
    }

    private async Task Serve(NamedPipeServerStream pipe)
    {
        JsonObject reply;
        var started = DateTime.Now;
        int? client = GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) ? (int)pid : null;
        Connections++; Connected = true;
        string? action = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            byte[]? line = await ReadLine(pipe, timeout.Token);
            reply = line == null ? Rejected(null, "invalid_request", "请求超过 1 MiB 或没有换行。") : await Dispatch(line, a => action = a);
        }
        catch (Exception ex) when (ex is OperationCanceledException or EndOfStreamException)
        {
            Record(new PreviewControlLogEntry(started, client, action, false, "no_request",
                ex is EndOfStreamException ? "客户端没发请求就断开了。" : "10 秒内没收到完整请求。", (DateTime.Now - started).TotalMilliseconds));
            Connected = false;
            throw;
        }
        bool ok = reply["ok"]?.GetValue<bool>() == true;
        Record(new PreviewControlLogEntry(started, client, action, ok, ok ? null : reply["error"]?["code"]?.GetValue<string>(),
            ok ? null : reply["error"]?["message"]?.GetValue<string>(), (DateTime.Now - started).TotalMilliseconds));
        Connected = false;
        byte[] bytes = new UTF8Encoding(false).GetBytes(reply.ToJsonString() + "\n");
        await pipe.WriteAsync(bytes, stop.Token);
        await pipe.FlushAsync(stop.Token);
        pipe.WaitForPipeDrain();
    }

    private void Record(PreviewControlLogEntry entry)
    {
        lock (logLock) { log.Add(entry); if (log.Count > LogLimit) log.RemoveAt(0); }
    }

    private async Task<JsonObject> Dispatch(byte[] line, Action<string> seen)
    {
        JsonObject request;
        try { request = JsonNode.Parse(line, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { return Rejected(null, "invalid_request", "请求必须是一行 JSON 对象。"); }
        string? id = request["id"] is JsonValue idValue && idValue.TryGetValue(out string? text) ? text : null;
        if (string.IsNullOrEmpty(id) || id.Length > 200) return Rejected(null, "invalid_request", "缺少有效的请求 id。");
        if (request["protocol"]?.ToJsonString() != Protocol.ToString()) return Rejected(id, "protocol_mismatch", $"预览窗口只支持控制协议 {Protocol}。");
        if (request["action"] is not JsonValue actionValue || !actionValue.TryGetValue(out string? action) || string.IsNullOrEmpty(action))
            return Rejected(id, "invalid_request", "缺少 action。");
        seen(action);
        var arguments = request["arguments"] as JsonObject ?? new JsonObject();
        var item = new PreviewControlRequest(id, action, (JsonObject)arguments.DeepClone());
        queue.Enqueue(item);
        using var registration = stop.Token.Register(() => item.Fail("window_closing", "预览窗口正在关闭，请求结果不确定。"));
        return await item.Reply.Task;
    }

    private static JsonObject Rejected(string? id, string code, string message) => new()
    { ["protocol"] = Protocol, ["id"] = id, ["ok"] = false, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private static async Task<byte[]?> ReadLine(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var line = new MemoryStream();
        while (true)
        {
            int count = await stream.ReadAsync(buffer, cancellationToken);
            // The client went away without a complete request: nobody would read a reply. Run() drops the connection.
            if (count == 0) throw new EndOfStreamException("The client closed the connection before sending a request.");
            int end = Array.IndexOf(buffer, (byte)'\n', 0, count);
            line.Write(buffer, 0, end < 0 ? count : end);
            if (line.Length > MaximumRequestBytes) return null;
            if (end >= 0) return line.ToArray();
        }
    }

    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static string OtherWindows()
    {
        int self = Environment.ProcessId;
        var others = System.Diagnostics.Process.GetProcessesByName("VfxStudio.Re4Preview").Select(p => { using (p) return p.Id; }).Where(id => id != self).ToArray();
        return others.Length == 0 ? "没有找到其他预览窗口，名字被别的程序占用。" : "其他预览窗口：pid " + string.Join(", ", others) + "。";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);

    public void Dispose()
    {
        stop.Cancel();
        while (queue.TryDequeue(out var item)) item.Fail("window_closing", "预览窗口正在关闭，请求未执行。");
        try { server?.Dispose(); } catch (IOException) { }
        try { loop.Wait(2000); } catch (AggregateException) { }
    }
}
