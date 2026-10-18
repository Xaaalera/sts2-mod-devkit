using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using HotMod.Contracts;
using HotMod.Shell.Runtime;

namespace Sts2.Devkit;

public sealed class ExtensionHost(string pluginRoot, Action<string> diagnosticLog) : IHotHost, IDisposable
{
    private readonly string _pluginRoot = Path.GetFullPath(pluginRoot);
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly Dictionary<string, Slot> _plugins = new(StringComparer.Ordinal);
    private bool _disposed;

    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    public async Task<ExtensionStatus> ReloadAsync(string pluginId)
    {
        if (!await _operations.WaitAsync(0)) { throw new SdkFault("SDK_BUSY"); }
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var sourcePath = ResolveSource(pluginId);
            _plugins.TryGetValue(pluginId, out var slot);
            slot ??= new Slot();
            var manager = new ReloadManager(this);
            ReloadOutcome? outcome = await manager.ReloadAsync(
                new ReloadRequest(sourcePath, Path.Combine(_pluginRoot, ".shadow", pluginId), null, "sdk-command", true),
                slot.Generation);
            var candidate = outcome.NewGeneration;
            var committed = false;
            try
            {
                diagnosticLog(ReloadJson.Serialize(outcome.Report));
                if (candidate is null) { throw new SdkFault("RELOAD_FAILED"); }
                if (candidate.Manifest.ModId != pluginId) { throw new SdkFault("PLUGIN_ID_MISMATCH"); }
                if (candidate.Manifest.Capabilities is null || string.IsNullOrWhiteSpace(candidate.Manifest.LogicVersion))
                { throw new SdkFault("RELOAD_FAILED"); }
                var capabilities = candidate.Manifest.Capabilities.ToDictionary();
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(candidate.ShadowAssemblyPath)));
                var release = ApplyGeneration(slot, candidate, capabilities, hash);
                _plugins[pluginId] = slot;
                committed = true;
                outcome = null;
                slot.PreviousCollected = release.Reference is null ? null : CollectPrevious(release.Reference);
                slot.PreviousDisposed = release.Disposed;
                return Snapshot(pluginId, slot);
            }
            finally { if (!committed && candidate is not null) { ReleaseGeneration(candidate); } }
        }
        finally { _operations.Release(); }
    }

    public HotLogicResult Invoke(string pluginId, string command, IReadOnlyDictionary<string, string> arguments)
    {
        if (!_operations.Wait(0)) { throw new SdkFault("SDK_BUSY"); }
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_plugins.TryGetValue(pluginId, out var slot) || slot.Generation?.Logic is null)
            { throw new SdkFault("PLUGIN_NOT_LOADED"); }
            if (!slot.Capabilities.ContainsKey($"command:{command}")) { throw new SdkFault("INVALID_REQUEST"); }
            var result = slot.Generation.Logic.OnHook(new HotHookContext(
                $"command:{command}", slot.Generation.Generation, arguments));
            return new HotLogicResult(result.Handled, result.Messages.ToArray());
        }
        finally { _operations.Release(); }
    }

    public IReadOnlyList<ExtensionStatus> Status()
    {
        if (!_operations.Wait(0)) { throw new SdkFault("SDK_BUSY"); }
        try { return _plugins.Select(pair => Snapshot(pair.Key, pair.Value)).ToArray(); }
        finally { _operations.Release(); }
    }

    public async Task<UnloadReceipt> UnloadAsync(string pluginId)
    {
        if (!await _operations.WaitAsync(0)) { throw new SdkFault("SDK_BUSY"); }
        try
        {
            if (!_plugins.Remove(pluginId, out var slot)) { throw new SdkFault("PLUGIN_NOT_LOADED"); }
            var released = ReleaseGeneration(slot.Generation);
            slot.Generation = null;
            return new UnloadReceipt(pluginId, released.Disposed == true,
                released.Reference is null || CollectPrevious(released.Reference));
        }
        finally { _operations.Release(); }
    }

    public void Dispose()
    {
        if (!_operations.Wait(0)) { throw new SdkFault("SDK_BUSY"); }
        try
        {
            if (_disposed) { return; }
            _disposed = true;
            foreach (var slot in _plugins.Values)
            {
                ReleaseGeneration(slot.Generation);
                slot.Generation = null;
            }
            _plugins.Clear();
        }
        finally { _operations.Release(); }
    }

    public void Log(HotLogLevel level, string message, IReadOnlyDictionary<string, string>? fields = null)
        => diagnosticLog(ReloadJson.Serialize(new { level, message, fields = fields?.ToDictionary() }));

    private string ResolveSource(string pluginId)
    {
        if (!Regex.IsMatch(pluginId, "^[a-z][a-z0-9-]{0,63}$")) { throw new SdkFault("INVALID_REQUEST"); }
        var directory = Path.Combine(_pluginRoot, pluginId);
        var source = Path.Combine(directory, $"{pluginId}.dll");
        if ((Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            || (File.Exists(source) && (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0))
        { throw new SdkFault("INVALID_REQUEST"); }
        return source;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference? Reference, bool? Disposed) ApplyGeneration(
        Slot slot, ReloadGeneration candidate, Dictionary<string, string> capabilities, string hash)
    {
        var previous = slot.Generation;
        slot.Generation = candidate;
        slot.Version = candidate.Manifest.LogicVersion;
        slot.Capabilities = capabilities;
        slot.Hash = hash;
        return ReleaseGeneration(previous);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference? Reference, bool? Disposed) ReleaseGeneration(ReloadGeneration? generation)
    {
        var reference = generation?.LoadContextWeakReference;
        var disposed = generation?.DisposeLogic().Disposed;
        generation?.RequestUnload();
        return (reference, disposed);
    }

    private static bool CollectPrevious(WeakReference reference)
    {
        for (var attempt = 0; reference.IsAlive && attempt < 3; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        return !reference.IsAlive;
    }

    private static ExtensionStatus Snapshot(string pluginId, Slot slot) => new(
        pluginId, slot.Version, slot.Generation!.Generation, HotContract.ContractVersion,
        slot.Hash, slot.Capabilities.ToDictionary(), slot.PreviousDisposed, slot.PreviousCollected);

    private sealed class Slot
    {
        public ReloadGeneration? Generation;
        public string Version = "";
        public string Hash = "";
        public Dictionary<string, string> Capabilities = new();
        public bool? PreviousDisposed;
        public bool? PreviousCollected;
    }
}

public sealed record ExtensionStatus(string PluginId, string Version, int Generation, int ContractVersion,
    string SourceSha256, IReadOnlyDictionary<string, string> Capabilities,
    bool? PreviousDisposed, bool? PreviousCollected);

public sealed record UnloadReceipt(string PluginId, bool Disposed, bool Collected);
