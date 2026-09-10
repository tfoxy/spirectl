using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// R15 producer CREATURE SPINE-ANCHOR FOLD (Sts2SpineAnchorFold) — a pure, Godot-free exercise of the two arms of
// the rule, the scene table behind arm B, the inert-class allowlist and the burst-tail policy.
//
// The properties asserted here are the ones a regression would break INVISIBLY on the wire (a fold that stops
// folding just costs bandwidth; a fold that folds too much strands a creature's VFX at a stale mouth):
//   * arm A (childless anchor) needs no table and no gate, and is the ONLY arm that fires without one;
//   * arm B never fires without BOTH the scene table and the live "every emitter is quiet" gate;
//   * a node that paints anything of its own is never folded, whichever arm would otherwise match;
//   * the table names exactly the anchors whose authored subtree is emitters — the anchors that also carry a
//     Line2D / Sprite2D / MeshInstance2D / nested SpineSprite are absent by construction;
//   * the burst tail is Godot's own one-shot cycle length, clamped.
public sealed class Sts2SpineAnchorFoldTests
{
    private const string Gardener = "res://scenes/creature_visuals/phantasmal_gardener.tscn";
    private const string SludgeSpinner = "res://scenes/creature_visuals/sludge_spinner.tscn";
    private const string Architect = "res://scenes/creature_visuals/architect.tscn";
    private const string SoulNexus = "res://scenes/creature_visuals/soul_nexus.tscn";
    private const string Necrobinder = "res://scenes/creature_visuals/necrobinder.tscn";
    private const string Regent = "res://scenes/creature_visuals/regent.tscn";

    // ================= the decision table ====================================================================

    [Fact]
    public void ChildlessAnchor_IsFoldedWithNoTableAndNoGate()
    {
        // Arm A. `sludge_spinner.tscn`'s `testBone` / `HoverHeightAdjust` / `TargetingDistanceBone` are pure
        // measurement probes the game reads in C#; 70 of the corpus's 236 anchors are like them. With no
        // descendants there is nothing whose composed global the transform could be a factor in, so the fold needs
        // neither a scene rule nor an emitter check — and MUST NOT, or it would only ever cover tabled scenes.
        Assert.True(Sts2SpineAnchorFold.ShouldSuppressTransform(
            skeletonLeaf: true, carriesOwnPaint: false, hasDescendants: false,
            tabledEmitterAnchor: false, subtreeQuiet: false));
    }

    [Fact]
    public void AnchorWithDescendants_NeedsBothTheTableAndTheLiveGate()
    {
        // Arm B. The table alone is not enough (the emitter may be mid-burst) and the gate alone is not enough (an
        // untabled subtree may hold something that paints).
        Assert.False(Sts2SpineAnchorFold.ShouldSuppressTransform(
            skeletonLeaf: true, carriesOwnPaint: false, hasDescendants: true,
            tabledEmitterAnchor: true, subtreeQuiet: false));
        Assert.False(Sts2SpineAnchorFold.ShouldSuppressTransform(
            skeletonLeaf: true, carriesOwnPaint: false, hasDescendants: true,
            tabledEmitterAnchor: false, subtreeQuiet: true));
        Assert.True(Sts2SpineAnchorFold.ShouldSuppressTransform(
            skeletonLeaf: true, carriesOwnPaint: false, hasDescendants: true,
            tabledEmitterAnchor: true, subtreeQuiet: true));
    }

    [Fact]
    public void NothingIsFoldedUnlessItIsAPaintlessSkeletonLeaf()
    {
        // A non-spine node is never an anchor however childless it is (this is what keeps the structural arm A from
        // becoming "fold anything with no children"), and a leaf that reports ANY browser-visible field of its own
        // is a node the client places — withholding its transform would strand it on screen.
        Assert.False(Sts2SpineAnchorFold.ShouldSuppressTransform(
            skeletonLeaf: false, carriesOwnPaint: false, hasDescendants: false,
            tabledEmitterAnchor: false, subtreeQuiet: true));
        Assert.False(Sts2SpineAnchorFold.ShouldSuppressTransform(
            skeletonLeaf: true, carriesOwnPaint: true, hasDescendants: false,
            tabledEmitterAnchor: false, subtreeQuiet: true));
        Assert.False(Sts2SpineAnchorFold.ShouldSuppressTransform(
            skeletonLeaf: true, carriesOwnPaint: true, hasDescendants: true,
            tabledEmitterAnchor: true, subtreeQuiet: true));
    }

    // ================= the scene table (arm B) ================================================================

