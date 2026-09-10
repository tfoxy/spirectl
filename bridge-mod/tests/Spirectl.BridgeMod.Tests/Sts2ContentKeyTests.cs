using System.Text.RegularExpressions;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// R13 producer CONTENT KEY (Sts2ContentKey) — a pure, Godot-free (and STS2-free: the registry keys on `object`)
// exercise of the three properties a client keying on card content depends on: the key is STABLE for one model, two
// models sharing a card DEFINITION never collide, and the format is the documented `nc:{entry}#{serial}`.
public sealed class Sts2ContentKeyTests
{
    // Stand-ins for CardModel. The registry deliberately keys on object IDENTITY (a ConditionalWeakTable), never on
    // equality or on a node instance id — pooled NCard visuals recycle their ids, card models do not.
    private sealed class FakeModel;

    [Fact]
    public void SameModel_KeepsOneKeyForever()
    {
        var model = new FakeModel();

        var first = Sts2ContentKey.ForModel(model, "Strike");
        var second = Sts2ContentKey.ForModel(model, "Strike");
        var third = Sts2ContentKey.ForModel(model, "Strike");

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(first, third);
        // Reference-equal too: after the first sight the lookup is a dictionary probe with no string formatting.
        Assert.Same(first, second);
    }

    [Fact]
    public void TwoModelsWithTheSameEntry_GetDistinctSerials()
    {
        // Two Strikes in one hand are the same DEFINITION but different cards. If they shared a key a client keying
        // on it would fold them into one element.
        var strikeA = new FakeModel();
        var strikeB = new FakeModel();

        var keyA = Sts2ContentKey.ForModel(strikeA, "Strike");
        var keyB = Sts2ContentKey.ForModel(strikeB, "Strike");

        Assert.NotEqual(keyA, keyB);
        // Both still carry the shared definition id, which is what makes the key content-addressable.
        Assert.StartsWith("nc:Strike#", keyA);
        Assert.StartsWith("nc:Strike#", keyB);
        // ...and each is still stable on re-read.
        Assert.Equal(keyA, Sts2ContentKey.ForModel(strikeA, "Strike"));
        Assert.Equal(keyB, Sts2ContentKey.ForModel(strikeB, "Strike"));
    }

    [Fact]
    public void KeyFormat_IsNamespacedEntryThenSerial()
    {
        var key = Sts2ContentKey.ForModel(new FakeModel(), "Bash");

        // `nc:{entry}#{serial}` — the format the client parses, so pin it as a whole-string shape.
        Assert.Matches(new Regex(@"^nc:Bash#\d+$"), key!);
        Assert.Equal("nc", Sts2ContentKey.CardPrefix);
        Assert.Equal("nc:Bash#7", Sts2ContentKey.Compose("Bash", 7));
    }

    [Fact]
    public void NoModelOrNoEntry_HasNoKey()
    {
        // A pooled NCard shell between assignments (Model == null), and the defensive no-entry case: the field is
        // simply omitted from the wire rather than shipping a meaningless key.
        Assert.Null(Sts2ContentKey.ForModel(null, "Strike"));
        Assert.Null(Sts2ContentKey.ForModel(new FakeModel(), null));
        Assert.Null(Sts2ContentKey.ForModel(new FakeModel(), string.Empty));
    }

    [Fact]
    public void CardScene_IsTheOnlyMatchedScene()
    {
        Assert.True(Sts2ContentKey.IsCardScene("res://scenes/cards/card.tscn"));
        Assert.Equal("res://scenes/cards/card.tscn", Sts2ContentKey.CardSceneFile);
        Assert.False(Sts2ContentKey.IsCardScene("res://scenes/cards/card_bundle.tscn"));
        Assert.False(Sts2ContentKey.IsCardScene("res://scenes/ui/proceed_button.tscn"));
        Assert.False(Sts2ContentKey.IsCardScene(null));
    }
}
