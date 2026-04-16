using FluentAssertions;
using Neo.App;
using Newtonsoft.Json;
using Xunit;

namespace Neo.App.Core.Tests;

public class PatchOperationTests
{
    // ── Default construction ────────────────────────────────────────

    [Fact]
    public void DefaultConstruction_AllFieldsAreEmptyStrings()
    {
        var op = new PatchOperation();

        op.Operation.Should().BeEmpty();
        op.Signature.Should().BeEmpty();
        op.ParentSignature.Should().BeEmpty();
        op.NewContent.Should().BeEmpty();
    }

    // ── Property assignment ─────────────────────────────────────────

    [Fact]
    public void Properties_CanBeSetAndRead()
    {
        var op = new PatchOperation
        {
            Operation = "REPLACE",
            Signature = "public void Foo()",
            ParentSignature = "public class Bar",
            NewContent = "public void Foo() { return; }",
        };

        op.Operation.Should().Be("REPLACE");
        op.Signature.Should().Be("public void Foo()");
        op.ParentSignature.Should().Be("public class Bar");
        op.NewContent.Should().Be("public void Foo() { return; }");
    }

    // ── JSON roundtrip (using Newtonsoft due to [JsonProperty]) ─────

    [Fact]
    public void JsonRoundtrip_PreservesAllFields()
    {
        var original = new PatchOperation
        {
            Operation = "ADD",
            Signature = "",
            ParentSignature = "public class MyClass",
            NewContent = "public int NewProp { get; set; }",
        };

        var json = JsonConvert.SerializeObject(original);
        var deserialized = JsonConvert.DeserializeObject<PatchOperation>(json);

        deserialized.Should().NotBeNull();
        deserialized!.Operation.Should().Be("ADD");
        deserialized.ParentSignature.Should().Be("public class MyClass");
        deserialized.NewContent.Should().Be("public int NewProp { get; set; }");
    }

    [Fact]
    public void JsonDeserialization_FromSnakeCaseKeys()
    {
        // The [JsonProperty] attributes use snake_case: "operation", "signature", etc.
        var json = """
            {
                "operation": "DELETE",
                "signature": "public void Remove()",
                "parent_signature": "",
                "new_content": ""
            }
            """;

        var deserialized = JsonConvert.DeserializeObject<PatchOperation>(json);

        deserialized.Should().NotBeNull();
        deserialized!.Operation.Should().Be("DELETE");
        deserialized.Signature.Should().Be("public void Remove()");
    }

    // ── Operation values (known set) ────────────────────────────────

    [Theory]
    [InlineData("REPLACE")]
    [InlineData("ADD")]
    [InlineData("DELETE")]
    public void Operation_AcceptsKnownValues(string operation)
    {
        var op = new PatchOperation { Operation = operation };

        op.Operation.Should().Be(operation);
    }
}
