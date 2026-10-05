using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// R15 producer CREATURE SPINE-ANCHOR FOLD (Sts2SpineAnchorFold) — a pure, Godot-free exercise of the three arms of
// the rule, the scene table behind arm B, the inert-class allowlist, the burst-tail policy, and arm C's
// hidden-descendant scan and flip flush.
//
// The properties asserted here are the ones a regression would break INVISIBLY on the wire (a fold that stops
// folding just costs bandwidth; a fold that folds too much strands a creature's VFX at a stale mouth):
//   * arm A (childless anchor) needs no table and no gate, and is the ONLY arm that fires without one;
//   * arm B never fires without BOTH the scene table and the live "every emitter is quiet" gate;
//   * a node that paints anything of its own is never folded, whichever arm would otherwise match;
//   * the table names exactly the anchors whose authored subtree is emitters — the anchors that also carry a
//     Line2D / Sprite2D / MeshInstance2D / nested SpineSprite are absent by construction;
//   * the burst tail is Godot's own one-shot cycle length, clamped;
//   * arm C never fires with its switch off, and on the tick a hidden descendant turns visible the anchor's live
//     transform ships in the SAME delta, ahead of the descendant (never one capture late).
public sealed class Sts2SpineAnchorFoldTests
{
    private const string Gardener = "res://scenes/creature_visuals/phantasmal_gardener.tscn";
    private const string SludgeSpinner = "res://scenes/creature_visuals/sludge_spinner.tscn";
    private const string Architect = "res://scenes/creature_visuals/architect.tscn";
    private const string SoulNexus = "res://scenes/creature_visuals/soul_nexus.tscn";
    private const string Necrobinder = "res://scenes/creature_visuals/necrobinder.tscn";
    private const string Regent = "res://scenes/creature_visuals/regent.tscn";

    // The arm A/B rows predate arm C; they read as "does the fold withhold this anchor?".
    private static bool Folds(
        bool skeletonLeaf,
        bool carriesOwnPaint,
        bool hasDescendants,
        bool tabledEmitterAnchor,
        bool subtreeQuiet,
        bool hiddenArmEnabled = false,
        bool descendantsHiddenOrInert = false)
        => Sts2SpineAnchorFold.DecideArm(
            skeletonLeaf, carriesOwnPaint, hasDescendants, tabledEmitterAnchor, subtreeQuiet,
            hiddenArmEnabled, descendantsHiddenOrInert) != Sts2SpineAnchorFold.Arm.None;

    // ================= the decision table ====================================================================

    [Fact]
    public void ChildlessAnchor_IsFoldedWithNoTableAndNoGate()
    {
        // Arm A. `sludge_spinner.tscn`'s `testBone` / `HoverHeightAdjust` / `TargetingDistanceBone` are pure
        // measurement probes the game reads in C#; 70 of the corpus's 236 anchors are like them. With no
        // descendants there is nothing whose composed global the transform could be a factor in, so the fold needs
        // neither a scene rule nor an emitter check — and MUST NOT, or it would only ever cover tabled scenes.
        Assert.True(Folds(
            skeletonLeaf: true, carriesOwnPaint: false, hasDescendants: false,
            tabledEmitterAnchor: false, subtreeQuiet: false));
    }

    [Fact]
    public void AnchorWithDescendants_NeedsBothTheTableAndTheLiveGate()
    {
        // Arm B. The table alone is not enough (the emitter may be mid-burst) and the gate alone is not enough (an
        // untabled subtree may hold something that paints).
        Assert.False(Folds(
            skeletonLeaf: true, carriesOwnPaint: false, hasDescendants: true,
            tabledEmitterAnchor: true, subtreeQuiet: false));
        Assert.False(Folds(
            skeletonLeaf: true, carriesOwnPaint: false, hasDescendants: true,
            tabledEmitterAnchor: false, subtreeQuiet: true));
        Assert.True(Folds(
            skeletonLeaf: true, carriesOwnPaint: false, hasDescendants: true,
            tabledEmitterAnchor: true, subtreeQuiet: true));
    }

