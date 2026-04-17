using Neo.App.WebApp.Services.Ai;

namespace Neo.App.WebApp.Tests;

public class StructuredResponseParserTests
{
    [Fact]
    public void ParsesPlainJson()
    {
        var r = StructuredResponseParser.Parse("{\"code\":\"x\",\"chat\":\"hi\"}");
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
            {"code":"Console.WriteLine(1);","explanation":"trivial"}
            ```
            """;
        var r = StructuredResponseParser.Parse(raw);
        Assert.NotNull(r);
        Assert.Contains("Console.WriteLine", r!.Code);
    }

    [Fact]
    public void ParsesEmbeddedJson()
    {
        var raw = "Let me think... {\"code\":\"A\",\"chat\":null} hope that helps.";
        var r = StructuredResponseParser.Parse(raw);
        Assert.NotNull(r);
        Assert.Equal("A", r!.Code);
    }

    [Fact]
    public void HandlesEscapedQuotesInside()
    {
        var raw = "{\"code\":\"var s = \\\"hello\\\";\",\"chat\":\"done\"}";
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
        var r = StructuredResponseParser.Parse("{\"chat\":\"only a chat message\"}");
        Assert.NotNull(r);
        Assert.Null(r!.Code);
        Assert.Equal("only a chat message", r.Chat);
    }

    [Fact]
    public void ParsesNuGetArray()
    {
        var raw = "{\"code\":\"class X{}\",\"nuget\":[{\"id\":\"MathNet.Numerics\",\"version\":\"5.0.0\"},{\"id\":\"NodaTime\",\"version\":\"3.1.9\"}]}";
        var r = StructuredResponseParser.Parse(raw);
        Assert.NotNull(r);
        Assert.NotNull(r!.NuGet);
        Assert.Equal(2, r.NuGet!.Length);
        Assert.Equal("MathNet.Numerics", r.NuGet[0].Id);
        Assert.Equal("5.0.0", r.NuGet[0].Version);
        Assert.Equal("NodaTime", r.NuGet[1].Id);
    }

    [Fact]
    public void OmittingNuGet_YieldsNull()
    {
        var r = StructuredResponseParser.Parse("{\"code\":\"class X{}\"}");
        Assert.NotNull(r);
        Assert.Null(r!.NuGet);
    }
}
