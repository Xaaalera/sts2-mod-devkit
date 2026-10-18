using HotMod.Contracts;
using System.Text.Json;

namespace Fixture;

public sealed class FixtureExtension : IHotLogic
{
    private IHotHost? _host;
    public HotLogicManifest Manifest => new("fixture", "Fixture", "1.0.0", HotContract.ContractVersion,
#if INVALID_CAPABILITIES
        null!);
#else
        new Dictionary<string, string> { ["command:echo"] = "fixture" });
#endif
    public void Initialize(IHotHost host, HotReloadContext context) => _host = host;
    public HotLogicResult OnHook(HotHookContext context) => new(true,
        [JsonSerializer.Serialize(new Dictionary<string, string> { ["value"] = context.Generation.ToString() })]);
    public void Dispose() => _host?.Log(HotLogLevel.Information, "fixture.disposed",
        new Dictionary<string, string> { ["assembly"] = typeof(FixtureExtension).Assembly.GetName().Name! });
}
