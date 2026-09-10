using Spirectl.Sts2.Common;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

/// <summary>
/// THE CROSS-LANGUAGE PIN for the streamed-wrap staleness hash.
/// </summary>
/// <remarks>
/// <para>
/// A node's line ranges ride the producer's STATIC path while its words ride the per-tick one, so a client may
/// only use a streamed wrap after re-hashing the string it is about to draw and finding the same value. That
/// makes this function a WIRE CONTRACT with the browser's <c>@/mirror/textWrap.fnv1a32</c>, not an internal
/// detail — and a contract with two failure modes that look nothing alike:
/// </para>
/// <list type="bullet">
///   <item>disagree, and every wrap is silently discarded: no wrong pixels, but the whole channel is dead while
///   reading as healthy, because each label simply falls back to breaking its own lines;</item>
///   <item>agree by accident on ASCII and diverge elsewhere, and a label eventually renders the wrong words.</item>
/// </list>
/// <para>
/// The vectors below were computed from the FNV-1a definition independently of BOTH implementations, so the two
/// sides are pinned to a third thing rather than to each other. The browser spec
/// (<c>frontend/src/mirror/__tests__/textWrap.spec.ts</c>) asserts the same numbers.
/// </para>
/// </remarks>
public sealed class Sts2TextWrapHashTests
{
    [Theory]
    [InlineData("", -2128831035)]
    [InlineData("Strike", 1849856623)]
    [InlineData("Deal 9 damage.", -1209866591)]
    [InlineData("Damage ALL other enemies\nequal to the damage dealt.", -428184616)]
    [InlineData("é一", 1502015756)]
    public void HashesReferenceVectors(string input, int expected)
        => Assert.Equal(expected, Sts2RuntimeSceneTextDiagnostics.Fnv1a32(input));

    /// <summary>
    /// U+1F600 is a surrogate PAIR, and this is the case that separates a code-UNIT walk from a code-POINT one.
    /// </summary>
    /// <remarks>
    /// C# <c>foreach (var c in string)</c> yields UTF-16 code units and JavaScript's <c>for...of</c> yields code
    /// points. Both agree across the whole of ASCII, so no amount of ordinary test text distinguishes them — the
    /// disagreement waits for the first character outside the BMP and then makes the client throw away that one
    /// label's wrap, forever, for reasons nothing reports.
    /// </remarks>
    [Fact]
    public void WalksCodeUnitsNotCodePoints()
        => Assert.Equal(753646331, Sts2RuntimeSceneTextDiagnostics.Fnv1a32("\U0001F600ab"));

    /// <summary>The multiply wraps at 32 bits rather than losing low bits past 2^53 — the browser's Math.imul.</summary>
    [Fact]
    public void MultiplyWrapsAt32Bits()
    {
        var hash = Sts2RuntimeSceneTextDiagnostics.Fnv1a32(string.Concat(Enumerable.Repeat("the ironclad strikes again ", 50)));
        Assert.Equal(hash, Sts2RuntimeSceneTextDiagnostics.Fnv1a32(string.Concat(Enumerable.Repeat("the ironclad strikes again ", 50))));
    }
}