    [Fact]
    public void NothingIsFoldedUnlessItIsAPaintlessSkeletonLeaf()
    {
        // A non-spine node is never an anchor however childless it is (this is what keeps the structural arm A from
        // becoming "fold anything with no children"), and a leaf that reports ANY browser-visible field of its own
        // is a node the client places — withholding its transform would strand it on screen.
        Assert.False(Folds(
            skeletonLeaf: false, carriesOwnPaint: false, hasDescendants: false,
            tabledEmitterAnchor: false, subtreeQuiet: true));
        Assert.False(Folds(
            skeletonLeaf: true, carriesOwnPaint: true, hasDescendants: false,
            tabledEmitterAnchor: false, subtreeQuiet: true));
        Assert.False(Folds(
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

    // ================= arm C: the decision table ==============================================================

    [Fact]
    public void HiddenDescendantArm_FoldsAnUntabledAnchorWhoseSubtreeIsHiddenOrInert()
    {
        // The measured case: the Ironclad's `Visuals/EyeSlot` is in no table and its only child `EyeFire` is a
        // hidden TextureRect. Arms A and B both refuse it; arm C takes it.
        Assert.False(Folds(
            skeletonLeaf: true, carriesOwnPaint: false, hasDescendants: true,
            tabledEmitterAnchor: false, subtreeQuiet: false));
        Assert.True(Folds(
            skeletonLeaf: true, carriesOwnPaint: false, hasDescendants: true,
            tabledEmitterAnchor: false, subtreeQuiet: false,
            hiddenArmEnabled: true, descendantsHiddenOrInert: true));
        Assert.Equal(
            Sts2SpineAnchorFold.Arm.HiddenDescendants,
            Sts2SpineAnchorFold.DecideArm(true, false, true, false, false, true, true));
    }

    [Fact]
    public void HiddenDescendantArm_KillSwitchOff_RestoresArmsAAndBExactly()
    {
        // SPIRECTL_SPINE_ANCHOR_HIDDEN_FOLD=0: the fact no longer matters, and arms A and B are untouched.
        Assert.Equal(
            Sts2SpineAnchorFold.Arm.None,
            Sts2SpineAnchorFold.DecideArm(true, false, true, false, false, hiddenArmEnabled: false, descendantsHiddenOrInert: true));
        Assert.Equal(
            Sts2SpineAnchorFold.Arm.Childless,
            Sts2SpineAnchorFold.DecideArm(true, false, false, false, false, hiddenArmEnabled: false, descendantsHiddenOrInert: false));
        Assert.Equal(
            Sts2SpineAnchorFold.Arm.TabledEmitters,
            Sts2SpineAnchorFold.DecideArm(true, false, true, true, true, hiddenArmEnabled: false, descendantsHiddenOrInert: false));
    }

    [Fact]
    public void HiddenDescendantArm_NeedsTheFact_AndNeverOverridesThePaintAndLeafGates()
    {
        Assert.Equal(
            Sts2SpineAnchorFold.Arm.None,
            Sts2SpineAnchorFold.DecideArm(true, false, true, false, false, true, descendantsHiddenOrInert: false));
        Assert.Equal(
            Sts2SpineAnchorFold.Arm.None,
            Sts2SpineAnchorFold.DecideArm(true, carriesOwnPaint: true, true, false, false, true, true));
        Assert.Equal(
            Sts2SpineAnchorFold.Arm.None,
            Sts2SpineAnchorFold.DecideArm(skeletonLeaf: false, false, true, false, false, true, true));
    }

    [Fact]
    public void ArmsAAndB_KeepTheirNames_SoOnlyArmCIsEverFlushed()
    {
        // Arms A and B decide on facts read live at the anchor, so they need no flush; the watcher records a flush
        // region only for HiddenDescendants. A tabled anchor whose emitters are quiet stays arm B even when arm C's
        // fact also holds.
        Assert.Equal(
            Sts2SpineAnchorFold.Arm.Childless,
            Sts2SpineAnchorFold.DecideArm(true, false, false, false, false, true, true));
        Assert.Equal(
            Sts2SpineAnchorFold.Arm.TabledEmitters,
            Sts2SpineAnchorFold.DecideArm(true, false, true, true, true, true, true));
        Assert.Equal(
            Sts2SpineAnchorFold.Arm.HiddenDescendants,
            Sts2SpineAnchorFold.DecideArm(true, false, true, true, subtreeQuiet: false, true, true));
    }

    // ================= arm C: the hidden-descendant scan ======================================================

    private static readonly Sts2SpineAnchorFold.DescendantKind Inert = Sts2SpineAnchorFold.DescendantKind.Inert;
    private static readonly Sts2SpineAnchorFold.DescendantKind Hidden = Sts2SpineAnchorFold.DescendantKind.Hidden;
    private static readonly Sts2SpineAnchorFold.DescendantKind Paints = Sts2SpineAnchorFold.DescendantKind.Paints;
    private static readonly Sts2SpineAnchorFold.DescendantKind Unknown = Sts2SpineAnchorFold.DescendantKind.Unknown;

    // A pre-order slice starting AT the anchor; records which entries the scan asked to classify.
    private readonly struct ArrayView((int Depth, Sts2SpineAnchorFold.DescendantKind Kind)[] nodes, List<int> asked)
        : Sts2SpineAnchorFold.ISubtreeView
    {
        public int Count => nodes.Length;

        public int DepthAt(int index) => nodes[index].Depth;

        public Sts2SpineAnchorFold.DescendantKind KindAt(int index)
        {
            asked.Add(index);
            return nodes[index].Kind;
        }
    }

    private static bool Scan(params (int Depth, Sts2SpineAnchorFold.DescendantKind Kind)[] nodes)
        => Scan(out _, nodes);

    private static bool Scan(out List<int> asked, params (int Depth, Sts2SpineAnchorFold.DescendantKind Kind)[] nodes)
    {
        asked = [];
        var view = new ArrayView(nodes, asked);
        return Sts2SpineAnchorFold.DescendantsHiddenOrInert(ref view);
    }

    [Fact]
    public void Scan_EyeSlotOverHiddenEyeFire_Holds()
        => Assert.True(Scan((5, Inert), (6, Hidden)));

    [Fact]
    public void Scan_AVisibleDescendantThatPaints_Fails()
    {
        Assert.False(Scan((5, Inert), (6, Paints)));
        // A node not read since it was added has no trustworthy `visible`: fail closed (stream).
        Assert.False(Scan((5, Inert), (6, Unknown)));
    }

    [Fact]
    public void Scan_PrunesAHiddenDescendantsSubtree_WithoutClassifyingIt()
    {
        // Hidden `H` with painting children: nothing under a hidden CanvasItem can paint, so they are skipped —
        // and never classified (the class probe is the scan's only non-trivial cost).
        Assert.True(Scan(out var asked, (5, Inert), (6, Hidden), (7, Paints), (8, Paints)));
        Assert.Equal([1], asked);
    }

    [Fact]
    public void Scan_ResumesAfterLeavingAPrunedSubtree()
    {
        // `H` hidden (pruned: its child), then a SIBLING of `H` that paints — the anchor's motion is on screen.
        Assert.False(Scan((5, Inert), (6, Hidden), (7, Paints), (6, Paints)));
        Assert.True(Scan((5, Inert), (6, Hidden), (7, Paints), (6, Inert), (7, Hidden)));
    }

    [Fact]
    public void Scan_LooksThroughVisibleInertGroups()
    {
        Assert.True(Scan((5, Inert), (6, Inert), (7, Hidden)));
        Assert.False(Scan((5, Inert), (6, Inert), (7, Paints)));
    }

    [Fact]
    public void Scan_StopsAtTheEndOfTheAnchorsSubtree()
    {
        // Entries at or above the anchor's depth are not its descendants, whatever they paint.
        Assert.True(Scan(out var asked, (5, Inert), (6, Hidden), (5, Paints), (4, Paints)));
        Assert.Equal([1], asked);
    }

    [Fact]
    public void Scan_IsBounded_AndFailsOpen()
    {
        // Too deep for an entry it must classify.
        var deep = new List<(int, Sts2SpineAnchorFold.DescendantKind)> { (0, Inert) };
        for (var d = 1; d <= Sts2SpineAnchorFold.MaxSubtreeDepth + 1; d++)
        {
            deep.Add((d, Inert));
        }
        Assert.False(Scan([.. deep]));

        // Too many entries — counting PRUNED ones, so a large hidden subtree cannot make the walk unbounded.
        var wide = new List<(int, Sts2SpineAnchorFold.DescendantKind)> { (0, Inert), (1, Hidden) };
        for (var i = 0; i < Sts2SpineAnchorFold.MaxSubtreeNodes; i++)
        {
            wide.Add((2, Paints));
        }
        Assert.False(Scan([.. wide]));

        // A pruned entry may sit deeper than the depth cap: it is never classified, so it never trips it.
        Assert.True(Scan((0, Inert), (1, Hidden), (2, Paints), (3, Paints), (4, Paints), (5, Paints)));
    }

    // ================= arm C: the flip flush — the per-node decision ==========================================

    private static Sts2SpineAnchorFold.FlushEntry Root(bool emitted = false, bool emittedTransform = false, bool differs = true)
        => new(Depth: 5, Kind: Inert, Emitted: emitted, EmittedTransform: emittedTransform, HasTransform: true, TransformDiffers: differs);

    private static Sts2SpineAnchorFold.FlushEntry Node(
        int depth, Sts2SpineAnchorFold.DescendantKind kind, bool emitted, bool emittedTransform = true, bool differs = false,
        bool tweenWindow = false)
        => new(Depth: depth, Kind: kind, Emitted: emitted, EmittedTransform: emitted && emittedTransform, HasTransform: true,
            TransformDiffers: differs, TweenWindow: tweenWindow);

    // The recorder's own trigger: arm C's scan over this tick's kinds no longer holds.
    private static bool Triggered(Sts2SpineAnchorFold.FlushEntry[] region)
        => !Scan([.. region.Select(e => (e.Depth, e.Kind))]);

    [Fact]
    public void Flip_ShipsTheRootAndEveryMovedNodeBetweenItAndTheNewlyVisibleOne()
    {
        // anchor → inert group G (moved, withheld) → hidden-until-now sprite S → its child C (read because S is
        // visible now). G must ship or S composes against G's stale local; an unmoved sibling group is already there.
        Sts2SpineAnchorFold.FlushEntry[] region =
        [
            Root(),
            Node(6, Inert, emitted: false, differs: true),
            Node(7, Paints, emitted: true),
            Node(8, Paints, emitted: false, differs: true),
            Node(6, Inert, emitted: false, differs: false),
        ];
        var triggered = Triggered(region);
        Assert.True(triggered);
        Assert.Equal(
            [
                Sts2SpineAnchorFold.FlushAction.Ship,
                Sts2SpineAnchorFold.FlushAction.Ship,
                Sts2SpineAnchorFold.FlushAction.Advance,
                Sts2SpineAnchorFold.FlushAction.Ship,
                Sts2SpineAnchorFold.FlushAction.None,
            ],
            region.Select(e => Sts2SpineAnchorFold.DecideFlush(e, triggered)));
    }

    [Fact]
    public void Flip_AnUnmovedAnchorNeedsNoShip()
    {
        Sts2SpineAnchorFold.FlushEntry[] region = [Root(differs: false), Node(6, Paints, emitted: true)];
        var triggered = Triggered(region);
        Assert.Equal(Sts2SpineAnchorFold.FlushAction.None, Sts2SpineAnchorFold.DecideFlush(region[0], triggered));
    }

    [Fact]
    public void NoFlip_WhileEverythingStaysHidden_NothingShips()
    {
        Sts2SpineAnchorFold.FlushEntry[] region = [Root(), Node(6, Hidden, emitted: false, differs: true)];
        var triggered = Triggered(region);
        Assert.False(triggered);
        Assert.All(region, e => Assert.Equal(Sts2SpineAnchorFold.FlushAction.None, Sts2SpineAnchorFold.DecideFlush(e, triggered)));
    }

    [Fact]
    public void NoFlip_WhenAHiddenChildEmitsForAnotherReason_ItsShippedTransformIsRecorded()
    {
        // A hidden child's modulate changed: it emitted (still `visible:false`) carrying its live transform. No flip,
        // but the client now holds that transform, so it is recorded as shipped.
        Sts2SpineAnchorFold.FlushEntry[] region = [Root(), Node(6, Hidden, emitted: true)];
        var triggered = Triggered(region);
        Assert.False(triggered);
        Assert.Equal(Sts2SpineAnchorFold.FlushAction.None, Sts2SpineAnchorFold.DecideFlush(region[0], triggered));
        Assert.Equal(Sts2SpineAnchorFold.FlushAction.Advance, Sts2SpineAnchorFold.DecideFlush(region[1], triggered));
    }

    [Fact]
    public void NoFlip_OnAKeyframe_WhereTheHiddenChildsOwnChildrenAreReadToo()
    {
        // A keyframe reads and emits every node, children of a hidden one included; the this-tick scan prunes them
        // exactly like the anchor's scan, so a keyframe is not mistaken for a flip.
        Sts2SpineAnchorFold.FlushEntry[] region =
            [Root(emitted: true, emittedTransform: true), Node(6, Hidden, emitted: true), Node(7, Paints, emitted: true)];
        var triggered = Triggered(region);
        Assert.False(triggered);
        Assert.All(region, e => Assert.Equal(Sts2SpineAnchorFold.FlushAction.Advance, Sts2SpineAnchorFold.DecideFlush(e, triggered)));
    }

    [Fact]
    public void Flip_PatchesAnUpsertThatOmittedItsTransform_UnlessATweenWindowCoversTheNode()
    {
        // Re-parented inside the region, so its upsert omits the transform; on a flip it must carry one...
        var reparented = Node(6, Paints, emitted: true, emittedTransform: false);
        Sts2SpineAnchorFold.FlushEntry[] region = [Root(), reparented];
        var triggered = Triggered(region);
        Assert.Equal(Sts2SpineAnchorFold.FlushAction.Patch, Sts2SpineAnchorFold.DecideFlush(region[1], triggered));
        Assert.Equal(Sts2SpineAnchorFold.FlushAction.None, Sts2SpineAnchorFold.DecideFlush(reparented, triggered: false));

        // ...but NOT when the omission is the reparent rule protecting a client-replayed tween on the node or an
        // ancestor: the client would pin a mid-transition transform for the rest of the window. Nor is one shipped
        // or advanced.
        Assert.Equal(
            Sts2SpineAnchorFold.FlushAction.None,
            Sts2SpineAnchorFold.DecideFlush(Node(6, Paints, emitted: true, emittedTransform: false, tweenWindow: true), triggered: true));
        Assert.Equal(
            Sts2SpineAnchorFold.FlushAction.None,
            Sts2SpineAnchorFold.DecideFlush(Node(6, Inert, emitted: false, differs: true, tweenWindow: true), triggered: true));
        Assert.Equal(
            Sts2SpineAnchorFold.FlushAction.None,
            Sts2SpineAnchorFold.DecideFlush(Node(6, Paints, emitted: true, tweenWindow: true), triggered: true));
    }

    [Fact]
    public void InsertInOrder_IsStableAndHandlesTheEnds()
    {
        var items = new List<string> { "a", "b" };
        Sts2SpineAnchorFold.InsertInOrder(items, [(0, "x"), (0, "y"), (2, "z")]);
        Assert.Equal(["x", "y", "a", "b", "z"], items);

        var empty = new List<string>();
        Sts2SpineAnchorFold.InsertInOrder(empty, [(0, "only")]);
        Assert.Equal(["only"], empty);

        var untouched = new List<string> { "a" };
        Sts2SpineAnchorFold.InsertInOrder(untouched, Array.Empty<(int, string)>());
        Assert.Equal(["a"], untouched);
    }

    // ================= frozen skeletons =======================================================================

    [Fact]
    public void FrozenElision_ExemptsOnlyAWithheldLeafThatHasDescendants()
    {
        // The normal elision: frozen, stationary root, incremental capture, skeleton leaf.
        Assert.True(Sts2SpineAnchorFold.ShouldElideFrozenLeaf(true, false, true, true, poseWithheld: false, hasDescendants: true));
        // A withheld pose with children placed against it: read it so it can ship.
        Assert.False(Sts2SpineAnchorFold.ShouldElideFrozenLeaf(true, false, true, true, poseWithheld: true, hasDescendants: true));
        // Withheld but childless (every arm-A mesh): nothing composes against it, keep eliding.
        Assert.True(Sts2SpineAnchorFold.ShouldElideFrozenLeaf(true, false, true, true, poseWithheld: true, hasDescendants: false));
        // Each pre-existing gate still applies on its own.
        Assert.False(Sts2SpineAnchorFold.ShouldElideFrozenLeaf(false, false, true, true, false, false));
        Assert.False(Sts2SpineAnchorFold.ShouldElideFrozenLeaf(true, true, true, true, false, false));
        Assert.False(Sts2SpineAnchorFold.ShouldElideFrozenLeaf(true, false, false, true, false, false));
        Assert.False(Sts2SpineAnchorFold.ShouldElideFrozenLeaf(true, false, true, false, false, false));
    }

    [Fact]
    public void UnderAFrozenStationaryRoot_NoArmWithholds_SoTheStalePoseShipsOnce()
    {
        foreach (var arm in new[] { (false, false, false), (true, true, false), (true, false, true) })
        {
            Assert.Equal(
                Sts2SpineAnchorFold.Arm.None,
                Sts2SpineAnchorFold.DecideArm(true, false, arm.Item1, arm.Item2, arm.Item2, true, arm.Item3, frozenStationaryRoot: true));
        }
    }

    [Fact]
    public void PoseMark_ArmAMarksWithoutACompare()
        => Assert.True(Sts2SpineAnchorFold.PoseStillWithheld(false, foldedChildless: true, false, true, false, false, false));

    [Fact]
    public void PoseMark_InsideAFold_FollowsWhetherTheClientHoldsTheLivePose()
    {
        Assert.True(Sts2SpineAnchorFold.PoseStillWithheld(false, false, insideFold: true, true, false,
            shippedLiveTransform: false, differsFromLastShipped: true));
        Assert.False(Sts2SpineAnchorFold.PoseStillWithheld(true, false, insideFold: true, true, false,
            shippedLiveTransform: false, differsFromLastShipped: false));
        // Its upsert carried this tick's transform (an arm-B anchor emitting for another reason): the client has it.
        Assert.False(Sts2SpineAnchorFold.PoseStillWithheld(true, false, insideFold: true, true, tweenWindow: false,
            shippedLiveTransform: true, differsFromLastShipped: true));
        // ...unless a tween window covers it: the client discarded that transform.
        Assert.True(Sts2SpineAnchorFold.PoseStillWithheld(true, false, insideFold: true, true, tweenWindow: true,
            shippedLiveTransform: true, differsFromLastShipped: true));
    }

    [Fact]
    public void PoseMark_SurvivesAnotherSuppression_UntilThePoseReallyShips()
    {
        // The re-review's defect: a tween window (a creature lunge) suppresses the anchor, the fold block is
        // skipped, and the mark must NOT drop — the pose still has not shipped.
        Assert.True(Sts2SpineAnchorFold.PoseStillWithheld(wasWithheld: true, false, insideFold: false,
            transformSuppressed: true, tweenWindow: true, false, false));
        // Never sets a mark the fold did not.
        Assert.False(Sts2SpineAnchorFold.PoseStillWithheld(wasWithheld: false, false, insideFold: false,
            transformSuppressed: true, tweenWindow: true, false, false));
        // Unsuppressed: the change test recorded the live pose.
        Assert.False(Sts2SpineAnchorFold.PoseStillWithheld(wasWithheld: true, false, insideFold: false,
            transformSuppressed: false, false, false, false));
    }

    // ================= arm C: the flip flush, driven through the recorder =====================================
    //
    // A miniature of the watcher's capture loop over a three-node tree — a parent P (streams normally), the anchor
    // A (a paintless skeleton leaf whose LOCAL moves every tick) and its child E (`EyeFire`: a constant local, its
    // own `visible` toggled by the test) — in LOCAL transform space, reduced to 1D offsets so the client's
    // composition is a sum. It makes the recorder calls the watcher makes, in the same order (Enter on every visited
    // node, BeginRoot on an arm-C decision, Record after the node's own emit step, Flush after the walk and before
    // the empty check), plus the frozen-spine elision and its exemption. What it CANNOT prove is that the watcher's
    // call sites match: that is covered by the live gates.

    private sealed class MiniNode(string id, int depth, bool skeletonLeaf)
    {
        public readonly string Id = id;
        public readonly int Depth = depth;
        public readonly bool SkeletonLeaf = skeletonLeaf;
        public double Live;            // this tick's local
        public bool Visible = true;
        public bool Tween;             // a client-replayed tween window is open on this node (SuppressTransformUntil)
        public bool WasTween;
        public double? LastShipped;    // Tracked.LastTransform
        public bool LastVisible;       // Tracked.LastVisible
        public bool JustAdded = true;
        public bool PoseWithheld;      // Tracked.AnchorPoseWithheld
        public int Reads;
    }

    private sealed record Upsert(string Id, double? Local, bool Visible, bool UnderTween);

    private sealed class MiniTarget : Sts2SpineAnchorFold.IFlushTarget<MiniNode, Upsert>
    {
        public Upsert BuildShip(in MiniNode node) => new(node.Id, node.Live, node.Visible, false);

        public Upsert WithTransform(Upsert upsert, in MiniNode node) => upsert with { Local = node.Live };

        public void Shipped(in MiniNode node)
        {
            node.LastShipped = node.Live;
            node.PoseWithheld = false;
        }

        public void Withheld(in MiniNode node)
        {
        }
    }

    private sealed class MiniWorld
    {
        public readonly MiniNode P = new("P", 0, skeletonLeaf: false);
        public readonly MiniNode A = new("A", 1, skeletonLeaf: true);
        public readonly MiniNode E = new("E", 2, skeletonLeaf: false) { Live = 3, Visible = false };
        public bool Frozen;            // the SpineSprite root (P here) is frozen and stationary
        public readonly Dictionary<string, (double Local, bool Visible)> Client = [];
        private readonly Sts2SpineAnchorFold.FlipFlushRecorder<MiniNode, Upsert> _recorder = new();
        private readonly MiniTarget _target = new();

        private MiniNode[] Ordered => [P, A, E];

        public List<Upsert> Capture()
        {
            _recorder.Reset();
            var upserts = new List<Upsert>();
            var ordered = Ordered;
            var suppressDepth = int.MaxValue;
            var anchorFoldDepth = int.MaxValue;
            var tweenWindowDepth = int.MaxValue;
            for (var i = 0; i < ordered.Length; i++)
            {
                var n = ordered[i];
                if (anchorFoldDepth != int.MaxValue && n.Depth <= anchorFoldDepth)
                {
                    anchorFoldDepth = int.MaxValue;
                }
                var role = _recorder.Enter(n.Depth);
                if (suppressDepth != int.MaxValue && n.Depth <= suppressDepth)
                {
                    suppressDepth = int.MaxValue;
                }
                if (tweenWindowDepth != int.MaxValue && n.Depth <= tweenWindowDepth)
                {
                    tweenWindowDepth = int.MaxValue;
                }
                var hasDescendants = i + 1 < ordered.Length && ordered[i + 1].Depth > n.Depth;
                if (Sts2SpineAnchorFold.ShouldElideFrozenLeaf(true, false, Frozen, n.SkeletonLeaf, n.PoseWithheld, hasDescendants))
                {
                    continue; // not read; (the watcher still stores its global for the children)
                }

                n.Reads++;
                var suppressed = n.Depth > suppressDepth;
                var forceResync = false;
                if (!suppressed)
                {
                    suppressed = n.Tween;
                    if (suppressed)
                    {
                        suppressDepth = n.Depth;
                    }
                    forceResync = n.WasTween && !n.Tween; // the window's settle re-emit, suppression root only
                }
                n.WasTween = n.Tween;
                if (tweenWindowDepth == int.MaxValue && n.Tween)
                {
                    tweenWindowDepth = n.Depth;
                }
                var tweenWindow = tweenWindowDepth != int.MaxValue;

                var foldedChildless = false;
                if (!suppressed && !n.JustAdded && n.SkeletonLeaf)
                {
                    var hiddenOrInert = Scan([.. ordered[i..].Select(o => (o.Depth, o == n ? Inert : o.JustAdded ? Unknown : !o.LastVisible ? Hidden : Paints))]);
                    var arm = Sts2SpineAnchorFold.DecideArm(true, false, hasDescendants, false, false, true, hiddenOrInert, Frozen);
                    if (arm != Sts2SpineAnchorFold.Arm.None)
                    {
                        suppressed = true;
                        suppressDepth = n.Depth;
                        if (arm == Sts2SpineAnchorFold.Arm.Childless)
                        {
                            foldedChildless = true;
                        }
                        else
                        {
                            anchorFoldDepth = n.Depth;
                        }
                        if (arm == Sts2SpineAnchorFold.Arm.HiddenDescendants)
                        {
                            role = _recorder.BeginRoot(n.Depth);
                        }
                    }
                }

                // ApplyIfChanged + BuildNodeDelta: a suppressed transform is out of the change test and LastTransform
                // does not advance, but an upsert built for another reason carries this tick's transform.
                var changed = n.JustAdded || forceResync || n.LastVisible != n.Visible || (!suppressed && n.LastShipped != n.Live);
                var emitted = false;
                if (changed)
                {
                    upserts.Add(new Upsert(n.Id, n.Live, n.Visible, tweenWindow));
                    emitted = true;
                    n.LastVisible = n.Visible;
                    if (!suppressed)
                    {
                        n.LastShipped = n.Live;
                    }
                    n.JustAdded = false;
                }

                _recorder.Record(role, upserts, n, n.Depth, n.Visible ? Paints : Hidden,
                    emitted, emittedTransform: true, hasTransform: true,
                    transformDiffers: n.LastShipped != n.Live, tweenWindow: tweenWindow);
                n.PoseWithheld = Sts2SpineAnchorFold.PoseStillWithheld(
                    n.PoseWithheld, foldedChildless, anchorFoldDepth != int.MaxValue, suppressed, tweenWindow,
                    shippedLiveTransform: emitted, differsFromLastShipped: n.LastShipped != n.Live);
            }

            if (_recorder.Count > 0)
            {
                _recorder.Flush(upserts, _target);
            }

            // The client discards a streamed transform for a node a tween window covers (it replays the tween); in
            // this model the tween is a no-op on the pose, so it simply keeps what it had.
            foreach (var u in upserts)
            {
                var keep = Client.TryGetValue(u.Id, out var had) && u.UnderTween;
                Client[u.Id] = (keep ? had.Local : u.Local ?? had.Local, u.Visible);
            }

            return upserts;
        }

        public double LiveGlobal(MiniNode n) => n == E ? P.Live + A.Live + E.Live : n == A ? P.Live + A.Live : P.Live;

        public double ClientGlobal(MiniNode n)
            => n == E ? Client["P"].Local + Client["A"].Local + Client["E"].Local
                : n == A ? Client["P"].Local + Client["A"].Local
                : Client["P"].Local;
    }

    [Fact]
    public void Recorder_IdleHiddenChild_GoesQuiet_ThenTheFlipLandsAtTheLivePoseInOneDelta_RootFirst()
    {
        var w = new MiniWorld();
        Assert.Equal(["P", "A", "E"], w.Capture().Select(u => u.Id)); // first appearance

        // Idle: the anchor's pose keeps changing, its only child is hidden → nothing on the wire at all.
        for (var t = 1; t <= 5; t++)
        {
            w.A.Live = t * 0.37;
            Assert.Empty(w.Capture());
        }

        // The flip: E turns visible on the same tick the anchor moved again. The anchor was decided on last
        // capture's facts (E hidden), so it was withheld — and its live pose must still land in THIS delta, first.
        w.A.Live = 9.5;
        w.E.Visible = true;
        var flip = w.Capture();
        Assert.Equal(["A", "E"], flip.Select(u => u.Id));
        Assert.Equal(9.5, flip[0].Local);
        Assert.True(flip[1].Visible);
        Assert.Equal(w.LiveGlobal(w.E), w.ClientGlobal(w.E));

        // Next capture: E's last `visible` is true, so the fold lets go on its own and the anchor streams.
        w.A.Live = 10.25;
        Assert.Equal(["A"], w.Capture().Select(u => u.Id));
        Assert.Equal(w.LiveGlobal(w.E), w.ClientGlobal(w.E));

        // E hides again: one more anchor emit on the edge, then quiet.
        w.E.Visible = false;
        w.A.Live = 11;
        Assert.Equal(["A", "E"], w.Capture().Select(u => u.Id));
        w.A.Live = 12;
        Assert.Empty(w.Capture());
    }

    [Fact]
    public void Recorder_UnrelatedEmitBeforeTheAnchor_FlushIsInsertedAtTheAnchorsPreOrderPosition()
    {
        var w = new MiniWorld();
        w.Capture();
        w.A.Live = 2;
        Assert.Empty(w.Capture());
        w.P.Live = 1;          // the parent moves this tick (emits first, in pre-order)
        w.A.Live = 4;
        w.E.Visible = true;
        Assert.Equal(["P", "A", "E"], w.Capture().Select(u => u.Id));
        Assert.Equal(w.LiveGlobal(w.E), w.ClientGlobal(w.E));
    }

    [Fact]
    public void Recorder_FreezeAfterAWithheldPose_ShipsItOnce_ThenElides_AndALaterFlipIsStillPlaced()
    {
        // The review's scenario: the anchor is withheld while the skeleton moves, then the skeleton freezes (its
        // leaves are read-elided from then on), then the hidden child turns visible.
        var w = new MiniWorld();
        w.Capture();
        w.A.Live = 6.5;
        Assert.Empty(w.Capture());
        Assert.True(w.A.PoseWithheld);

        w.Frozen = true;   // A cannot move any more
        Assert.Equal(["A"], w.Capture().Select(u => u.Id)); // exempt from elision: the stale pose ships once
        Assert.False(w.A.PoseWithheld);
        var readsAfterSettle = w.A.Reads;
        Assert.Empty(w.Capture());
        Assert.Equal(readsAfterSettle, w.A.Reads);          // ...and from then on it is elided like any frozen leaf

        w.E.Visible = true;
        Assert.Equal(["E"], w.Capture().Select(u => u.Id));
        Assert.Equal(w.LiveGlobal(w.E), w.ClientGlobal(w.E));
    }

    [Fact]
    public void Recorder_TweenWindowOverAWithheldAnchor_KeepsTheMark_SoAFreezeStillShipsIt_AndALaterFlipIsPlaced()
    {
        // The re-review's scenario: arm C withholds the anchor; an ancestor tween (a creature lunge) opens a window,
        // which skips the fold block; the skeleton freezes and the window closes; the hidden child later turns
        // visible. If the tween had wiped the mark, the frozen-spine elision would skip the anchor for good and the
        // child would compose against its stale pose.
        var w = new MiniWorld();
        w.Capture();
        w.A.Live = 6.5;
        Assert.Empty(w.Capture());
        Assert.True(w.A.PoseWithheld);

        w.P.Tween = true;      // the lunge: P's subtree is client-replayed
        w.A.Live = 7.25;
        w.Capture();
        Assert.True(w.A.PoseWithheld);

        w.Frozen = true;       // the skeleton freezes...
        w.P.Tween = false;     // ...and the window closes: P settles, the anchor is exempt and ships once
        Assert.Equal(["P", "A"], w.Capture().Select(u => u.Id));
        Assert.False(w.A.PoseWithheld);
        Assert.Empty(w.Capture());

        w.E.Visible = true;
        Assert.Equal(["E"], w.Capture().Select(u => u.Id));
        Assert.Equal(w.LiveGlobal(w.E), w.ClientGlobal(w.E));
    }

    [Fact]
    public void Recorder_TwoAnchorsInOneCapture_OnlyTheFlippedRegionIsFlushed()
    {
        var recorder = new Sts2SpineAnchorFold.FlipFlushRecorder<string, string>();
        var upserts = new List<string>();
        var target = new StringTarget();

        // Anchor 1 (depth 5) with a still-hidden child that moved; anchor 2 (depth 5) whose child flipped.
        var role = recorder.Enter(5);
        role = recorder.BeginRoot(5);
        recorder.Record(role, upserts, "A1", 5, Inert, false, false, true, true, false);
        role = recorder.Enter(6);
        recorder.Record(role, upserts, "E1", 6, Hidden, false, false, true, true, false);
        role = recorder.Enter(5);
        Assert.Equal(Sts2SpineAnchorFold.FlushRole.None, role);
        role = recorder.BeginRoot(5);
        recorder.Record(role, upserts, "A2", 5, Inert, false, false, true, true, false);
        role = recorder.Enter(6);
        upserts.Add("E2");
        recorder.Record(role, upserts, "E2", 6, Paints, true, true, true, false, false);
        role = recorder.Enter(4);
        Assert.Equal(Sts2SpineAnchorFold.FlushRole.None, role); // left the region

        recorder.Flush(upserts, target);
        Assert.Equal(["ship:A2", "E2"], upserts);
        Assert.Equal(["A1", "E1"], target.Withheld.OrderBy(x => x));
        Assert.Equal(0, recorder.Count); // a flush leaves nothing behind for the next capture
    }

    private sealed class StringTarget : Sts2SpineAnchorFold.IFlushTarget<string, string>
    {
        public readonly List<string> Withheld = [];

        public string BuildShip(in string node) => "ship:" + node;

        public string WithTransform(string upsert, in string node) => upsert + "+t";

        public void Shipped(in string node)
        {
        }

        void Sts2SpineAnchorFold.IFlushTarget<string, string>.Withheld(in string node) => Withheld.Add(node);
    }
}
