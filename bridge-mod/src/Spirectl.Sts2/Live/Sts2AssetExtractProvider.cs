using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using Spirectl.Sts2.Core.Artifacts;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2;
using Spirectl.Sts2.Live.EncounterVisuals;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace Spirectl.Sts2.Live;

public sealed partial class Sts2AssetExtractProvider : IAssetExtractProvider
{
    // Aliases, not copies: the canonical values live in the Godot-free Sts2SceneFitFrame so the fit algebra can
    // be unit-tested without the game, and two constants that must agree cannot drift if there is only one.
    private const int DefaultSceneSize = Sts2SceneFitFrame.DefaultSceneSize;
    private const int MaxSceneSize = Sts2SceneFitFrame.MaxSceneSize;
    private const int Padding = Sts2SceneFitFrame.Padding;
    private const int SpineCropPadding = 16;
    private const bool CharacterBattlefieldNormalizePreviewAlpha = true;
    private const bool CharacterBattlefieldDisableProcessDuringCapture = false;
    private const string CharacterBattlefieldAlphaNormalizationNote =
        "Enabled transparent-background alpha normalization for model-backed battlefield character previews.";
    private const bool CharacterSelectBgSpineStillNormalizePreviewAlpha = true;
    private const string CharacterSelectBgSpineStillAlphaNormalizationNote =
        "Enabled transparent-background alpha normalization for character-select background Spine still previews.";
    private const bool EventBackgroundSpineStillNormalizePreviewAlpha = false;
    private const string EventBackgroundSpineStillAlphaNormalizationNote =
        "Preserved transparent-background alpha from the captured event background Spine still.";
    private const int SkeletonPreviewBoundsSize = 1024;
    private const float CharacterSelectBackgroundScale = 1.1f;
    private const float CharacterSelectBackgroundWidth = 2560f;
    private const float CharacterSelectBackgroundHeight = 1200f;
    private const float CharacterSelectBackgroundOffsetX = -388f;
    private const float CharacterSelectBackgroundOffsetY = -80f;
    private const float CombatBackgroundContainerScale = 0.9f;
    private const float CombatBackgroundContainerOffsetX = 23f;
    private const string SkeletonResourceRenderMode = "flattened-spine-skeleton-resource-preview";
    private const string EventBackgroundScenePrefix = "res://scenes/events/background_scenes/";
    private const string CombatBackgroundScenePrefix = "res://scenes/backgrounds/";
    private const string CombatBackgroundRenderMode = "flattened-combat-background-scene";
    private const string CharacterSelectBgSpineStillRenderMode = "flattened-character-select-bg-spine-still";
    private const string EventBackgroundSpineStillRenderMode = "flattened-event-background-spine-still";
    // WS-NEOW: the composed in-situ event-background animated clip lane (distinct render mode so a caller/probe can
    // tell it apart from the generic spine-character-clip and the 1-frame still).
    private const string EventBackgroundSpineClipRenderMode = "composed-event-background-spine-clip";
    // WS-NEOW: scene-root-space padding grown around the composed clip's union bounds so animation edges + AA don't
    // clip at the cell border (per-frame tight-crop then trims each frame within the padded cell).
    private const float EventBgClipPadding = 24f;
    private const string ComposedCombatBackgroundRenderMode = "flattened-combat-background-composed";
    private const string EncounterBackgroundRenderMode = "flattened-encounter-background";
    private const string EncounterVisualOverlayRenderMode = "flattened-encounter-visual-overlay";
    private const string EncounterVisualPartRenderMode = "flattened-encounter-visual-part";
    private const string SpineCharacterClipRenderMode = "spine-character-clip";
    // Character Spine animation clips are sampled at a fixed cadence (no get_fps binding is
    // exposed by this spine-godot build) and bounded so a long/looping clip cannot produce an
    // unbounded frame stream.
    private const int SpineClipSampleFps = 30;
    private const int SpineClipMaxFrames = 300;
    // A clip whose decoded footprint (canvas px × frame count) exceeds this collapses to a single STILL frame.
    // The browser holds every decoded frame (ImageBitmap) to blit without re-decoding, so a huge many-frame
    // clip — the animated main-menu Logo is 1703×918 × 300f ≈ 1.9GB decoded — would exhaust memory and can't be
    // frame-swapped smoothly. Full-screen backgrounds (caught separately by the dimension cap) and such heavy
    // clips render as flicker-free stills; creatures (≈946×1023 × ~17f ≈ 16M px) stay well under and animate.
    // ~96M px ≈ 384MB decoded RGBA. (A future enhancement could stream these as a looping VIDEO instead.)
    private const long SpineClipMaxDecodedPixels = 96_000_000L;
    // #4: rest bounds beyond this (px, either dimension) are a GENUINE full-screen background — collapse to a
    // flicker-free still. Between MaxSceneSize (2048) and this are large CREATURES (the waterfall giant is a
    // ~2340px rig): they used to trip the old 2048 background cap and freeze to ONE frame; now they downscale
    // and keep animating. Above this (true backdrops) still collapse.
    private const int SpineClipCreatureCeiling = 4096;
    // #4: the floor a heavy clip's frames may be downscaled to (per axis) so its decoded footprint fits the
    // budget without collapsing to a single still. shrink = sqrt(budget / footprint) clamped to this. At 0.35 a
    // frame keeps ~12% of its pixels — plenty for a lower-res-but-animating creature; the client cancels the
    // fit-scale so it still displays at true size.
    private const float MinSpineClipDownscale = 0.35f;
    // Number of independent character clones rendered side-by-side per game-frame. One process-frame
    // rebuilds all lanes' meshes, so we capture this many clip frames per render wait — keeping clip
    // production comfortably ahead of real-time playback so a live stream never starves.
    private const int SpineClipBatchSize = 2;