    [Fact]
    public void TableNamesTheMeasuredAnchors()
    {
        // The four instances of this one anchor were 100% of the measured 6s idle wire (52.8 msg/s, 8 node ids).
        Assert.True(Sts2SpineAnchorFold.IsEmitterAnchor(Gardener, "Visuals/SpewSlotNode"));
        // The Aug-6 recording's two emitter-parent sludge-spinner anchors (868/868 messages each).
        Assert.True(Sts2SpineAnchorFold.IsEmitterAnchor(SludgeSpinner, "Visuals/MouthSpraySlot"));
        Assert.True(Sts2SpineAnchorFold.IsEmitterAnchor(SludgeSpinner, "Visuals/MouthDribbleBoneNode"));
        // `testBone` was also 868/868 — but it is CHILDLESS, so it is arm A's and must NOT be in the table (listing
        // it would imply the table is what keeps it safe).
        Assert.False(Sts2SpineAnchorFold.IsEmitterAnchor(SludgeSpinner, "Visuals/testBone"));
        Assert.False(Sts2SpineAnchorFold.IsEmitterAnchor(SludgeSpinner, "Visuals/HoverHeightAdjust"));
        Assert.False(Sts2SpineAnchorFold.IsEmitterAnchor(SludgeSpinner, "Visuals/TargetingDistanceBone"));
    }

    [Fact]
    public void TableExcludesAnchorsWhoseSubtreePaints()
    {
        // Every one of these positions something the browser DRAWS, so its motion is on screen and its transform
        // must keep streaming: a Line2D trail / path, a Sprite2D head, a nested SpineSprite weapon rig.
        Assert.False(Sts2SpineAnchorFold.IsEmitterAnchor(Architect, "Visuals/TrailSlot"));
        Assert.False(Sts2SpineAnchorFold.IsEmitterAnchor(SoulNexus, "Visuals/PathSlot1"));
        Assert.False(Sts2SpineAnchorFold.IsEmitterAnchor(Necrobinder, "Visuals/HeadBoneNode"));
        Assert.False(Sts2SpineAnchorFold.IsEmitterAnchor(Regent, "Visuals/Weapons"));
        // ...while the same creature's emitter-only anchors ARE tabled — the exclusion is per anchor, not per scene.
        Assert.True(Sts2SpineAnchorFold.IsEmitterAnchor(Architect, "Visuals/FireSlot"));
        Assert.True(Sts2SpineAnchorFold.IsEmitterAnchor(Necrobinder, "Visuals/ScytheVfxSlot1"));
    }

    [Fact]
    public void TableMissesUnknownScenesPathsAndNulls()
    {
        Assert.False(Sts2SpineAnchorFold.IsEmitterAnchor(Gardener, "Visuals"));
        Assert.False(Sts2SpineAnchorFold.IsEmitterAnchor(Gardener, "."));
        Assert.False(Sts2SpineAnchorFold.IsEmitterAnchor(Gardener, "Visuals/SpewSlotNode/SpewParticles"));
        Assert.False(Sts2SpineAnchorFold.IsEmitterAnchor("res://scenes/creature_visuals/osty.tscn", "Visuals/Flame"));
        Assert.False(Sts2SpineAnchorFold.IsEmitterAnchor(null, "Visuals/SpewSlotNode"));
        Assert.False(Sts2SpineAnchorFold.IsEmitterAnchor(Gardener, null));
    }

    [Fact]
    public void TableRoutesThroughTheSharedChannelTable()
    {
        // The watcher reaches this fold the same way it reaches every other one: scene identity resolved once, then
        // a channel flag. A tabled anchor must therefore be a WATCHED scene and carry exactly this channel.
        Assert.True(Sts2DecorEmitSuppress.IsWatchedScene(Gardener));
        Assert.Equal(
            Sts2DecorEmitSuppress.Channels.SpineAnchorTransform,
            Sts2DecorEmitSuppress.Lookup(Gardener, "Visuals/SpewSlotNode"));
        // Siblings inside the same watched scene keep streaming everything.
        Assert.Equal(Sts2DecorEmitSuppress.Channels.None, Sts2DecorEmitSuppress.Lookup(Gardener, "Bounds"));
        Assert.Equal(Sts2DecorEmitSuppress.Channels.None, Sts2DecorEmitSuppress.Lookup(Gardener, "Visuals"));
        // ...and no anchor rule leaked onto the R14 combat channels (or vice versa).
        Assert.Equal(
            Sts2DecorEmitSuppress.Channels.None,
            Sts2DecorEmitSuppress.Lookup(Sts2IntentBobFold.SceneFile, "Visuals/SpewSlotNode"));
    }

