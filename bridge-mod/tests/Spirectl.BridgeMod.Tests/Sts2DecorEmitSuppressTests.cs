using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// R12 producer decorative-animator SCENE-IDENTITY TABLE (Sts2DecorEmitSuppress) — a pure, Godot-free exercise of the
// rule table and the rel-path composer the watcher applies before ApplyIfChanged. Verifies: the five measured
// churners route to the fold that owns them; unlisted scenes/paths and the matched nodes' SIBLINGS route to None (so
// a fold can never leak onto a real animation); and the rel-path composer matches the watcher's descent and stops at
// MaxRelDepth.
//
// R13 note: the table no longer carries the blunt `Transform` / `SelfModulateAlpha` "pin to the first-emitted
// sample" channels — every rule is now a channel owned by an analytic fold (see Sts2TopBarFoldTests /
// Sts2ProceedGlowTests / Sts2MapPointPulseTests for each one's math and gate).
public sealed class Sts2DecorEmitSuppressTests
{
    private const string DeckButton = "res://scenes/ui/top_bar/top_bar_deck_button.tscn";
    private const string MapButton = "res://scenes/ui/top_bar/top_bar_map_button.tscn";
    private const string SettingsButton = "res://scenes/ui/top_bar/top_bar_settings_button.tscn";
    private const string ProceedButton = "res://scenes/ui/proceed_button.tscn";
    private const string MapPoint = "res://scenes/ui/normal_map_point.tscn";

    // `expected` is the channel widened to int: the enum is internal, so it cannot appear in a public signature.
    [Theory]
    // `Control/Icon` is the node the three NTopBarButton subclasses rock/spin while their screen is open. Only its
    // ROTATION is folded — position and scale must keep streaming (that is what fixed the mispositioned icons).
    [InlineData(DeckButton, "Control/Icon", (int)Sts2DecorEmitSuppress.Channels.TopBarRotation)]
    [InlineData(MapButton, "Control/Icon", (int)Sts2DecorEmitSuppress.Channels.TopBarRotation)]
    [InlineData(SettingsButton, "Control/Icon", (int)Sts2DecorEmitSuppress.Channels.TopBarRotation)]
    // The Proceed glow pulses its self_modulate ALPHA forever; its RGB and its transform must not be touched.
    [InlineData(ProceedButton, "Image/Outline", (int)Sts2DecorEmitSuppress.Channels.ProceedGlowAlpha)]
    // The travelable map point's icon group pulses its uniform SCALE; its per-point random tilt keeps streaming.
    [InlineData(MapPoint, "IconContainer", (int)Sts2DecorEmitSuppress.Channels.MapPointPulseScale)]
    public void EachChurner_RoutesToItsOwnFold(string scene, string relPath, int expected)
    {
        Assert.Equal((Sts2DecorEmitSuppress.Channels)expected, Sts2DecorEmitSuppress.Lookup(scene, relPath));
        Assert.True(Sts2DecorEmitSuppress.IsWatchedScene(scene));
    }

    [Theory]
    // A sibling inside a WATCHED scene: the rule is per-path, so the button root, its label and the Icon's parent are
    // all untouched — only the exact animated node is folded.
    [InlineData(DeckButton, ".")]
    [InlineData(DeckButton, "Control")]
    [InlineData(DeckButton, "DeckCardCount")]
    [InlineData(ProceedButton, ".")]
    [InlineData(ProceedButton, "Image")]
    [InlineData(ProceedButton, "Image/Label")]
    // An UNWATCHED scene that happens to share a path shape with a watched one. (`intent.tscn` used to be the
    // second example here; R14 added folds for it, so the co-op player-intent scene — which really does mount an
    // `IntentHolder` and really is not folded — took its place.)
    [InlineData("res://scenes/ui/top_bar.tscn", "Control/Icon")]
    [InlineData("res://scenes/combat/multiplayer_player_intent.tscn", "IntentHolder")]
    // The map point's SIBLINGS and its pulsing container's CHILDREN (the icons ride the container; they are not it).
    [InlineData(MapPoint, ".")]
    [InlineData(MapPoint, "SelectionReticle")]
    [InlineData(MapPoint, "IconContainer/Icon")]
    public void UnlistedSceneOrPath_FoldsNothing(string scene, string relPath)
        => Assert.Equal(Sts2DecorEmitSuppress.Channels.None, Sts2DecorEmitSuppress.Lookup(scene, relPath));

