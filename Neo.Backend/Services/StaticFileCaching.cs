using System.Text.RegularExpressions;

namespace Neo.Backend.Services;

internal static partial class StaticFileCaching
{
    /// <summary>
    /// True for files the WebAssembly publish fingerprints, e.g. <c>Avalonia.Base.5d2jkk9qdl.dll</c>
    /// or <c>dotnet.native.f8ry4st1s8.wasm(.br|.gz)</c>: their content can never change under the
    /// same name, so browsers may cache them without revalidating.
    /// </summary>
    public static bool IsFingerprinted(string fileName) => Fingerprint().IsMatch(fileName);

    [GeneratedRegex(@"\.[a-z0-9]{10}\.[A-Za-z0-9]+(\.(br|gz))?$")]
    private static partial Regex Fingerprint();
}
