using FluentAssertions;
using Neo.App;
using Xunit;

namespace Neo.App.Core.Tests;

public class LoopUnrollerTests
{
    // ── foreach with inline implicit array → unrolled ───────────────

    [Fact]
    public void UnrollLoops_ForeachWithImplicitArray_UnrollsIntoBlocks()
    {
        var code = """
            class C {
                void M() {
                    foreach (var item in new[] { "A", "B", "C" })
                    {
                        Console.WriteLine(item);
                    }
                }
            }
            """;

        var result = LoopUnroller.UnrollLoops(code);

        // Should NOT contain the foreach anymore.
        result.Should().NotContain("foreach");
        // Should contain the unrolled values.
        result.Should().Contain("\"A\"");
        result.Should().Contain("\"B\"");
        result.Should().Contain("\"C\"");
    }

    // ── foreach with explicit typed array → unrolled ────────────────

    [Fact]
    public void UnrollLoops_ForeachWithExplicitArray_Unrolls()
    {
        var code = """
            class C {
                void M() {
                    foreach (string s in new string[] { "X", "Y" })
                    {
                        Console.WriteLine(s);
                    }
                }
            }
            """;

        var result = LoopUnroller.UnrollLoops(code);

        result.Should().NotContain("foreach");
        result.Should().Contain("\"X\"");
        result.Should().Contain("\"Y\"");
    }

    // ── foreach with variable reference → finds initializer ─────────

    [Fact]
    public void UnrollLoops_ForeachWithVariable_FindsInitializerAndUnrolls()
    {
        var code = """
            class C {
                void M() {
                    var items = new[] { "P", "Q" };
                    foreach (var item in items)
                    {
                        Console.WriteLine(item);
                    }
                }
            }
            """;

        var result = LoopUnroller.UnrollLoops(code);

        result.Should().NotContain("foreach");
        result.Should().Contain("\"P\"");
        result.Should().Contain("\"Q\"");
    }

    // ── Nested foreach → outer level unrolled (inner may remain as syntax rewriter is single-pass) ──

    [Fact]
    public void UnrollLoops_NestedForeach_OuterLevelUnrolled()
    {
        var code = """
            class C {
                void M() {
                    foreach (var outer in new[] { "A", "B" })
                    {
                        foreach (var inner in new[] { "1", "2" })
                        {
                            Console.WriteLine(outer + inner);
                        }
                    }
                }
            }
            """;

        var result = LoopUnroller.UnrollLoops(code);

        // The outer foreach with literal array is unrolled, producing blocks
        // containing var outer = "A"; ... and var outer = "B"; ...
        result.Should().Contain("\"A\"");
        result.Should().Contain("\"B\"");
        // Inner foreach elements should still be present in the code.
        result.Should().Contain("\"1\"");
        result.Should().Contain("\"2\"");
    }

    // ── foreach over non-literal collection → left unchanged ────────

    [Fact]
    public void UnrollLoops_ForeachOverMethodCall_LeftUnchanged()
    {
        var code = """
            class C {
                void M() {
                    foreach (var item in GetItems())
                    {
                        Console.WriteLine(item);
                    }
                }
                IEnumerable<string> GetItems() => new[] { "X" };
            }
            """;

        var result = LoopUnroller.UnrollLoops(code);

        // Should still contain the foreach since GetItems() cannot be resolved at syntax level.
        result.Should().Contain("foreach");
    }

    // ── Empty/null input → returns as-is ────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnrollLoops_EmptyOrNullInput_ReturnsAsIs(string? input)
    {
        var result = LoopUnroller.UnrollLoops(input!);

        result.Should().Be(input);
    }

    // ── Code without foreach → unchanged ────────────────────────────

    [Fact]
    public void UnrollLoops_NoForeach_ReturnsUnchanged()
    {
        var code = """
            class C {
                void M() {
                    var x = 42;
                }
            }
            """;

        var result = LoopUnroller.UnrollLoops(code);

        result.Should().Contain("var x = 42");
    }
}