    [Fact]
    public void NullIdentity_FoldsNothing()
    {
        // Every node outside a watched scene reaches Lookup with a null scene/path — it must be an unconditional miss.
        Assert.Equal(Sts2DecorEmitSuppress.Channels.None, Sts2DecorEmitSuppress.Lookup(null, "Control/Icon"));
        Assert.Equal(Sts2DecorEmitSuppress.Channels.None, Sts2DecorEmitSuppress.Lookup(DeckButton, null));
        Assert.False(Sts2DecorEmitSuppress.IsWatchedScene(null));
        Assert.False(Sts2DecorEmitSuppress.IsWatchedScene("res://scenes/ui/top_bar.tscn"));
    }

    [Fact]
    public void Channels_AreDisjointBits_SoTheWatcherCanRouteByMask()
    {
        // The watcher tests each rule with `(channels & X) != 0`, so the folds must not share a bit — otherwise
        // one fold's pin would fire on another fold's node. (R14 added four combat channels; see
        // Sts2IdleWireFoldTests for their own isolation assertions.)
        var all = Sts2DecorEmitSuppress.Channels.TopBarRotation
                  | Sts2DecorEmitSuppress.Channels.ProceedGlowAlpha
                  | Sts2DecorEmitSuppress.Channels.MapPointPulseScale
                  | Sts2DecorEmitSuppress.Channels.IntentBobPosition
                  | Sts2DecorEmitSuppress.Channels.OrbSpinRotation
                  | Sts2DecorEmitSuppress.Channels.IntentGlyphTexture
                  | Sts2DecorEmitSuppress.Channels.EndTurnGlowPulse;
        Assert.Equal(127, (int)all);
        Assert.Equal(0, (int)Sts2DecorEmitSuppress.Channels.None);
    }

    [Fact]
    public void ChildRelPath_ComposesTheWatcherDescent()
    {
        // The watcher walks pre-order: scene root "." -> "Control" -> "Control/Icon", which is what the table keys on.
        var control = Sts2DecorEmitSuppress.ChildRelPath(Sts2DecorEmitSuppress.RootRelPath, "Control");
        Assert.Equal("Control", control);
        var icon = Sts2DecorEmitSuppress.ChildRelPath(control, "Icon");
        Assert.Equal("Control/Icon", icon);
        Assert.Equal(Sts2DecorEmitSuppress.Channels.TopBarRotation, Sts2DecorEmitSuppress.Lookup(DeckButton, icon));
    }

    [Fact]
    public void ChildRelPath_StopsAtMaxDepth()
    {
        // Past MaxRelDepth the composer returns null, which is how the watcher stops building strings (and stops
        // treating the subtree as watched) inside anything deeper than the tiny scenes the table covers.
        string? path = Sts2DecorEmitSuppress.RootRelPath;
        for (var depth = 1; depth <= Sts2DecorEmitSuppress.MaxRelDepth; depth++)
        {
            path = Sts2DecorEmitSuppress.ChildRelPath(path, "N" + depth);
            Assert.NotNull(path);
        }

        Assert.Null(Sts2DecorEmitSuppress.ChildRelPath(path, "TooDeep"));
        // A null parent path (already past the cap / unwatched) stays null.
        Assert.Null(Sts2DecorEmitSuppress.ChildRelPath(null, "Anything"));
    }
}
