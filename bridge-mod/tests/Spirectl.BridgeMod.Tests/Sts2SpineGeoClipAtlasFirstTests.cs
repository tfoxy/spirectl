using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// The ATLAS-FIRST SHADOW ARM's core: can the atlas alone do DISCOVERY — resolve every slot to a mesh — instead
// of standing behind the colour-flip probe as a tie-breaker and a fallback?
//
// Synthetic atlases, no Godot, no live host. Every case here is about the property that makes the shadow arm's
// numbers worth reading: it must resolve what is genuinely determined and REFUSE what is not, because a shadow
// arm that guessed would report agreement it had not earned, and the whole point is to decide whether the
// expensive probe can be narrowed.
public sealed class Sts2SpineGeoClipAtlasFirstTests
{
    private const int PageWidth = 1000;

    private const int PageHeight = 1000;

    private static readonly (int Width, int Height)[] OnePage = [(PageWidth, PageHeight)];

    // The libgdx/Spine text atlas the parser reads: a page line, its size, then `name` + `bounds: x, y, w, h`.
    private static SpineAtlasDocument Atlas(params (string Name, int X, int Y, int Width, int Height)[] regions)
    {
        var text = $"page.png\nsize: {PageWidth}, {PageHeight}\n";
        foreach (var (name, x, y, width, height) in regions)
        {
            text += $"{name}\nbounds: {x}, {y}, {width}, {height}\n";
        }

        return Sts2SpineAtlasText.Parse(text);
    }

    /// <summary>A candidate mesh whose uvs address the page pixels (x0,y0)-(x1,y1).</summary>
    private static Sts2SpineGeoClipAtlas.MeshUvBox Box(int order, double x0, double y0, double x1, double y1)
        => new(order, x0 / PageWidth, y0 / PageHeight, x1 / PageWidth, y1 / PageHeight);

    // (a) The easy case, and the one the whole arm is proposed on: distinct attachments over distinct regions.
    [Fact]
    public void UniqueRegionPerAttachmentResolvesEveryOrdinal()
    {
        var result = Sts2SpineGeoClipAtlas.AssociateAll(
            new Dictionary<int, string?> { [0] = "head", [1] = "torso", [2] = "arm" },
            [
                Box(0, 400, 0, 460, 80),     // arm
                Box(1, 0, 0, 100, 100),      // head
                Box(2, 200, 0, 320, 140),    // torso
            ],
            Atlas(("head", 0, 0, 100, 100), ("torso", 200, 0, 120, 140), ("arm", 400, 0, 60, 80)),
            OnePage);

        Assert.Empty(result.Refused);
        Assert.Equal(3, result.Resolved.Count);
        // Ordinal → the mesh's own Order handle, NOT its position in the candidate list.
        Assert.Equal(1, result.OrderByOrdinal[0]);
        Assert.Equal(2, result.OrderByOrdinal[1]);
        Assert.Equal(0, result.OrderByOrdinal[2]);
        Assert.All(result.Resolved, match => Assert.Equal("uv-region-exact", match.Method));
    }

    // (b) THE REFUSAL THAT MATTERS. Two slots drawing the same attachment over one identical uv box are not
    // distinguishable by the atlas — there is no evidence, only a list order. Both must come back unresolved.
    // Handing them out in list order would be a 50/50 guess reported as an answer, and the shadow arm exists to
    // measure how far the atlas gets, not to invent a number.
    [Fact]
    public void TwoSlotsSharingAnAttachmentOverOneBoxAreBothRefused()
    {
        var result = Sts2SpineGeoClipAtlas.AssociateAll(
            new Dictionary<int, string?> { [0] = "shard", [1] = "shard" },
            [Box(0, 0, 0, 100, 100), Box(1, 0, 0, 100, 100)],
            Atlas(("shard", 0, 0, 100, 100)),
            OnePage);

        Assert.Empty(result.Resolved);
        Assert.Empty(result.OrderByOrdinal);
        Assert.Equal([0, 1], result.Refused.Select(refusal => refusal.Ordinal));
        Assert.All(result.Refused, refusal => Assert.Equal("ambiguous", refusal.Reason));
    }

