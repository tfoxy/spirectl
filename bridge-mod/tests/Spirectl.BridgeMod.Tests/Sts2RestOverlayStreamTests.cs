using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// FIX 4 — rest-site option DESCRIPTION stuck invisible on refocus. The pure overlay-stream predicates
// (Sts2RestOverlayStream): IsRestSiteRoomRoot (the depth-sentinel pin point), ExemptFromVisiblePrune (part 1:
// un-prune a Visible=false node inside the room subtree) and EmitVisible (part 2: flip the overlay-quirk false→true
// only inside the ACTIVE overlay). Truth tables prove: kill-switch off ⇒ no exemption / no flip; outside the overlay
// ⇒ untouched; a VISIBLE node is never pruned/flipped; an INACTIVE screen never resurrects a backgrounded room.
public sealed class Sts2RestOverlayStreamTests
{
    // ---- IsRestSiteRoomRoot ------------------------------------------------------------------------------------

    [Fact]
    public void IsRestSiteRoomRoot_MatchesManagedType()
    {
        // The live NRestSiteRoom node reports node.GetType().FullName as this managed type (verified vs a live capture).
        Assert.True(Sts2RestOverlayStream.IsRestSiteRoomRoot(
            "MegaCrit.Sts2.Core.Nodes.Rooms.NRestSiteRoom", sceneFilePath: null));
    }

    [Fact]
    public void IsRestSiteRoomRoot_MatchesScenePath()
    {
        // The instanced-scene root also carries this SceneFilePath (verified vs a live capture); either match pins it.
        Assert.True(Sts2RestOverlayStream.IsRestSiteRoomRoot(
            nodeType: null, sceneFilePath: "res://scenes/rooms/rest_site_room.tscn"));
        // A wrapped/renamed node that only exposes its scene path (nodeType a base class) still pins.
        Assert.True(Sts2RestOverlayStream.IsRestSiteRoomRoot(
            "Godot.Control", "res://scenes/rooms/rest_site_room.tscn"));
    }

    [Fact]
    public void IsRestSiteRoomRoot_RejectsOtherRoomsAndNull()
    {
        Assert.False(Sts2RestOverlayStream.IsRestSiteRoomRoot(null, null));
        Assert.False(Sts2RestOverlayStream.IsRestSiteRoomRoot(
            "MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom", "res://scenes/rooms/event_room.tscn"));
        Assert.False(Sts2RestOverlayStream.IsRestSiteRoomRoot(
            "MegaCrit.Sts2.Core.Nodes.Rooms.NMerchantRoom", "res://scenes/rooms/merchant_room.tscn"));
        // Combat/map/shop node under an unrelated path must never pin the sentinel.
        Assert.False(Sts2RestOverlayStream.IsRestSiteRoomRoot(
            "MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand", "res://scenes/combat/player_hand.tscn"));
    }

    // ---- ExemptFromVisiblePrune (part 1: un-prune) -------------------------------------------------------------

    private static bool ExpectedExempt(bool enabled, bool insideRestOverlay, bool localVisible)
        => enabled && insideRestOverlay && !localVisible;

    [Fact]
    public void ExemptFromVisiblePrune_TruthTable()
    {
        var bools = new[] { false, true };
        foreach (var enabled in bools)
        foreach (var inside in bools)
        foreach (var localVisible in bools)
        {
            var actual = Sts2RestOverlayStream.ExemptFromVisiblePrune(enabled, inside, localVisible);
            var expected = ExpectedExempt(enabled, inside, localVisible);
            Assert.True(actual == expected,
                $"enabled={enabled} inside={inside} localVisible={localVisible}: expected {expected}, got {actual}");
        }
    }

    [Fact]
    public void ExemptFromVisiblePrune_ExemptsHiddenNodeInsideActiveRoom()
    {
        // The ChoicesScreen description reads Visible=false (the overlay quirk) → EXEMPT so it keeps streaming.
        Assert.True(Sts2RestOverlayStream.ExemptFromVisiblePrune(enabled: true, insideRestOverlay: true, localVisible: false));
    }

    [Fact]
    public void ExemptFromVisiblePrune_VisibleNodeNeverExempted()
    {
        // A genuinely-visible node is not pruned in the first place, so it is never "exempted".
        Assert.False(Sts2RestOverlayStream.ExemptFromVisiblePrune(enabled: true, insideRestOverlay: true, localVisible: true));
    }

    [Fact]
    public void ExemptFromVisiblePrune_OutsideOverlay_NeverExempts()
    {
        // Combat/map/shop/event: insideRestOverlay=false ⇒ the prune gate is byte-identical.
        foreach (var localVisible in new[] { false, true })
        {
            Assert.False(Sts2RestOverlayStream.ExemptFromVisiblePrune(enabled: true, insideRestOverlay: false, localVisible));
        }
    }

