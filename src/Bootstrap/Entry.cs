using System.Reflection;
using System.Runtime.Loader;
using Godot;
using MegaCrit.Sts2.Core.Modding;
using Environment = System.Environment;

namespace Sts2.Devkit.Bootstrap;

[ModInitializer(nameof(Initialize))]
public static class Entry
{
    public static void Initialize()
    {
        var directory = Path.GetDirectoryName(typeof(Entry).Assembly.Location)!;
        var control = Environment.GetEnvironmentVariable("STS2_DEVKIT_CONTROL");
        if (string.IsNullOrWhiteSpace(control)) { return; }
        Directory.CreateDirectory(control);
        File.WriteAllText(Path.Combine(control, "bootstrap.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            pid = Environment.ProcessId,
            userDirectory = OS.GetUserDataDir(),
            threadId = Environment.CurrentManagedThreadId,
            hasSynchronizationContext = SynchronizationContext.Current is not null,
            gameLoadContext = AssemblyLoadContext.GetLoadContext(typeof(ModManager).Assembly)?.Name,
            godotLoadContext = AssemblyLoadContext.GetLoadContext(typeof(GD).Assembly)?.Name
        }));
        AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(directory, "HotMod.Contracts.dll"));
        var gameContext = AssemblyLoadContext.GetLoadContext(typeof(Entry).Assembly)!;
        gameContext.Resolving += ResolveSdk;
        gameContext.LoadFromAssemblyPath(Path.Combine(directory, "Sts2.Devkit.Runtime.dll"));
        var host = gameContext.LoadFromAssemblyPath(Path.Combine(directory, "Sts2.Devkit.GameHost.dll"));
        host.GetType("Sts2.Devkit.GameHost.GameSession", true)!.GetMethod("Start")!.Invoke(null, null);
    }

    private static Assembly? ResolveSdk(AssemblyLoadContext context, AssemblyName name)
        => name.Name is "HotMod.Contracts"
            ? AssemblyLoadContext.Default.Assemblies.SingleOrDefault(assembly =>
                AssemblyName.ReferenceMatchesDefinition(name, assembly.GetName())) : null;
}
