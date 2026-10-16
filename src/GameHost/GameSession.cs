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
                _ => throw new SdkFault("INVALID_REQUEST")
            };
            if (Environment.CurrentManagedThreadId != _gameThread) { throw new SdkFault("SESSION_MISMATCH"); }
            WriteAtomic("response.json", new { requestId, session = _session, result, gameThread = _gameThread });
            PublishStatus();
        }
        catch (Exception exception)
        {
            AppendDiagnostic(exception.ToString());
            WriteAtomic("response.json", new { requestId, session = _session, result = SdkErrors.Normalize(exception) });
        }
        finally
        {
            File.Delete(processingPath);
            _busy = false;
        }
    }

    private static void PublishStatus()
    {
        using var process = Process.GetCurrentProcess();
        WriteAtomic("status.json", new
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

    private static void WriteAtomic(string name, object value)
    {
        var destination = Path.Combine(_control, name);
        var temporary = destination + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
        File.Move(temporary, destination, true);
    }

    private static void AppendDiagnostic(string message)
        => File.AppendAllText(Path.Combine(_control, "events.jsonl"),
            JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, session = _session, message }, Json) + Environment.NewLine);
}