    // #4 downscale-instead-of-collapse (default ON; SPIRECTL_SPINE_CLIP_DOWNSCALE=0 restores the legacy
    // freeze-to-one-still). Read once (env vars are process-stable). Kill-switch for A/B + safety.
    private static readonly bool DownscaleOversizedClips =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_SPINE_CLIP_DOWNSCALE") ?? "1")
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    // #3 clip-lane skin UNION fallback (default ON; SPIRECTL_SPINE_CLIP_SKIN_UNION=0 disables). When the client
    // captured NO runtime skin (&skin= absent) and the skeleton has MORE THAN ONE skin, the clip lane unions all
    // skins so slots whose attachment lives only in a non-default skin still render. The live Fossil Stalker /
    // Skulking Colony wear a runtime skin that never touches the node's `skin` property (empty capture), so the
    // offline clip rendered skin-less = only the setup-pose legs, no core body. Single-skin skeletons keep the
    // default skin untouched (guarded on skins.Count > 1), so ordinary creatures are byte-identical.
    private static readonly bool UnionClipLaneSkins =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_SPINE_CLIP_SKIN_UNION") ?? "1")
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    // WS-NEOW composed event-background clip lane (default ON; SPIRECTL_SPINE_EVENTBG_COMPOSED_CLIP=0 restores
    // today's routing where an animated event-bg spine falls to the generic detached tight-crop renderer — the
    // uncentered+blurry path — the A/B lever). Read once (env vars are process-stable).
    private static readonly bool EventBgComposedClipEnabled =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_SPINE_EVENTBG_COMPOSED_CLIP") ?? "1")
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    // WS-NEOW full-res composed event-bg capture (default ON). SPIRECTL_SPINE_EVENTBG_FULLRES=0 folds a decoded
    // footprint budget INTO the composed lane's capture scale (uniform shrink, stays centered — the phone-memory
    // fallback). SPIRECTL_SPINE_CLIP_DOWNSCALE (the creature #4 lever) is untouched; event backgrounds never reach it.
    private static readonly bool EventBgFullResCapture =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_SPINE_EVENTBG_FULLRES") ?? "1")
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    // WS5 char-select still placement (default ON; SPIRECTL_SPINE_CHARSELECT_PLACEMENT=0 restores the documented
    // latent null placement). The char-select background-still lane used to return NO ClipPlacement, so
    // WrapStillImageAsTimeline forwarded null, the wire shipped ClipLocalWidth=0 and the web client's
    // `scale = localWidth / canvasWidth` computed scale(0) — the char-select background rendered as NOTHING.
    private static readonly bool CharacterSelectStillPlacementEnabled =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_SPINE_CHARSELECT_PLACEMENT") ?? "1")
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    // R10 char-select still OVERSCAN (default ON; SPIRECTL_SPINE_CHARSELECT_OVERSCAN=0 restores the bare
    // root-viewport capture). The char-select rig is authored 2560x1200 inside a 1.1-scaled AnimatedBg container
    // that reaches from -516 to 2300 in screen space, so a capture at the 1920x1080 ROOT VIEWPORT physically
    // lacks the side pixels — a mirror stage wider than 16:9 then has nothing to show beside the still (the
    // menu backdrop shows through on the right) and no client-side trick can invent them. Capturing over the
    // authored container extent instead (and composing the node-local placement against THAT rect) hands the
    // client exactly the pixels the game itself would draw if its own window were that wide.
    private static readonly bool CharacterSelectStillOverscanEnabled =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_SPINE_CHARSELECT_OVERSCAN") ?? "1")
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    // WS5 generic clip-lane composite width cap (px). The generic lane renders SpineClipBatchSize lanes
    // side-by-side into ONE SubViewport (`viewport.Size = cellWidth * laneCount`). A full-bleed rig fits to
    // MaxSceneSize (2048) + 2*Padding = a 2176px cell, so 2 lanes ask for a 4352px render target: past the cap
    // the resize is refused/clamped, `GetImage()` reads back NARROWER than the composite, and `Image.get_region`
    // (which allocates the REQUESTED size and blits the clipped source) silently returns a lane cell whose right
    // side is transparent — the "merchant shows only the LEFT side of its background" defect. Lanes are a
    // throughput knob only, so capping them is always safe. SPIRECTL_SPINE_CLIP_MAX_COMPOSITE_WIDTH overrides.
    // WS5 lane-cap kill switch (default ON; SPIRECTL_SPINE_CLIP_LANE_CAP=0 restores the legacy
    // "always lay out SpineClipBatchSize lanes" behaviour — the A/B lever for the merchant left-truncation).
    private static readonly bool SpineClipLaneCapEnabled =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_SPINE_CLIP_LANE_CAP") ?? "1")
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    private static readonly int SpineClipMaxCompositeWidth =
        int.TryParse(
            System.Environment.GetEnvironmentVariable("SPIRECTL_SPINE_CLIP_MAX_COMPOSITE_WIDTH"),
            out var parsedMaxCompositeWidth)
        && parsedMaxCompositeWidth > 0
            ? parsedMaxCompositeWidth
            : 4096;

