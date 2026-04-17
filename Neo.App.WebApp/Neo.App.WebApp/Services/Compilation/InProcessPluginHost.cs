using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Avalonia.Controls;

namespace Neo.App.WebApp.Services.Compilation;

/// <summary>
/// Drop-in replacement for Neo's desktop ChildProcessService: loads a
/// freshly compiled plugin DLL into a collectible AssemblyLoadContext in the
/// same WASM runtime as the host, hosts the plugin Control in a slot Panel.
/// NuGet dependency DLLs can be passed alongside and are resolved on demand
/// by the ALC when the JIT first touches a referenced type.
/// </summary>
public sealed class InProcessPluginHost
{
    private CollectibleAlc? _alc;
    private WeakReference? _alcWeak;
    private Control? _currentControl;

    public event Action<Control?>? ContentChanged;

    public Control? Current => _currentControl;

    /// <param name="dependencyAssemblies">
    /// Optional map of assembly simple name → DLL bytes. These are made
    /// available to the plugin via the ALC's <c>Load</c> hook so e.g.
    /// NodaTime.dll / MathNet.Numerics.dll resolve without disk access.
    /// </param>
    public Control? LoadFromBytes(
        byte[] assemblyBytes,
        byte[]? pdbBytes = null,
        IReadOnlyDictionary<string, byte[]>? dependencyAssemblies = null)
    {
        Unload();

        _alc = new CollectibleAlc(dependencyAssemblies);
        _alcWeak = new WeakReference(_alc, trackResurrection: true);

        using var asmMs = new MemoryStream(assemblyBytes);
        using var pdbMs = pdbBytes is null ? null : new MemoryStream(pdbBytes);
        var asm = pdbMs is null
            ? _alc.LoadFromStream(asmMs)
            : _alc.LoadFromStream(asmMs, pdbMs);

        // GetTypes() throws ReflectionTypeLoadException when any referenced
        // type fails to resolve — very common if a NuGet dep isn't in the
        // ALC. Surface the inner loader errors instead of the opaque outer.
        Type[] types;
        try
        {
            types = asm.GetTypes();
        }
        catch (System.Reflection.ReflectionTypeLoadException rtle)
        {
            var loaders = (rtle.LoaderExceptions ?? Array.Empty<Exception?>())
                .Where(e => e is not null)
                .Select(e => e!.Message)
                .Distinct()
                .Take(5)
                .ToArray();
            throw new InvalidOperationException(
                "GetTypes failed: " + string.Join(" | ", loaders), rtle);
        }

        var type = types.FirstOrDefault(t =>
            typeof(Control).IsAssignableFrom(t) && !t.IsAbstract);

        if (type is null)
            throw new InvalidOperationException(
                "No public non-abstract Control-derived type found in the compiled assembly.");

        try
        {
            _currentControl = (Control?)Activator.CreateInstance(type);
        }
        catch (System.Reflection.TargetInvocationException tie)
        {
            // Always unwrap — even if InnerException is null, fabricate a
            // meaningful message instead of "Exception has been thrown by
            // the target of an invocation."
            var inner = tie.InnerException;
            if (inner is not null) throw inner;
            throw new InvalidOperationException(
                $"Constructor of '{type.FullName}' threw, but no inner exception was attached.", tie);
        }

        ContentChanged?.Invoke(_currentControl);
        return _currentControl;
    }

    public void Unload()
    {
        _currentControl = null;
        ContentChanged?.Invoke(null);

        if (_alc is null) return;
        _alc.Unload();
        _alc = null;

        for (int i = 0; i < 10 && _alcWeak?.IsAlive == true; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    public bool IsAlcAlive => _alcWeak?.IsAlive ?? false;

    private sealed class CollectibleAlc : AssemblyLoadContext
    {
        private readonly IReadOnlyDictionary<string, byte[]>? _deps;

        public CollectibleAlc(IReadOnlyDictionary<string, byte[]>? deps)
            : base(isCollectible: true)
        {
            _deps = deps;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (_deps is null || assemblyName.Name is null) return null;
            if (_deps.TryGetValue(assemblyName.Name, out var bytes))
            {
                using var ms = new MemoryStream(bytes, writable: false);
                return LoadFromStream(ms);
            }
            return null; // fall through to default (host-shared) resolution
        }
    }
}