    // (c) Containment outranks score. A candidate that sits INSIDE the named region is the slot's mesh even
    // when a sloppier candidate scores better on corner/area agreement — the box that pokes outside the region
    // cannot be sampling it.
    [Fact]
    public void ContainmentOutranksAStrictlyBetterScore()
    {
        var result = Sts2SpineGeoClipAtlas.AssociateAll(
            new Dictionary<int, string?> { [0] = "head" },
            [
                // Score 6480 (corner 80 + area 6400) but strictly INSIDE the region.
                Box(0, 20, 20, 80, 80),
                // Score 8 (corner 8 + area 0) — a far better fit numerically — but it overhangs the region by
                // 2px on each side, well past the 1.5px containment tolerance, so it is not contained.
                Box(1, -2, -2, 98, 98),
            ],
            Atlas(("head", 0, 0, 100, 100)),
            OnePage);

        var match = Assert.Single(result.Resolved);
        Assert.Equal(0, match.Ordinal);
        Assert.Equal(0, match.Order);
    }

    // (d) THE FIXPOINT. Ordinal 0 is a genuine two-way tie on the first pass. Ordinal 1 then resolves uniquely
    // and CLAIMS one of the two tied meshes — which turns ordinal 0's tie into a single remaining candidate on
    // the next pass. A single-pass arm reports ordinal 0 unresolved and undercounts what the atlas can do.
    [Fact]
    public void ClaimingOneMeshTurnsAnEarlierSlotsTieIntoAUniqueResolution()
    {
        var result = Sts2SpineGeoClipAtlas.AssociateAll(
            new Dictionary<int, string?> { [0] = "wide", [1] = "right" },
            [
                // Both overhang "wide" by 10px on opposite sides: identical scores, neither contained.
                Box(0, 90, 100, 190, 200),
                Box(1, 110, 100, 210, 200),
                // …and mesh #1 is EXACTLY region "right", which nothing else fits.
            ],
            Atlas(("wide", 100, 100, 100, 100), ("right", 110, 100, 100, 100)),
            OnePage);

        Assert.Empty(result.Refused);
        Assert.Equal(2, result.Resolved.Count);
        Assert.Equal(1, result.OrderByOrdinal[1]);
        // The payoff: ordinal 0 was ambiguous until ordinal 1 took mesh #1 away from it.
        Assert.Equal(0, result.OrderByOrdinal[0]);
    }

    // (e) An attachment the atlas has never heard of is refused with a reason that says so — not scored against
    // whatever else is lying around. A slot renamed by an attachment `path` presents exactly this way, and the
    // colour probe is still free to resolve it.
    [Fact]
    public void AnAttachmentWithNoRegionIsRefusedAsNoSuchRegion()
    {
        var result = Sts2SpineGeoClipAtlas.AssociateAll(
            new Dictionary<int, string?> { [0] = "head", [1] = "renamed-by-path", [2] = null },
            [Box(0, 0, 0, 100, 100)],
            Atlas(("head", 0, 0, 100, 100)),
            OnePage);

        Assert.Equal([0], result.Resolved.Select(match => match.Ordinal));
        Assert.Equal(
            [(1, "no-such-region"), (2, "no-such-region")],
            result.Refused.Select(refusal => (refusal.Ordinal, refusal.Reason)));
    }

    // ── THE RESIDUE PARTITION, which is what the ARMED arm acts on ───────────────────────────────────