    [Fact]
    public void ExemptFromVisiblePrune_KillSwitchOff_NeverExempts()
    {
        // SPIRECTL_SCENE_WATCH_REST_OVERLAY_STREAM=0 ⇒ pre-fix prune for every combo.
        var bools = new[] { false, true };
        foreach (var inside in bools)
        foreach (var localVisible in bools)
        {
            Assert.False(Sts2RestOverlayStream.ExemptFromVisiblePrune(enabled: false, inside, localVisible));
        }
    }

    // ---- EmitVisible (part 2: flip false→true only inside the ACTIVE overlay) ----------------------------------

    private static bool ExpectedEmit(bool enabled, bool insideRestOverlay, bool restSiteActive, bool localVisible)
        => localVisible || (enabled && insideRestOverlay && restSiteActive);

    [Fact]
    public void EmitVisible_TruthTable()
    {
        var bools = new[] { false, true };
        foreach (var enabled in bools)
        foreach (var inside in bools)
        foreach (var active in bools)
        foreach (var localVisible in bools)
        {
            var actual = Sts2RestOverlayStream.EmitVisible(enabled, inside, active, localVisible);
            var expected = ExpectedEmit(enabled, inside, active, localVisible);
            Assert.True(actual == expected,
                $"enabled={enabled} inside={inside} active={active} localVisible={localVisible}: expected {expected}, got {actual}");
        }
    }

    [Fact]
    public void EmitVisible_FlipsHiddenDescriptionInsideActiveOverlay()
    {
        // The core cure: the description reads Visible=false (overlay quirk) but the room is ACTIVE → stream visible=true
        // so its focus fade-in (modulate.a) is read/diffed/emitted and both clients composite it.
        Assert.True(Sts2RestOverlayStream.EmitVisible(enabled: true, insideRestOverlay: true, restSiteActive: true, localVisible: false));
    }

    [Fact]
    public void EmitVisible_NeverForcesTrueToFalse()
    {
        // A genuinely-visible node is never flipped off, in or out of the overlay, active or not.
        foreach (var inside in new[] { false, true })
        foreach (var active in new[] { false, true })
        {
            Assert.True(Sts2RestOverlayStream.EmitVisible(enabled: true, insideRestOverlay: inside, restSiteActive: active, localVisible: true));
        }
    }

    [Fact]
    public void EmitVisible_InactiveScreen_NeverResurrectsBackgroundedRoom()
    {
        // A BACKGROUNDED rest room (its nodes still in the tree but _screenType != Rooms.NRestSiteRoom) must NOT have
        // its hidden nodes flipped visible — restSiteActive=false ⇒ the live (false) value passes through untouched.
        Assert.False(Sts2RestOverlayStream.EmitVisible(enabled: true, insideRestOverlay: true, restSiteActive: false, localVisible: false));
    }

    [Fact]
    public void EmitVisible_OutsideOverlay_Untouched()
    {
        // Combat/map/shop/event: insideRestOverlay=false ⇒ the emitted `visible` is byte-identical to the live value.
        foreach (var localVisible in new[] { false, true })
        {
            Assert.Equal(localVisible, Sts2RestOverlayStream.EmitVisible(enabled: true, insideRestOverlay: false, restSiteActive: true, localVisible));
        }
    }

    [Fact]
    public void EmitVisible_KillSwitchOff_Untouched()
    {
        // SPIRECTL_SCENE_WATCH_REST_OVERLAY_STREAM=0 ⇒ the live value passes through for every combo (pre-fix).
        var bools = new[] { false, true };
        foreach (var inside in bools)
        foreach (var active in bools)
        foreach (var localVisible in bools)
        {
            Assert.Equal(localVisible, Sts2RestOverlayStream.EmitVisible(enabled: false, inside, active, localVisible));
        }
    }

    // ---- R10 controller-prompt denylist (the "press Y" glyph at the rest site's Continue button) ----------------

    [Fact]
    public void IsControllerPromptNode_MatchesTheGamesPromptGlyphNames()
    {
        Assert.True(Sts2RestOverlayStream.IsControllerPromptNode("ControllerIcon"));
        Assert.True(Sts2RestOverlayStream.IsControllerPromptNode("ControllerBindingIcon"));
        Assert.True(Sts2RestOverlayStream.IsControllerPromptNode("ControllerHeader"));
    }

