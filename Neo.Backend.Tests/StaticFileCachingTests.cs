using Neo.Backend.Services;

namespace Neo.Backend.Tests;

/// <summary>
/// Files that keep their name across publishes must be revalidated by the browser; otherwise an
/// upgraded backend serves a new _framework to a cached, old loader and the Web App never boots.
/// </summary>
public class StaticFileCachingTests
{
    [Theory]
    [InlineData("Avalonia.Base.5d2jkk9qdl.dll")]
    [InlineData("dotnet.native.f8ry4st1s8.wasm")]
    [InlineData("dotnet.runtime.nkt61nz8g3.js")]
    [InlineData("icudt_CJK.tjcz0u77k5.dat")]
    [InlineData("Avalonia.Base.5d2jkk9qdl.dll.br")]
    public void Fingerprinted_files_may_be_cached(string fileName)
        => Assert.True(StaticFileCaching.IsFingerprinted(fileName));

    [Theory]
    [InlineData("index.html")]
    [InlineData("main.js")]
    [InlineData("dotnet.js")]
    [InlineData("blazor.boot.json")]
    [InlineData("blazor.boot.json.br")]
    [InlineData("neo-monaco.js")]
    [InlineData("app.css")]
    [InlineData("System.Private.CoreLib.dll")]
    public void Stable_names_are_always_revalidated(string fileName)
        => Assert.False(StaticFileCaching.IsFingerprinted(fileName));
}
