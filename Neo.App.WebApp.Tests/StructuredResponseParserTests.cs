using Neo.AssemblyForge;

namespace Neo.App.WebApp.Tests;

public class StructuredResponseParserTests
{
    [Fact]
    public void ParsesPlainJson_Lowercase()
    {
        var r = StructuredResponseParser.Parse("{\"code\":\"x\",\"chat\":\"hi\"}");
        Assert.NotNull(r);
        Assert.Equal("x", r!.Code);
        Assert.Equal("hi", r.Chat);
    }

    [Fact]
    public void ParsesPlainJson_PascalCase()
    {
        // Desktop/MCP wire format.
        var r = StructuredResponseParser.Parse("{\"Code\":\"x\",\"Chat\":\"hi\"}");
        Assert.NotNull(r);
        Assert.Equal("x", r!.Code);
        Assert.Equal("hi", r.Chat);
    }

    [Fact]
    public void ParsesFencedJson()
    {
        var raw = """
            Here you go:
            ```json
            {"Code":"Console.WriteLine(1);","Explanation":"trivial"}
            ```
            """;
        var r = StructuredResponseParser.Parse(raw);
        Assert.NotNull(r);
        Assert.Contains("Console.WriteLine", r!.Code);
    }

    [Fact]
    public void ParsesEmbeddedJson()
    {
        var raw = "Let me think... {\"Code\":\"A\",\"Chat\":null} hope that helps.";
        var r = StructuredResponseParser.Parse(raw);
        Assert.NotNull(r);
        Assert.Equal("A", r!.Code);
    }

    [Fact]
    public void HandlesEscapedQuotesInside()
    {
        var raw = "{\"Code\":\"var s = \\\"hello\\\";\",\"Chat\":\"done\"}";
        var r = StructuredResponseParser.Parse(raw);
        Assert.NotNull(r);
        Assert.Contains("\"hello\"", r!.Code);
    }

    [Fact]
    public void ReturnsNullOnEmpty()
    {
        Assert.Null(StructuredResponseParser.Parse(""));
        Assert.Null(StructuredResponseParser.Parse("   "));
    }

    [Fact]
    public void ReturnsNullOnMalformed()
    {
        Assert.Null(StructuredResponseParser.Parse("not json at all"));
    }

    [Fact]
    public void AllowsChatOnlyResponse()
    {
        var r = StructuredResponseParser.Parse("{\"Chat\":\"only a chat message\"}");
        Assert.NotNull(r);
        Assert.Equal(string.Empty, r!.Code);
        Assert.Equal("only a chat message", r.Chat);
    }

    [Fact]
    public void ParsesNuGetPackagesArray()
    {
        // Forge wire format: list of "Id|Version" strings.
        var raw = """
            {"Code":"class X{}","NuGetPackages":["MathNet.Numerics|5.0.0","NodaTime|3.1.9"]}
            """;
        var r = StructuredResponseParser.Parse(raw);
        Assert.NotNull(r);
        Assert.Equal(2, r!.NuGetPackages.Count);
        Assert.Equal("MathNet.Numerics|5.0.0", r.NuGetPackages[0]);
        Assert.Equal("NodaTime|3.1.9", r.NuGetPackages[1]);
    }

    [Fact]
    public void OmittingNuGet_YieldsEmptyList()
    {
        var r = StructuredResponseParser.Parse("{\"Code\":\"class X{}\"}");
        Assert.NotNull(r);
        Assert.Empty(r!.NuGetPackages);
    }
}
