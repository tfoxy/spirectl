using MegaCrit.Sts2.addons.mega_text;

using Spirectl.Sts2.Common;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

/// <summary>
/// THE RANGE READER, pinned against the shape the engine actually returns.
/// </summary>
/// <remarks>
/// <para>
/// Written because the channel shipped to a live game and produced ZERO ranges across 201 text nodes, while the
/// sibling metrics read in the SAME probe loop — <c>GetLineSize</c>, <c>GetLineWidth</c> — came back fine. That
/// pattern held on both call sites at once (a reconstructed <c>TextParagraph</c> and a <c>RichTextLabel</c>'s own
/// query), which points past either call site at the one thing they share: the reader.
/// </para>
/// <para>
/// And the one way `GetLineRange` differs from its working siblings is its RETURN TYPE. Godot answers it with a
/// <c>Vector2I</c> — a struct with <c>int</c> components — where <c>GetLineSize</c> answers with a float
/// <c>Vector2</c>. Hence the double below, and hence a test that would have caught this before it ever reached a
/// live game.
/// </para>
/// </remarks>
public sealed class Sts2TextLineRangeTests
{
    private static (int Start, int End)[] RangesOf(params (int, int)[] ranges)
    {
        var node = new MegaLabel(text: "Damage ALL other enemies equal to the damage dealt.", themeFontSize: 24)
        {
            LineRanges = ranges
        };
        var described = Sts2RuntimeSceneTextDiagnostics.Describe(node, lean: false);
        var lines = described?.RenderedMetrics?.Lines ?? [];
        return [.. lines
            .Where(line => line.RangeStart.HasValue && line.RangeEnd.HasValue)
            .Select(line => (line.RangeStart!.Value, line.RangeEnd!.Value))];
    }

    [Fact]
    public void ReadsIntComponentRangesFromTheNodesOwnLineQuery()
        => Assert.Equal([(0, 24), (25, 51)], RangesOf((0, 24), (25, 51)));

    [Fact]
    public void ReadsAZeroStartRatherThanTreatingItAsAbsent()
        => Assert.Equal([(0, 6)], RangesOf((0, 6)));

    [Fact]
    public void NamesTheStringTheOffsetsAddressAndCarriesItsStalenessPair()
    {
        var node = new MegaLabel(text: "Strike", themeFontSize: 24) { LineRanges = [(0, 6)] };
        var metrics = Sts2RuntimeSceneTextDiagnostics.Describe(node, lean: false)?.RenderedMetrics;
        Assert.Equal("text", metrics?.RangeBasis);
        Assert.Equal(6, metrics?.RangeSourceLength);
        Assert.Equal(Sts2RuntimeSceneTextDiagnostics.Fnv1a32("Strike"), metrics?.RangeSourceHash);
    }
}