    // R19 texture render-size honouring (default ON; SPIRECTL_TEXTURE_RENDER_RESIZE=0 restores the pre-R19
    // behaviour where a texture extract ignores RenderWidth/RenderHeight outright and always returns the
    // source resolution). A request that leaves both null is byte-identical either way — the resize is
    // reached only when a caller asks for a specific pixel size. See ApplyRequestedTextureResize.
    private static readonly bool TextureRenderResizeEnabled =
        (System.Environment.GetEnvironmentVariable("SPIRECTL_TEXTURE_RENDER_RESIZE") ?? "1")
            .Trim().ToLowerInvariant() is not ("0" or "false" or "off" or "no");

    private readonly ILogStream _logStream;

    private sealed record CharacterVisualRequest(string CharacterId, string Variant);

    // spine://<scene>?node=<sceneRelativePath>&anim=<name>[&codec=&fps=&q=]: render a SpineSprite (addressed
    // by its scene + scene-relative node path) playing the named animation. NodePath null => the sole/first
    // SpineSprite in the scene. Codec/Fps/Quality are the SERVER-SIDE SIZE policy (a host appends them to the
    // key so they ride the cache key); the default (png, full sample rate) is what the bare CLI sees.
    //   Codec   = "png" (lossless) | "webp" (lossy at Quality).
    //   Fps     = target sample rate; 0 = the full SpineClipSampleFps. Sampling at a lower rate renders FEWER
    //             frames (smaller clip + faster render), not a re-sample of a full render.
    //   Quality = webp lossy quality (0..1].
    private sealed record SpineClipRequest(
        string SceneResPath,
        string? NodePath,
        string AnimationName,
        string Codec = "png",
        int Fps = 0,
        float Quality = 0.85f,
        // `&still=1`: render a SINGLE frame (a static pose) instead of the full clip. The low-end/mobile mirror
        // tiers request this so a weak device shows the character as one small image (an "is something here"
        // indicator) without downloading/decoding a multi-MB animated clip.
        bool Still = false,
        // `&skin=<name>` (#3): the runtime skin the live SpineSprite wore, applied to every render lane
        // (FindSkin → SetSkin → SetSlotsToSetupPose) so a creature whose skin is set at runtime (Fossil Stalker /
        // Skulking Colony — no authored skin in the .tscn) renders its full body. Null → no override (the scene
        // instantiate's authored skin, today's behavior).
        string? SkinName = null,
        // `&skel=<res-path>` (#8): a res:// skeleton-data resource path for the STANDALONE fallback lane, used
        // only when the scene/node address does not resolve offline (a dynamically-spawned spine — the treasure
        // chest). Null → scene-addressed render only. Validated to a res:// prefix at parse time.
        string? SkelResPath = null,
        // `&mat=<signature>` (R9): the SHADER-MATERIAL signature the producer minted for the addressed LIVE node
        // (Sts2SpineMaterialKey). It has always ridden the clip URL as a cache discriminator; the render now also
        // RESOLVES it (Sts2SpineLiveMaterials) to recover the live uniform values, so the tint the clip is cached
        // under is the tint it is rendered with. Null / unknown → the offline scene material (round-8 behaviour).
        string? MaterialKey = null,
        // `&t=<seconds>` (R10): the animation time a STILL should sample, overriding Sts2SpineStillFrame's mid/end
        // heuristic. A client sends it only for a track the game has PAUSED (SetTimeScale(0)) — the treasure chest
        // frozen at t=0 of its lid-opening "animation" — where the frozen track time IS the authoritative pose and
        // the mid-clip guess renders a half-open chest on an untouched room. Meaningless for an animated clip (only
        // a collapsed still samples a single time) and clamped into the clip's duration. Null → the heuristic, i.e.
        // today's byte-identical bake for every request that omits it.
        float? StillTime = null);