    // Armed, `Resolved` is CLAIMED before a probe frame is awaited and `Refused` is the residue that still costs
    // the colour probe. So the two lists have to partition the visible ordinals exactly: an ordinal in neither is
    // a slot silently dropped from the bake, and an ordinal in both is a slot claimed AND re-probed, which would
    // let the probe pair it a second time with a different mesh.
    [Fact]
    public void ResolvedAndRefusedPartitionEveryVisibleOrdinalWithNoOverlap()
    {
        var attachments = new Dictionary<int, string?>
        {
            [0] = "head",       // unique region: resolved
            [1] = "shard",      // …two slots over one identical box:
            [2] = "shard",      // …both refused
            [3] = "no-region",  // named by nothing in the atlas: refused
        };

        var result = Sts2SpineGeoClipAtlas.AssociateAll(
            attachments,
            [Box(0, 0, 0, 100, 100), Box(1, 300, 0, 400, 100), Box(2, 300, 0, 400, 100)],
            Atlas(("head", 0, 0, 100, 100), ("shard", 300, 0, 100, 100)),
            OnePage);

        var resolvedOrdinals = result.Resolved.Select(match => match.Ordinal).ToArray();
        var refusedOrdinals = result.Refused.Select(refusal => refusal.Ordinal).ToArray();

        Assert.Equal([0], resolvedOrdinals);
        Assert.Equal([1, 2, 3], refusedOrdinals);
        Assert.Empty(resolvedOrdinals.Intersect(refusedOrdinals));
        Assert.Equal(attachments.Keys.Order(), resolvedOrdinals.Concat(refusedOrdinals).Order());

        // A claim is a claim: the armed caller maps `Order` straight onto a swept mesh, so `OrderByOrdinal` must
        // agree with `Resolved` and never hand the same mesh to two ordinals.
        Assert.Equal(0, result.OrderByOrdinal[0]);
        Assert.Equal(result.Resolved.Count, result.OrderByOrdinal.Count);
        Assert.Equal(
            result.OrderByOrdinal.Values.Count(),
            result.OrderByOrdinal.Values.Distinct().Count());
    }

    // The tie the armed arm must NOT claim. Both meshes are legitimate candidates for both slots, so claiming
    // either one is a coin flip that would bake another slot's art onto this slot for the whole clip — and,
    // worse, would remove the right mesh from the residue's candidate pool before the probe ever ran. Two slots
    // in, two slots out, still on the probe's plate.
    [Fact]
    public void ATieIsLeftToTheProbeRatherThanClaimedInListOrder()
    {
        var result = Sts2SpineGeoClipAtlas.AssociateAll(
            new Dictionary<int, string?> { [4] = "shard", [5] = "shard" },
            [Box(0, 0, 0, 100, 100), Box(1, 0, 0, 100, 100)],
            Atlas(("shard", 0, 0, 100, 100)),
            OnePage);

        Assert.Empty(result.Resolved);
        Assert.Empty(result.OrderByOrdinal);
        Assert.Equal([4, 5], result.Refused.Select(refusal => refusal.Ordinal));
    }

    // A refusal has to survive the fixpoint's own re-evaluation: the loop overwrites an ordinal's verdict every
    // pass, and only the LAST one describes the fixpoint. This is the shape that would silently duplicate or
    // lose refusals if they were appended instead.
    [Fact]
    public void EachRefusedOrdinalIsReportedExactlyOnceAfterSeveralPasses()
    {
        var result = Sts2SpineGeoClipAtlas.AssociateAll(
            new Dictionary<int, string?> { [0] = "ghost", [1] = "wide", [2] = "right" },
            [Box(0, 90, 100, 190, 200), Box(1, 110, 100, 210, 200)],
            Atlas(("wide", 100, 100, 100, 100), ("right", 110, 100, 100, 100)),
            OnePage);

        // Ordinals 1 and 2 resolve across two passes; ordinal 0 names nothing and stays refused, once.
        Assert.Equal(2, result.Resolved.Count);
        var refusal = Assert.Single(result.Refused);
        Assert.Equal(0, refusal.Ordinal);
        Assert.Equal("no-such-region", refusal.Reason);
    }
}
