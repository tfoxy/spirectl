#if ENABLE_STS2_LIVE_HOST
using Spirectl.Sts2;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Embedding;
using Spirectl.Sts2;
using Spirectl.Sts2.Live;
using Spirectl.Proto.V0;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class Sts2EmbeddableAssetProviderTests
{
    private const string EndTurnButtonPath = "res://scenes/gameplay/room/combat/control/n_end_turn_button.tscn";
    private const string CardFramePath = "res://resources/textures/cards/card_default.png";
    private const string BlockSparkVfxPath = "res://scenes/vfx/block_spark_vfx.tscn";

    [Fact]
    public void Sts2EmbeddableAssetProviderResolvesDirectResourceKeyAndPreservesRequestFields()
    {
        var extract = new CapturingExtractProvider();
        var provider = CreateProvider(extract);

        var result = provider.GetAsset(new EmbeddableAssetRequest(EndTurnButtonPath, "png", "req-123"));

        Assert.True(result.Success);
        Assert.NotNull(result.Payload);
        Assert.Equal("req-123", extract.LastRequest!.RequestId);
        Assert.Equal("png", extract.LastRequest.OutputFormat);
        Assert.Equal("resources", extract.LastRequest.SourceRoot);
        Assert.Equal(EndTurnButtonPath, extract.LastRequest.SourcePath);
        Assert.Equal(EndTurnButtonPath, extract.LastRequest.LoadPath);
        Assert.Equal(EndTurnButtonPath, result.Payload!.Key);
    }

    [Fact]
    public void Sts2EmbeddableAssetProviderBatchUsesSts2Resolver()
    {
        var extract = new CapturingExtractProvider();
        var provider = CreateProvider(extract);

        var batch = provider.GetAssets(new EmbeddableAssetBatchRequest([
            new EmbeddableAssetRequest(EndTurnButtonPath, "png", "one"),
            new EmbeddableAssetRequest(CardFramePath, "png", "two")
        ]));

        Assert.Equal("ok", batch.Status);
        Assert.Equal(2, extract.Requests.Count);
        Assert.Equal(EndTurnButtonPath, extract.Requests[0].LoadPath);
        Assert.Equal(CardFramePath, extract.Requests[1].LoadPath);
    }

    [Fact]
    public void Sts2EmbeddableAssetProviderResolvesResourceUri()
    {
        var extract = new CapturingExtractProvider();
        var provider = CreateProvider(extract);

        var result = provider.GetAsset(new EmbeddableAssetRequest("res://images/icon.ico", "auto", "icon"));

        Assert.True(result.Success);
        Assert.NotNull(extract.LastRequest);
        Assert.Equal("resources", extract.LastRequest!.SourceRoot);
        Assert.Equal("res://images/icon.ico", extract.LastRequest.SourcePath);
        Assert.Equal("res://images/icon.ico", extract.LastRequest.LoadPath);
        Assert.Equal("res://images/icon.ico", result.Payload!.Key);
    }

    [Fact]
    public void Sts2EmbeddableAssetProviderReturnsStructuredResolverError()
    {
        var provider = CreateProvider(new CapturingExtractProvider());

        var result = provider.GetAsset(new EmbeddableAssetRequest("status:missing:icon", "png", "req-x"));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal("invalid-asset-key", result.Error!.Code);
        Assert.Equal("key", result.Error.Field);
        Assert.Equal("status:missing:icon", result.Error.Value);
        Assert.NotNull(result.Error.Notices);
        Assert.Contains(result.Error.Notices!, n => n.Code == "invalid-asset-key");
    }

    [Fact]
    public void Sts2EmbeddableAssetProviderResolvesEventBackgroundToScenePath()
    {
        var extract = new CapturingExtractProvider();
        var provider = CreateProvider(extract);

        var result = provider.GetAsset(new EmbeddableAssetRequest("res://scenes/events/background_scenes/the_city.tscn", "png", "ev-1"));

        Assert.True(result.Success);
        Assert.NotNull(extract.LastRequest);
        Assert.Equal("resources", extract.LastRequest!.SourceRoot);
        Assert.Equal("res://scenes/events/background_scenes/the_city.tscn", extract.LastRequest.SourcePath);
        Assert.Equal("res://scenes/events/background_scenes/the_city.tscn", extract.LastRequest.LoadPath);
    }

    [Fact]
    public void Sts2EmbeddableAssetProviderEncounterRejectsUnsupportedTargetForm()
    {
        var provider = CreateProvider(new CapturingExtractProvider());

        var result = provider.GetAsset(new EmbeddableAssetRequest("encounter:kaiser_crab_boss:visuals", "png", "enc-err"));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal("unsupported-asset-kind", result.Error!.Code);
        Assert.Equal("encounter:kaiser_crab_boss:visuals", result.Error.Value);
    }

    [Fact]
    public void Sts2EmbeddableAssetProviderThreadsRenderSizeAndCompositionSelectorIntoSnapshot()
    {
        var extract = new CapturingExtractProvider();
        var provider = CreateProvider(extract);

        var sized = provider.GetAsset(new EmbeddableAssetRequest(
            "composed://combat-background/overgrowth/image",
            "png",
            "cbg-size",
            RenderWidth: 2520,
            RenderHeight: 1080,
            CompositionSelector: "res://scenes/backgrounds/overgrowth/layers/overgrowth_bg_00_c.tscn"));

        Assert.True(sized.Success);
        Assert.NotNull(extract.LastRequest);
        Assert.Equal(2520, extract.LastRequest!.RenderWidth);
        Assert.Equal(1080, extract.LastRequest.RenderHeight);
        Assert.Equal(
            "res://scenes/backgrounds/overgrowth/layers/overgrowth_bg_00_c.tscn",
            extract.LastRequest.CompositionSelector);

        var plain = provider.GetAsset(new EmbeddableAssetRequest(
            "composed://combat-background/overgrowth/image",
            "png",
            "cbg-default"));

        Assert.True(plain.Success);
        Assert.Null(extract.LastRequest!.RenderWidth);
        Assert.Null(extract.LastRequest.RenderHeight);
        Assert.Null(extract.LastRequest.CompositionSelector);
    }

    // R21 encoder knobs, same additive contract as the render size above: present => threaded through verbatim,
    // ABSENT => null/false, which is what makes "a request that omits them is byte-identical to before" checkable.
    [Fact]
    public void Sts2EmbeddableAssetProviderThreadsImageQualityAndOpacityIntoSnapshot()
    {
        var extract = new CapturingExtractProvider();
        var provider = CreateProvider(extract);

        var lossy = provider.GetAsset(new EmbeddableAssetRequest(
            "composed://combat-background/overgrowth/image",
            "webp",
            "cbg-lossy",
            RenderWidth: 2520,
            RenderHeight: 1080,
            ImageQuality: 0.85f,
            ImageOpaque: true));

        Assert.True(lossy.Success);
        Assert.Equal("webp", extract.LastRequest!.OutputFormat);
        Assert.Equal(0.85f, extract.LastRequest.ImageQuality!.Value, 4);
        Assert.True(extract.LastRequest.ImageOpaque);

        var plain = provider.GetAsset(new EmbeddableAssetRequest(
            "composed://combat-background/overgrowth/image",
            "png",
            "cbg-plain"));

        Assert.True(plain.Success);
        Assert.Null(extract.LastRequest!.ImageQuality);
        Assert.False(extract.LastRequest.ImageOpaque);
    }

    [Fact]
    public void Sts2EmbeddableAssetProviderPreservesResolvedProvenanceForVirtualAndResourceKeys()
    {
        var extract = new CapturingExtractProvider();
        var provider = CreateProvider(extract);

        var combatBackground = provider.GetAsset(new EmbeddableAssetRequest("composed://combat-background/overgrowth/image", "png", "cbg-1"));
        var catalogCombatBackground = provider.GetAsset(new EmbeddableAssetRequest("composed://combat-background/hive/image", "png", "cbg-2"));
        var catalogVfx = provider.GetAsset(new EmbeddableAssetRequest(BlockSparkVfxPath, "png", "vfx-1"));
        var encounterBackground = provider.GetAsset(new EmbeddableAssetRequest("composed://encounters/kaiser_crab_boss/background/image", "png", "enc-bg"));
        var characterBattlefield = provider.GetAsset(new EmbeddableAssetRequest("model://characters/ironclad/visuals", "png", "char-1"));
        var eventBackground = provider.GetAsset(new EmbeddableAssetRequest("res://scenes/events/background_scenes/the_city.tscn", "png", "ev-2"));

        Assert.True(combatBackground.Success);
        Assert.True(catalogCombatBackground.Success);
        Assert.True(catalogVfx.Success);
        Assert.True(encounterBackground.Success);
        Assert.True(characterBattlefield.Success);
        Assert.True(eventBackground.Success);
        Assert.Equal("composed", combatBackground.Payload!.Provenance.SourceRoot);
        Assert.Equal("composed://combat-background/overgrowth/image", combatBackground.Payload.Provenance.LoadPath);
        Assert.Equal("composed", catalogCombatBackground.Payload!.Provenance.SourceRoot);
        Assert.Equal("composed://combat-background/hive/image", catalogCombatBackground.Payload.Provenance.LoadPath);
        Assert.Equal("resources", catalogVfx.Payload!.Provenance.SourceRoot);
        Assert.Equal(BlockSparkVfxPath, catalogVfx.Payload.Provenance.LoadPath);
        Assert.Equal("composed", encounterBackground.Payload!.Provenance.SourceRoot);
        Assert.Equal("composed://encounters/kaiser_crab_boss/background/image", encounterBackground.Payload.Provenance.LoadPath);
        Assert.Equal("model", characterBattlefield.Payload!.Provenance.SourceRoot);
        Assert.Equal("model://characters/ironclad/visuals", characterBattlefield.Payload.Provenance.LoadPath);
        Assert.Equal("resources", eventBackground.Payload!.Provenance.SourceRoot);
        Assert.Equal("res://scenes/events/background_scenes/the_city.tscn", eventBackground.Payload.Provenance.LoadPath);
    }

    [Fact]
    public void Sts2EmbeddableAssetProviderScenePackageIncludesStructuredNotices()
    {
        var extract = new CapturingExtractProvider(
            explainResult: request => AssetExplainOperationResult.SuccessEncounterScenePackage(
                request.RequestId,
                DataSourceKind.Live,
                false,
                new AssetEncounterScenePackageSnapshot(
                    "1",
                    "kaiser_crab_boss",
                    new AssetEncounterViewportSnapshot(1920, 1080, "world"),
                    new AssetEncounterCameraSnapshot(1.0, new AssetVector2Snapshot(0, 0), "live", "live"),
                    new AssetEncounterBackgroundSnapshot("res://background.tscn", "combat-background:overgrowth:image", "combat-background:overgrowth:image"),
                    [],
                    [new AssetEncounterVisualPartSnapshot(
                        "rocket",
                        "rocket",
                        "right",
                        "right",
                        20,
                        new AssetCompositionRectSnapshot(1, 2, 3, 4),
                        new AssetEncounterSelectorDiagnosticSnapshot(
                            "rocket",
                            "_rightRocket",
                            ["_rightRocket", "rightRocket"],
                            [new AssetEncounterSelectorCandidateSnapshot("/root/KaiserCrab", "_rightRocket", "field", "resolved")],
                            new AssetEncounterResolvedNodeSnapshot("/root/KaiserCrab/RightRocket", "RightRocket", "Node2D"),
                            new AssetCompositionRectSnapshot(1, 2, 3, 4),
                            new AssetCompositionRectSnapshot(5, 6, 7, 8),
                            "resolved",
                            "rocket-charge-up",
                            "rocket-charge-up-overlay"))],
                    [],
                    [],
                    [new AssetEncounterRenderTargetSnapshot(
                        "rocket-charge-up-overlay",
                        "overlay",
                        "encounter:kaiser_crab_boss:visual-state:rocket-charge-up:overlay:image",
                        new AssetEncounterRenderTargetDecisionSnapshot(
                            "rocket-charge-up-overlay",
                            "overlay",
                            "rocket-charge-up",
                            string.Empty,
                            "hidden",
                            "Overlay hides non-state parts.",
                            ["rocket"]))],
                    [new AssetExplainNoticeSnapshot("package-warning", "warning", "$.states", "Package has optional overlays.", false)],
                    [new AssetEncounterSelectorDiagnosticSnapshot(
                        "missing",
                        "_missingPart",
                        ["_missingPart", "missingPart"],
                        [new AssetEncounterSelectorCandidateSnapshot("/root/KaiserCrab", "_missingPart", "field", "searched")],
                        null,
                        null,
                        null,
                        "unresolved",
                        "rocket-charge-up",
                        "rocket-charge-up-overlay")])));
        var provider = CreateProvider(extract);

        var result = provider.GetAsset(new EmbeddableAssetRequest("composed://encounters/kaiser_crab_boss/scene-package", "auto", "scene-1"));

        Assert.True(result.Success);
        Assert.NotNull(result.Payload);
        Assert.Equal("metadata", result.Payload!.ArtifactKind);
        Assert.Equal("application/json", result.Payload.ContentType);
        Assert.NotEmpty(result.Payload.Contents);
        Assert.Contains(result.Payload.Notices, n => n.Code == "package-warning");
        var json = System.Text.Encoding.UTF8.GetString(result.Payload.Contents);
        Assert.Contains("\"selectorDiagnostic\"", json, StringComparison.Ordinal);
        Assert.Contains("\"selectorDiagnostics\"", json, StringComparison.Ordinal);
        Assert.Contains("\"resolvedNode\"", json, StringComparison.Ordinal);
        Assert.Contains("\"localBounds\"", json, StringComparison.Ordinal);
        Assert.Contains("\"decision\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Sts2EmbeddableAssetProviderScenePackageExplainFailureMapsDetailNotices()
    {
        var extract = new CapturingExtractProvider(
            explainResult: request => AssetExplainOperationResult.Failure(
                request.RequestId,
                DataSourceKind.Live,
                false,
                AssetExtractFailureCode.RuntimeFailure,
                "Explain failed.",
                [new AssetExtractDetail("asset", "composed://encounters/kaiser_crab_boss/scene-package", "Missing encounter scene metadata.")]));
        var provider = CreateProvider(extract);

        var result = provider.GetAsset(new EmbeddableAssetRequest("composed://encounters/kaiser_crab_boss/scene-package", "auto", "scene-fail"));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal("runtime-failure", result.Error!.Code);
        Assert.Contains(result.Error.Notices!, n =>
            n.Code == "asset-explain-detail" &&
            n.Path == "asset" &&
            n.Message.Contains("Missing encounter scene metadata.", StringComparison.Ordinal) &&
            n.Message.Contains("field=asset", StringComparison.Ordinal) &&
            n.Message.Contains("value=composed://encounters/kaiser_crab_boss/scene-package", StringComparison.Ordinal));
    }

    [Fact]
    public void BridgeEmbeddableAssetProviderPreservesRasterPayloadMetadata()
    {
        var extract = new CapturingExtractProvider(
            extractResult: request => new AssetExtractOperationResult(
                request.RequestId,
                DataSourceKind.Live,
                false,
                "PNG",
                "image/png",
                64,
                32,
                [9, 8, 7],
                "flattened-static",
                ["source note"],
                new AssetExtractProvenanceSnapshot("resources", "res://icon.png", "res://icon.png", "texture-preview", "live"),
                [new AssetExtractNoticeSnapshot("format-normalized", "info", "Output format was normalized.", "format")],
                AssetExtractArtifactKind.Raster,
                0,
                [],
                null)
            {
                ExtractionMs = 42,
            });
        var provider = CreateProvider(extract);

        var result = provider.GetAsset(new EmbeddableAssetRequest(EndTurnButtonPath, "PNG", "payload-1"));

        Assert.True(result.Success);
        Assert.NotNull(result.Payload);
        Assert.Equal("payload-1", result.Payload!.RequestId);
        Assert.Equal(EndTurnButtonPath, result.Payload.Key);
        Assert.Equal("raster", result.Payload.ArtifactKind);
        Assert.Equal("png", result.Payload.Format);
        Assert.Equal("image/png", result.Payload.ContentType);
        Assert.Equal(64, result.Payload.Width);
        Assert.Equal(32, result.Payload.Height);
        Assert.Equal([9, 8, 7], result.Payload.Contents);
        Assert.Equal(3, result.Payload.ByteLength);
        Assert.Equal(42, result.Payload.ExtractionMs);
        Assert.Equal("resources", result.Payload.Provenance.SourceRoot);
        Assert.Equal("res://icon.png", result.Payload.Provenance.SourcePath);
        Assert.Equal("res://icon.png", result.Payload.Provenance.LoadPath);
        Assert.Equal("texture-preview", result.Payload.Provenance.RenderMode);
        Assert.Equal("live", result.Payload.Provenance.SourceKind);
        Assert.Contains(result.Payload.Notices, notice => notice.Code == "format-normalized" && notice.Path == "format");
        Assert.Contains(result.Payload.Notices, notice => notice.Code == "asset_extract_note" && notice.Message == "source note");
        Assert.Equal("png", extract.LastRequest!.OutputFormat);
    }

    [Fact]
    public void BridgeEmbeddableAssetProviderPreservesBrowserFontPayloadMetadata()
    {
        byte[] woff2Bytes = [(byte)'w', (byte)'O', (byte)'F', (byte)'2', 0, 1, 2, 3];
        var extract = new CapturingExtractProvider(
            extractResult: request => AssetExtractOperationResult.SuccessFont(
                request.RequestId,
                DataSourceKind.Live,
                false,
                "woff2",
                "font/woff2",
                woff2Bytes,
                "raw-font-file",
                ["font note"]) with
                {
                    ExtractionMs = 7,
                });
        var provider = CreateProvider(extract);

        var result = provider.GetAsset(new EmbeddableAssetRequest("res://fonts/kreon.woff2", "auto", "font-1"));

        Assert.True(result.Success);
        Assert.NotNull(result.Payload);
        Assert.Equal("font", result.Payload!.ArtifactKind);
        Assert.Equal("woff2", result.Payload.Format);
        Assert.Equal("font/woff2", result.Payload.ContentType);
        Assert.Equal(woff2Bytes, result.Payload.Contents);
        Assert.Equal(woff2Bytes.Length, result.Payload.ByteLength);
        Assert.Equal(7, result.Payload.ExtractionMs);
        Assert.Contains(result.Payload.Notices, notice => notice.Code == "asset_extract_note" && notice.Message == "font note");
    }

    [Fact]
    public void BridgeEmbeddableAssetProviderPreservesTimelineFramesAndManifest()
    {
        var extract = new CapturingExtractProvider(
            extractResult: request => AssetExtractOperationResult.SuccessTimeline(
                request.RequestId,
                DataSourceKind.Live,
                false,
                "webp",
                10,
                20,
                "animated-texture",
                150,
                [
                    new AssetExtractFrame(0, "webp", "image/webp", 10, 20, [1], 50),
                    new AssetExtractFrame(1, "webp", "image/webp", 10, 20, [2], 100),
                ],
                ["timeline note"]));
        var provider = CreateProvider(extract);

        var result = provider.GetAsset(new EmbeddableAssetRequest(EndTurnButtonPath, "webp", "timeline-1"));

        Assert.True(result.Success);
        Assert.NotNull(result.Payload);
        Assert.Equal("timeline", result.Payload!.ArtifactKind);
        Assert.Equal("application/json", result.Payload.ContentType);
        Assert.Equal(2, result.Payload.Frames.Count);
        Assert.Equal([1], result.Payload.Frames[0].Contents);
        Assert.Equal(100, result.Payload.Frames[1].DurationMs);
        var manifest = System.Text.Encoding.UTF8.GetString(result.Payload.Contents);
        Assert.Contains("\"artifactKind\":\"timeline\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"frameCount\":2", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void BridgeEmbeddableAssetProviderMapsExtractFailuresWithDiagnosticFieldNotices()
    {
        var extract = new CapturingExtractProvider(
            extractResult: request => AssetExtractOperationResult.Failure(
                request.RequestId,
                DataSourceKind.Live,
                false,
                AssetExtractFailureCode.BridgeNotAttached,
                "Live host is not attached.",
                [new AssetExtractDetail("live_host", "missing", "Rendering requires a live host.", new Dictionary<string, object?> { ["mainThread"] = true })]));
        var provider = CreateProvider(extract);

        var result = provider.GetAsset(new EmbeddableAssetRequest(EndTurnButtonPath, "png", "fail-1"));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal("bridge-not-attached", result.Error!.Code);
        Assert.Contains(result.Error.Notices!, notice =>
            notice.Code == "asset-extract-detail" &&
            notice.Path == "live_host" &&
            notice.Message.Contains("field=live_host", StringComparison.Ordinal) &&
            notice.Message.Contains("value=missing", StringComparison.Ordinal) &&
            notice.Message.Contains("diagnostic=mainThread", StringComparison.Ordinal));
    }

    [Fact]
    public void Sts2EmbeddableAssetProviderSupportsRepresentativeModelFamilies()
    {
        var extract = new CapturingExtractProvider();
        var provider = CreateProvider(extract);
        var directKeys = new[]
        {
            "res://resources/textures/icons/status_vulnerable.png",
            "res://resources/textures/icons/status_weak.png",
            "res://resources/textures/icons/status_strength.png",
            CardFramePath,
            EndTurnButtonPath,
            "res://resources/textures/icons/intent_attack.png",
            "res://images/atlases/card_atlas.sprites/status/slimed.tres",
            BlockSparkVfxPath
        };

        foreach (var key in directKeys)
        {
            var result = provider.GetAsset(new EmbeddableAssetRequest(key, "png", "rep-static"));

            Assert.True(result.Success, $"Expected key to resolve: {key}");
            Assert.NotNull(extract.LastRequest);
            Assert.Equal("resources", extract.LastRequest!.SourceRoot);
            Assert.StartsWith("res://", extract.LastRequest.LoadPath, StringComparison.Ordinal);
        }

        var dynamicKeys = new List<string>();
        try
        {
            dynamicKeys.Add($"model://cards/{FirstResolvable(["strike_r", "defend_r"], id => Sts2ModelResolver.TryResolveCard(id, out _))}/image");
            dynamicKeys.Add($"model://relics/{FirstResolvable(["burning-blood", "anchor"], id => Sts2ModelResolver.TryResolveRelic(id, out _))}/icon");
            dynamicKeys.Add($"model://potions/{FirstResolvable(["fire-potion", "block-potion"], id => Sts2ModelResolver.TryResolvePotion(id, out _))}/icon");
            dynamicKeys.Add($"model://potions/{FirstResolvable(["fire-potion", "block-potion"], id => Sts2ModelResolver.TryResolvePotion(id, out _))}/outline");
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            // Some test environments compile with live host enabled but do not initialize full ModelDb catalogs.
            return;
        }

        foreach (var key in dynamicKeys)
        {
            var result = provider.GetAsset(new EmbeddableAssetRequest(key, "png", "rep-dynamic"));

            Assert.True(result.Success, $"Expected key to resolve: {key}");
            Assert.NotNull(extract.LastRequest);
            Assert.Equal("resources", extract.LastRequest!.SourceRoot);
            Assert.StartsWith("res://", extract.LastRequest.LoadPath, StringComparison.Ordinal);
        }
    }

    private static string FirstResolvable(IEnumerable<string> candidates, Func<string, bool> tryResolve)
    {
        foreach (var candidate in candidates)
        {
            if (tryResolve(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"No resolvable candidate ids found: {string.Join(", ", candidates)}");
    }


    [Fact]
    public void Sts2EmbeddableAssetProviderRejectsExtraKeySegmentsForStrictFamilies()
    {
        var provider = CreateProvider(new CapturingExtractProvider());

        var result = provider.GetAsset(new EmbeddableAssetRequest("model://cards/strike_r/image:extra", "png", "bad-1"));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal("invalid-asset-key", result.Error!.Code);
    }

    [Theory]
    [InlineData("enemy::visual", "invalid-asset-key")]
    [InlineData("enemy:jaw-worm:visual", "invalid-asset-key")]
    [InlineData("status:not-a-real-status:icon", "invalid-asset-key")]
    [InlineData("combat-ui:not-a-real-control:image", "invalid-asset-key")]
    [InlineData("model:character:ironclad:selectIcon", "invalid-asset-key")]
    [InlineData("model:character:ironclad:selectBg", "invalid-asset-key")]
    [InlineData("composed:combat-background:overgrowth:image", "invalid-asset-key")]
    [InlineData("composed://encounters/not_a_real_encounter/background/image", "asset-key-not-found")]
    [InlineData("composed://encounters/kaiser_crab_boss/visuals", "unsupported-asset-kind")]
    public void Sts2EmbeddableAssetProviderReturnsStableResolverCodesForUnsupportedKnownFamilies(string key, string code)
    {
        var provider = CreateProvider(new CapturingExtractProvider());

        var result = provider.GetAsset(new EmbeddableAssetRequest(key, "png", "bad-known-family"));

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(code, result.Error!.Code);
        Assert.Equal("key", result.Error.Field);
        Assert.Equal(key.Trim(), result.Error.Value);
    }

    /// <summary>
    /// Pins the SHAPE of the asset payload to SpirectlSts2Runtime.AssetPayloadVersion.
    ///
    /// <para>Embedders cache asset bytes keyed by a generation number, and before this constant existed that
    /// number was maintained by hand on the CONSUMER side — bumped 12 times by reading spirectl's commits. A
    /// missed bump is not a build error anywhere: the consumer serves stale bytes of the previous shape
    /// indefinitely, and the only symptom is a picture that is quietly wrong.</para>
    ///
    /// <para>So the pin is here, on the producing side, and it is deliberately BROAD: one payload per artifact
    /// kind, asserted field-by-field, plus the reflected member list of every record on the payload graph. Any
    /// change to what a cached payload contains fails this test, and the fix is to update the pin AND bump the
    /// constant in the same change (that is the whole protocol — the assertion on the constant at the end is what
    /// makes updating the pin without bumping impossible to do silently).</para>
    /// </summary>
    [Fact]
    public void AssetPayloadShapesArePinnedToAssetPayloadVersion()
    {
        // Shape: the members a consumer can persist, per record on the payload graph.
        Assert.Equal(
            "ArtifactKind, ByteLength, ClipLocalHeight, ClipLocalWidth, ClipLocalX, ClipLocalY, ContentType, "
            + "Contents, DurationMs, ExtractionMs, Format, Frames, Height, Key, Notices, Provenance, RequestId, Width",
            MemberSignature<EmbeddableAssetPayload>());
        Assert.Equal(
            "CanvasHeight, CanvasWidth, ContentType, Contents, DurationMs, Format, Height, Index, OffsetX, "
            + "OffsetY, Width",
            MemberSignature<EmbeddableAssetFrame>());
        Assert.Equal(
            "LoadPath, RenderMode, SourceKind, SourcePath, SourceRoot",
            MemberSignature<EmbeddableAssetProvenance>());
        Assert.Equal("Code, Message, Path, Severity", MemberSignature<EmbeddableAssetNotice>());

        // Raster: bytes straight through, kind/format/content-type normalized to lower case.
        var raster = RequirePayload(CreateProvider(new CapturingExtractProvider(
            extractResult: request => AssetExtractOperationResult.Success(
                request.RequestId,
                DataSourceKind.Live,
                false,
                "PNG",
                4,
                2,
                [7, 7, 7],
                "flattened-static",
                []))).GetAsset(new EmbeddableAssetRequest(EndTurnButtonPath, "PNG", "pin-raster")));
        Assert.Equal("raster", raster.ArtifactKind);
        Assert.Equal("png", raster.Format);
        Assert.Equal(4, raster.Width);
        Assert.Equal(2, raster.Height);
        Assert.Equal([7, 7, 7], raster.Contents);
        Assert.Equal(3, raster.ByteLength);
        Assert.Empty(raster.Frames);

        // Timeline: per-frame images plus a synthesized JSON manifest in Contents.
        var timeline = RequirePayload(CreateProvider(new CapturingExtractProvider(
            extractResult: request => AssetExtractOperationResult.SuccessTimeline(
                request.RequestId,
                DataSourceKind.Live,
                false,
                "webp",
                10,
                20,
                "animated-texture",
                150,
                [new AssetExtractFrame(0, "webp", "image/webp", 10, 20, [1], 50, 3, 4, 30, 40)],
                []))).GetAsset(new EmbeddableAssetRequest(EndTurnButtonPath, "webp", "pin-timeline")));
        Assert.Equal("timeline", timeline.ArtifactKind);
        // As SHIPPED today: the payload's ContentType describes the FRAME images, while Contents carries a JSON
        // manifest. A pin records what a cache would actually store, not what the shape ought to be — so if this
        // pair is ever reconciled, this assertion is the thing that forces the version bump.
        Assert.Equal("image/webp", timeline.ContentType);
        Assert.Equal(150, timeline.DurationMs);
        var frame = Assert.Single(timeline.Frames);
        Assert.Equal(0, frame.Index);
        Assert.Equal("webp", frame.Format);
        Assert.Equal(50, frame.DurationMs);
        Assert.Equal((3, 4, 30, 40), (frame.OffsetX, frame.OffsetY, frame.CanvasWidth, frame.CanvasHeight));
        Assert.Contains(
            "\"artifactKind\":\"timeline\"",
            System.Text.Encoding.UTF8.GetString(timeline.Contents),
            StringComparison.Ordinal);

        // Font: a raw font binary, served under its own kind rather than as a raster.
        var font = RequirePayload(CreateProvider(new CapturingExtractProvider(
            extractResult: request => AssetExtractOperationResult.SuccessFont(
                request.RequestId,
                DataSourceKind.Live,
                false,
                "woff2",
                "font/woff2",
                [(byte)'w', (byte)'O', (byte)'F', (byte)'2'],
                "raw-font-file",
                []))).GetAsset(new EmbeddableAssetRequest("res://fonts/kreon.woff2", "auto", "pin-font")));
        Assert.Equal("font", font.ArtifactKind);
        Assert.Equal("woff2", font.Format);
        Assert.Equal("font/woff2", font.ContentType);
        Assert.Equal(4, font.ByteLength);

        // The bump protocol. A change above without a change here is exactly the silent staleness this guards.
        Assert.Equal(13, SpirectlSts2Runtime.AssetPayloadVersion);
        Assert.Equal(
            SpirectlSts2Runtime.AssetPayloadVersion,
            new EmbeddableRuntimeCapabilities(
                "spirectl/v0",
                "sts2-live",
                "test",
                "embedded",
                RuntimeAttachmentState.Attached,
                DataSourceKind.Live,
                false,
                [],
                []).AssetPayloadVersion);
    }

    private static EmbeddableAssetPayload RequirePayload(EmbeddableAssetResult result)
    {
        Assert.True(result.Success, result.Error?.Message ?? "asset request failed");
        Assert.NotNull(result.Payload);
        return result.Payload!;
    }

    private static string MemberSignature<T>()
        => string.Join(
            ", ",
            typeof(T).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Select(property => property.Name)
                .Where(name => name != "EqualityContract")
                .OrderBy(name => name, StringComparer.Ordinal));

    private static Sts2EmbeddableAssetProvider CreateProvider(IAssetExtractProvider provider)
        => new(provider, provider);

    private static BridgeRuntime CreateRuntime(IAssetExtractProvider extractProvider)
    {
        var scaffold = BridgeRuntimeBootstrap.CreateScaffold();
        return BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = scaffold.StateExtractor,
            ActionHandler = scaffold.ActionHandler,
            LogStream = scaffold.LogStream,
            PerspectiveProvider = scaffold.PerspectiveProvider,
            FixtureLoader = scaffold.FixtureLoader,
            ScreenshotProvider = scaffold.ScreenshotProvider,
            AssetExtractProvider = extractProvider,
            BridgeHost = scaffold.BridgeHost,
        });
    }

    private sealed class CapturingExtractProvider(
        Func<AssetExplainRequestSnapshot, AssetExplainOperationResult>? explainResult = null,
        Func<AssetExtractRequestSnapshot, AssetExtractOperationResult>? extractResult = null) : AssetOperationFallbackPorts, IAssetExtractProvider
    {
        private readonly Func<AssetExplainRequestSnapshot, AssetExplainOperationResult> _explainResult =
            explainResult ?? (request => AssetExplainOperationResult.Failure(request.RequestId, DataSourceKind.Live, false, AssetExtractFailureCode.NotImplemented, "not implemented", []));
        private readonly Func<AssetExtractRequestSnapshot, AssetExtractOperationResult> _extractResult =
            extractResult ?? (request => AssetExtractOperationResult.Success(request.RequestId, DataSourceKind.Live, false, request.OutputFormat, 1, 1, [1, 2, 3], "extract", []));

        public AssetExtractRequestSnapshot? LastRequest { get; private set; }
        public List<AssetExtractRequestSnapshot> Requests { get; } = [];

        public AssetExtractOperationResult Extract(AssetExtractRequestSnapshot request)
        {
            LastRequest = request;
            Requests.Add(request);
            return _extractResult(request);
        }

        public AssetExplainOperationResult Explain(AssetExplainRequestSnapshot request) => _explainResult(request);
    }
}
#endif