    private sealed record RelicVisualRequest(string RelicId, string Variant);

    private sealed record CombatBackgroundAliasRequest(string BackgroundId, string RootScenePath);

    private sealed record EncounterScenePackageRequest(string EncounterId);

    private sealed record EncounterRenderTargetRequest(
        string EncounterId,
        string Kind,
        string? StateId,
        string? PartId);

    private sealed record EncounterRenderDiagnostics(
        string RequestId,
        string RenderTargetId,
        string RenderMode,
        string RootNodePath,
        string RootNodeType,
        IReadOnlyList<AssetEncounterSelectorDiagnosticSnapshot> SelectorDiagnostics,
        IReadOnlyList<string> HiddenPartIds,
        IReadOnlyList<string> KeptPartIds,
        Vector2I? ViewportSize,
        Vector2? FramePosition,
        Vector2? FrameScale,
        IReadOnlyList<string> CaptureTimingNotes,
        bool ReadyHookRan,
        bool SpinePreviewSetupRan,
        EncounterAlphaEvidence? AlphaEvidence,
        EncounterRgbEvidence? RgbEvidence,
        bool RgbNonzeroBeforeAlphaNormalization,
        AssetEncounterRenderTargetDecisionSnapshot? RenderTargetDecision)
    {
        public IReadOnlyDictionary<string, object?> ToDictionary()
            => new Dictionary<string, object?>
            {
                ["requestId"] = RequestId,
                ["renderTargetId"] = RenderTargetId,
                ["renderMode"] = RenderMode,
                ["rootNodePath"] = RootNodePath,
                ["rootNodeType"] = RootNodeType,
                ["selectorDiagnostics"] = SelectorDiagnostics.Select(diagnostic => Sts2AssetExtractProvider.ToDictionary(diagnostic)).Cast<object?>().ToList(),
                ["hiddenPartIds"] = HiddenPartIds.Cast<object?>().ToList(),
                ["keptPartIds"] = KeptPartIds.Cast<object?>().ToList(),
                ["viewportSize"] = ViewportSize is { } viewportSize
                    ? new Dictionary<string, object?> { ["width"] = viewportSize.X, ["height"] = viewportSize.Y }
                    : null,
                ["framePosition"] = FramePosition is { } framePosition
                    ? new Dictionary<string, object?> { ["x"] = framePosition.X, ["y"] = framePosition.Y }
                    : null,
                ["frameScale"] = FrameScale is { } frameScale
                    ? new Dictionary<string, object?> { ["x"] = frameScale.X, ["y"] = frameScale.Y }
                    : null,
                ["captureTimingNotes"] = CaptureTimingNotes.Cast<object?>().ToList(),
                ["readyHookRan"] = ReadyHookRan,
                ["spinePreviewSetupRan"] = SpinePreviewSetupRan,
                ["alphaEvidence"] = AlphaEvidence?.ToDictionary(),
                ["rgbEvidence"] = RgbEvidence?.ToDictionary(),
                ["rgbNonzeroBeforeAlphaNormalization"] = RgbNonzeroBeforeAlphaNormalization,
                ["renderTargetDecision"] = RenderTargetDecision is null ? null : Sts2AssetExtractProvider.ToDictionary(RenderTargetDecision),
            };
    }

