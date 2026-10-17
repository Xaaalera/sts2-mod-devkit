using System.Diagnostics;
using System.Text.Json;
using Godot;
using HotMod.Shell.Runtime;
using Environment = System.Environment;

namespace Sts2.Devkit.GameHost;

public static class GameSession
{
    private static readonly JsonSerializerOptions Json = ReloadJson.Options;
    private static ExtensionHost? _extensions;
    private static Godot.Timer? _timer;
    private static string _control = "";
    private static string _session = "";
    private static int _gameThread;
    private static bool _busy;
    private static readonly Dictionary<string, Func<string, string, string>> Adapters = new(StringComparer.Ordinal);

    public static IDisposable RegisterAdapter(string pluginId, Func<string, string, string> callback)
    {
        RequireGameThread();
        if (!System.Text.RegularExpressions.Regex.IsMatch(pluginId, "^[a-z][a-z0-9-]{0,63}$") || !Adapters.TryAdd(pluginId, callback))
        { throw new SdkFault("INVALID_REQUEST"); }
        return new AdapterLease(pluginId, callback);
    }

    public static string InvokeExtensionCommand(string pluginId, string command, IReadOnlyDictionary<string, string> arguments)
    {
        RequireGameThread();
        try { return JsonSerializer.Serialize(_extensions!.Invoke(pluginId, command, arguments), Json); }
        catch (SdkFault exception) when (exception.Reason is "PLUGIN_NOT_LOADED" or "SDK_BUSY")
        { return JsonSerializer.Serialize(new { available = false, reason = exception.Reason }, Json); }
    }

    private static void RequireGameThread()
    {
        if (_extensions is null || Environment.CurrentManagedThreadId != _gameThread) { throw new SdkFault("SESSION_MISMATCH"); }
    }

    private sealed class AdapterLease(string pluginId, Func<string, string, string> callback) : IDisposable
    {
        public void Dispose()
        {
            RequireGameThread();
            if (Adapters.TryGetValue(pluginId, out var current) && ReferenceEquals(current, callback)) { Adapters.Remove(pluginId); }
        }
    }

    public static void Start()
    {
        _control = Environment.GetEnvironmentVariable("STS2_DEVKIT_CONTROL")!;
        _session = Environment.GetEnvironmentVariable("STS2_DEVKIT_SESSION")!;
        var pluginRoot = Environment.GetEnvironmentVariable("STS2_DEVKIT_PLUGINS")!;
        var expectedUserDirectory = Environment.GetEnvironmentVariable("STS2_DEVKIT_USER_DIR")!;
        if (string.IsNullOrWhiteSpace(_session) || !Path.IsPathFullyQualified(pluginRoot)
            || !Path.IsPathFullyQualified(_control) || !Path.IsPathFullyQualified(expectedUserDirectory)
            || !string.Equals(Path.GetFullPath(OS.GetUserDataDir()).TrimEnd('/', '\\'),
                Path.GetFullPath(expectedUserDirectory).TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase))
        { throw new SdkFault("SESSION_MISMATCH"); }
        _extensions = new ExtensionHost(pluginRoot, AppendDiagnostic);
        _gameThread = Environment.CurrentManagedThreadId;
        Callable.From(InstallDispatcher).CallDeferred();
    }

    private static void InstallDispatcher()
    {
        _gameThread = Environment.CurrentManagedThreadId;
        var tree = (SceneTree)Engine.GetMainLoop();
        _timer = new Godot.Timer { WaitTime = 0.05, Autostart = true, ProcessMode = Node.ProcessModeEnum.Always };
        _timer.Timeout += Poll;
        tree.Root.AddChild(_timer);
        PublishStatus();
    }

