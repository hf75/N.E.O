using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Neo.ErrorSurfacing.Tests;

/// <summary>Compiles app code the way the compile agents do (default options, everything the runtime has as references).</summary>
internal static class TestCompiler
{
    private static readonly Lazy<IReadOnlyList<MetadataReference>> s_references = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && IsManaged(path))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList());

    public static CSharpCompilation Compilation(params string[] sources)
        => CSharpCompilation.Create("App_" + Guid.NewGuid().ToString("N")[..8],
            sources.Select(source => CSharpSyntaxTree.ParseText(source)),
            s_references.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    /// <summary>Instruments, compiles and loads the app; fails with the compiler's messages.</summary>
    public static Assembly Load(params string[] sources)
    {
        var compilation = LostExceptions.Instrument(Compilation(sources));
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        if (!result.Success)
            throw new InvalidOperationException("The instrumented app does not compile:\n" +
                string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)) +
                "\n\n" + string.Join("\n\n", compilation.SyntaxTrees.Select(t => t.ToString())));
        stream.Position = 0;
        return new AssemblyLoadContext(compilation.AssemblyName, isCollectible: true).LoadFromStream(stream);
    }

    private static bool IsManaged(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var reader = new PEReader(file);
            return reader.HasMetadata && reader.GetMetadataReader().IsAssembly;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// A UI thread's context, as far as the runtime class can tell: it runs what is posted to it with
/// itself as the current context, and keeps what that throws — the exceptions that surfaced.
/// </summary>
internal sealed class RecordingContext : SynchronizationContext
{
    private readonly BlockingCollection<Exception> _thrown = new();

    public override void Post(SendOrPostCallback callback, object? state)
        => ThreadPool.QueueUserWorkItem(_ =>
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { callback(state); }
            catch (Exception exception) { _thrown.Add(exception); }
            finally { SetSynchronizationContext(previous); }
        });

    public Exception? NextThrown(TimeSpan timeout) => _thrown.TryTake(out var exception, timeout) ? exception : null;

    /// <summary>Runs <paramref name="action"/> on this thread with this context current, like code on the UI thread.</summary>
    public void Run(Action action)
    {
        var previous = Current;
        SetSynchronizationContext(this);
        try { action(); }
        finally { SetSynchronizationContext(previous); }
    }
}