    private sealed record EncounterAlphaEvidence(
        double TransparentPixelRatio,
        bool HasVisiblePixels,
        AssetCompositionRectSnapshot? VisibleAlphaBounds)
    {
        public IReadOnlyDictionary<string, object?> ToDictionary()
            => new Dictionary<string, object?>
            {
                ["transparentPixelRatio"] = TransparentPixelRatio,
                ["hasVisiblePixels"] = HasVisiblePixels,
                ["visibleAlphaBounds"] = VisibleAlphaBounds is null ? null : Sts2AssetExtractProvider.ToDictionary(VisibleAlphaBounds),
            };
    }

    private sealed record EncounterRgbEvidence(int NonzeroPixelCount, double NonzeroPixelRatio)
    {
        public IReadOnlyDictionary<string, object?> ToDictionary()
            => new Dictionary<string, object?>
            {
                ["nonzeroPixelCount"] = NonzeroPixelCount,
                ["nonzeroPixelRatio"] = NonzeroPixelRatio,
            };
    }

    private static IReadOnlyDictionary<string, object?> ToDictionary(AssetCompositionRectSnapshot rect)
        => new Dictionary<string, object?>
        {
            ["x"] = rect.X,
            ["y"] = rect.Y,
            ["width"] = rect.Width,
            ["height"] = rect.Height,
        };

    private static IReadOnlyDictionary<string, object?> ToDictionary(AssetEncounterResolvedNodeSnapshot node)
        => new Dictionary<string, object?>
        {
            ["path"] = node.Path,
            ["name"] = node.Name,
            ["type"] = node.Type,
        };

    private static IReadOnlyDictionary<string, object?> ToDictionary(AssetEncounterSelectorCandidateSnapshot candidate)
        => new Dictionary<string, object?>
        {
            ["path"] = candidate.Path,
            ["selector"] = candidate.Selector,
            ["source"] = candidate.Source,
            ["status"] = candidate.Status,
        };

    private static IReadOnlyDictionary<string, object?> ToDictionary(AssetEncounterSelectorDiagnosticSnapshot diagnostic)
        => new Dictionary<string, object?>
        {
            ["partId"] = diagnostic.PartId,
            ["selector"] = diagnostic.Selector,
            ["normalizedSelectors"] = diagnostic.NormalizedSelectors.Cast<object?>().ToList(),
            ["candidates"] = diagnostic.Candidates.Select(ToDictionary).Cast<object?>().ToList(),
            ["resolvedNode"] = diagnostic.ResolvedNode is null ? null : ToDictionary(diagnostic.ResolvedNode),
            ["localBounds"] = diagnostic.LocalBounds is null ? null : ToDictionary(diagnostic.LocalBounds),
            ["visibleBounds"] = diagnostic.VisibleBounds is null ? null : ToDictionary(diagnostic.VisibleBounds),
            ["status"] = diagnostic.Status,
            ["targetStateId"] = diagnostic.TargetStateId,
            ["renderTargetId"] = diagnostic.RenderTargetId,
        };

    private static IReadOnlyDictionary<string, object?> ToDictionary(AssetEncounterRenderTargetDecisionSnapshot decision)
        => new Dictionary<string, object?>
        {
            ["targetId"] = decision.TargetId,
            ["kind"] = decision.Kind,
            ["stateId"] = decision.StateId,
            ["partId"] = decision.PartId,
            ["decision"] = decision.Decision,
            ["reason"] = decision.Reason,
            ["affectedPartIds"] = decision.AffectedPartIds.Cast<object?>().ToList(),
        };

    private sealed class EncounterRenderDiagnosticsBuilder
    {
        private readonly List<string> _captureTimingNotes = [];

        public EncounterRenderDiagnosticsBuilder(
            string requestId,
            string renderTargetId,
            string renderMode,
            Node? rootNode = null)
        {
            RequestId = requestId;
            RenderTargetId = renderTargetId;
            RenderMode = renderMode;
            if (rootNode is not null)
            {
                SetRootNode(rootNode);
            }
        }

        public string RequestId { get; }

        public string RenderTargetId { get; }

        public string RenderMode { get; }

        public string RootNodePath { get; private set; } = string.Empty;

        public string RootNodeType { get; private set; } = string.Empty;

        public IReadOnlyList<AssetEncounterSelectorDiagnosticSnapshot> SelectorDiagnostics { get; private set; } = [];

        public IReadOnlyList<string> HiddenPartIds { get; private set; } = [];

        public IReadOnlyList<string> KeptPartIds { get; private set; } = [];

        public Vector2I? ViewportSize { get; private set; }

        public Vector2? FramePosition { get; private set; }

