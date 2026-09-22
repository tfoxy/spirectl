using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// The scene-subtree:// key grammar (extracted from the live-host-only TryParseSceneSubtreeRequest so it is
// provable without a game). Two halves with different failure rules, and the split is the point:
//   * ADDRESSING (scheme, res:// scene, node) is all-or-nothing — a malformed key parses to null and the caller
//     falls through to the ordinary resource path rather than rendering something nobody asked for.
//   * POSING (rect / shaderParam / modulate / particles) drops per-knob — a still rendered at the lane's default
//     is noticeable, one rendered at a wrong-but-plausible rect is not.
public sealed class Sts2SceneSubtreeStillKeyTests
{
    private const string CardHighlight =
        "scene-subtree://res://scenes/cards/card.tscn?node=CardContainer%2FHighlight";

    // `Assert.NotNull` returns void for reference types here, so the two nullable collections get a narrowing
    // accessor rather than an inline assertion.
    private static IReadOnlyDictionary<string, float> ShaderParameters(Sts2SceneSubtreeStillKey.ParsedKey parsed)
    {
        Assert.NotNull(parsed.Options.ShaderParameters);
        return parsed.Options.ShaderParameters!;
    }

    private static IReadOnlyList<float> Modulate(Sts2SceneSubtreeStillKey.ParsedKey parsed)
    {
        Assert.NotNull(parsed.Options.Modulate);
        return parsed.Options.Modulate!;
    }

    [Fact]
    public void TryParse_BareKey_IsTheUnchangedBackdropLane()
    {
        var parsed = Assert.NotNull(Sts2SceneSubtreeStillKey.TryParse(CardHighlight));

        Assert.Equal("res://scenes/cards/card.tscn", parsed.ScenePath);
        Assert.Equal("CardContainer/Highlight", parsed.NodePath);
        // IsDefault is what the request rewrite keys off to leave the render byte-identical to before the knobs
        // existed, so a key that mentions none of them must report exactly that.
        Assert.True(parsed.Options.IsDefault);
    }

    [Fact]
    public void TryParse_EffectStillKey_CarriesEveryPosingKnob()
    {
        var parsed = Assert.NotNull(Sts2SceneSubtreeStillKey.TryParse(
            CardHighlight
            + "&rect=0,0,759,951&shaderParam.width=0.075&modulate=1,1,1,0.98&particles=live&backdrop=black"));

        Assert.Equal(new SceneSubtreeRect(0f, 0f, 759f, 951f), parsed.Options.NodeLocalRect);
        Assert.Equal(0.075f, ShaderParameters(parsed)["width"]);
        Assert.Equal<float[]>([1f, 1f, 1f, 0.98f], [.. Modulate(parsed)]);
        Assert.True(parsed.Options.LiveParticles);
        Assert.True(parsed.Options.BlackBackdrop);
        Assert.False(parsed.Options.IsDefault);
    }

    [Theory]
    [InlineData("backdrop=black", true)]
    [InlineData("backdrop=BLACK", true)]
    [InlineData("backdrop=white", false)]
    [InlineData("backdrop=", false)]
    public void TryParse_BackdropToken_OnlyBlackOptsIn(string query, bool expected)
    {
        var parsed = Assert.NotNull(Sts2SceneSubtreeStillKey.TryParse($"{CardHighlight}&{query}"));

        Assert.Equal(expected, parsed.Options.BlackBackdrop);
        // A backdrop alone is a real request, so it must not read as "nothing was asked for" — that is what
        // decides whether the render branch keeps its historical byte-identical path.
        Assert.Equal(!expected, parsed.Options.IsDefault);
    }

    [Fact]
    public void TryParse_RootNodePath_AddressesTheSceneRoot()
    {
        // A rarity glow scene IS a single GPUParticles2D, so "." is the only way to name the node to render.
        var parsed = Assert.NotNull(Sts2SceneSubtreeStillKey.TryParse(
            "scene-subtree://res://scenes/vfx/uncommon_glow_vfx.tscn?node=.&particles=live"));

        Assert.Equal(".", parsed.NodePath);
        Assert.True(Sts2SceneSubtreeStillKey.AddressesSceneRoot(parsed.NodePath));
        Assert.False(Sts2SceneSubtreeStillKey.AddressesSceneRoot("CardContainer/Highlight"));
    }

    [Theory]
    // Uniform names are case-sensitive in Godot, so the parser must not fold them.
    [InlineData("shaderParam.Width=0.5", "Width")]
    [InlineData("shaderParam.ripple_speed=0.03", "ripple_speed")]
    public void TryParse_ShaderParameterNames_AreCasePreserving(string query, string expectedName)
    {
        var parsed = Assert.NotNull(Sts2SceneSubtreeStillKey.TryParse($"{CardHighlight}&{query}"));

        Assert.Contains(expectedName, ShaderParameters(parsed).Keys);
    }

