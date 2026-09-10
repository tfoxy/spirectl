using System.Collections.Generic;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// Q3 (P2) — the pure tween-hint emitted-parent selection order (Sts2TweenParentResolver.Resolve). Locks the
// precedence the watcher's EmittedParentGlobalTuple relies on: the nearest LIVE CanvasItem ancestor wins (with its
// own registry prefix when it is tracked, else the tracked node's), then the registry ParentId fallback, then
// identity. This is what stops a reparent+tween same frame from localizing the endpoint against a stale/wrong
// registry parent (the lower-right double-apply).
public sealed class Sts2TweenParentResolverTests
{
    private static Sts2TweenParentResolver.Candidate C(bool isCanvasItem, bool registered)
        => new(isCanvasItem, registered);

    [Fact]
    public void NearestLiveCanvasItemAncestorWins_OverRegistryFallback()
    {
        // The reparent case: the live tree exposes the true current parent (a tracked CanvasItem at index 0). Even
        // though the registry fallback would also resolve (a stale parent), the live ancestor is chosen.
        var pick = Sts2TweenParentResolver.Resolve(
            new List<Sts2TweenParentResolver.Candidate> { C(isCanvasItem: true, registered: true) },
            registryFallbackResolves: true);
        Assert.Equal(Sts2TweenParentResolver.Source.LiveAncestor, pick.Source);
        Assert.Equal(0, pick.AncestorIndex);
        Assert.True(pick.UseAncestorPrefix); // registered → use the ancestor's own viewport prefix
    }

    [Fact]
    public void SkipsNonCanvasItemAncestors_ToTheFirstCanvasItem()
    {
        // ReconcileNode reparents through skipped non-CanvasItem nodes; the resolver mirrors that by walking past a
        // non-CanvasItem parent (index 0) to the first CanvasItem ancestor (index 1).
        var pick = Sts2TweenParentResolver.Resolve(
            new List<Sts2TweenParentResolver.Candidate>
            {
                C(isCanvasItem: false, registered: false),
                C(isCanvasItem: true, registered: true),
            },
            registryFallbackResolves: true);
        Assert.Equal(Sts2TweenParentResolver.Source.LiveAncestor, pick.Source);
        Assert.Equal(1, pick.AncestorIndex);
    }

    [Fact]
    public void UntrackedLiveCanvasItemAncestor_UsesTrackedNodePrefix()
    {
        // A freshly-created parent that the registry hasn't tracked yet is still the true parent (UseAncestorPrefix
        // false → the watcher uses the tracked node's own viewport prefix, same SubViewport).
        var pick = Sts2TweenParentResolver.Resolve(
            new List<Sts2TweenParentResolver.Candidate> { C(isCanvasItem: true, registered: false) },
            registryFallbackResolves: true);
        Assert.Equal(Sts2TweenParentResolver.Source.LiveAncestor, pick.Source);
        Assert.False(pick.UseAncestorPrefix);
    }

    [Fact]
    public void NoLiveCanvasItemAncestor_FallsBackToRegistry()
    {
        // Live walk disabled (empty chain) or no CanvasItem ancestor present → the registry ParentId fallback (the
        // round-3 behaviour) when it resolves a valid CanvasItem.
        var pickEmpty = Sts2TweenParentResolver.Resolve(
            new List<Sts2TweenParentResolver.Candidate>(), registryFallbackResolves: true);
        Assert.Equal(Sts2TweenParentResolver.Source.RegistryFallback, pickEmpty.Source);

        var pickNoCanvas = Sts2TweenParentResolver.Resolve(
            new List<Sts2TweenParentResolver.Candidate> { C(isCanvasItem: false, registered: true) },
            registryFallbackResolves: true);
        Assert.Equal(Sts2TweenParentResolver.Source.RegistryFallback, pickNoCanvas.Source);
    }

    [Fact]
    public void NothingResolves_ReturnsIdentity()
    {
        // A root node (no CanvasItem ancestor, no registry parent) → identity tuple, endpoint left unchanged.
        var pick = Sts2TweenParentResolver.Resolve(
            new List<Sts2TweenParentResolver.Candidate> { C(isCanvasItem: false, registered: false) },
            registryFallbackResolves: false);
        Assert.Equal(Sts2TweenParentResolver.Source.Identity, pick.Source);
        Assert.Equal(-1, pick.AncestorIndex);
    }
}