        public Vector2? FrameScale { get; private set; }

        public bool ReadyHookRan { get; set; }

        public bool SpinePreviewSetupRan { get; set; }

        public EncounterAlphaEvidence? AlphaEvidence { get; private set; }

        public EncounterRgbEvidence? RgbEvidence { get; private set; }

        public bool RgbNonzeroBeforeAlphaNormalization { get; private set; }

        public AssetEncounterRenderTargetDecisionSnapshot? RenderTargetDecision { get; private set; }

        public void SetRootNode(Node node)
        {
            RootNodePath = NodePathForDiagnostic(node);
            RootNodeType = node.GetType().Name;
        }

        public void SetSelectorResult(
            IReadOnlyList<AssetEncounterSelectorDiagnosticSnapshot> selectorDiagnostics,
            IReadOnlyList<string> hiddenPartIds,
            IReadOnlyList<string> keptPartIds,
            AssetEncounterRenderTargetDecisionSnapshot renderTargetDecision)
        {
            SelectorDiagnostics = selectorDiagnostics;
            HiddenPartIds = hiddenPartIds;
            KeptPartIds = keptPartIds;
            RenderTargetDecision = renderTargetDecision;
        }

        public void SetFrame(SceneRenderFrame frame)
        {
            ViewportSize = frame.ViewportSize;
            FramePosition = frame.NodePosition;
            FrameScale = frame.NodeScale;
        }

        public void AddTimingNote(string note)
        {
            if (!string.IsNullOrWhiteSpace(note))
            {
                _captureTimingNotes.Add(note);
            }
        }

        public void SetPixelEvidence(Image image, bool beforeAlphaNormalization)
        {
            var inspected = EnsureRgba8(image);
            var rgba = inspected.GetData();
            var transparentRatio = ComputeTransparentPixelRatio(rgba, inspected.GetWidth(), inspected.GetHeight());
            var hasVisible = TryFindVisiblePixelBounds(
                rgba,
                inspected.GetWidth(),
                inspected.GetHeight(),
                0,
                out var visibleRect);
            AlphaEvidence = new EncounterAlphaEvidence(
                transparentRatio,
                hasVisible,
                hasVisible ? ToSnapshot(visibleRect) : null);
            RgbEvidence = ComputeRgbEvidence(rgba, inspected.GetWidth(), inspected.GetHeight());
            if (beforeAlphaNormalization)
            {
                RgbNonzeroBeforeAlphaNormalization = RgbEvidence.NonzeroPixelCount > 0;
            }
        }

        public EncounterRenderDiagnostics Build()
            => new(
                RequestId,
                RenderTargetId,
                RenderMode,
                RootNodePath,
                RootNodeType,
                SelectorDiagnostics,
                HiddenPartIds,
                KeptPartIds,
                ViewportSize,
                FramePosition,
                FrameScale,
                _captureTimingNotes,
                ReadyHookRan,
                SpinePreviewSetupRan,
                AlphaEvidence,
                RgbEvidence,
                RgbNonzeroBeforeAlphaNormalization,
                RenderTargetDecision);
    }

    private readonly record struct CombatBackgroundLayerPlan(string LayerName, string LayerPath);

    private readonly record struct CombatBackgroundLayerCandidate(int? BackgroundLayerIndex, bool IsForeground, string LayerPath);

