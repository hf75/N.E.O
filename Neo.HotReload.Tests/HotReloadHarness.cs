using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Neo.Agents;
using Neo.IPC;
using Neo.Shared;

namespace Neo.HotReload.Tests;

/// <summary>
/// Both halves of hot reload in one process, wired the way the hosts and the plugin windows wire
/// them: the real compile agent builds the baseline, the plugin is loaded from bytes, a
/// <see cref="HotReloadSession"/> computes the update, <see cref="HotReloadRuntime"/> applies it
/// to the running object.
/// </summary>
internal sealed class HotReloadHarness : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "neo_hotreload_tests", Guid.NewGuid().ToString("N")[..10]);

    public HotReloadSession Session { get; private set; } = null!;
    public string DllPath { get; private set; } = "";
    public string AssemblyName { get; private set; } = "";
    public Assembly Plugin { get; private set; } = null!;
    public object Root { get; private set; } = null!;

    public static async Task<HotReloadHarness> StartAsync(string source)
    {
        var harness = new HotReloadHarness();
        Directory.CreateDirectory(harness._directory);

        // Unique per test: the assemblies stay loaded for the lifetime of the test process.
        var assemblyName = "HotReloadTest_" + Guid.NewGuid().ToString("N")[..8];
        var dllPath = Path.Combine(harness._directory, assemblyName + ".dll");
        await CompileAsync(source, assemblyName, dllPath);

        // Hosts only know the DLL path; everything else is in the file the agent wrote next to it.
        var baseline = HotReloadBaseline.TryLoad(dllPath)
            ?? throw new InvalidOperationException("The compile agent left no hot reload baseline next to the DLL.");
        harness.DllPath = dllPath;
        harness.AssemblyName = assemblyName;

        // Like the plugin windows: from bytes, into a context that cannot be unloaded.
        var context = new AssemblyLoadContext(assemblyName, isCollectible: false);
        harness.Plugin = context.LoadFromStream(new MemoryStream(await File.ReadAllBytesAsync(dllPath)));
        harness.Root = Activator.CreateInstance(harness.Plugin.GetType("DynamicUserControl", throwOnError: true)!)!;

        harness.Session = await HotReloadSession.StartAsync(baseline, HotReloadRuntime.Capabilities ?? "Baseline");
        return harness;
    }

    /// <summary>The real compile agent with its defaults — which are hot-reloadable builds.</summary>
    public static async Task CompileAsync(string source, string assemblyName, string dllPath)
    {
        var agent = new CSharpDllCompileAgent();
        agent.SetOption("CoreDllPath", new List<string> { RuntimeEnvironment.GetRuntimeDirectory() });
        agent.SetInput("Code", new List<string> { source });
        agent.SetInput("AssemblyName", assemblyName);
        agent.SetInput("DllOutputPath", dllPath);
        agent.SetInput("NuGetDlls", new List<string>());
        agent.SetInput("AdditionalDlls", new List<string>());
        await agent.ExecuteAsync();
    }

    /// <summary>What a plugin window does with a request (without a UI toolkit).</summary>
    public HotReloadResultMessage ApplyLikeAPluginWindow(HotReloadRequest request)
    {
        request = Json.FromJson<HotReloadRequest>(Json.ToJson(request))!; // as over the pipe
        var applied = HotReloadRuntime.Apply(Plugin, Root, request);
        if (request.RebuildUi)
            HotReloadRuntime.InvokeRebuild(Root, request.RebuildMethodName!);
        return new HotReloadResultMessage(true, Details: applied.ToString());
    }

    /// <summary>Computes the update for the new source and, if there is one, applies it to the running plugin.</summary>
    public async Task<(HotReloadUpdate Update, HotReloadRuntime.ApplyResult? Applied)> UpdateAsync(string newSource)
    {
        var update = await Session.CreateUpdateAsync([newSource]);
        if (update.Status != HotReloadStatus.Ready) return (update, null);

        // Through JSON, as over the pipe.
        var request = Json.FromJson<HotReloadRequest>(Json.ToJson(update.Request!))!;
        var applied = HotReloadRuntime.Apply(Plugin, Root, request);
        if (request.RebuildUi)
            HotReloadRuntime.InvokeRebuild(Root, request.RebuildMethodName!);
        return (update, applied);
    }

    public T Get<T>(string member) => (T)Read(Root, member)!;

    public object? Call(string method, params object?[] args)
        => Root.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Root, args);

    public static object? Read(object target, string member)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var type = target.GetType();
        return type.GetField(member, all)?.GetValue(target) ?? type.GetProperty(member, all)?.GetValue(target);
    }

    public void Dispose()
    {
        Session?.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch { /* loaded files */ }
    }
}
