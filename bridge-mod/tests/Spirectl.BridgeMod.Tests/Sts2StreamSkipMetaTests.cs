using Spirectl.Sts2.Core.SceneInspection;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// WS-0 embedder mirror-stream opt-out — the pure subtree-skip predicate (Sts2StreamSkipMeta.ShouldSkip) and the
// published metadata KEY. The runtime scene watcher streams every node under the game root to mirror clients,
// including the UI a downstream mod injects into the live tree (the CouchCoop host-lobby "Show Couch Co-Op QR Code"
// button and its dialog, AddChild'd onto NCharacterSelectScreen / NMultiplayerLoadGameScreen). Those are HOST-LOCAL
// chrome, and because mirror clients drive the host with REAL injected input, a phone tapping the mirrored button
// would open the dialog on the host's TV — so the filter must live in the producer. Truth table over
// {enabled}x{hasMeta} plus the key-string contract (the embedder stamps the literal, so a rename is a wire break).
public sealed class Sts2StreamSkipMetaTests
{
    // The exact contract: skip ⇔ the feature is enabled AND the node carries the metadata key.
    private static bool Expected(bool enabled, bool hasMeta) => enabled && hasMeta;

    [Fact]
    public void TruthTable_SkipOnlyWhenEnabledAndStamped()
    {
        var bools = new[] { false, true };
        foreach (var enabled in bools)
        {
            foreach (var hasMeta in bools)
            {
                var actual = Sts2StreamSkipMeta.ShouldSkip(enabled, hasMeta);
                var expected = Expected(enabled, hasMeta);
                Assert.True(
                    actual == expected,
                    $"enabled={enabled} hasMeta={hasMeta}: expected {expected}, got {actual}");
            }
        }
    }

    [Fact]
    public void StampedSubtreeRoot_IsSkipped()
    {
        // The embedder stamps the injected root (SetMeta before AddChild) — the walk returns before descending, so
        // neither the root nor anything under it is ever tracked or emitted.
        Assert.True(Sts2StreamSkipMeta.ShouldSkip(enabled: true, hasMeta: true));
    }

    [Fact]
    public void UnstampedNodes_KeepStreaming()
    {
        // Every game-authored node is unstamped, so the mirror wire is unchanged for the entire real scene tree.
        Assert.False(Sts2StreamSkipMeta.ShouldSkip(enabled: true, hasMeta: false));
    }

    [Fact]
    public void Disabled_KillSwitch_NeverSkips()
    {
        // SPIRECTL_SCENE_WATCH_HONOR_STREAM_SKIP=0 → the pre-fix stream, byte-identical, even for a stamped subtree.
        foreach (var hasMeta in new[] { false, true })
        {
            Assert.False(Sts2StreamSkipMeta.ShouldSkip(enabled: false, hasMeta));
        }
    }

    [Fact]
    public void MetaKey_IsThePublishedEmbedderContract()
    {
        // Embedders write the LITERAL string in their own assembly (CouchCoop: SetMeta("spirectl_stream_skip", true)),
        // so this constant is a cross-repo contract, not an implementation detail. Renaming it silently re-exposes
        // every previously excluded subtree to mirror clients — hence a hard assertion on the exact value.
        Assert.Equal("spirectl_stream_skip", Sts2StreamSkipMeta.MetaKey);
    }

    [Fact]
    public void PublishedStreamSkipMetaKey_IsTheSameStringTheWatcherReads()
    {
        // The embedder-facing constant lives on the PUBLIC SpirectlSceneStreamMeta (an embedder cannot see the
        // internal Sts2StreamSkipMeta at all). Assert the literal here too, and that the producer's own spelling is
        // an alias of it rather than a second copy — a divergence would mean the stamp an embedder writes by
        // reference is not the key the walk probes for, and the filter would fail OPEN with nothing failing.
        Assert.Equal("spirectl_stream_skip", SpirectlSceneStreamMeta.StreamSkipMetaKey);
        Assert.Equal(SpirectlSceneStreamMeta.StreamSkipMetaKey, Sts2StreamSkipMeta.MetaKey);
    }
}