    private sealed record EncounterSelectorResolutionDiagnostic(
        string PartId,
        string Selector,
        IReadOnlyList<string> NormalizedSelectors,
        IReadOnlyList<AssetEncounterSelectorCandidateSnapshot> Candidates,
        object? Part,
        bool Resolved,
        string TargetStateId,
        string RenderTargetId)
    {
        public static EncounterSelectorResolutionDiagnostic Found(
            string partId,
            string selector,
            IReadOnlyList<string> normalizedSelectors,
            IReadOnlyList<AssetEncounterSelectorCandidateSnapshot> candidates,
            object part,
            string targetStateId,
            string renderTargetId)
            => new(partId, selector, normalizedSelectors, candidates, part, true, targetStateId, renderTargetId);

        public static EncounterSelectorResolutionDiagnostic Missing(
            string partId,
            string selector,
            IReadOnlyList<string> normalizedSelectors,
            IReadOnlyList<AssetEncounterSelectorCandidateSnapshot> candidates,
            string targetStateId,
            string renderTargetId)
            => new(partId, selector, normalizedSelectors, candidates, null, false, targetStateId, renderTargetId);

        public AssetEncounterSelectorDiagnosticSnapshot ToSnapshot()
        {
            AssetEncounterResolvedNodeSnapshot? resolvedNode = null;
            AssetCompositionRectSnapshot? localBounds = null;
            AssetCompositionRectSnapshot? visibleBounds = null;
            if (Part is Node node)
            {
                resolvedNode = new AssetEncounterResolvedNodeSnapshot(
                    NodePathForDiagnostic(node),
                    node.Name.ToString(),
                    node.GetType().Name);
                if (TryFindAuthoredBounds(node, out var authoredBounds))
                {
                    localBounds = Sts2AssetExtractProvider.ToSnapshot(authoredBounds);
                }

                if (TryReadBoundsNode(node, out var bounds))
                {
                    visibleBounds = Sts2AssetExtractProvider.ToSnapshot(bounds);
                }
            }
            else if (Part is not null)
            {
                resolvedNode = new AssetEncounterResolvedNodeSnapshot(
                    string.Empty,
                    string.Empty,
                    Part.GetType().Name);
            }

            return new AssetEncounterSelectorDiagnosticSnapshot(
                PartId,
                Selector,
                NormalizedSelectors,
                Candidates,
                resolvedNode,
                localBounds,
                visibleBounds,
                Resolved ? "resolved" : "unresolved",
                TargetStateId,
                RenderTargetId);
        }
    }

    private sealed record CombatBackgroundCompositionPlan(
        string BackgroundId,
        IReadOnlyList<CombatBackgroundPlaceholderPlan> Placeholders,
        IReadOnlyList<CombatBackgroundLayerGroupPlan> LayerGroups,
        IReadOnlyList<AssetCompositionWarningSnapshot> Warnings);

    private sealed record CombatBackgroundPlaceholderPlan(string Name, string NodePath, int Order, bool Matched);

    private sealed record CombatBackgroundLayerGroupPlan(
        string Name,
        string Placeholder,
        IReadOnlyList<string> Candidates,
        string? SelectedPath,
        string SelectionSource,
        int Order);

    public Sts2AssetExtractProvider(ILogStream logStream)
        : this(logStream, null)
    {
    }

    public Sts2AssetExtractProvider(
        ILogStream logStream,
        IReadOnlyDictionary<string, byte[]>? preloadedRawResources,
        IReadOnlySet<string>? preloadedOnlyRawResourcePaths = null)
    {
        _logStream = logStream;
        _preloadedRawResources = preloadedRawResources ?? new Dictionary<string, byte[]>(StringComparer.Ordinal);
        _preloadedOnlyRawResourcePaths = preloadedOnlyRawResourcePaths ?? new HashSet<string>(StringComparer.Ordinal);
        _explanations = new Sts2AssetExplanationService(logStream, new ExplanationRenderer(RenderNodeToImageAsync));
        _catalog = new Sts2AssetCatalogService(logStream);
        _spineOperations = new Sts2SpineCatalogService(logStream);
    }

    private readonly IReadOnlyDictionary<string, byte[]> _preloadedRawResources;
    private readonly IReadOnlySet<string> _preloadedOnlyRawResourcePaths;
    private readonly Sts2AssetExplanationService _explanations;
    private readonly Sts2AssetCatalogService _catalog;
    private readonly Sts2SpineCatalogService _spineOperations;

    public AssetExtractOperationResult Extract(AssetExtractRequestSnapshot request)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (_preloadedRawResources.TryGetValue(request.LoadPath, out var preloaded))
            {
                return FinalizeExtractResult(SuccessRawResource(request, request.LoadPath, preloaded), request, stopwatch);
            }

            if (_preloadedOnlyRawResourcePaths.Contains(request.LoadPath))
            {
                return FinalizeExtractResult(AssetExtractOperationResult.Failure(
                    requestId: request.RequestId,
                    source: DataSourceKind.Live,
                    provisional: false,
                    code: AssetExtractFailureCode.RuntimeFailure,
                    message: "The requested raw resource has not finished preloading.",
                    details:
                    [
                        new AssetExtractDetail(
                            Field: "loadPath",
                            Value: request.LoadPath,
                            Note: "Raw resource preloading is deferred until the Godot main loop can read the resource.")
                    ]), request, stopwatch);
            }