    [Fact]
    public void EveryTabledAnchorPathIsReachableAtTheSharedDepthCap()
    {
        // The watcher only builds scene-relative paths down to Sts2DecorEmitSuppress.MaxRelDepth; a table row
        // deeper than that would be dead code that silently never matches.
        foreach (var (scene, relPaths) in Sts2SpineAnchorFold.EmitterAnchorPathsByScene)
        {
            Assert.StartsWith("res://scenes/creature_visuals/", scene, System.StringComparison.Ordinal);
            foreach (var relPath in relPaths)
            {
                var segments = relPath.Split('/').Length;
                Assert.True(
                    segments <= Sts2DecorEmitSuppress.MaxRelDepth,
                    $"{scene} :: {relPath} is {segments} segments deep, past MaxRelDepth");
                Assert.Equal(
                    Sts2DecorEmitSuppress.Channels.SpineAnchorTransform,
                    Sts2DecorEmitSuppress.Lookup(scene, relPath));
            }
        }
    }

    // ================= the inert-descendant allowlist =========================================================

    [Theory]
    [InlineData("Node2D")]        // the `Fire_Particles` / `fxnode` grouping nodes
    [InlineData("Marker2D")]      // editor markers; draw nothing at runtime
    [InlineData("SpineBoneNode")] // a nested anchor (mecha_knight's EngineSlot/EngineBone)
    [InlineData("SpineSlotNode")]
    [InlineData("SpineMesh2D")]
    public void InertClasses_AreTheOnesThatPaintNothing(string nativeClass)
        => Assert.True(Sts2SpineAnchorFold.IsInertDescendantClass(nativeClass));

    [Theory]
    [InlineData("Sprite2D")]
    [InlineData("TextureRect")]
    [InlineData("Line2D")]
    [InlineData("MeshInstance2D")]
    [InlineData("ColorRect")]
    [InlineData("Label")]
    [InlineData("SpineSprite")]   // a nested clip root DOES render
    [InlineData("GPUParticles2D")] // recognised by its ParticleSpec + gated on Emitting, never by class
    [InlineData("CPUParticles2D")]
    [InlineData("")]
    [InlineData(null)]
    public void EverythingElse_DisqualifiesTheAnchor(string? nativeClass)
        => Assert.False(Sts2SpineAnchorFold.IsInertDescendantClass(nativeClass));

    // ================= the burst tail =========================================================================

    [Fact]
    public void BurstTail_IsGodotsOwnCycleLength()
    {
        // particles.cpp `active_time` = lifetime * (2 - explosiveness).
        Assert.Equal(1800, Sts2SpineAnchorFold.BurstTailMs(0.9, 0.0)); // the gardener's SpewParticles
        Assert.Equal(900, Sts2SpineAnchorFold.BurstTailMs(0.9, 1.0));
        Assert.Equal(1500, Sts2SpineAnchorFold.BurstTailMs(1.0, 0.5));
    }

    [Fact]
    public void BurstTail_IsClamped_SoADegenerateSpecCanNeitherSkipNorStall()
    {
        Assert.Equal(Sts2SpineAnchorFold.MinBurstTailMs, Sts2SpineAnchorFold.BurstTailMs(0.0, 0.0));
        Assert.Equal(Sts2SpineAnchorFold.MinBurstTailMs, Sts2SpineAnchorFold.BurstTailMs(-5.0, 0.0));
        Assert.Equal(Sts2SpineAnchorFold.MaxBurstTailMs, Sts2SpineAnchorFold.BurstTailMs(60.0, 0.0));
        Assert.Equal(Sts2SpineAnchorFold.DefaultBurstTailMs, Sts2SpineAnchorFold.BurstTailMs(double.NaN, 0.0));
        Assert.Equal(Sts2SpineAnchorFold.DefaultBurstTailMs, Sts2SpineAnchorFold.BurstTailMs(double.PositiveInfinity, 0.0));
    }

    [Fact]
    public void SubtreeScanCaps_AreBoundedButCoverEveryAuthoredSubtree()
    {
        // The authored emitter subtrees are 1-3 nodes, 1-2 deep; the caps only exist so a runtime reparent cannot
        // turn the per-tick scan into an unbounded walk. Past either cap the scan fails OPEN (keeps streaming).
        Assert.True(Sts2SpineAnchorFold.MaxSubtreeDepth >= 2);
        Assert.True(Sts2SpineAnchorFold.MaxSubtreeNodes >= 4);
    }
}