    private static async void Poll()
    {
        if (_busy) { return; }
        var requestPath = Path.Combine(_control, "request.json");
        var processingPath = Path.Combine(_control, "processing.json");
        if (!File.Exists(requestPath)) { return; }
        _busy = true;
        string? requestId = null;
        object response;
        try
        {
            if (Environment.CurrentManagedThreadId != _gameThread || SynchronizationContext.Current is null)
            { throw new SdkFault("SESSION_MISMATCH"); }
            File.Move(requestPath, processingPath, true);
            using var document = JsonDocument.Parse(File.ReadAllText(processingPath));
            var request = document.RootElement;
            requestId = request.GetProperty("requestId").GetString();
            if (request.GetProperty("session").GetString() != _session) { throw new SdkFault("SESSION_MISMATCH"); }
            if (!Guid.TryParse(requestId, out _)) { throw new SdkFault("INVALID_REQUEST"); }
            var action = request.GetProperty("action").GetString();
            var pluginId = request.TryGetProperty("pluginId", out var pluginValue) ? pluginValue.GetString() ?? "" : "";
            object result = action switch
            {
                "reload" => await _extensions!.ReloadAsync(pluginId),
                "unload" => await _extensions!.UnloadAsync(pluginId),
                "invoke" => _extensions!.Invoke(pluginId, request.GetProperty("command").GetString() ?? "",
                    request.TryGetProperty("arguments", out var arguments)
                        ? arguments.Deserialize<Dictionary<string, string>>(Json) ?? new() : new()),
                "status" => _extensions!.Status(),
                "adapter" => InvokeAdapter(pluginId, request.GetProperty("command").GetString() ?? "",
                    request.TryGetProperty("arguments", out var adapterArguments) ? adapterArguments.GetRawText() : "{}"),
                _ => throw new SdkFault("INVALID_REQUEST")
            };
            if (Environment.CurrentManagedThreadId != _gameThread) { throw new SdkFault("SESSION_MISMATCH"); }
            response = new { requestId, session = _session, result, gameThread = _gameThread };
        }
        catch (Exception exception)
        {
            AppendDiagnostic(exception.ToString());
            response = new { requestId, session = _session, result = SdkErrors.Normalize(exception) };
        }
        try
        {
            await WriteAtomicAsync("response.json", response);
            await PublishStatusAsync();
        }
        catch (Exception exception)
        {
            // The operation may already be committed. Never replace its successful response with a status-publication error.
            AppendDiagnostic($"Publication failed for request {requestId}; query actual state before retrying. {exception}");
        }
        finally
        {
            try { File.Delete(processingPath); }
            finally { _busy = false; }
        }
    }

    private static void PublishStatus() => PublishStatusAsync().GetAwaiter().GetResult();

    private static Task PublishStatusAsync()
    {
        using var process = Process.GetCurrentProcess();
        return WriteAtomicAsync("status.json", new
        {
            session = _session,
            pid = Environment.ProcessId,
            startedAtUtc = process.StartTime.ToUniversalTime(),
            executable = process.MainModule!.FileName,
            userDirectory = OS.GetUserDataDir(),
            gameThread = _gameThread,
            windowMode = DisplayServer.WindowGetMode().ToString(),
            windowWidth = DisplayServer.WindowGetSize().X,
            windowHeight = DisplayServer.WindowGetSize().Y,
            plugins = _extensions!.Status(),
            libraries = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => assembly.GetName().Name is "BaseLib" or "STS2-RitsuLib" or "STS2-RitsuLib.Runtime")
                .Select(assembly => new { name = assembly.GetName().Name, version = assembly.GetName().Version?.ToString() }).ToArray()
        });
    }

    private static JsonElement InvokeAdapter(string pluginId, string command, string arguments)
    {
        if (!Adapters.TryGetValue(pluginId, out var adapter)) { throw new SdkFault("PLUGIN_NOT_LOADED"); }
        try
        {
            using var document = JsonDocument.Parse(adapter(command, arguments));
            return document.RootElement.Clone();
        }
        catch (ArgumentException) { throw new SdkFault("INVALID_REQUEST"); }
    }

    private static Task WriteAtomicAsync(string name, object value) =>
        AtomicJsonFile.WriteAsync(Path.Combine(_control, name), JsonSerializer.Serialize(value, Json));

    private static void AppendDiagnostic(string message)
        => File.AppendAllText(Path.Combine(_control, "events.jsonl"),
            JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, session = _session, message }, Json) + Environment.NewLine);
}