            // Open the phase recorder HERE, not on the main thread: the hop itself is a real part of what a
            // caller waits for (the main loop only drains the dispatcher between frames), and it is the one
            // segment the render lanes cannot see. The recorder is ambient and flows with the ExecutionContext
            // captured by InvokeAsync, so every phase the lane stamps lands on this render. Completed in a
            // finally so a FAILED render still leaves a breakdown — that is exactly when one is wanted.
            var recorder = Sts2RenderPhaseProfile.Begin(request.RequestId);
            try
            {
                var dispatchWait = Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.DispatchWait, blocking: false);
                // The dispatcher POSTS a raw delegate, so nothing of this thread's context reaches the main
                // thread on its own: the marshalled work has to adopt the recorder explicitly or every phase the
                // lane stamps is dropped on the floor.
                async Task<AssetExtractOperationResult> RunOnMainThreadAsync()
                {
                    dispatchWait.Dispose();
                    using var adoption = Sts2RenderPhaseProfile.Adopt(recorder);
                    return await ExtractOnMainThreadAsync(request);
                }

                var resultTask = request.Timeout is { } timeout
                    ? Sts2MainThreadDispatcher.InvokeAsync(RunOnMainThreadAsync, timeout)
                    : Sts2MainThreadDispatcher.InvokeAsync(RunOnMainThreadAsync);
                var result = resultTask
                    .GetAwaiter()
                    .GetResult();
                return FinalizeExtractResult(result, request, stopwatch);
            }
            finally
            {
                Sts2RenderPhaseProfile.Complete();
            }
        }
        catch (Exception ex)
        {
            _logStream.Write(
                BridgeLogLevel.Error,
                "bridge.assets",
                $"Asset extract '{request.SourcePath}' failed: {ex}");
            return FinalizeExtractResult(AssetExtractOperationResult.Failure(
                requestId: request.RequestId,
                source: DataSourceKind.Live,
                provisional: false,
                code: AssetExtractFailureCode.RuntimeFailure,
                message: "failed to extract a live asset from the running bridge host.",
                details:
                [
                    new AssetExtractDetail(
                        Field: "exception",
                        Value: ex.GetType().Name,
                        Note: ex.Message),
                    new AssetExtractDetail(
                        Field: "live_host",
                        Value: "main-thread-extract-failed",
                        Note: "Live asset extraction must run inside the attached STS2 live host on the Godot main thread."),
                ]), request, stopwatch);
        }
    }

    private static AssetExtractOperationResult FinalizeExtractResult(
        AssetExtractOperationResult result,
        AssetExtractRequestSnapshot request,
        Stopwatch? stopwatch = null)
    {
        stopwatch?.Stop();
        var renderMode = string.IsNullOrWhiteSpace(result.Provenance.RenderMode)
            ? result.RenderMode
            : result.Provenance.RenderMode;
        var sourcePath = string.IsNullOrWhiteSpace(result.Provenance.SourcePath)
            ? request.SourcePath
            : result.Provenance.SourcePath;
        var loadPath = string.IsNullOrWhiteSpace(result.Provenance.LoadPath)
            ? request.LoadPath
            : result.Provenance.LoadPath;
        var sourceRoot = string.IsNullOrWhiteSpace(result.Provenance.SourceRoot)
            ? request.SourceRoot
            : result.Provenance.SourceRoot;
        var notices = result.Notices.ToList();
        if (result.Notes.Any(note => note.Contains("encoded PNG bytes", StringComparison.OrdinalIgnoreCase)))
        {
            notices.Add(new AssetExtractNoticeSnapshot(
                Code: "asset-output-format-fallback",
                Severity: "info",
                Message: "Rendered output was encoded as PNG for compatibility with the live bridge encoder.",
                Path: "outputFormat"));
        }

        return result with
        {
            Provenance = AssetExtractProvenanceSnapshot.FromRequest(
                requestId: result.RequestId,
                sourceRoot: sourceRoot,
                sourcePath: sourcePath,
                loadPath: loadPath,
                renderMode: renderMode,
                source: result.Source),
            Notices = notices,
            ExtractionMs = result.ExtractionMs > 0
                ? result.ExtractionMs
                : (int)Math.Min(int.MaxValue, Math.Max(0L, stopwatch?.ElapsedMilliseconds ?? 0L)),
        };
    }

    public AssetExplainOperationResult Explain(AssetExplainRequestSnapshot request) => _explanations.Explain(request);

    public AssetCatalogOperationResult Catalog(AssetCatalogRequestSnapshot request) => _catalog.Catalog(request);

    public SpineCatalogOperationResult CatalogSpines(SpineCatalogRequestSnapshot request) => _spineOperations.CatalogSpines(request);

    public SpineGeoClipBakeResultSnapshot BakeSpineGeoClip(SpineGeoClipBakeRequestSnapshot request) => _spineOperations.BakeSpineGeoClip(request);

}
