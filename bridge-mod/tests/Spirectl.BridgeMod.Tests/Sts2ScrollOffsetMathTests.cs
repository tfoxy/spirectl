using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// The Godot-free half of `set-scroll-offset`: the offset parse and the order-agnostic window clamp. The live
// resolve (which surface owns a container node, and writing its scroll target) needs a running game and is
// exercised by the couch-coop live leg, not here.
public sealed class Sts2ScrollOffsetMathTests
{
    [Theory]
    [InlineData("0", 0d)]
    [InlineData("-600", -600d)]
    [InlineData("  1234.5  ", 1234.5d)]
    [InlineData("-1398.75", -1398.75d)]
    [InlineData("1e2", 100d)]
    public void ParsesAPlainInvariantNumber(string raw, double expected)
    {
        Assert.True(Sts2ScrollOffsetMath.TryParseOffset(raw, out var value));
        Assert.Equal(expected, value, 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("top")]
    [InlineData("1,5")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void RefusesAnythingThatIsNotAFinitePosition(string? raw)
    {
        // A scroll target is a POSITION: coercing a bad one would park the surface somewhere nobody asked for,
        // and the caller can only tell the difference if the parse refuses.
        Assert.False(Sts2ScrollOffsetMath.TryParseOffset(raw, out var value));
        Assert.Equal(0d, value);
    }

    [Fact]
    public void ClampsIntoTheMapWindowSpelledLowToHigh()
    {
        Assert.Equal(-600d, Sts2ScrollOffsetMath.Clamp(-4000d, -600d, 1800d));
        Assert.Equal(1800d, Sts2ScrollOffsetMath.Clamp(9999d, -600d, 1800d));
        Assert.Equal(240d, Sts2ScrollOffsetMath.Clamp(240d, -600d, 1800d));
    }

    [Fact]
    public void ClampsIntoAGridWindowSpelledHighToLow()
    {
        // A card grid reports its window the other way round (its "bottom" is the most NEGATIVE offset), and the
        // caller must not have to know which surface spells it which way.
        Assert.Equal(-1398d, Sts2ScrollOffsetMath.Clamp(-5000d, -1398d, 0d));
        Assert.Equal(-1398d, Sts2ScrollOffsetMath.Clamp(-5000d, 0d, -1398d));
        Assert.Equal(0d, Sts2ScrollOffsetMath.Clamp(120d, 0d, -1398d));
        Assert.Equal(-700d, Sts2ScrollOffsetMath.Clamp(-700d, 0d, -1398d));
    }

    [Fact]
    public void AnUnknownWindowRestrictsNothing()
    {
        // ScrollLimitsFor answers ±infinity when the live surface would not report its window — the surface's own
        // pull-back still applies, and inventing a window would be worse than deferring to it.
        Assert.Equal(12345d, Sts2ScrollOffsetMath.Clamp(12345d, double.NegativeInfinity, double.PositiveInfinity));
    }

    [Fact]
    public void TheKillSwitchDefaultsOn()
    {
        // Default ON, in the SPIRECTL_* style; the env read is a one-time static, so this asserts the shipped
        // default rather than round-tripping the variable.
        Assert.True(Sts2ScrollOffsetMath.Enabled);
    }

    [Fact]
    public void TheActionKindKeepsItsWireCode()
    {
        // The wire `kind` code IS the enum ordinal, so a kind inserted anywhere but the end renumbers every later
        // action. This is the guard the enum's own comments ask for. It pins THIS kind's ordinal rather than
        // asserting it is still last: "last" moves to each newly appended kind (and is asserted there), while the
        // ordinal is the thing a client has already encoded and that must never move again.
        Assert.Equal(73, (int)SemanticActionKind.SetScrollOffset);
    }

    [Fact]
    public void ASuccessfulResultCanCarryTheClampedValueBack()
    {
        // The result's Values dict is how a client that led the scroll locally learns the game refused the last
        // stretch of travel. It is trailing + defaulted, so a result built without it is unchanged.
        var withValues = ActionExecutionResult.Success(
            actionInstanceId: "action:set-scroll-offset:1",
            kind: SemanticActionKind.SetScrollOffset,
            message: "ok",
            values: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [Sts2ScrollOffsetMath.ResultOffsetKey] = "-1398",
                [Sts2ScrollOffsetMath.ResultRequestedKey] = "-2000",
                [Sts2ScrollOffsetMath.ResultSurfaceKey] = "grid",
            });

        Assert.True(withValues.Accepted);
        Assert.NotNull(withValues.Values);
        Assert.Equal("-1398", withValues.Values![Sts2ScrollOffsetMath.ResultOffsetKey]);
        Assert.Equal("grid", withValues.Values[Sts2ScrollOffsetMath.ResultSurfaceKey]);

        var plain = ActionExecutionResult.Success("action:end-turn:1", SemanticActionKind.EndTurn, "ok");
        Assert.Null(plain.Values);
    }
}
