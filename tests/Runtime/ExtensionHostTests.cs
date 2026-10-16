using System.Text.Json;
using Sts2.Devkit;
using Xunit;

namespace Sts2.Devkit.Tests;

public sealed class ExtensionHostTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sts2-devkit-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _diagnostics = new();

    public ExtensionHostTests()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "fixture"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "Fixture.dll"), Path.Combine(_directory, "fixture", "fixture.dll"));
    }

    [Fact]
    public async Task LoadedExtensionAdvertisesAndExecutesItsCommand()
    {
        using var host = new ExtensionHost(_directory, _diagnostics.Add);
        var status = await host.ReloadAsync("fixture");
        Assert.Equal("fixture", status.PluginId);
        Assert.Equal("{\"value\":\"1\"}", host.Invoke("fixture", "echo", new Dictionary<string, string>()).Messages.Single());
    }

    [Fact]
    public async Task FailedReplacementPreservesThePreviousCommandBehavior()
    {
        using var host = new ExtensionHost(_directory, _diagnostics.Add);
        await host.ReloadAsync("fixture");
        File.WriteAllText(Path.Combine(_directory, "fixture", "fixture.dll"), "incomplete artifact");
        var fault = await Assert.ThrowsAsync<SdkFault>(() => host.ReloadAsync("fixture"));
        Assert.Equal("RELOAD_FAILED", fault.Reason);
        Assert.Equal("{\"value\":\"1\"}", host.Invoke("fixture", "echo", new Dictionary<string, string>()).Messages.Single());
    }

    [Fact]
    public async Task ReplacementDisposesAndCollectsItsPreviousGeneration()
    {
        using var host = new ExtensionHost(_directory, _diagnostics.Add);
        await host.ReloadAsync("fixture");
        host.Invoke("fixture", "echo", new Dictionary<string, string>());
        var status = await host.ReloadAsync("fixture");
        Assert.Equal(2, status.Generation);
        Assert.True(status.PreviousDisposed);
        Assert.True(status.PreviousCollected);
        Assert.Contains(_diagnostics, message => message.Contains("fixture.disposed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DirectoryTraversalIsRejectedBeforeLoadingAnAssembly()
    {
        using var host = new ExtensionHost(_directory, _diagnostics.Add);
        var fault = await Assert.ThrowsAsync<SdkFault>(() => host.ReloadAsync("../fixture"));
        Assert.Equal("INVALID_REQUEST", fault.Reason);
        Assert.Empty(host.Status());
    }

    [Fact]
    public async Task ManifestIdentityCannotRegisterUnderAnotherPluginId()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "other"));
        File.Copy(Path.Combine(_directory, "fixture", "fixture.dll"), Path.Combine(_directory, "other", "other.dll"));
        using var host = new ExtensionHost(_directory, _diagnostics.Add);
        var fault = await Assert.ThrowsAsync<SdkFault>(() => host.ReloadAsync("other"));
        Assert.Equal("PLUGIN_ID_MISMATCH", fault.Reason);
        Assert.Empty(host.Status());
    }

    [Fact]
    public async Task UnadvertisedCommandsCannotReachAnExtension()
    {
        using var host = new ExtensionHost(_directory, _diagnostics.Add);
        await host.ReloadAsync("fixture");
        var fault = Assert.Throws<SdkFault>(() => host.Invoke("fixture", "undeclared", new Dictionary<string, string>()));
        Assert.Equal("INVALID_REQUEST", fault.Reason);
    }

    [Fact]
    public async Task UnloadedExtensionStopsReceivingCommandsAndReleasesItsAssembly()
    {
        using var host = new ExtensionHost(_directory, _diagnostics.Add);
        await host.ReloadAsync("fixture");
        host.Invoke("fixture", "echo", new Dictionary<string, string>());
        var receipt = await host.UnloadAsync("fixture");
        Assert.True(receipt.Disposed);
        Assert.True(receipt.Collected);
        Assert.Empty(host.Status());
        Assert.Equal("PLUGIN_NOT_LOADED", Assert.Throws<SdkFault>(() =>
            host.Invoke("fixture", "echo", new Dictionary<string, string>())).Reason);
    }

    [Fact]
    public void PublicErrorsKeepStableReasonsAndHideRawExceptionMessages()
    {
        var wire = JsonSerializer.Serialize(SdkErrors.Normalize(new IOException("private diagnostic")), HotMod.Shell.Runtime.ReloadJson.Options);
        using var document = JsonDocument.Parse(wire);
        Assert.Equal("INTERNAL", document.RootElement.GetProperty("error").GetProperty("status").GetString());
        Assert.Equal("INTERNAL_FAILURE", document.RootElement.GetProperty("error").GetProperty("details")[0].GetProperty("reason").GetString());
        Assert.DoesNotContain("private diagnostic", wire);
    }

    public void Dispose()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sts2-devkit-tests"));
        if (Directory.GetParent(Path.GetFullPath(_directory))!.FullName == expectedParent)
        { Directory.Delete(_directory, true); }
    }
}