    [Fact]
    public void IsControllerPromptNode_RejectsEverythingElse()
    {
        // Exact ordinal match only — no case folding, no substring, no null/empty.
        Assert.False(Sts2RestOverlayStream.IsControllerPromptNode(null));
        Assert.False(Sts2RestOverlayStream.IsControllerPromptNode(string.Empty));
        Assert.False(Sts2RestOverlayStream.IsControllerPromptNode("controllericon"));
        Assert.False(Sts2RestOverlayStream.IsControllerPromptNode("ControllerIconContainer"));
        // The rest-room nodes the original fix exists for must never be denied.
        Assert.False(Sts2RestOverlayStream.IsControllerPromptNode("Description"));
        Assert.False(Sts2RestOverlayStream.IsControllerPromptNode("ChoicesScreen"));
        Assert.False(Sts2RestOverlayStream.IsControllerPromptNode("ProceedButton"));
        Assert.False(Sts2RestOverlayStream.IsControllerPromptNode("Label"));
        Assert.False(Sts2RestOverlayStream.IsControllerPromptNode("Icon"));
    }

    [Fact]
    public void EmitVisible_DenylistedPromptIsNeverFlippedInsideTheActiveOverlay()
    {
        // The defect: the game hides ProceedButton/%ControllerIcon (no gamepad attached) and the flip resurrected
        // it, so a touch mirror showed a "press Y" prompt the game itself did not.
        Assert.False(Sts2RestOverlayStream.EmitVisible(
            enabled: true, insideRestOverlay: true, restSiteActive: true, localVisible: false,
            promptDenylist: true, nodeName: "ControllerIcon"));
    }

    [Fact]
    public void EmitVisible_DenylistDoesNotTouchTheRestOfTheRoom()
    {
        // The description chain the original fix was written for still flips (that is the whole cure).
        Assert.True(Sts2RestOverlayStream.EmitVisible(
            enabled: true, insideRestOverlay: true, restSiteActive: true, localVisible: false,
            promptDenylist: true, nodeName: "Description"));
        Assert.True(Sts2RestOverlayStream.EmitVisible(
            enabled: true, insideRestOverlay: true, restSiteActive: true, localVisible: false,
            promptDenylist: true, nodeName: "ProceedButton"));
        // An unnamed node (no name plumbed) keeps the pre-R10 flip.
        Assert.True(Sts2RestOverlayStream.EmitVisible(
            enabled: true, insideRestOverlay: true, restSiteActive: true, localVisible: false,
            promptDenylist: true, nodeName: null));
    }

    [Fact]
    public void EmitVisible_DenylistNeverHidesAGenuinelyVisiblePrompt()
    {
        // A controller IS attached ⇒ the game shows the glyph ⇒ so does the mirror. The denylist only ever declines
        // to RESURRECT; it can't turn a visible node off.
        Assert.True(Sts2RestOverlayStream.EmitVisible(
            enabled: true, insideRestOverlay: true, restSiteActive: true, localVisible: true,
            promptDenylist: true, nodeName: "ControllerIcon"));
    }

    [Fact]
    public void EmitVisible_DenylistKillSwitchOff_RestoresThePreR10Flip()
    {
        // SPIRECTL_SCENE_WATCH_REST_OVERLAY_PROMPT_DENYLIST=0 ⇒ every hidden node in the active room flips again.
        Assert.True(Sts2RestOverlayStream.EmitVisible(
            enabled: true, insideRestOverlay: true, restSiteActive: true, localVisible: false,
            promptDenylist: false, nodeName: "ControllerIcon"));
    }

    [Fact]
    public void EmitVisible_DenylistIsScopedToTheActiveOverlay()
    {
        // Outside the overlay the denylist is moot — the live value already passes through.
        Assert.False(Sts2RestOverlayStream.EmitVisible(
            enabled: true, insideRestOverlay: false, restSiteActive: true, localVisible: false,
            promptDenylist: true, nodeName: "ControllerIcon"));
        Assert.Equal(
            Sts2RestOverlayStream.EmitVisible(enabled: true, insideRestOverlay: false, restSiteActive: true, localVisible: false),
            Sts2RestOverlayStream.EmitVisible(
                enabled: true, insideRestOverlay: false, restSiteActive: true, localVisible: false,
                promptDenylist: true, nodeName: "Description"));
    }

    // The DEFAULT parameters must keep every pre-R10 call site byte-identical (the truth tables above call the
    // 4-argument overload, which is exactly what proves this).
    [Fact]
    public void EmitVisible_DefaultArguments_MatchThePreR10Behaviour()
    {
        var bools = new[] { false, true };
        foreach (var enabled in bools)
        foreach (var inside in bools)
        foreach (var active in bools)
        foreach (var localVisible in bools)
        {
            Assert.Equal(
                ExpectedEmit(enabled, inside, active, localVisible),
                Sts2RestOverlayStream.EmitVisible(enabled, inside, active, localVisible));
        }
    }
}
