using System.Text;

namespace Neo.ErrorSurfacing;

/// <summary>The source files in Runtime/: compiled into apps, not into this library, which carries them as resources.</summary>
internal static class EmbeddedSource
{
    public static string Read(string fileName)
    {
        using var stream = typeof(EmbeddedSource).Assembly.GetManifestResourceStream(fileName)
            ?? throw new InvalidOperationException($"{fileName} is missing from {typeof(EmbeddedSource).Assembly.GetName().Name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        // The same text on every machine: the file's line ends depend on how it was checked out.
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }
}