    [Fact]
    public void TryParse_UnclampedModulate_IsKept()
    {
        // Godot's modulate is itself unclamped; refusing an over-1 tint here would be the parser inventing a rule
        // the engine does not have.
        var parsed = Assert.NotNull(Sts2SceneSubtreeStillKey.TryParse($"{CardHighlight}&modulate=2,0,0,1"));

        Assert.Equal(2f, Modulate(parsed)[0]);
    }

    [Theory]
    [InlineData("res://scenes/cards/card.tscn?node=X")]                 // no scheme
    [InlineData("scene-subtree://?node=X")]                             // no scene path
    [InlineData("scene-subtree://scenes/cards/card.tscn?node=X")]       // scene path is not a res:// resource
    [InlineData("scene-subtree://res://scenes/cards/card.tscn")]        // no query at all
    [InlineData("scene-subtree://res://scenes/cards/card.tscn?rect=0,0,8,8")] // query, but no node selector
    [InlineData("scene-subtree://res://scenes/cards/card.tscn?node=")]  // empty node selector
    public void TryParse_MalformedAddressing_IsRefusedWhole(string key)
    {
        Assert.Null(Sts2SceneSubtreeStillKey.TryParse(key));
    }

    [Theory]
    [InlineData("rect=0,0,759")]          // three components
    [InlineData("rect=0,0,759,nope")]     // non-numeric
    [InlineData("rect=0,0,0,951")]        // zero-area: a blank image, not a smaller one
    [InlineData("rect=0,0,-8,-8")]        // negative extent
    public void TryParse_MalformedRect_DropsOnlyTheRect(string query)
    {
        var parsed = Assert.NotNull(Sts2SceneSubtreeStillKey.TryParse($"{CardHighlight}&{query}&shaderParam.width=0.075"));

        Assert.Null(parsed.Options.NodeLocalRect);
        Assert.Equal(0.075f, ShaderParameters(parsed)["width"]);
    }

    [Theory]
    [InlineData("modulate=1,1,1")]        // three channels
    [InlineData("modulate=1,1,1,x")]      // non-numeric alpha
    public void TryParse_MalformedModulate_DropsOnlyTheModulate(string query)
    {
        var parsed = Assert.NotNull(Sts2SceneSubtreeStillKey.TryParse($"{CardHighlight}&{query}&particles=live"));

        Assert.Null(parsed.Options.Modulate);
        Assert.True(parsed.Options.LiveParticles);
    }

    [Theory]
    [InlineData("particles=frozen")]
    [InlineData("particles=")]
    public void TryParse_NonLiveParticlesToken_LeavesTheDefaultStabilization(string query)
    {
        var parsed = Assert.NotNull(Sts2SceneSubtreeStillKey.TryParse($"{CardHighlight}&{query}"));

        Assert.False(parsed.Options.LiveParticles);
    }

    [Fact]
    public void TryParse_ShaderParameterWithoutAName_IsIgnored()
    {
        var parsed = Assert.NotNull(Sts2SceneSubtreeStillKey.TryParse($"{CardHighlight}&shaderParam.=1"));

        Assert.True(parsed.Options.IsDefault);
    }

    [Fact]
    public void Describe_NamesWhatWasPosed()
    {
        var notes = Sts2SceneSubtreeStillKey.Describe(new SceneSubtreeStillOptions
        {
            ShaderParameters = new Dictionary<string, float> { ["width"] = 0.075f, ["ease"] = 0.005f },
            Modulate = [1f, 1f, 1f, 0.98f],
        });

        // Sorted, so the note is stable across dictionary iteration order.
        Assert.Contains(notes, note => note.Contains("ease=0.005, width=0.075"));
        Assert.Contains(notes, note => note.Contains("(1, 1, 1, 0.98)"));
    }

    [Fact]
    public void Describe_SaysTheAlphaChannelIsMeaninglessUnderABlackBackdrop()
    {
        // The note is the only place a consumer is told not to read the alpha channel of that artifact.
        var notes = Sts2SceneSubtreeStillKey.Describe(new SceneSubtreeStillOptions { BlackBackdrop = true });

        Assert.Contains(notes, note => note.Contains("black backdrop"));
    }

    [Fact]
    public void Describe_SaysNothingAboutKnobsThatWereNotUsed()
    {
        Assert.Empty(Sts2SceneSubtreeStillKey.Describe(new SceneSubtreeStillOptions { LiveParticles = true }));
    }
}
