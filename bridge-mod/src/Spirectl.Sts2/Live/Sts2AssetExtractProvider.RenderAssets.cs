using Godot;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
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
using System.Threading;
using System.Threading.Tasks;

namespace Spirectl.Sts2.Live;

public sealed partial class Sts2AssetExtractProvider
{
    // Creates ONE fresh, independent (visualRoot, spineNode) render lane. Called once per lane (lane 0
    // plus each clone). On failure it returns false, frees any partial node it created, and reports a
    // diagnostic note; `notes` carries lane-0 provenance on success. This is the only subject-specific
    // seam — everything downstream (seek, render, crop, encode) is node-generic.
    private delegate bool SpineClipLaneFactory(
        out Node visualRoot,
        out Node spineNode,
        out IReadOnlyList<string> notes,
        out string failNote);

    // Stage-A1 size-addressable combat-background renders: the request's capture viewport override
    // (RenderWidth/RenderHeight, both positive) or the live root viewport size when absent — used ONLY
    // by the two combat-background render branches (the literal-scene branch and the composed alias);
    // every other render mode keeps calling ResolveRootViewportSize() directly and is byte-identical.
    // TryResolveCombatBackgroundFrame already centers its runtime BgContainer framing on whatever
    // viewport it is given, so a 2520x1080 request renders the widescreen capture the mirror stage needs.
    private static Vector2I ResolveRequestedViewportSize(AssetExtractRequestSnapshot request)
        => request is { RenderWidth: > 0, RenderHeight: > 0 }
            ? new Vector2I(request.RenderWidth.Value, request.RenderHeight.Value)
            : ResolveRootViewportSize();

    // spine://<scene>?node=<relPath>&anim=<name> -> render the named Spine animation of the addressed
    // SpineSprite as a Timeline clip (load the scene offline, find the node, drive the clip).
    private async Task<AssetExtractOperationResult> ExtractSpineClipAsync(
        SpineClipRequest clip,
        AssetExtractRequestSnapshot request)
    {
        var sceneResPath = clip.SceneResPath;

        // A full-bleed BACKGROUND Spine (char-select / event background) needs the specialized in-situ composition
        // — the scene root MOUNTED (its container transforms preserved), non-subtree siblings hidden — that the
        // generic tight-crop clip renderer (which bare-DETACHES the SpineSprite, dropping those transforms) does
        // NOT do. IsBackgroundSpineStillScene returns FALSE for the merchant shop, so the merchant (and any other
        // scripted-child scene) stays on the proven-safe generic detach path — the shop-crash guard is intact.
        var isBackgroundStillScene = IsBackgroundSpineStillScene(sceneResPath, out var isCharacterSelectBg);
        var laneKind = Sts2SpineEventBackgroundClip.ResolveSpineClipLaneKind(
            isBackgroundStillScene, isCharacterSelectBg, clip.Still, EventBgComposedClipEnabled);

        // BACKGROUND STILL (&still=1): reuse the existing background-still extractors verbatim so the recon/min-tier
        // view matches the retired model://…SpineStill look exactly, then wrap their single raster into a 1-frame
        // Timeline (spine-clip consumers require one). Now also FORWARDS the still's clipPlacement (see below).
        if (laneKind == Sts2SpineEventBackgroundClip.SpineClipLaneKind.BackgroundStill)
        {
            if (ResourceLoader.Load(sceneResPath) is not PackedScene bgScene
                || bgScene.Instantiate() is not Node bgRoot)
            {
                return Failure(
                    request,
                    "scene",
                    sceneResPath,
                    $"Could not load a PackedScene at '{sceneResPath}' for a background Spine still.");
            }

            // The background extractors resolve their frame from the request scene path; a spine:// request
            // carries the spine:// key there, so rewrite it to the real res:// scene path the predicates expect.
            var bgRequest = request with { SourcePath = sceneResPath, LoadPath = sceneResPath };
            var bgResult = isCharacterSelectBg
                ? await ExtractCharacterSelectBgSpineStillAsync(bgRoot, bgRequest)
                : await ExtractEventBackgroundSpineStillAsync(bgRoot, bgRequest);
            return WrapStillImageAsTimeline(bgResult, request);
        }

        // COMPOSED EVENT-BACKGROUND CLIP: animated event background rendered in situ + expressed node-locally so the
        // client re-applies the streamed node+ancestor transforms exactly once (centered, full-res). Returns null
        // only for the exotic rotated/skewed-node case, which falls through to the generic lane below (+ note).
        if (laneKind == Sts2SpineEventBackgroundClip.SpineClipLaneKind.ComposedEventClip)
        {
            var composedRequest = request with { SourcePath = sceneResPath, LoadPath = sceneResPath };
            var composed = await RenderEventBackgroundSpineClipAsync(clip, composedRequest);
            if (composed is not null)
            {
                return composed;
            }

            return await RenderGenericSpineClipAsync(
                clip,
                request,
                leadingNotes:
                [
                    "The event-background Spine node has a rotated/skewed transform that cannot be expressed as a node-local clip rect; rendered via the generic detached clip lane instead.",
                ]);
        }

        return await RenderGenericSpineClipAsync(clip, request);
    }

    // The generic (creature / merchant / char-select clip / skeleton-fallback) Spine clip path: the tight-crop
    // renderer that DETACHES the SpineSprite. Extracted verbatim so the composed event-bg lane can fall back to it
    // for the exotic rotated-node case. `leadingNotes` prepend onto the result's notes (the fallback provenance).
    private async Task<AssetExtractOperationResult> RenderGenericSpineClipAsync(
        SpineClipRequest clip,
        AssetExtractRequestSnapshot request,
        IReadOnlyList<string>? leadingNotes = null)
    {
        var sceneResPath = clip.SceneResPath;
        var relPath = clip.NodePath;

        // R9 LIVE-UNIFORM CARRY. `&mat=` is the signature of the LIVE node's `normal_material` uniform values, and
        // the producer recorded those values under it (Sts2SpineLiveMaterials). Resolve it ONCE here and hand the
        // resulting material to every lane of BOTH paths below, so the clip is rendered with the very tint its cache
        // key promises. Null (no `&mat=`, unknown/evicted signature, kill switch off) ⇒ every lane falls back to the
        // offline scene material exactly as in round-8. See TryCaptureSpineNormalMaterial for why that fallback is a
        // degradation for a `resource_local_to_scene` material like boss_map_point.tscn's.
        var liveMaterial = Sts2SpineLiveMaterials.TryBuildMaterial(clip.MaterialKey, out var liveMaterialNote);
        LogLiveVsOfflineMaterial(clip, liveMaterial);

        bool SceneLane(out Node root, out Node spine, out IReadOnlyList<string> notes, out string note)
        {
            notes = [];
            if (!TryLoadSceneAndFindNode(sceneResPath, relPath, out root, out spine, out note))
            {
                return false;
            }

            // The scene lane renders the node's OWN offline instance, whose `normal_material` carries the authored
            // uniforms; re-point it at the live-valued material when we have one. A no-op without `&mat=`, so every
            // creature (no shader material ⇒ no signature) renders byte-identically to before.
            if (liveMaterial is not null)
            {
                var laneNotes = new List<string>();
                ApplySpineNormalMaterial(spine, liveMaterial, liveMaterialNote, laneNotes);
                notes = laneNotes;
            }

            return true;
        }

        var subject = string.IsNullOrWhiteSpace(relPath) ? sceneResPath : $"{sceneResPath}#{relPath}";
        var genericResult = await RenderSpineAnimationClipAsync(
            SceneLane, clip.AnimationName, subject, "spine_scene", request, clip.Codec, clip.Fps, clip.Quality, clip.Still, clip.SkinName,
            stillTime: clip.StillTime);

        // The scene-addressed lane resolved (or there's no skeleton fallback to try) → done.
        if (genericResult.Error is null || string.IsNullOrWhiteSpace(clip.SkelResPath))
        {
            return WithLeadingNotes(genericResult, leadingNotes);
        }

        // #8 STANDALONE SKELETON FALLBACK LANE: the scene/node address did not resolve offline (a
        // dynamically-spawned spine whose node isn't in the saved scene — the treasure chest). The client
        // retried with `&skel=<res-path>`, so load the skeleton-data resource directly and render a fresh
        // SpineSprite host (Load → TryInstantiateSpineSpriteNode → TryApplySpineSkinAndPose), then drive it
        // through the same generic clip renderer. Prefer the request's `&skin=`; else resolve the model's
        // authoritative skin (act chest normal skin); else TryApplySpineSkinAndPose's skin-union keeps it visible.
        var skelResPath = clip.SkelResPath!;
        var preferredSkin = string.IsNullOrWhiteSpace(clip.SkinName)
            ? TryResolvePreferredSkinForSkeleton(skelResPath)
            : clip.SkinName;

        // #8 MATERIAL CAPTURE (round-8): the standalone host below is a BARE `ClassDB.Instantiate("SpineSprite")`
        // with none of the addressed scene node's own rendering state, so a skeleton whose scene node paints
        // through a `normal_material` shader used to bake UNSHADED. The boss map point is exactly that: its art is
        // an R/G/B channel-remap MASK and `boss_map_point.gdshader` turns it into the near-grayscale boss the game
        // shows (red→act map color, green→white, blue→traveled/untraveled color), so the raw skeleton renders as a
        // garish colored blob. Resolve the material ONCE, out here, and hand it to every lane.
        //
        // R9: prefer the LIVE-valued material rebuilt from the `&mat=` signature; the offline scene read is now only
        // the fallback (see TryCaptureSpineNormalMaterial — it observes the AUTHORED uniforms for a
        // `resource_local_to_scene` material, which is exactly what boss_map_point.tscn declares).
        var normalMaterial = (GodotObject?)liveMaterial ?? TryCaptureSpineNormalMaterial(sceneResPath, relPath);
        var normalMaterialNote = liveMaterial is not null ? liveMaterialNote : null;

        bool SkeletonLane(out Node root, out Node spine, out IReadOnlyList<string> notes, out string note)
        {
            root = null!;
            spine = null!;
            var noteList = new List<string>
            {
                $"Scene address did not resolve offline; rendered the standalone skeleton '{skelResPath}' via &skel= fallback.",
            };
            notes = noteList;
            note = string.Empty;

            if (ResourceLoader.Load(skelResPath) is not Resource skeletonData)
            {
                note = $"Could not load a skeleton-data resource at '{skelResPath}' for the &skel= fallback.";
                return false;
            }

            if (TryInstantiateSpineSpriteNode(noteList) is not Node node)
            {
                note = "Could not instantiate a SpineSprite host node for the standalone skeleton lane.";
                return false;
            }

            if (!TryApplySpineSkinAndPose(node, skeletonData, noteList, preferredSkin))
            {
                node.QueueFree();
                note = "Could not apply the skin/pose to the standalone skeleton for the &skel= fallback.";
                return false;
            }

            ApplySpineNormalMaterial(node, normalMaterial, normalMaterialNote, noteList);

            root = node;
            spine = node;
            return true;
        }

        var skelResult = await RenderSpineAnimationClipAsync(
            SkeletonLane, clip.AnimationName, skelResPath, "spine_skel", request, clip.Codec, clip.Fps, clip.Quality, clip.Still, preferredSkin,
            unionSkinsWhenUnknown: false,
            stillTime: clip.StillTime);

        // Prefer a working skeleton-fallback render; otherwise surface the ORIGINAL scene error (the more useful
        // diagnostic — the skeleton lane is the last resort, its failure is secondary).
        return WithLeadingNotes(skelResult.Error is null ? skelResult : genericResult, leadingNotes);
    }

    // #8: the `normal_material` the ADDRESSED scene node paints its skeleton through, or null when it has none
    // (the overwhelmingly common case — this returns null for every creature, so their clips are unchanged).
    //
    // Read by INSTANTIATING the saved scene offline and reading the addressed node's property. That is SAFE:
    // `PackedScene.Instantiate` constructs nodes but runs no `_Ready` (that fires on tree ENTRY, which is what the
    // merchant/axebot crash class was about), and this instance never enters a tree — it is freed immediately. It is
    // also the exact call the scene lane already makes for this same address.
    //
    // R9 — THIS IS THE FALLBACK, NOT THE PREFERRED SOURCE. Round-8 reasoned that a `.tscn` sub-resource which is not
    // `resource_local_to_scene` is shared by every instantiation, so the captured object would be the very one
    // `NBossMapPoint.RefreshColorInstantly` re-tints. `boss_map_point.tscn` declares
    // `resource_local_to_scene = true` on exactly that ShaderMaterial, so the premise did not hold: each instantiate
    // gets a PRIVATE copy carrying the AUTHORED uniforms (map_color 0.671,0.58,0.478 / black_layer_color 0,0,0 —
    // the raw three-tone mask), while the `&mat=` cache key hashed the LIVE act tint. Callers now prefer
    // Sts2SpineLiveMaterials.TryBuildMaterial and only fall back here (a shader at authored values still beats no
    // shader at all — the round-8 behaviour, retained behind SPIRECTL_SPINE_LIVE_MATERIAL=0).
    // Best-effort at every hop: a failure just means today's unshaded render.
    //
    // Deliberately does NOT reuse TryLoadSceneAndFindNode: that helper gates on LooksLikeSpinePreviewNode, which
    // asks whether the node already HAS a skeleton — false for exactly the runtime-injected nodes this lane
    // exists for (the offline instance reports `Godot.Node2D` with a null `skeleton_data_res`), so it rejects the
    // very address we are trying to read the material off. We only need the addressed node's property, so we
    // resolve the path directly and let a missing/non-material value fall out as null.
    private static GodotObject? TryCaptureSpineNormalMaterial(string sceneResPath, string? relPath)
    {
        try
        {
            if (ResourceLoader.Load(sceneResPath) is not PackedScene scene || scene.Instantiate() is not Node root)
            {
                return null;
            }

            try
            {
                var target = string.IsNullOrWhiteSpace(relPath) ? root : root.GetNodeOrNull(relPath);
                if (target is null)
                {
                    return null;
                }

                var material = target.Get("normal_material");
                var captured = material.VariantType == Variant.Type.Object ? material.AsGodotObject() : null;
                if (Sts2SpineDiagnostics.Current.Enabled)
                {
                    Sts2SpineDiagnostics.Current.Log(
                        $"bake normal_material capture scene={sceneResPath} rel={relPath ?? "<root>"} "
                        + $"targetClass={target.GetClass()} material={captured?.GetType().Name ?? "<none>"} "
                        + $"shader={(captured as ShaderMaterial)?.Shader?.ResourcePath ?? "<none>"}");
                }

                return captured;
            }
            finally
            {
                root.QueueFree();
            }
        }
        catch
        {
            return null;
        }
    }

    // Apply a resolved `normal_material` to a render host (the standalone skeleton lane's bare SpineSprite, or the
    // scene lane's offline instance). A no-op (and note-free) when there was nothing to resolve, so the
    // byte-identical unshaded render is preserved for every skeleton whose scene node carries no material.
    // `provenance` (R9) is the live-carry note appended after the generic one when the material was rebuilt from the
    // `&mat=` signature rather than read off the offline scene instance.
    private static void ApplySpineNormalMaterial(Node node, GodotObject? material, string? provenance, List<string> noteList)
    {
        if (material is null || !GodotObject.IsInstanceValid(material))
        {
            return;
        }

        try
        {
            node.Set("normal_material", material);
            if (Sts2SpineDiagnostics.Current.Enabled)
            {
                var applied = node.Get("normal_material");
                Sts2SpineDiagnostics.Current.Log(
                    $"bake normal_material apply host={node.GetClass()} "
                    + $"readback={(applied.VariantType == Variant.Type.Object ? applied.AsGodotObject()?.GetType().Name ?? "<null-obj>" : applied.VariantType.ToString())}");
            }

            var shaderPath = material is ShaderMaterial { Shader: { } shader } ? shader.ResourcePath : null;
            noteList.Add(string.IsNullOrWhiteSpace(shaderPath)
                ? "Applied the scene node's normal_material to the standalone skeleton host."
                : $"Applied the scene node's normal_material ('{shaderPath}') to the standalone skeleton host.");
            if (!string.IsNullOrWhiteSpace(provenance))
            {
                noteList.Add(provenance);
            }
        }
        catch
        {
            // Host does not accept the property — fall back to the unshaded render (today's behavior).
        }
    }

    // SPIRECTL_SPINE_DEBUG A/B EVIDENCE for the R9 live-uniform carry: for a clip that carries a `&mat=` signature,
    // log the LIVE uniform values the bake will now render with next to the AUTHORED ones the offline scene
    // instantiate would have handed it (round-8 behaviour). The two lines differing IS the defect; them matching
    // means the material was genuinely shared and nothing changed. Gated hard on the debug lever — the offline
    // capture it performs costs a scene instantiate, so it must never run on the ordinary bake path.
    private static void LogLiveVsOfflineMaterial(SpineClipRequest clip, ShaderMaterial? liveMaterial)
    {
        if (!Sts2SpineDiagnostics.Current.Enabled || string.IsNullOrWhiteSpace(clip.MaterialKey))
        {
            return;
        }

        try
        {
            var live = Sts2SpineLiveMaterials.DescribeLive(clip.MaterialKey) ?? "<unresolved>";
            var offline = "<none>";
            if (TryCaptureSpineNormalMaterial(clip.SceneResPath, clip.NodePath) is ShaderMaterial { Shader: { } shader } captured)
            {
                var parts = new List<string>();
                foreach (var entry in shader.GetShaderUniformList())
                {
                    if (entry.VariantType != Variant.Type.Dictionary)
                    {
                        continue;
                    }

                    var dictionary = entry.AsGodotDictionary();
                    if (dictionary.TryGetValue("name", out var nameVar) && nameVar.VariantType == Variant.Type.String)
                    {
                        var name = nameVar.AsString();
                        parts.Add($"{name}={Sts2SpineInspector.DescribeUniformValue(captured.GetShaderParameter(name))}");
                    }
                }

                parts.Sort(StringComparer.Ordinal);
                offline = $"{shader.ResourcePath}[{string.Join(' ', parts)}]";
            }

            Sts2SpineDiagnostics.Current.Log(
                $"bake material A/B scene={clip.SceneResPath} rel={clip.NodePath ?? "<root>"} mat={clip.MaterialKey} "
                + $"carried={(liveMaterial is not null ? "live" : "offline")} live={live} offline={offline}");
        }
        catch
        {
            // A diagnostic must never disrupt a bake.
        }
    }

    // Prepend provenance notes (e.g. the composed-lane rotation fallback) onto a result without disturbing its
    // own notes/notices. A null/empty list is a no-op (byte-identical to the un-annotated result).
    private static AssetExtractOperationResult WithLeadingNotes(
        AssetExtractOperationResult result,
        IReadOnlyList<string>? leadingNotes)
    {
        if (leadingNotes is null || leadingNotes.Count == 0)
        {
            return result;
        }

        return result with { Notes = [.. leadingNotes, .. result.Notes] };
    }

    // #8: the authoritative preferred skin for a standalone skeleton resource, matched against ModelDb acts by
    // ChestSpineResourcePath → ChestSpineSkinNameNormal (an act chest bundles a normal + a stroke/outline skin;
    // the normal one avoids the white halo). Null when no act owns the skeleton (→ TryApplySpineSkinAndPose's
    // skin-union fallback keeps the spine visible). Best-effort; never throws into the render path.
    private static string? TryResolvePreferredSkinForSkeleton(string skelResPath)
    {
        if (string.IsNullOrWhiteSpace(skelResPath))
        {
            return null;
        }

        try
        {
            foreach (var act in ModelDb.Acts)
            {
                if (string.Equals(act.ChestSpineResourcePath, skelResPath, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(act.ChestSpineSkinNameNormal))
                {
                    return act.ChestSpineSkinNameNormal;
                }
            }
        }
        catch
        {
            // Model lookup unavailable — fall back to the skin union in TryApplySpineSkinAndPose.
        }

        return null;
    }

    // Scene-path membership for the full-bleed background Spines that need the specialized still composition
    // (matching the retired model://…SpineStill routing): the char-select lobby backdrops and the event
    // background_scenes (e.g. neow). These extractors MOUNT THE FULL SCENE in a viewport to compose overscan +
    // isolation, which fires every node's _Ready — safe for those scenes, but NOT for the merchant shop
    // (scenes/rooms/merchant_room): its NMerchantHand child's _Ready assumes the live scene context and throws
    // an InvalidCastException across the native boundary that kills the host (the "crashes on entering the shop"
    // bug). The merchant (and any other non-listed spine) therefore falls through to the generic tight-crop
    // clip-still, whose DetachSpineForRender + StripSpineRenderSubtree remove such scripted children BEFORE the
    // node ever enters a viewport — the proven-safe path. Cheap: no instantiation needed.
    private static bool IsBackgroundSpineStillScene(string sceneResPath, out bool isCharacterSelect)
    {
        var normalized = NormalizeResourcePath(sceneResPath);
        isCharacterSelect = IsCharacterSelectBackgroundScenePath(normalized);
        if (isCharacterSelect)
        {
            return true;
        }

        return IsEventBackgroundScenePath(normalized);
    }

    // Wrap a single rendered background still into a 1-frame Timeline: the spine-clip consumers (the couch-coop
    // /spines/ provider) require a Timeline payload with >=1 frame, but the background-still extractors return a
    // single raster. The already-encoded bytes are reused as-is (no re-encode); the frame's canvas is its own
    // size. Failures pass through unchanged.
    internal AssetExtractOperationResult WrapStillImageAsTimeline(
        AssetExtractOperationResult still,
        AssetExtractRequestSnapshot request)
    {
        if (still.Error is not null)
        {
            return still;
        }

        var frame = new AssetExtractFrame(
            Index: 0,
            Format: still.Format,
            ContentType: still.ContentType,
            Width: still.Width,
            Height: still.Height,
            Contents: still.Contents,
            DurationMs: 0,
            OffsetX: 0,
            OffsetY: 0,
            CanvasWidth: still.Width,
            CanvasHeight: still.Height);

        // WS-NEOW still zero-placement fix: forward the still's node-LOCAL clipPlacement (computed by
        // ExtractEventBackgroundSpineStillAsync from the captured overscan rect + T_node). Without it the wrapped
        // timeline shipped ClipLocal*=0, so the web min/static tier — which draws the shared canvas at
        // clipPlacement — rendered scale(0) = invisible. WS5 closed the same gap on the OTHER still lane: the
        // char-select background still (WarmUpCharacterSelectSceneThenRenderSpineSubtreeAsync) now composes its
        // placement from the reported TRIM region, so the char-select backdrop is no longer painted at scale(0).
        // A null placement (a rotated/skewed node, or SPIRECTL_SPINE_CHARSELECT_PLACEMENT=0) still passes through.
        return BuildTimelineResult(
                request,
                [frame],
                durationMs: 0,
                renderMode: still.RenderMode,
                notes: still.Notes,
                clipPlacement: still.ClipPlacement)
            with { Notices = still.Notices };
    }

    // WS-NEOW: the COMPOSED event-background clip lane. Mounts the SCENE ROOT in situ (no DetachSpineForRender /
    // StripSpineRenderSubtree — preserving the in-scene container transforms full-bleed event backgrounds need),
    // hides non-subtree siblings, seeks the animation deterministically (timescale 0 + per-frame track time), sizes
    // a render cell to the UNION of the posed skeleton bounds over the whole clip (mapped to scene-root space by
    // T_node), and expresses that cell node-LOCALLY (Local = T_node^-1(union)). The client re-applies the streamed
    // node+ancestor transforms (SpineSprite 0.58 origin(-390,-57) <- Neow <- AncientBgContainer 0.89) exactly once
    // — so the clip lands centered at full raster resolution instead of the generic tight-crop lane's uncentered,
    // creature-downscaled (blurry) render. Ancestor transforms are NEVER baked (that would double-apply). Returns
    // null ONLY for the exotic rotated/skewed-node case (cannot be a simple clip rect) so the caller falls back to
    // the generic detached lane. The creature #4 decoded-footprint downscale is never reached here (an event bg at
    // its authored scale is well under the budget); SPIRECTL_SPINE_EVENTBG_FULLRES=0 folds a footprint budget into
    // the capture scale instead — a uniform shrink that (because the placement is capture-scale-invariant) stays
    // centered and true-size.
    private async Task<AssetExtractOperationResult?> RenderEventBackgroundSpineClipAsync(
        SpineClipRequest clip,
        AssetExtractRequestSnapshot request)
    {
        if (IsRenderingHeadless())
        {
            return Failure(request, "display", DisplayServer.GetName(), HeadlessRenderExplanation);
        }

        var sceneResPath = clip.SceneResPath;
        var relPath = clip.NodePath;
        var subjectLabel = string.IsNullOrWhiteSpace(relPath) ? sceneResPath : $"{sceneResPath}#{relPath}";
        var animationName = clip.AnimationName;

        var rootViewport = (Engine.GetMainLoop() as SceneTree)?.Root;
        if (rootViewport is null)
        {
            return Failure(request, "viewport", "root", "Engine.GetMainLoop() did not expose a root viewport for clip rendering.");
        }

        var viewport = new SubViewport
        {
            // The marker a host's tree walk recognises an OFF-SCREEN EXTRACTION subtree by, so it can skip
            // it whole rather than freezing the detached rig this render is in the middle of posing.
            // See Sts2OffscreenExtraction.
            Name = Sts2OffscreenExtraction.SubViewportNodeName,
            TransparentBg = true,
            Size = new Vector2I(DefaultSceneSize, DefaultSceneSize),
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };

        // laneRoots[i] is a full scene clone mounted IN SITU; laneSpines[i]/laneEntries[i]/laneTransforms[i] its
        // SpineSprite, track entry, and (invariant) node-relative transform. All are viewport children, so
        // viewport.QueueFree() in finally frees them on every exit path.
        var laneRoots = new List<Node>();
        var laneSpines = new List<Node>();
        var laneEntries = new List<MegaTrackEntry>();
        var laneTransforms = new List<Transform2D>();

        // Build ONE render lane: fresh scene instance + SpineSprite found + non-subtree siblings hidden + script
        // drivers (NSpineAutoPlayer) frozen so the deterministic seek is authoritative. Animation prepared by the
        // caller after the node is in-tree (the skeleton builds on tree entry).
        bool TryBuildLane(out Node laneRoot, out Node laneSpine, out string note)
        {
            laneRoot = null!;
            laneSpine = null!;
            if (!TryLoadSceneAndFindNode(sceneResPath, relPath, out laneRoot, out var spineNode, out note))
            {
                return false;
            }

            laneSpine = spineNode;
            HideNodesOutsideCapturedSubtree(laneRoot, spineNode);
            return true;
        }

        try
        {
            rootViewport.AddChild(viewport);

            if (!TryBuildLane(out var root0, out var spine0, out var laneNote))
            {
                return Failure(
                    request,
                    "spine_scene",
                    subjectLabel,
                    string.IsNullOrWhiteSpace(laneNote)
                        ? "The live bridge could not obtain an event-background SpineSprite node to animate."
                        : laneNote);
            }

            viewport.AddChild(root0);
            laneRoots.Add(root0);
            laneSpines.Add(spine0);
            FreezeSpineSubtreeDrivers(spine0);

            var sprite0 = new MegaSprite(spine0);
            var skeleton0 = sprite0.GetSkeleton();
            if (skeleton0 is null)
            {
                return Failure(request, "spine", subjectLabel, "The event-background SpineSprite did not expose a skeleton.");
            }

            var data0 = skeleton0.GetData();
            if (string.IsNullOrWhiteSpace(animationName))
            {
                var resolved = ResolveSpinePreviewAnimation(spine0);
                animationName = data0.HasAnimation(resolved)
                    ? resolved
                    : data0.GetAnimationNames().FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? resolved;
            }

            if (!data0.HasAnimation(animationName))
            {
                var available = data0.GetAnimationNames()
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();
                return Failure(
                    request,
                    "animation_name",
                    animationName,
                    available.Length > 0
                        ? $"The event-background SpineSprite skeleton has no animation '{animationName}'. Available: {string.Join(", ", available)}."
                        : $"The event-background SpineSprite skeleton has no animation '{animationName}' and exposed no animations.");
            }

            if (!TryPrepareComposedClipLane(spine0, animationName, out var entry0))
            {
                return Failure(request, "animation_name", animationName, "Setting the event-background Spine animation returned no track entry.");
            }

            laneEntries.Add(entry0);
            laneTransforms.Add(ComputeNodeRelativeTransform(root0, spine0));
            var duration = Math.Max(0f, entry0.GetAnimationDuration());

            // Extra render lanes (throughput). Each is a full scene clone mounted in situ; on any failure fall back
            // to fewer lanes — only throughput is affected, not correctness.
            for (var laneIndex = 1; laneIndex < SpineClipBatchSize; laneIndex += 1)
            {
                if (!TryBuildLane(out var laneRoot, out var laneSpine, out _))
                {
                    laneRoot?.QueueFree();
                    break;
                }

                viewport.AddChild(laneRoot);
                FreezeSpineSubtreeDrivers(laneSpine);
                if (!TryPrepareComposedClipLane(laneSpine, animationName, out var laneEntry))
                {
                    viewport.RemoveChild(laneRoot);
                    laneRoot.QueueFree();
                    break;
                }

                laneRoots.Add(laneRoot);
                laneSpines.Add(laneSpine);
                laneEntries.Add(laneEntry);
                laneTransforms.Add(ComputeNodeRelativeTransform(laneRoot, laneSpine));
            }

            // T_node (lane 0), relative to the scene root and INVARIANT to where we later place the scene root.
            // Reject a rotated/skewed/degenerate node transform up front -> null -> generic detached lane.
            var tNode = laneTransforms[0];
            var nodeAxisAligned = MathF.Abs(tNode.X.Y) < 1e-4f && MathF.Abs(tNode.Y.X) < 1e-4f;
            double nodeScaleX = tNode.X.X;
            double nodeScaleY = tNode.Y.Y;
            if (!nodeAxisAligned || nodeScaleX <= 1e-4 || nodeScaleY <= 1e-4)
            {
                return null;
            }

            // Sample cadence + frame count — identical policy to the generic lane.
            var sampleFps = clip.Fps > 0 ? Math.Min(SpineClipSampleFps, clip.Fps) : SpineClipSampleFps;
            var frameDurationMs = (int)Math.Round(1000d / sampleFps);
            var frameCount = Math.Clamp((int)Math.Ceiling(duration * sampleFps) + 1, 1, SpineClipMaxFrames);
            var truncated = duration * sampleFps + 1 > SpineClipMaxFrames;
            var laneCount = laneEntries.Count;
            float SampleTime(int frameIndex) => frameIndex <= 0 ? 0f : Math.Min(duration, frameIndex / (float)sampleFps);

            await AwaitRenderWarmupFramesAsync(rootViewport, 3);

            // Union the posed skeleton bounds over EVERY sampled frame in SCENE-ROOT space (T_node · GetBounds),
            // padded, so the cell covers the whole animation (a lunge no longer clips at a t=0-sized edge).
            var union = await ComputeComposedUnionBoundsAsync(
                rootViewport, laneSpines, laneTransforms, laneEntries, frameCount, SampleTime);
            if (union.Size.X <= 1f || union.Size.Y <= 1f)
            {
                // Nothing posed (a fully-transparent animation, or a rig whose bounds don't resolve offline) — fall
                // back to the generic detached lane rather than emit an empty clip.
                return null;
            }

            union = union.Grow(EventBgClipPadding);

            // Capture scale s = min(1, ceiling/maxDim). The neow rig at 0.58x is well under -> s == 1 (full-res).
            var maxDim = MathF.Max(union.Size.X, union.Size.Y);
            var captureScale = (float)Sts2SpineEventBackgroundClip.ComputeCaptureScale(maxDim, SpineClipCreatureCeiling);
            var cellWidth = Math.Max(1, (int)MathF.Ceiling(union.Size.X * captureScale));
            var cellHeight = Math.Max(1, (int)MathF.Ceiling(union.Size.Y * captureScale));

            // SPIRECTL_SPINE_EVENTBG_FULLRES=0: fold a decoded-footprint budget INTO the capture scale (uniform,
            // stays centered — placement is capture-scale-invariant). ON by default -> no fold (full-res).
            var footprintFolded = false;
            if (!EventBgFullResCapture)
            {
                var folded = (float)Sts2SpineEventBackgroundClip.FoldFootprintBudget(
                    captureScale, cellWidth, cellHeight, frameCount, SpineClipMaxDecodedPixels);
                if (folded < captureScale)
                {
                    captureScale = folded;
                    cellWidth = Math.Max(1, (int)MathF.Ceiling(union.Size.X * captureScale));
                    cellHeight = Math.Max(1, (int)MathF.Ceiling(union.Size.Y * captureScale));
                    footprintFolded = true;
                }
            }

            // Place each scene-root clone so the union rect's top-left maps to that lane's cell origin at capture
            // scale (Control anchor_left/top=0 makes Position == offset, so the position sticks across layout).
            viewport.Size = new Vector2I(cellWidth * laneCount, cellHeight);
            for (var laneIndex = 0; laneIndex < laneCount; laneIndex += 1)
            {
                var laneFrame = new SceneRenderFrame(
                    new Vector2I(cellWidth * laneCount, cellHeight),
                    new Vector2((laneIndex * cellWidth) - (captureScale * union.Position.X), -(captureScale * union.Position.Y)),
                    new Vector2(captureScale, captureScale),
                    Vector2.Zero,
                    Vector2.Zero,
                    PreserveControlSize: true);
                PositionCanvasItem(laneRoots[laneIndex], laneFrame);
            }

            // Capture + encode (batched). Duplicated from the generic lane so the creature path stays byte-identical.
            var frameResults = new AssetExtractFrame[frameCount];
            var encodeTasks = new List<Task>(frameCount);
            // #14 ENCODE BUDGET. The frame encoders used to fan out to the WHOLE machine (max(2, ProcessorCount)),
            // which oversubscribes every core whenever several game instances share the box (one headless STS2 per
            // co-op seat). Sts2RenderEncodeBudget leaves one core per live instance alone, so the games keep their
            // frame budget (and their ENet tick) while a bake runs. Throughput knob only — never correctness. The
            // gate is PROCESS-WIDE: a per-bake semaphore only capped one bake's own fan-out, so two overlapping
            // encodes (a second bake, or the single-image lane that now encodes off-thread too) could still take
            // every core between them.
            for (var batchStart = 0; batchStart < frameCount; batchStart += laneCount)
            {
                for (var laneIndex = 0; laneIndex < laneCount; laneIndex += 1)
                {
                    var frameIndex = Math.Min(batchStart + laneIndex, frameCount - 1);
                    laneEntries[laneIndex].SetTrackTime(SampleTime(frameIndex));
                }

                await AwaitRenderWarmupFramesAsync(rootViewport, 1, Sts2RenderPhaseProfile.Phase.BatchWait);
                var image = viewport.GetTexture()?.GetImage();
                if (image is null || image.IsEmpty())
                {
                    return Failure(
                        request,
                        "frame",
                        batchStart.ToString(),
                        IsRenderingHeadless() ? HeadlessRenderExplanation : "The composed event-background clip render produced an empty viewport frame.");
                }

                var rgba = EnsureRgba8(image);
                for (var laneIndex = 0; laneIndex < laneCount; laneIndex += 1)
                {
                    var frameIndex = batchStart + laneIndex;
                    if (frameIndex >= frameCount)
                    {
                        break;
                    }

                    var cell = rgba.GetRegion(new Rect2I(laneIndex * cellWidth, 0, cellWidth, cellHeight));
                    var capturedFrameIndex = frameIndex;
                    var encodeLease = await Sts2RenderEncodeBudget.AcquireAsync();
                    encodeTasks.Add(Task.Run(() =>
                    {
                        try
                        {
                            var cropped = cell;
                            var offsetX = 0;
                            var offsetY = 0;
                            if (TryFindVisiblePixelBounds(cell.GetData(), cellWidth, cellHeight, SpineCropPadding, out var visible))
                            {
                                cropped = cell.GetRegion(visible);
                                offsetX = visible.Position.X;
                                offsetY = visible.Position.Y;
                            }
                            else
                            {
                                cropped = cell.GetRegion(new Rect2I(0, 0, 1, 1));
                            }

                            frameResults[capturedFrameIndex] = EncodeTimelineFrameFromImage(
                                cropped, request, capturedFrameIndex, frameDurationMs, offsetX, offsetY, cellWidth, cellHeight,
                                codecOverride: clip.Codec, webpLossy: clip.Codec == "webp", webpQuality: clip.Quality);
                        }
                        finally
                        {
                            encodeLease.Dispose();
                        }
                    }));
                }
            }

            using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.EncodeWait, blocking: false))
            {
                await Task.WhenAll(encodeTasks);
            }

            var frames = new List<AssetExtractFrame>(frameResults);
            var totalDurationMs = frames.Count * frameDurationMs;

            // Local = T_node^-1(union). The rotation/degenerate guard above already passed, so this is non-null.
            var placement = Sts2SpineEventBackgroundClip.ComposeCellPlacement(
                union.Position.X,
                union.Position.Y,
                tNode.Origin.X,
                tNode.Origin.Y,
                nodeScaleX,
                nodeScaleY,
                nodeAxisAligned: true,
                captureScale,
                cellWidth,
                cellHeight);
            if (placement is not { } cell2)
            {
                return null;
            }

            var clipPlacement = new AssetExtractClipPlacement(cell2.LocalX, cell2.LocalY, cell2.LocalWidth, cell2.LocalHeight);

            var clipNotes = new List<string>
            {
                $"Rendered event-background Spine animation '{animationName}' for '{subjectLabel}' IN SITU as a {frames.Count}-frame {clip.Codec} clip ({duration:0.###}s) at {sampleFps}fps across {laneCount} parallel render lane(s); the scene root's container transforms are preserved and the ancestor chain is re-applied by the client (never baked).",
                $"Composed clip cell {cellWidth}x{cellHeight}px at capture scale {captureScale:0.###} over the union bounds ({union.Position.X:0.#},{union.Position.Y:0.#} {union.Size.X:0.#}x{union.Size.Y:0.#}); node-local placement ({cell2.LocalX:0.#},{cell2.LocalY:0.#} {cell2.LocalWidth:0.#}x{cell2.LocalHeight:0.#}).",
                EventBgFullResCapture
                    ? $"Full-res composed capture (SPIRECTL_SPINE_EVENTBG_FULLRES on): event backgrounds are exempt from the {SpineClipMaxDecodedPixels:N0}px creature footprint budget."
                    : footprintFolded
                        ? $"Folded a {SpineClipMaxDecodedPixels:N0}px decoded-footprint budget into the capture scale (SPIRECTL_SPINE_EVENTBG_FULLRES=0), keeping the clip centered at reduced raster resolution."
                        : $"SPIRECTL_SPINE_EVENTBG_FULLRES=0 but the composed footprint already fits the {SpineClipMaxDecodedPixels:N0}px budget; no downscale applied.",
            };
            if (truncated)
            {
                clipNotes.Add($"The clip was truncated to {SpineClipMaxFrames} frames; the source animation is longer than the per-clip frame cap.");
            }

            return BuildTimelineResult(
                request,
                frames,
                totalDurationMs,
                renderMode: EventBackgroundSpineClipRenderMode,
                notes: clipNotes,
                clipPlacement: clipPlacement);
        }
        catch (Exception ex)
        {
            return Failure(
                request,
                "spine_clip",
                animationName,
                $"The live bridge failed while rendering the composed event-background Spine clip: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (viewport.GetParent() is not null)
            {
                rootViewport.RemoveChild(viewport);
            }

            viewport.QueueFree();
        }
    }

    // Prepare an IN-SITU event-background SpineSprite as a deterministic clip lane: leave preview mode, pin the named
    // animation, freeze the driver (timescale 0) so the per-frame track-time seek is authoritative, reset to t=0. No
    // skin manipulation — the mounted scene already wears its authored skin (unlike the generic detached lane, which
    // must re-apply a runtime skin / skin union).
    private static bool TryPrepareComposedClipLane(Node spineNode, string animationName, out MegaTrackEntry entry)
    {
        entry = null!;
        TrySetDynamicValue(spineNode, "preview_frame", false);
        TrySetDynamicValue(spineNode, "visible", true);

        var sprite = new MegaSprite(spineNode);
        var animationState = sprite.GetAnimationState();
        var trackEntry = animationState.SetAnimation(animationName, loop: false);
        if (trackEntry is null)
        {
            return false;
        }

        animationState.SetTimeScale(0f);
        trackEntry.SetTrackTime(0f);
        entry = trackEntry;
        return true;
    }

    // Freeze SCRIPT DRIVER nodes inside a mounted spine subtree (e.g. NSpineAutoPlayer, a plain scripted Node that
    // re-plays the animation) so they cannot fight the deterministic track-time seek. The SpineSprite itself and its
    // Spine* structural nodes keep processing (the SpineSprite rebuilds its mesh from the seeked animation state in
    // _Process), and bone-attached CanvasItems (VFX) stay visible to bake in — matching the still extractor.
    private static void FreezeSpineSubtreeDrivers(Node spineNode)
    {
        foreach (var node in EnumerateNodes(spineNode))
        {
            if (ReferenceEquals(node, spineNode)
                || node is CanvasItem
                || node.GetClass().Contains("Spine", StringComparison.Ordinal))
            {
                continue;
            }

            node.ProcessMode = Node.ProcessModeEnum.Disabled;
        }
    }

    // The SpineSprite's transform relative to the mounted scene root — INVARIANT to where the scene root is later
    // placed (both globals are pre-multiplied by the same root transform, which cancels), so it can be read once
    // and reused for the union mapping and the node-local placement. Identity when either end isn't a CanvasItem.
    private static Transform2D ComputeNodeRelativeTransform(Node root, Node node)
    {
        if (root is CanvasItem rootCanvas && node is CanvasItem nodeCanvas)
        {
            return rootCanvas.GetGlobalTransform().AffineInverse() * nodeCanvas.GetGlobalTransform();
        }

        return Transform2D.Identity;
    }

    // Union the posed skeleton bounds across every sampled frame in SCENE-ROOT space (T_node · GetBounds), batched
    // across the same lanes the render uses (bounds-only, no GetImage/encode). GetBounds is skeleton-space, so it is
    // node-position independent — lane placement is applied later.
    private async Task<Rect2> ComputeComposedUnionBoundsAsync(
        Viewport rootViewport,
        IReadOnlyList<Node> laneSpines,
        IReadOnlyList<Transform2D> laneTransforms,
        IReadOnlyList<MegaTrackEntry> laneEntries,
        int frameCount,
        Func<int, float> sampleTime)
    {
        var laneCount = laneEntries.Count;
        var laneSkeletons = laneSpines.Select(node => new MegaSprite(node).GetSkeleton()).ToArray();

        Rect2? union = null;
        for (var batchStart = 0; batchStart < frameCount; batchStart += laneCount)
        {
            for (var lane = 0; lane < laneCount; lane += 1)
            {
                var frameIndex = Math.Min(batchStart + lane, frameCount - 1);
                laneEntries[lane].SetTrackTime(sampleTime(frameIndex));
            }

            await AwaitRenderWarmupFramesAsync(rootViewport, 1, Sts2RenderPhaseProfile.Phase.UnionBounds);

            for (var lane = 0; lane < laneCount; lane += 1)
            {
                if (batchStart + lane >= frameCount)
                {
                    break;
                }

                var bounds = laneSkeletons[lane]?.GetBounds();
                if (bounds is { } b && b.Size.X > 0f && b.Size.Y > 0f)
                {
                    // Map the skeleton-space bounds into scene-root space via this lane's node transform.
                    var sceneRootBounds = TransformRect(laneTransforms[lane], b);
                    union = union is { } u ? u.Merge(sceneRootBounds) : sceneRootBounds;
                }
            }
        }

        return union ?? default;
    }

    // Load a PackedScene offline (not added to the live tree, so no render/GPU side effects), instantiate
    // it, and resolve the SpineSprite node: an explicit scene-relative `relPath` (the canonical address),
    // else the first SpineSprite in the tree. Frees the instance on any failure.
    // `internal` (not private) so the offline spine-geometry probe mounts its targets through exactly this
    // helper rather than growing a second, subtly different scene loader.
    internal static bool TryLoadSceneAndFindNode(
        string sceneResPath,
        string? relPath,
        out Node root,
        out Node spineNode,
        out string failNote)
    {
        root = null!;
        spineNode = null!;
        failNote = string.Empty;

        if (ResourceLoader.Load(sceneResPath) is not PackedScene scene)
        {
            failNote = $"Could not load a PackedScene at '{sceneResPath}'.";
            return false;
        }

        if (scene.Instantiate() is not Node instantiated)
        {
            failNote = $"The scene '{sceneResPath}' did not instantiate to a Node.";
            return false;
        }

        Node? found;
        if (!string.IsNullOrWhiteSpace(relPath))
        {
            found = instantiated.GetNodeOrNull(relPath);
            // The canonical path points AT the SpineSprite; if it resolves to an ancestor, search under it.
            if (found is not null && !LooksLikeSpinePreviewNode(found))
            {
                found = FindFirstSpinePreviewNode(found) as Node;
            }
        }
        else
        {
            found = FindFirstSpinePreviewNode(instantiated) as Node;
        }

        if (found is null)
        {
            instantiated.QueueFree();
            failNote = string.IsNullOrWhiteSpace(relPath)
                ? $"The scene '{sceneResPath}' did not contain a SpineSprite."
                : $"The scene '{sceneResPath}' has no SpineSprite at node '{relPath}'.";
            return false;
        }

        root = instantiated;
        spineNode = found;
        return true;
    }

    // Detach a SpineSprite from its scene ancestors so the clip renderer can place it at a KNOWN scale as a
    // lone viewport child. The rendered clip must NOT bake in the SpineSprite's authored local scale or any
    // ancestor scale: the live mirror re-applies the node's FULL global transform when it paints the clip, so
    // a scale left baked in here double-applies and the creature renders at scale² (the "half size" / tiny
    // bug — e.g. a 0.35-scaled creature came out at 0.35×0.35). Returns the node to add to the render
    // viewport — the detached SpineSprite (now parentless), or the root unchanged when the root already IS the
    // SpineSprite — and frees the orphaned scene root. The skeleton renders identically as a lone viewport
    // child; PositionCanvasItem then sets the bounds-fit scale that the frontend's transform cancels back to
    // true size.
    // `internal` (not private) so the offline spine-geometry probe mounts its targets through the same
    // detach + subtree strip, and therefore inherits the crash-safety rules documented on StripSpineRenderSubtree.
    internal static Node DetachSpineForRender(Node root, Node spineNode)
    {
        if (!ReferenceEquals(root, spineNode))
        {
            spineNode.GetParent()?.RemoveChild(spineNode);
            root.QueueFree();
        }

        StripSpineRenderSubtree(spineNode);
        return spineNode;
    }

    // Reduce the SpineSprite to its bare skeleton before it enters our render viewport. Two reasons, both about
    // the SpineSprite's AUTHORED child scene nodes (NOT the skeleton itself — its slot meshes are SpineMesh2D
    // children created later, at runtime, when the skeleton rebuilds on tree entry):
    //   1. CRASH SAFETY. A scripted child (e.g. `NMerchantHand`, `NNecrobinderFlameVfx`, `NTheObscuraVfx`) runs
    //      its `_Ready` on tree entry, and those scripts assume the LIVE scene context — they expect ancestors and
    //      siblings that exist in the real room but not under our render SubViewport, so they fault across the
    //      native→managed boundary and kill the whole game (the silent modded-crash chain). Removing the child
    //      before AddChild means its `_Ready` never fires here. This was the "host crashes on entering the shop"
    //      bug.
    //   2. NO DOUBLE-RENDER. Bone-attached CanvasItems (e.g. `SteppedFireMix_dark`, a flame Sprite2D carrying a
    //      ShaderMaterial) would bake into the clip AND get streamed+rendered by the live mirror as their own
    //      nodes (positioned by the bone transform, shader re-applied) → the effect appears twice. Dropping them
    //      here leaves the bare skeleton; the mirror composites the attachments (and their shaders) on top once.
    // The skeleton renders identically from the bare SpineSprite, so this is loss-free for the clip itself.
    private static void StripSpineRenderSubtree(Node spineNode)
    {
        // Clear any authored game script on THIS render node before it enters the render SubViewport. Stripping
        // its non-structural children (below) is not enough: a script attached to the SpineSprite node itself
        // (e.g. NAxebotVfx) still runs its `_Ready` on tree entry, and it expects the particle children we just
        // removed (SparksBoneNode/HurtParticles*, SmokeNode*/SmokeParticles) to be there — reaching for one that
        // is gone is a hard native segfault that killed the host mid clip-render. The clip is driven entirely by
        // the render lane (explicit
        // animation seek + skeleton), so no game-script `_Ready` side effect is needed; this completes the
        // "bare skeleton only" intent. Cleared recursively: the kept Spine* structural children (below) can
        // carry their own authored scripts, and this same recursion clears each. Safe because this node is a
        // throwaway offline scene instance freed after render (never a live-scene node, so nothing to restore)
        // and is only ever driven post-strip via the native MegaSpine bindings / Node2D transforms, never cast
        // back to its game script type.
        spineNode.SetScript(default(Variant));

        foreach (var child in spineNode.GetChildren())
        {
            // KEEP Spine structural children (SpineSlotNode / SpineBoneNode): the SpineSprite binds them to its
            // skeleton in NATIVE code, and removing one BEFORE the skeleton is built (we strip pre-AddChild)
            // derefs a null skeleton inside the engine -> a general-protection fault (ip:15a20e0) that crashed the
            // host while rendering ANY creature with such a node (e.g. the Necrobinder's HeadBoneNode) — the
            // "constant crash on every scene transition". Recurse instead, so their non-structural extras
            // (bone-attached shader sprites, scripted VFX) are still dropped. GetClass() returns the NATIVE class,
            // so a scripted child reports its base (Node/Control/...), never "Spine...".
            if (child.GetClass().Contains("Spine", StringComparison.Ordinal))
            {
                StripSpineRenderSubtree(child);
                continue;
            }

            // RemoveChild is synchronous (so the child never enters the render tree / never runs `_Ready`);
            // QueueFree then disposes it at end-of-frame.
            spineNode.RemoveChild(child);
            child.QueueFree();
        }

        // A ShaderMaterial authored directly on the SpineSprite would likewise be re-applied by the mirror on the
        // spine canvas, so strip it too. (Leave non-shader materials — e.g. additive CanvasItemMaterial — alone:
        // those aren't separately re-applied and belong baked in the clip.)
        if (spineNode is CanvasItem canvasItem && canvasItem.Material is ShaderMaterial)
        {
            canvasItem.Material = null;
        }
    }

    // Shared clip renderer: drive a SpineSprite (produced fresh per lane by `laneFactory`) through the
    // named animation via a fixed-cadence deterministic seek, capturing one frame per sample into a
    // shared-canvas frame sequence. The seek is deterministic (timescale 0 + explicit per-frame track
    // time) so the clip is reproducible and frame-aligned, regardless of how the node was addressed.
    // A game launched with --headless runs the dummy DisplayServer/RenderingServer: viewport render
    // passes "complete" but GetTexture().GetImage() yields nothing, so every offscreen render fails as
    // a bare empty frame. Detect the real cause up front and name it, instead of the generic message.
    private static bool IsRenderingHeadless()
        => string.Equals(DisplayServer.GetName(), "headless", StringComparison.OrdinalIgnoreCase);

    private const string HeadlessRenderExplanation =
        "The game is running without a rendering display (headless): offscreen renders produce empty "
        + "frames. Restart the game with a display to render spine clips/stills.";

    private async Task<AssetExtractOperationResult> RenderSpineAnimationClipAsync(
        SpineClipLaneFactory laneFactory,
        string animationName,
        string subjectLabel,
        string subjectField,
        AssetExtractRequestSnapshot request,
        string codec = "png",
        int targetFps = 0,
        float webpQuality = 0.85f,
        bool still = false,
        string? skinName = null,
        // R9: false for the STANDALONE (&skel=) lane — its factory already picked a skin (default-if-renderable,
        // union only as a fallback), so the per-lane union below must not overwrite that with a stroke/outline halo.
        bool unionSkinsWhenUnknown = true,
        // R10 `&t=`: the explicit animation time a COLLAPSED still samples (see Sts2SpineStillFrame.ChooseSampleTime).
        // Null (every request that omits it, and every animated clip) → the mid/end heuristic, byte-identical to R9.
        float? stillTime = null)
    {
        if (IsRenderingHeadless())
        {
            return Failure(request, "display", DisplayServer.GetName(), HeadlessRenderExplanation);
        }

        bool laneReady;
        Node node;
        Node spineNode;
        IReadOnlyList<string> baseNotes;
        string laneNote;
        using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.LaneBuild))
        {
            laneReady = laneFactory(out node, out spineNode, out baseNotes, out laneNote);
            Sts2RenderPhaseProfile.Count(Sts2RenderPhaseProfile.Counter.LanesPrepared);
        }

        if (!laneReady)
        {
            return Failure(
                request,
                subjectField,
                subjectLabel,
                string.IsNullOrWhiteSpace(laneNote)
                    ? "The live bridge could not obtain a SpineSprite node to animate."
                    : laneNote);
        }

        var rootViewport = (Engine.GetMainLoop() as SceneTree)?.Root;
        if (rootViewport is null)
        {
            node.QueueFree();
            return Failure(
                request,
                "viewport",
                "root",
                "Engine.GetMainLoop() did not expose a root viewport for clip rendering.");
        }

        // One SubViewport holds N side-by-side render "lanes" (independent clones). Seeking each lane to a
        // different animation time and rendering ONE process-frame rebuilds all N meshes at once, so we
        // capture N clip frames per game-frame wait. Sized to the posed skeleton bounds after warmup.
        var viewport = new SubViewport
        {
            // The marker a host's tree walk recognises an OFF-SCREEN EXTRACTION subtree by, so it can skip
            // it whole rather than freezing the detached rig this render is in the middle of posing.
            // See Sts2OffscreenExtraction.
            Name = Sts2OffscreenExtraction.SubViewportNodeName,
            TransparentBg = true,
            Size = new Vector2I(DefaultSceneSize, DefaultSceneSize),
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };

        // laneNodes[i] (the visual root) is driven by laneEntries[i] (its track entry). Lane 0 is the
        // node created above; extra lanes are independent clones. All are children of the viewport, so
        // viewport.QueueFree() in finally frees them on every exit path.
        var laneNodes = new List<Node>();
        var laneEntries = new List<MegaTrackEntry>();

        try
        {
            rootViewport.AddChild(viewport);
            // Render the SpineSprite DETACHED from its scene ancestors so its authored local scale (and any
            // ancestor scale) doesn't bake into the clip and double-apply against the live transform the
            // frontend re-applies. See DetachSpineForRender.
            var renderNode = DetachSpineForRender(node, spineNode);
            viewport.AddChild(renderNode);
            laneNodes.Add(renderNode);

            var sprite = new MegaSprite(spineNode);
            var skeleton = sprite.GetSkeleton();
            if (skeleton is null)
            {
                return Failure(
                    request,
                    "spine",
                    subjectLabel,
                    "The SpineSprite did not expose a skeleton.");
            }

            var data = skeleton.GetData();

            // A still may arrive with no explicit animation: the recon/restructured view addresses a SpineSprite
            // it cannot name (it has no live clip name), so it asks for a single frame via &still=1 alone. Fall
            // back to the node's preview animation ("idle_loop" default, matching the background-still path); if
            // the skeleton lacks it, use the first available animation so ANY spine yields a frame.
            if (string.IsNullOrWhiteSpace(animationName))
            {
                var resolved = ResolveSpinePreviewAnimation(spineNode);
                animationName = data.HasAnimation(resolved)
                    ? resolved
                    : data.GetAnimationNames().FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? resolved;
            }

            if (!data.HasAnimation(animationName))
            {
                var available = data.GetAnimationNames()
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();
                return Failure(
                    request,
                    "animation_name",
                    animationName,
                    available.Length > 0
                        ? $"The SpineSprite skeleton has no animation '{animationName}'. Available: {string.Join(", ", available)}."
                        : $"The SpineSprite skeleton has no animation '{animationName}' and exposed no animations.");
            }

            // Wire lane 0, then create up to SpineClipBatchSize-1 more independent clones (each a fresh
            // skeleton with its own animation state, so they pose independently).
            bool lane0Prepared;
            MegaTrackEntry entry0;
            using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.LanePrepare))
            {
                lane0Prepared = TryPrepareClipLane(spineNode, animationName, out entry0, skinName, unionSkinsWhenUnknown);
            }

            if (!lane0Prepared)
            {
                return Failure(
                    request,
                    "animation_name",
                    animationName,
                    "Setting the Spine animation returned no track entry.");
            }
            laneEntries.Add(entry0);
            var duration = Math.Max(0f, entry0.GetAnimationDuration());

            // The extra lanes are built BEFORE the still-vs-animate decision below, so a single-frame bake pays
            // for a clone it will retire at the lane cap. Recorded as its own phase (with the prepared/retired
            // counts) so that cost is visible rather than hidden inside lane setup.
            for (var laneIndex = 1; laneIndex < SpineClipBatchSize; laneIndex += 1)
            {
                using var extraLaneScope = Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.LaneExtra);
                if (!laneFactory(out var laneNode, out var laneSpine, out _, out _)
                    || !TryPrepareClipLane(laneSpine, animationName, out var laneEntry, skinName, unionSkinsWhenUnknown))
                {
                    laneNode?.QueueFree();
                    break; // Fall back to fewer lanes; only throughput is affected, not correctness.
                }

                var laneRenderNode = DetachSpineForRender(laneNode, laneSpine);
                viewport.AddChild(laneRenderNode);
                laneNodes.Add(laneRenderNode);
                laneEntries.Add(laneEntry);
                Sts2RenderPhaseProfile.Count(Sts2RenderPhaseProfile.Counter.LanesPrepared);
            }

            // Sample cadence + frame count first (independent of bounds — needed to sample the union below). The
            // SERVER-SIDE fps policy lowers the sample rate (seek + render fewer frames): cheaper + smaller on
            // the wire, not a resample. Capped at the native cadence; 0 = full rate.
            var sampleFps = targetFps > 0 ? Math.Min(SpineClipSampleFps, targetFps) : SpineClipSampleFps;
            var frameDurationMs = (int)Math.Round(1000d / sampleFps);
            var frameCount = Math.Clamp(
                (int)Math.Ceiling(duration * sampleFps) + 1,
                1,
                SpineClipMaxFrames);
            var truncated = duration * sampleFps + 1 > SpineClipMaxFrames;
            var laneCount = laneEntries.Count;

            // Build meshes at t=0, read the REST-pose bounds, and decide STILL-vs-animate vs DOWNSCALE. Collapse
            // to a single still frame ONLY for: the client asking (`&still=1`); or a GENUINE full-screen
            // BACKGROUND (rest bounds beyond the creature ceiling, e.g. the char-select bg). A merely-heavy
            // creature whose decoded footprint (canvas px × frames) would blow the budget is instead DOWNSCALED
            // (#4) so it keeps animating — the old 2048 cap froze the waterfall giant to one frame.
            await AwaitRenderWarmupFramesAsync(rootViewport, 3);
            Rect2 restBounds;
            using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.BoundsRead))
            {
                restBounds = skeleton.GetBounds();
            }
            // #4/#6: the waterfall giant's REST bounds measure ~4200px — ABOVE the 4096 creature ceiling — so the
            // pure size test mis-classified this creature as a background and froze EVERY clip of it (idle, and its
            // die/explode) to one frame; the frozen corpse then read as "the exploded giant lingers". Creature
            // scenes live under scenes/creature_visuals/ (genuine backgrounds do not), so never treat one as a
            // background regardless of size — it downscales and animates instead. Genuine oversized backdrops
            // (not creature_visuals) still collapse. (When SPIRECTL_SPINE_CLIP_DOWNSCALE=0 disables downscaling, an
            // oversized creature still collapses via the legacy budget path below, so the escape hatch is intact.)
            var isCreatureScene = subjectLabel.Contains("creature_visuals", StringComparison.OrdinalIgnoreCase);
            var backgroundSized = !isCreatureScene
                && MathF.Max(restBounds.Size.X, restBounds.Size.Y) > SpineClipCreatureCeiling;
            // Collapse to a still only for an explicit still or a genuine background; a heavy-but-animatable
            // creature downscales after the cell is sized (below).
            var collapseToStill = still || backgroundSized;

            // #14 STILL-FRAME SELECTION. A collapsed clip has exactly one frame, and t=0 is the worst pick: for most
            // one-shots that is the wind-up/entry pose (often near-empty). Sample the MID of the clip instead — and
            // the LAST frame for a terminal die/defeat animation, whose meaningful resting state is the corpse. An
            // ANIMATED clip is unaffected (frameIndex-driven cadence, byte-identical to before).
            // R10: an explicit `&t=` (a PAUSED track's authoritative frozen time — the closed treasure chest sits at
            // t=0 of its lid-opening "animation") OVERRIDES the heuristic; absent, nothing changes.
            var stillSampleTime = Sts2SpineStillFrame.ChooseSampleTime(animationName, duration, stillTime);
            float SampleTime(int frameIndex) => collapseToStill
                ? stillSampleTime
                : frameIndex <= 0 ? 0f : Math.Min(duration, frameIndex / (float)sampleFps);

            // The cell must cover the pose we actually sample, so a collapsed clip measures its bounds AT the still
            // time rather than at rest (a mid-animation lunge reaches past the rest pose and would be cropped).
            // Falls back to the rest bounds if the posed read is degenerate.
            var stillBounds = restBounds;
            if (collapseToStill && stillSampleTime > 0f)
            {
                foreach (var laneEntry in laneEntries)
                {
                    laneEntry.SetTrackTime(stillSampleTime);
                }

                await AwaitRenderWarmupFramesAsync(rootViewport, 1, Sts2RenderPhaseProfile.Phase.StillPoseWait);
                Rect2 posedBounds;
                using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.BoundsRead))
                {
                    posedBounds = skeleton.GetBounds();
                }

                if (posedBounds.Size.X > 1f && posedBounds.Size.Y > 1f)
                {
                    stillBounds = posedBounds;
                }
            }

            // CREATURES: union the bounds over EVERY sampled frame so the render cell covers the WHOLE animation —
            // a lunging attack no longer reaches past a t=0-sized cell and get clipped/displaced. Bounds-only (no
            // GetImage/encode), batched across lanes, so cheaper than the render pass. Skipped only when collapsing.
            var boundsForCell = collapseToStill
                ? stillBounds
                : await ComputeUnionBoundsAsync(rootViewport, laneNodes, laneEntries, frameCount, SampleTime);

            var cellFrame = boundsForCell.Size.X > 1f && boundsForCell.Size.Y > 1f
                ? FrameFromBoundsFitted(boundsForCell)
                : new SceneRenderFrame(new Vector2I(DefaultSceneSize, DefaultSceneSize), new Vector2(Padding, Padding));
            var cellWidth = cellFrame.ViewportSize.X;
            var cellHeight = cellFrame.ViewportSize.Y;

            // #4 DOWNSCALE-INSTEAD-OF-COLLAPSE. When a creature's decoded footprint (cell px × frames) blows the
            // budget, shrink every frame by sqrt(budget / footprint) — clamped to MinSpineClipDownscale — rather
            // than freezing to one still. The clipPlacement below is in node-LOCAL units (NodePosition/cellSize ÷
            // NodeScale), so a UNIFORM shrink of ViewportSize + NodePosition + NodeScale cancels out entirely: the
            // creature still displays at true size and animates, only at lower raster resolution. Kill switch
            // (SPIRECTL_SPINE_CLIP_DOWNSCALE=0) restores the legacy collapse.
            var downscale = 1f;
            if (!collapseToStill && (long)cellWidth * cellHeight * frameCount > SpineClipMaxDecodedPixels)
            {
                if (DownscaleOversizedClips)
                {
                    var footprint = (long)cellWidth * cellHeight * frameCount;
                    downscale = Math.Clamp(
                        MathF.Sqrt((float)SpineClipMaxDecodedPixels / footprint), MinSpineClipDownscale, 1f);
                    cellFrame = cellFrame with
                    {
                        ViewportSize = new Vector2I(
                            Math.Max(1, (int)MathF.Ceiling(cellWidth * downscale)),
                            Math.Max(1, (int)MathF.Ceiling(cellHeight * downscale))),
                        NodePosition = cellFrame.NodePosition * downscale,
                        NodeScale = cellFrame.NodeScale * downscale,
                    };
                    cellWidth = cellFrame.ViewportSize.X;
                    cellHeight = cellFrame.ViewportSize.Y;
                }
                else
                {
                    collapseToStill = true; // legacy freeze-to-one-still under the kill switch
                }
            }

            if (collapseToStill)
            {
                frameCount = 1;
                truncated = false;
            }

            // WS5 COMPOSITE WIDTH CAP. The lanes are laid out side-by-side in ONE SubViewport, so the render
            // target is `cellWidth * laneCount` wide. A full-bleed background rig fits to MaxSceneSize + 2*Padding
            // = a 2176px cell, and 2 lanes then ask for 4352px — past the ceiling the resize is refused/clamped,
            // GetImage() reads back narrower, and Image.GetRegion silently pads the missing right-hand columns
            // with transparency (the merchant "left side of the background only" defect). Lanes are a throughput
            // knob only, so retire the excess: a collapsed still needs exactly one, and no composite may exceed
            // SpineClipMaxCompositeWidth. Generic — no per-scene special case.
            var preparedLaneCount = laneCount;
            var effectiveLaneCount = SpineClipLaneCapEnabled
                ? Sts2SpineClipComposite.ResolveLaneCount(
                    laneCount, cellWidth, collapseToStill, SpineClipMaxCompositeWidth)
                : laneCount;
            if (effectiveLaneCount < laneCount)
            {
                // Retired lanes must LEAVE the viewport: they were never repositioned into a cell, so a merely
                // unused lane would still draw at its authored position — on top of lane 0's cell.
                for (var laneIndex = laneNodes.Count - 1; laneIndex >= effectiveLaneCount; laneIndex -= 1)
                {
                    var retired = laneNodes[laneIndex];
                    if (retired.GetParent() is not null)
                    {
                        retired.GetParent().RemoveChild(retired);
                    }

                    retired.QueueFree();
                    laneNodes.RemoveAt(laneIndex);
                    if (laneIndex < laneEntries.Count)
                    {
                        laneEntries.RemoveAt(laneIndex);
                    }

                    Sts2RenderPhaseProfile.Count(Sts2RenderPhaseProfile.Counter.LanesRetired);
                }

                laneCount = effectiveLaneCount;
            }

            viewport.Size = new Vector2I(cellWidth * laneCount, cellHeight);
            Sts2RenderPhaseProfile.Set(
                Sts2RenderPhaseProfile.Counter.ViewportPixels,
                (long)viewport.Size.X * viewport.Size.Y);

            // A SubViewport resize only lands on the render target on a later draw, so give it one frame BEFORE
            // the first capture; otherwise the first readback can still be the previous (smaller) target.
            await AwaitRenderWarmupFramesAsync(rootViewport, 1, Sts2RenderPhaseProfile.Phase.ResizeWait);
            if (Sts2SpineDiagnostics.Current.Enabled)
            {
                Sts2SpineDiagnostics.Current.Log(
                    $"clip-composite subject={subjectLabel} anim={animationName} still={still} "
                    + $"restBounds={restBounds.Size.X:0.#}x{restBounds.Size.Y:0.#}@({restBounds.Position.X:0.#},{restBounds.Position.Y:0.#}) "
                    + $"creatureScene={isCreatureScene} backgroundSized={backgroundSized} collapse={collapseToStill} "
                    + $"stillT={stillSampleTime:0.###} cellBounds={boundsForCell.Size.X:0.#}x{boundsForCell.Size.Y:0.#}@({boundsForCell.Position.X:0.#},{boundsForCell.Position.Y:0.#}) "
                    + $"cell={cellWidth}x{cellHeight} fit={cellFrame.NodeScale.X:0.####} downscale={downscale:0.###} "
                    + $"lanes={laneCount}(prepared={preparedLaneCount}) "
                    + $"requestedViewport={cellWidth * laneCount}x{cellHeight} actualViewport={viewport.Size.X}x{viewport.Size.Y} "
                    + $"frames={frameCount}");
            }

            for (var laneIndex = 0; laneIndex < laneCount; laneIndex += 1)
            {
                PositionCanvasItem(
                    laneNodes[laneIndex],
                    cellFrame with
                    {
                        NodePosition = new Vector2(
                            cellFrame.NodePosition.X + (laneIndex * cellWidth),
                            cellFrame.NodePosition.Y),
                    });
            }

            // Each frame is cropped to ITS OWN visible pixels (within its lane's cell) and carries its
            // (offset, canvas) so the per-frame-tight images composite aligned. No up-front full-clip pass:
            // a batch's frames are ready the moment that batch renders, so a streaming consumer can emit
            // them progressively as production stays ahead of playback.
            //
            // The GPU readback (GetImage) must stay on the main thread, but the per-frame scan +
            // tight-crop + PNG encode is pure CPU on an UNSHARED Image (each lane's cell is sliced on the
            // main thread, then owned by one task), so it is offloaded to background Tasks that overlap the
            // NEXT batch's render-wait. Verified: Godot Image GetData/GetRegion/SavePngToBuffer run safely
            // off the main thread, and this roughly halves wall-clock (production stays ahead of playback).
            // A semaphore caps in-flight encodes so a long clip cannot hold an unbounded number of full
            // cell images in memory. Frames are written into a fixed array by index (no cross-task sharing).
            var frameResults = new AssetExtractFrame[frameCount];
            var encodeTasks = new List<Task>(frameCount);
            // WS5: set on the first batch whose readback is smaller than the composite it was laid out for
            // (see the assertion below); surfaced as a clip note so a truncated bake is self-reporting.
            string? readbackTruncation = null;
            // #14 ENCODE BUDGET. The frame encoders used to fan out to the WHOLE machine (max(2, ProcessorCount)),
            // which oversubscribes every core whenever several game instances share the box (one headless STS2 per
            // co-op seat). Sts2RenderEncodeBudget leaves one core per live instance alone, so the games keep their
            // frame budget (and their ENet tick) while a bake runs. Throughput knob only — never correctness. The
            // gate is PROCESS-WIDE: a per-bake semaphore only capped one bake's own fan-out, so two overlapping
            // encodes (a second bake, or the single-image lane that now encodes off-thread too) could still take
            // every core between them.
            for (var batchStart = 0; batchStart < frameCount; batchStart += laneCount)
            {
                for (var laneIndex = 0; laneIndex < laneCount; laneIndex += 1)
                {
                    // Park lanes past the end of the clip on the last frame; they are not emitted.
                    var frameIndex = Math.Min(batchStart + laneIndex, frameCount - 1);
                    laneEntries[laneIndex].SetTrackTime(SampleTime(frameIndex));
                }

                await AwaitRenderWarmupFramesAsync(rootViewport, 1, Sts2RenderPhaseProfile.Phase.BatchWait);
                Image? image;
                using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.Readback))
                {
                    image = viewport.GetTexture()?.GetImage();
                }

                if (image is null || image.IsEmpty())
                {
                    return Failure(
                        request,
                        "frame",
                        batchStart.ToString(),
                        IsRenderingHeadless()
                            ? HeadlessRenderExplanation
                            : "The clip render produced an empty viewport frame.");
                }

                // WS5 READBACK ASSERTION. Image.GetRegion never fails on an out-of-range slice — it allocates the
                // REQUESTED cell and blits whatever the (possibly smaller) readback has, padding the rest with
                // transparency. So a render target that came back narrower than the composite ships a silently
                // half-empty frame. Record it once, loudly, instead of letting it look like a content bug.
                if (readbackTruncation is null
                    && Sts2SpineClipComposite.IsReadbackTruncated(
                        image.GetWidth(), image.GetHeight(), cellWidth, cellHeight, laneCount))
                {
                    readbackTruncation =
                        $"The clip render read back a {image.GetWidth()}x{image.GetHeight()} viewport for a "
                        + $"{cellWidth * laneCount}x{cellHeight} composite ({laneCount} lane(s) of {cellWidth}x{cellHeight}); "
                        + "pixels past the readback edge are transparent padding, not rendered content.";
                    Sts2SpineDiagnostics.Current.Log($"clip-readback-truncated subject={subjectLabel} {readbackTruncation}");
                }

                Image rgba;
                using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.SliceRegion))
                {
                    rgba = EnsureRgba8(image);
                }

                for (var laneIndex = 0; laneIndex < laneCount; laneIndex += 1)
                {
                    var frameIndex = batchStart + laneIndex;
                    if (frameIndex >= frameCount)
                    {
                        break;
                    }

                    // Slice this lane's cell on the main thread (cheap copy), then hand the unshared cell
                    // image to a background task for scan + tight-crop + encode.
                    Image cell;
                    using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.SliceRegion))
                    {
                        cell = rgba.GetRegion(new Rect2I(laneIndex * cellWidth, 0, cellWidth, cellHeight));
                    }

                    var capturedFrameIndex = frameIndex;
                    var encodeLease = await Sts2RenderEncodeBudget.AcquireAsync();
                    encodeTasks.Add(Task.Run(() =>
                    {
                        try
                        {
                            var cropped = cell;
                            var offsetX = 0;
                            var offsetY = 0;
                            if (TryFindVisiblePixelBounds(cell.GetData(), cellWidth, cellHeight, SpineCropPadding, out var visible))
                            {
                                cropped = cell.GetRegion(visible);
                                offsetX = visible.Position.X;
                                offsetY = visible.Position.Y;
                            }
                            else
                            {
                                // Fully transparent frame (e.g. a blink): emit a 1x1 placeholder.
                                cropped = cell.GetRegion(new Rect2I(0, 0, 1, 1));
                            }

                            frameResults[capturedFrameIndex] = EncodeTimelineFrameFromImage(
                                cropped, request, capturedFrameIndex, frameDurationMs, offsetX, offsetY, cellWidth, cellHeight,
                                codecOverride: codec, webpLossy: codec == "webp", webpQuality: webpQuality);
                        }
                        finally
                        {
                            encodeLease.Dispose();
                        }
                    }));
                }
            }

            using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.EncodeWait, blocking: false))
            {
                await Task.WhenAll(encodeTasks);
            }

            var frames = new List<AssetExtractFrame>(frameResults);
            var totalDurationMs = frames.Count * frameDurationMs;

            // The clip's shared canvas (each frame's CanvasWidth/Height = the cell) maps to the SpineSprite's
            // node-LOCAL space: the node origin sits at cellFrame.NodePosition within the cell, rendered at
            // fitScale cell-px per local-unit. A live SpineSprite carries only its transform (no localRect),
            // so the consumer aligns the clip by drawing this node-local rect under the node's transform.
            //
            // The algebra moved to Sts2SceneFitFrame.Place VERBATIM (same float ops, same order) because the
            // geoclip baker now emits the same four numbers into its own manifest, and "the delta and the raster
            // still agree about placement" has to be an equality rather than an approximation.
            var fitPlacement = Sts2SceneFitFrame.Place(
                cellFrame.ViewportSize.X,
                cellFrame.ViewportSize.Y,
                cellFrame.NodePosition.X,
                cellFrame.NodePosition.Y,
                cellFrame.NodeScale.X);
            var clipPlacement = new AssetExtractClipPlacement(
                LocalX: fitPlacement.LocalX,
                LocalY: fitPlacement.LocalY,
                LocalWidth: fitPlacement.LocalWidth,
                LocalHeight: fitPlacement.LocalHeight);

            var clipNotes = baseNotes.ToList();
            clipNotes.Add(
                $"Rendered Spine animation '{animationName}' for '{subjectLabel}' as a {frames.Count}-frame {codec} clip ({duration:0.###}s) sampled at {sampleFps}fps via deterministic track-time seek across {laneCount} parallel render lane(s).");
            if (truncated)
            {
                clipNotes.Add(
                    $"The clip was truncated to {SpineClipMaxFrames} frames; the source animation is longer than the per-clip frame cap.");
            }
            if (collapseToStill && !still)
            {
                var reason = backgroundSized
                    ? $"rest bounds ({(int)restBounds.Size.X}x{(int)restBounds.Size.Y}) exceed the {SpineClipCreatureCeiling}px creature ceiling (full-screen background)"
                    : $"the decoded footprint exceeds the {SpineClipMaxDecodedPixels:N0}px budget and clip downscaling is disabled (SPIRECTL_SPINE_CLIP_DOWNSCALE=0)";
                clipNotes.Add(
                    $"Collapsed to a single still frame: {reason} — rendered as a flicker-free, lightweight still rather than an animated clip.");
            }
            if (downscale < 1f)
            {
                // #4: self-report the downscale so a caller (spirectl assets get spine://…waterfall_giant…) can
                // confirm the clip animated (frameCount > 1) at reduced resolution instead of freezing.
                clipNotes.Add(
                    $"Downscaled clip render to {downscale:0.###}x ({cellWidth}x{cellHeight} cell) so its decoded footprint fits the {SpineClipMaxDecodedPixels:N0}px budget while keeping all {frames.Count} frames animating.");
            }
            if (effectiveLaneCount < preparedLaneCount)
            {
                // WS5: self-report the lane retirement so a caller can tell a capped composite apart from a
                // lane factory that simply failed to clone (the other reason laneCount can be below the batch size).
                clipNotes.Add(
                    $"Reduced the side-by-side render composite from {preparedLaneCount} to {effectiveLaneCount} lane(s) "
                    + $"({cellWidth}x{cellHeight} cell; ceiling {SpineClipMaxCompositeWidth}px"
                    + (collapseToStill ? ", single still frame" : string.Empty)
                    + ") so the render target stays within the readback ceiling. Throughput only; the clip is unchanged.");
            }
            if (readbackTruncation is not null)
            {
                clipNotes.Add(readbackTruncation);
            }

            return BuildTimelineResult(
                request,
                frames,
                totalDurationMs,
                renderMode: SpineCharacterClipRenderMode,
                notes: clipNotes,
                clipPlacement: clipPlacement);
        }
        catch (Exception ex)
        {
            return Failure(
                request,
                "spine_clip",
                animationName,
                $"The live bridge failed while rendering the Spine clip: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (viewport.GetParent() is not null)
            {
                rootViewport.RemoveChild(viewport);
            }

            viewport.QueueFree();
        }
    }

    // Union the posed skeleton bounds across every sampled frame so the render cell covers the WHOLE animation
    // (not just the rest pose) — the fix for lunging attacks getting clipped/displaced at the cell edge. This is
    // a bounds-only pre-pass (no GetImage/encode), batched across the same lanes the render uses, so it costs
    // ~frameCount/laneCount process frames (one-time; the clip is then disk-cached). GetBounds is skeleton-space
    // (independent of node Position/Scale), so lanes need no positioning here.
    private async Task<Rect2> ComputeUnionBoundsAsync(
        Viewport rootViewport,
        IReadOnlyList<Node> laneNodes,
        IReadOnlyList<MegaTrackEntry> laneEntries,
        int frameCount,
        Func<int, float> sampleTime)
    {
        var laneCount = laneEntries.Count;
        var laneSkeletons = laneNodes.Select(node => new MegaSprite(node).GetSkeleton()).ToArray();

        Rect2? union = null;
        for (var batchStart = 0; batchStart < frameCount; batchStart += laneCount)
        {
            for (var lane = 0; lane < laneCount; lane += 1)
            {
                var frameIndex = Math.Min(batchStart + lane, frameCount - 1);
                laneEntries[lane].SetTrackTime(sampleTime(frameIndex));
            }

            await AwaitRenderWarmupFramesAsync(rootViewport, 1, Sts2RenderPhaseProfile.Phase.UnionBounds);

            for (var lane = 0; lane < laneCount; lane += 1)
            {
                if (batchStart + lane >= frameCount)
                {
                    break;
                }

                var bounds = laneSkeletons[lane]?.GetBounds();
                if (bounds is { } b && b.Size.X > 0f && b.Size.Y > 0f)
                {
                    union = union is { } u ? u.Merge(b) : b;
                }
            }
        }

        return union ?? default;
    }

    // Wire a freshly-created character SpineSprite as a clip render lane: apply the runtime skin (#3), pin the
    // named animation, freeze the driver (timescale 0) so the per-frame track-time seek is authoritative, and
    // reset to t=0.
    private static bool TryPrepareClipLane(
        Node spineNode,
        string animationName,
        out MegaTrackEntry entry,
        string? skinName = null,
        bool unionSkinsWhenUnknown = true)
    {
        entry = null!;
        var sprite = new MegaSprite(spineNode);

        // #3: apply the runtime skin the live node wore BEFORE pinning the animation, so a creature whose skin is
        // set at runtime (Fossil Stalker / Skulking Colony) renders its full body instead of missing slot
        // attachments. Mirrors TryApplySpineSkinAndPose's named-skin branch (FindSkin → SetSkin →
        // SetSlotsToSetupPose). Best-effort per lane: a missing/failed skin name just leaves the default skin.
        if (!string.IsNullOrWhiteSpace(skinName))
        {
            TryApplyClipLaneSkin(sprite, skinName!);
        }
        else if (unionSkinsWhenUnknown)
        {
            // #3: no runtime skin was captured (Fossil Stalker / Skulking Colony set their body skin natively,
            // never touching the node's `skin` property). Union all skeleton skins so runtime-skinned body slots
            // render instead of collapsing to the setup-pose legs. No-op for single-skin skeletons (guarded).
            //
            // R9: SKIPPED for the STANDALONE (&skel=) lane, which has already made this decision itself in
            // TryApplySpineSkinAndPose (default skin if it renders, union only as a fallback) — re-unioning here
            // would overwrite that choice and put the stroke/outline halo straight back on the boss map node.
            TryApplyClipLaneSkinUnion(sprite);
        }

        var animationState = sprite.GetAnimationState();
        var trackEntry = animationState.SetAnimation(animationName, loop: false);
        if (trackEntry is null)
        {
            return false;
        }

        animationState.SetTimeScale(0f);
        trackEntry.SetTrackTime(0f);
        entry = trackEntry;
        return true;
    }

    // Apply a named skin to a clip render lane's skeleton in isolation (#3): FindSkin → SetSkin →
    // SetSlotsToSetupPose, exactly like the standalone TryApplySpineSkinAndPose named-skin branch. The skeleton
    // is built from the node's assigned skeleton_data_res (available with or without tree membership), so this
    // works on every lane. Best-effort: any failure (unknown skin name, no skeleton) leaves the default skin.
    private static void TryApplyClipLaneSkin(MegaSprite sprite, string skinName)
    {
        try
        {
            var skeleton = sprite.GetSkeleton();
            var data = skeleton?.GetData();
            if (skeleton is null || data is null)
            {
                return;
            }

            if (data.FindSkin(skinName) is { } skin)
            {
                skeleton.SetSkin(skin);
                skeleton.SetSlotsToSetupPose();
            }
        }
        catch
        {
            // Skin application is best-effort — a bad name / unbuilt skeleton just leaves the default skin.
        }
    }

    // #3: union ALL of a skeleton's skins onto the clip lane (FindSkin-less) so a creature whose body slots are
    // filled by a runtime-applied skin — never mirrored to the node's `skin` property, so nothing to capture —
    // still renders its full body. Mirrors TryApplySpineSkinAndPose's union branch. Guarded to a no-op for a
    // single-skin skeleton so ordinary creatures render byte-identically; whole thing best-effort + gated.
    private static void TryApplyClipLaneSkinUnion(MegaSprite sprite)
    {
        if (!UnionClipLaneSkins)
        {
            return;
        }

        try
        {
            var skeleton = sprite.GetSkeleton();
            var data = skeleton?.GetData();
            if (skeleton is null || data is null)
            {
                return;
            }

            var skins = data.GetSkins();
            if (skins.Count <= 1)
            {
                return; // only the default skin → nothing to union → default render unchanged
            }

            var combined = sprite.NewSkin("spirectl_clip_union");
            foreach (var skin in skins)
            {
                combined.AddSkin(new MegaSkin(skin));
            }

            skeleton.SetSkin(combined);
            skeleton.SetSlotsToSetupPose();
        }
        catch
        {
            // Union is best-effort — any failure just leaves the default skin (today's behavior).
        }
    }

    private async Task<AssetExtractOperationResult> ExtractCharacterVisualAsync(
        CharacterVisualRequest characterVisual,
        AssetExtractRequestSnapshot request)
    {
        var variant = characterVisual.Variant.ToLowerInvariant();
        if (variant is "icon"
            or "iconoutline"
            or "selecticon"
            or "selectlockedicon"
            or "characterselecticon"
            or "characterselectlockedicon"
            or "mapmarker")
        {
            if (!TryResolveCharacterModel(characterVisual.CharacterId, out var iconModel))
            {
                return Failure(
                    request,
                    "character_id",
                    characterVisual.CharacterId,
                    "No character with this id was found in ModelDb.AllCharacters.");
            }

            var texture = variant switch
            {
                "icon" => iconModel.IconTexture,
                "iconoutline" => iconModel.IconOutlineTexture,
                "characterselectlockedicon" => iconModel.CharacterSelectLockedIcon,
                "mapmarker" => iconModel.MapMarker,
                _ => iconModel.CharacterSelectIcon,
            };
            if (texture is null)
            {
                return Failure(
                    request,
                    "character_visual",
                    characterVisual.CharacterId,
                    $"The live bridge could not resolve a character model {CanonicalCharacterVariant(variant)} texture for this character.");
            }

            return await ExtractTextureAsync(
                texture,
                request,
                $"flattened-character-model-{CanonicalCharacterVariant(variant)}",
                [$"Rendered character model {CanonicalCharacterVariant(variant)} for '{CharacterModelIdOrUnknown(iconModel)}'."]);
        }

        if (!string.Equals(variant, "visuals", StringComparison.Ordinal))
        {
            return Failure(
                request,
                "character_variant",
                characterVisual.Variant,
                "Only model://characters/<id>/icon, model://characters/<id>/characterSelectIcon, model://characters/<id>/characterSelectLockedIcon, and model://characters/<id>/visuals are supported character model renders.");
        }

        if (!TryResolveCharacterModel(characterVisual.CharacterId, out var model))
        {
            return Failure(
                request,
                "character_id",
                characterVisual.CharacterId,
                "No character with this id was found in ModelDb.AllCharacters.");
        }

        if (!TryCreateCharacterVisualNode(model, out var node, out var notes))
        {
            return Failure(
                request,
                "character_visual",
                characterVisual.CharacterId,
                "The live bridge could not obtain a Node from a live ally or CharacterModel.CreateVisuals().");
        }

        const string renderMode = "flattened-character-model-battlefield";
        var renderNotes = notes.ToList();
        if (TryConfigureSpineFirstFramePreview(node, out var animationName))
        {
            renderNotes.Add(
                $"Selected deterministic Spine preview animation '{animationName}' for the model-backed battlefield character visual.");
        }
        renderNotes.Add(CharacterBattlefieldAlphaNormalizationNote);
        renderNotes.Add("Allowed the model-backed battlefield character visual to process during render warmup.");
        var diagnostics = new EncounterRenderDiagnosticsBuilder(
            request.RequestId,
            $"model://characters/{characterVisual.CharacterId}/visuals",
            renderMode,
            node)
        {
            SpinePreviewSetupRan = true,
        };

        return await RenderNodeResultAsync(
            node,
            request,
            renderMode,
            renderNotes,
            warmupFrames: 3,
            trimTransparentBounds: true,
            normalizePreviewAlpha: CharacterBattlefieldNormalizePreviewAlpha,
            afterAttach: () =>
            {
                TryConfigureSpineFirstFramePreview(node, out _);
                QueueRedrawCanvasItems(node);
            },
            encounterDiagnostics: diagnostics,
            disableProcessDuringCapture: CharacterBattlefieldDisableProcessDuringCapture);
    }

    private async Task<AssetExtractOperationResult> ExtractRelicVisualAsync(
        RelicVisualRequest relicVisual,
        AssetExtractRequestSnapshot request)
    {
        if (!TryResolveRelicModel(relicVisual.RelicId, out var model))
        {
            return Failure(
                request,
                "relic_id",
                relicVisual.RelicId,
                "No relic with this id was found in ModelDb.AllRelics.");
        }

        var variant = relicVisual.Variant.ToLowerInvariant();
        var texture = variant switch
        {
            "iconoutline" => model.IconOutline,
            "bigicon" => model.BigIcon,
            _ => null,
        };
        if (texture is null)
        {
            return Failure(
                request,
                "relic_visual",
                relicVisual.RelicId,
                $"The live bridge could not resolve a relic model {CanonicalRelicVariant(variant)} texture for this relic.");
        }

        return await ExtractTextureAsync(
            texture,
            request,
            $"flattened-relic-model-{CanonicalRelicVariant(variant)}",
            [$"Rendered relic model {CanonicalRelicVariant(variant)} for '{RelicModelIdOrUnknown(model)}'."]);
    }

    private async Task<AssetExtractOperationResult> ExtractStandaloneSkeletonDataAsync(
        Resource skeletonData,
        AssetExtractRequestSnapshot request)
    {
        if (!TryCreateStandaloneSkeletonPreviewNode(
            skeletonData,
            request,
            out var previewNode,
            out var defaults,
            out var notes))
        {
            return UnsupportedResourceTypeFailure(
                request,
                skeletonData,
                "is standalone skeleton data, but the live bridge could not construct a deterministic Spine preview node for this resource shape.");
        }

        var renderNotes = notes.ToList();
        // A freshly-instantiated SpineSprite rebuilds its skeleton when it enters the
        // tree (_ready), discarding any skin/pose applied beforehand, and only builds
        // its render mesh once it has processed with a concrete pose. So re-apply the
        // skin union + setup pose in afterAttach (post _ready) and let the Spine driver
        // advance during warmup; without this the node renders fully transparent.
        renderNotes.Add("Re-applied the standalone skeleton skin/pose after tree attach and processed during warmup.");

        return await RenderNodeResultAsync(
            previewNode,
            request,
            SkeletonResourceRenderMode,
            renderNotes,
            warmupFrames: 3,
            trimTransparentBounds: true,
            normalizePreviewAlpha: false,
            // Scale-to-fit (not clamp-and-crop) so oversized rigs like the chest capture in full.
            frameOverride: FrameFromBoundsFitted(defaults.Bounds),
            afterAttach: () =>
            {
                // Use the model-resolved skin (not the generic defaults.SkinName) so only chest
                // spines render in isolation; generic skeletons keep the union behavior.
                var preferredSkinName = string.IsNullOrWhiteSpace(request.PreferredSkinName) ? null : request.PreferredSkinName;
                if (!TryApplySpineSkinAndPose(previewNode, skeletonData, new List<string>(), preferredSkinName))
                {
                    TryConfigureSpineFirstFramePreview(previewNode, out _);
                }
                QueueRedrawCanvasItems(previewNode);
            },
            disableProcessDuringCapture: false);
    }

#pragma warning disable CS0618 // AnimatedTexture support is retained for legacy STS2/Godot asset compatibility.
    private AssetExtractOperationResult ExtractAnimatedTexture(
        AnimatedTexture animated,
        AssetExtractRequestSnapshot request)
    {
        var frameCount = animated.Frames;
        if (frameCount <= 0)
        {
            return Failure(
                request,
                "frame_count",
                frameCount.ToString(),
                "AnimatedTexture did not expose any frames for timeline export.");
        }

        var frames = new List<AssetExtractFrame>(frameCount);
        var totalDurationMs = 0;
        for (var index = 0; index < frameCount; index += 1)
        {
            var texture = animated.GetFrameTexture(index);
            if (texture is null)
            {
                return Failure(
                    request,
                    "frame",
                    index.ToString(),
                    "AnimatedTexture did not expose a texture for one of its timeline frames.");
            }

            var durationMs = DurationMs(animated.GetFrameDuration(index));
            var frame = EncodeTimelineFrame(texture, request, index, durationMs);
            if (frame is null)
            {
                return Failure(
                    request,
                    "frame",
                    index.ToString(),
                    "AnimatedTexture timeline export could not read pixels for one of its frames.");
            }

            frames.Add(frame);
            totalDurationMs += durationMs;
        }

        return BuildTimelineResult(
            request,
            frames,
            totalDurationMs,
            renderMode: "timeline-frames",
            notes:
            [
                "Rendered all AnimatedTexture frames as a timeline export.",
            ]);
    }
#pragma warning restore CS0618

    private AssetExtractOperationResult ExtractSpriteFrames(
        SpriteFrames spriteFrames,
        AssetExtractRequestSnapshot request)
    {
        var animations = spriteFrames.GetAnimationNames();
        if (!animations.Any())
        {
            return Failure(
                request,
                "animation",
                string.Empty,
                "SpriteFrames did not expose any animations for timeline export.");
        }

        var animation = animations[0].ToString();
        var frameCount = spriteFrames.GetFrameCount(animation);
        if (frameCount <= 0)
        {
            return Failure(
                request,
                "animation",
                animation,
                "SpriteFrames exposed an empty animation for timeline export.");
        }

        var frames = new List<AssetExtractFrame>(frameCount);
        var totalDurationMs = 0;
        for (var index = 0; index < frameCount; index += 1)
        {
            var texture = spriteFrames.GetFrameTexture(animation, index);
            if (texture is null)
            {
                return Failure(
                    request,
                    "frame",
                    $"{animation}:{index}",
                    "SpriteFrames did not expose a texture for one of its timeline frames.");
            }

            var durationMs = DurationMs(spriteFrames.GetFrameDuration(animation, index));
            var frame = EncodeTimelineFrame(texture, request, index, durationMs);
            if (frame is null)
            {
                return Failure(
                    request,
                    "frame",
                    $"{animation}:{index}",
                    "SpriteFrames timeline export could not read pixels for one of its frames.");
            }

            frames.Add(frame);
            totalDurationMs += durationMs;
        }

        return BuildTimelineResult(
            request,
            frames,
            totalDurationMs,
            renderMode: "timeline-frames",
            notes:
            [
                $"Rendered SpriteFrames animation '{animation}' as a timeline export.",
            ]);
    }

    private async Task<AssetExtractOperationResult> ExtractTextureAsync(
        Texture2D texture,
        AssetExtractRequestSnapshot request,
        string renderMode,
        IReadOnlyList<string> notes)
    {
        if (texture is AtlasTexture atlasTexture)
        {
            return await ExtractAtlasTextureAsync(atlasTexture, request);
        }

        var image = texture.GetImage();
        if (image is null || image.IsEmpty())
        {
            return Failure(
                request,
                "texture",
                request.SourcePath,
                "Texture2D.get_image() returned no pixels for the requested asset.");
        }

        var resizeNotes = notes.ToList();
        image = ApplyRequestedTextureResize(image, request, resizeNotes);

        // AFTER the resize, never before: the offloaded encoder owns the image it is handed, so the last
        // mutation of it has to have happened on this thread already. See EncodeImageResultAsync.
        return await EncodeImageResultAsync(image, request, renderMode, resizeNotes);
    }

    /// <summary>
    /// Honour a texture extract's requested output size (<c>RenderWidth</c>/<c>RenderHeight</c>) by
    /// resampling the decoded pixels, so a consumer that needs a specific pixel size (a PWA home-screen
    /// icon at 192/512px, a thumbnail) gets it from the extractor instead of shipping the full-resolution
    /// source and scaling it in the browser.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the TEXTURE lane only. The combat-background scene renders honour the same two fields by
    /// re-framing their capture viewport (<see cref="ResolveRequestedViewportSize"/>) — a genuinely
    /// different operation, because a scene render can produce the requested pixels natively while a
    /// texture's pixels already exist and can only be resampled.
    /// </para>
    /// <para>
    /// BYTE-IDENTICAL WHEN UNASKED. Both fields must be positive to reach the resize at all, and a request
    /// whose size already matches the source returns the same image; every pre-existing caller leaves both
    /// null, so nothing that works today changes. Kill switch
    /// <c>SPIRECTL_TEXTURE_RENDER_RESIZE=0</c>.
    /// </para>
    /// <para>
    /// Two engine preconditions are handled here rather than left to fail: <c>Image.Resize</c> refuses a
    /// COMPRESSED image (atlas pages ship as BPTC/S3TC and <c>GetImage()</c> hands them back compressed), and
    /// it reports a Godot error — it does not throw — when it refuses, so the size is re-read afterwards and
    /// the note is only added when the resample actually took. A refusal degrades to the source-resolution
    /// image, which is a worse icon but never a failed request. Lanczos is the filter because these are
    /// DOWNscales of art with fine detail, which is where a box/bilinear filter visibly aliases.
    /// </para>
    /// </remarks>
    private static Image ApplyRequestedTextureResize(
        Image image,
        AssetExtractRequestSnapshot request,
        List<string> notes)
    {
        if (!TextureRenderResizeEnabled || request is not { RenderWidth: > 0, RenderHeight: > 0 })
        {
            return image;
        }

        var targetWidth = request.RenderWidth!.Value;
        var targetHeight = request.RenderHeight!.Value;
        if (image.GetWidth() == targetWidth && image.GetHeight() == targetHeight)
        {
            return image;
        }

        var sourceWidth = image.GetWidth();
        var sourceHeight = image.GetHeight();

        try
        {
            if (image.IsCompressed())
            {
                image.Decompress();
            }

            if (image.IsCompressed())
            {
                notes.Add(
                    $"Requested {targetWidth}x{targetHeight} but the source texture could not be decompressed; returned {sourceWidth}x{sourceHeight}.");
                return image;
            }

            image.Resize(targetWidth, targetHeight, Image.Interpolation.Lanczos);
        }
        catch (Exception exception)
        {
            notes.Add(
                $"Requested {targetWidth}x{targetHeight} but the resample failed ({exception.GetType().Name}); returned {sourceWidth}x{sourceHeight}.");
            return image;
        }

        if (image.GetWidth() != targetWidth || image.GetHeight() != targetHeight)
        {
            notes.Add(
                $"Requested {targetWidth}x{targetHeight} but Image.resize refused it; returned {image.GetWidth()}x{image.GetHeight()}.");
            return image;
        }

        notes.Add($"Resampled the source texture from {sourceWidth}x{sourceHeight} to {targetWidth}x{targetHeight} (Lanczos).");
        return image;
    }

    private async Task<AssetExtractOperationResult> ExtractAtlasTextureAsync(
        AtlasTexture atlasTexture,
        AssetExtractRequestSnapshot request)
    {
        if (atlasTexture.Atlas is null)
        {
            return Failure(
                request,
                "texture",
                request.SourcePath,
                "AtlasTexture did not expose a source atlas texture.");
        }

        var atlasImage = atlasTexture.Atlas.GetImage();
        if (atlasImage is null || atlasImage.IsEmpty())
        {
            return Failure(
                request,
                "texture",
                request.SourcePath,
                "AtlasTexture source atlas returned no pixels.");
        }

        // Atlas pages ship as GPU-compressed images (BPTC/S3TC). Image.GetRegion (blit_rect) and
        // Convert both hard-error on compressed formats, and that error path can take the whole
        // process down under the embedded host's crash handler (the Act 2 Ancient event's option-button
        // sprite hits this). Decompress to a CPU format first. GetImage() returns a fresh copy, so
        // decompressing in place does not affect the live atlas.
        if (atlasImage.IsCompressed())
        {
            atlasImage.Decompress();
        }

        var region = atlasTexture.Region;
        var crop = ClampRegionToImage(region, atlasImage.GetWidth(), atlasImage.GetHeight());
        if (crop.Size.X <= 0 || crop.Size.Y <= 0)
        {
            return Failure(
                request,
                "texture",
                request.SourcePath,
                $"AtlasTexture region {region} did not overlap the source atlas.");
        }

        var image = atlasImage.GetRegion(crop);
        if (image is null || image.IsEmpty())
        {
            return Failure(
                request,
                "texture",
                request.SourcePath,
                "AtlasTexture region crop returned no pixels.");
        }

        if (!HasVisiblePixels(image))
        {
            var rendered = await TryRenderTextureAsync(atlasTexture, request);
            if (rendered is not null)
            {
                return rendered;
            }

            return Failure(
                request,
                "texture",
                request.SourcePath,
                "AtlasTexture region crop and SubViewport fallback both contained only fully transparent pixels.");
        }

        var notes = new List<string>
        {
            "Rendered an AtlasTexture by cropping its source atlas region.",
        };

        // Godot's AtlasTexture reports get_size() = region.size + margin.size and draws
        // the region pixels at margin.position inside that full frame. TextureRect stretch
        // modes (e.g. keep-aspect-centered) fit and center that full frame, so the exported
        // asset must be the framed image (region blitted over transparent margin padding),
        // not the bare region crop -- otherwise the presentation paint centers the trimmed
        // sprite and the icon renders mis-scaled/offset inside its (correct) box. Mirrors the
        // engine's atlasTextureDataUrl. Zero-margin atlases keep their bare crop unchanged.
        var margin = atlasTexture.Margin;
        if (margin.Position != Vector2.Zero || margin.Size != Vector2.Zero)
        {
            var frameSize = atlasTexture.GetSize();
            var frameWidth = Mathf.RoundToInt(frameSize.X);
            var frameHeight = Mathf.RoundToInt(frameSize.Y);
            if (frameWidth > 0 && frameHeight > 0)
            {
                if (image.GetFormat() != Image.Format.Rgba8)
                {
                    image.Convert(Image.Format.Rgba8);
                }

                var frame = Image.CreateEmpty(frameWidth, frameHeight, false, Image.Format.Rgba8);
                // The region may have been clamped to the atlas bounds; keep the visible
                // pixels at the in-frame position the un-clamped region would have drawn at.
                var dstX = Mathf.RoundToInt(margin.Position.X + (crop.Position.X - region.Position.X));
                var dstY = Mathf.RoundToInt(margin.Position.Y + (crop.Position.Y - region.Position.Y));
                frame.BlitRect(image, new Rect2I(Vector2I.Zero, image.GetSize()), new Vector2I(dstX, dstY));
                image = frame;
                notes.Add(
                    "Restored the AtlasTexture margin frame (region+margin) so keep-aspect-centered matches Godot.");
            }
        }

        // AFTER the region crop and the margin frame, never before: the requested size names the pixels the
        // CALLER receives, and both of those steps change what the exported frame is. Resizing the source
        // atlas page first would scale the region rectangle out from under the crop.
        image = ApplyRequestedTextureResize(image, request, notes);

        // The region crop, the margin blit and the resize are all done: this image is finished, unshared CPU
        // data, which is the precondition for handing it to the off-thread encoder.
        return await EncodeImageResultAsync(
            image,
            request,
            renderMode: "flattened-atlas-texture",
            notes: notes);
    }

    private async Task<AssetExtractOperationResult?> TryRenderTextureAsync(
        Texture2D texture,
        AssetExtractRequestSnapshot request)
    {
        var rootViewport = (Engine.GetMainLoop() as SceneTree)?.Root;
        if (rootViewport is null)
        {
            return null;
        }

        var textureSize = texture.GetSize();
        var width = (int)MathF.Ceiling(Math.Clamp(textureSize.X, 1, MaxSceneSize));
        var height = (int)MathF.Ceiling(Math.Clamp(textureSize.Y, 1, MaxSceneSize));
        var size = new Vector2(width, height);
        var rect = new TextureRect
        {
            Texture = texture,
            CustomMinimumSize = size,
            Size = size,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
        };

        var viewport = new SubViewport
        {
            // The marker a host's tree walk recognises an OFF-SCREEN EXTRACTION subtree by, so it can skip
            // it whole rather than freezing the detached rig this render is in the middle of posing.
            // See Sts2OffscreenExtraction.
            Name = Sts2OffscreenExtraction.SubViewportNodeName,
            TransparentBg = true,
            Size = new Vector2I(width, height),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };

        rootViewport.AddChild(viewport);
        viewport.AddChild(rect);

        Image image;
        try
        {
            await AwaitRenderWarmupFramesAsync(rootViewport, 3);
            RenderingServer.ForceDraw();
            var captured = viewport.GetTexture()?.GetImage();
            if (captured is null || captured.IsEmpty() || !HasVisiblePixels(captured))
            {
                return null;
            }

            image = captured;
        }
        finally
        {
            if (viewport.GetParent() is not null)
            {
                rootViewport.RemoveChild(viewport);
            }
            viewport.QueueFree();
        }

        // OUTSIDE the finally on purpose. GetImage() already handed back an unshared CPU copy, so the rig this
        // render needed can be torn down on the main thread immediately rather than being kept alive for the
        // length of an encode the main thread is only parked on.
        return await EncodeImageResultAsync(
            image,
            request,
            renderMode: "flattened-atlas-texture",
            notes:
            [
                "Rendered an AtlasTexture through a temporary SubViewport because its source atlas pixels were not CPU-readable.",
            ]);
    }

    // A raw `res://…tscn`/`.scn` PackedScene resolves to scene STRUCTURE (godot-scene-web
    // GodotSceneState JSON) for the default/auto/structure formats, so a browser fetching
    // a scene through the asset seam gets a renderable tree instead of a PNG. An explicit
    // raster format (png/webp/…) still renders. BREAKING: `.tscn` + `auto` previously
    // rendered a PNG. Semantic visual/background/encounter/texture keys resolve to their
    // own render paths BEFORE this generic PackedScene branch, so they are unaffected.
    private static bool WantsSceneStructure(string? format)
    {
        var normalized = string.IsNullOrWhiteSpace(format) ? "auto" : format.Trim().ToLowerInvariant();
        // "raw" (Godot-native-first) is a structure-family format: it keeps scenes as GodotSceneState JSON
        // and shaders as raw `.gdshader` text, and only diverts the GodotResource-JSON documents
        // (AtlasTexture/Font/Material `.tres`) to raw resource bytes via WantsRawResourceBytes below. So a
        // caller asking for "raw" still reaches this branch for scenes/shaders (unchanged), never a PNG render.
        return normalized is "auto" or "structure" or "json" or "scene-state" or "raw";
    }

    // Godot-native-first: a `?format`-driven request for the RAW resource file bytes instead of the
    // GodotResource-JSON document. Only "raw" opts in; every existing caller (spirectl CLI/dev/presentation
    // pass "auto"/"structure"/"json") keeps the JSON documents byte-for-byte. The couch-coop `/res` route
    // defaults its res:// fetches to "raw" (a native Godot client re-hydrates the `.tres`) and passes "json"
    // only when a JSON consumer (the web) sets `?format=json`.
    internal static bool WantsRawResourceBytes(string? format)
        => string.Equals(format?.Trim(), "raw", StringComparison.OrdinalIgnoreCase);

    // Test seam for the structure-family gate (scenes/shaders stay structured under "raw").
    internal static bool WantsSceneStructureFormat(string? format) => WantsSceneStructure(format);

    // Serve a Godot resource DOCUMENT (AtlasTexture/Font/Material `.tres`) as its RAW file bytes rather than the
    // GodotResource-JSON `ProduceJson` output — the "raw" side of the Godot-native-first switch. The content type
    // is sniffed from the bytes: a Godot TEXT resource (`[gd_resource …]`/`[gd_scene …]`) is `text/plain`, a
    // BINARY resource container (`RSRC`/`RSCC` magic, e.g. an exported `.res`) is `application/octet-stream`. If
    // the raw bytes are unavailable (some exports remap a `.tres` to a binary path this reader can't reach), fall
    // back to the JSON producer so the resource is never lost — with a notice so the fallback is not silent.
    private AssetExtractOperationResult ExtractRawResourceDocument(
        Resource resource,
        string loadPath,
        AssetExtractRequestSnapshot request)
    {
        var bytes = TryReadRawResourceBytes(loadPath);
        if (bytes.Length == 0)
        {
            var json = ExtractGodotResource(resource, request);
            return json with
            {
                Notes = [.. json.Notes, $"format=raw requested but no raw resource bytes were readable at '{loadPath}'; served the GodotResource JSON document instead."],
            };
        }

        var (format, contentType) = ClassifyRawResourceBytes(loadPath, bytes);
        return AssetExtractOperationResult.SuccessRaw(
            requestId: request.RequestId,
            source: DataSourceKind.Live,
            provisional: false,
            format: format,
            contentType: contentType,
            contents: bytes,
            renderMode: "raw-resource-document",
            notes: ["Served the raw Godot resource bytes (format=raw)."]);
    }

    // Sniff the raw resource bytes to a (format token, content type). Godot text resources begin with a
    // `[gd_…` header (`[gd_resource`, `[gd_scene`), binary resource containers with `RSRC` (uncompressed) or
    // `RSCC` (compressed). Falls back to the file extension for anything else.
    internal static (string Format, string ContentType) ClassifyRawResourceBytes(string loadPath, byte[] bytes)
    {
        var extension = System.IO.Path.GetExtension(loadPath).TrimStart('.').ToLowerInvariant();
        var format = string.IsNullOrWhiteSpace(extension) ? "res" : extension;

        if (StartsWith(bytes, (byte)'R', (byte)'S', (byte)'R', (byte)'C')
            || StartsWith(bytes, (byte)'R', (byte)'S', (byte)'C', (byte)'C'))
        {
            return (format, "application/octet-stream");
        }

        if (StartsWith(bytes, (byte)'[', (byte)'g', (byte)'d', (byte)'_'))
        {
            return (format, "text/plain; charset=utf-8");
        }

        var isTextExtension = extension is "tres" or "tscn" or "gdshader" or "gd" or "json" or "txt" or "cfg" or "import" or "godot" or "remap";
        return (format, isTextExtension ? "text/plain; charset=utf-8" : "application/octet-stream");
    }

    private static bool StartsWith(byte[] bytes, byte a, byte b, byte c, byte d)
        => bytes.Length >= 4 && bytes[0] == a && bytes[1] == b && bytes[2] == c && bytes[3] == d;

    // Serve a non-scene Resource (today: fonts) as a godot-scene-web GodotResource JSON
    // document — the resource-level analog of ExtractPackedSceneStructure. Runs on the Godot
    // main thread (property reads require it).
    private AssetExtractOperationResult ExtractGodotResource(
        Resource resource,
        AssetExtractRequestSnapshot request)
    {
        var bytes = Sts2GodotResourceProducer.ProduceJson(resource);
        return AssetExtractOperationResult.SuccessRaw(
            requestId: request.RequestId,
            source: DataSourceKind.Live,
            provisional: false,
            format: "json",
            contentType: "application/json",
            contents: bytes,
            renderMode: Sts2GodotResourceProducer.RenderMode,
            notes: []);
    }

    // Serve a Shader's source text verbatim — the renderer's opt-in WebGL runtime fetches
    // a `.gdshader` path, transpiles the canvas_item subset to GLSL, and runs it live.
    private static AssetExtractOperationResult ExtractShaderSource(
        Shader shader,
        AssetExtractRequestSnapshot request)
        => AssetExtractOperationResult.SuccessRaw(
            requestId: request.RequestId,
            source: DataSourceKind.Live,
            provisional: false,
            format: "gdshader",
            contentType: "text/plain; charset=utf-8",
            contents: System.Text.Encoding.UTF8.GetBytes(shader.Code ?? string.Empty),
            renderMode: "shader-source",
            notes: []);

    // Serve a ShaderInclude's source text verbatim — a `.gdshaderinc` fetch returns the raw
    // include body (`ShaderInclude.Code`) so the WebGL runtime and the native mirror client
    // can inline it into any shader that `#include`s it before transpiling/compiling. Same
    // shape as ExtractShaderSource; the include has no raster rendering.
    private static AssetExtractOperationResult ExtractShaderIncludeSource(
        ShaderInclude shaderInclude,
        AssetExtractRequestSnapshot request)
        => AssetExtractOperationResult.SuccessRaw(
            requestId: request.RequestId,
            source: DataSourceKind.Live,
            provisional: false,
            format: "gdshaderinc",
            contentType: "text/plain; charset=utf-8",
            contents: System.Text.Encoding.UTF8.GetBytes(shaderInclude.Code ?? string.Empty),
            renderMode: "shader-include-source",
            notes: []);

    private AssetExtractOperationResult ExtractPackedSceneStructure(
        PackedScene scene,
        AssetExtractRequestSnapshot request)
    {
        // Runs on the Godot main thread (the whole extract is dispatched there), which
        // SceneState walking + Variant reads require.
        var resourcePath = string.IsNullOrWhiteSpace(scene.ResourcePath) ? request.LoadPath : scene.ResourcePath;
        var bytes = Sts2GodotSceneStateProducer.ProduceJson(scene, resourcePath);
        return AssetExtractOperationResult.SuccessRaw(
            requestId: request.RequestId,
            source: DataSourceKind.Live,
            provisional: false,
            format: "json",
            contentType: "application/json",
            contents: bytes,
            renderMode: Sts2GodotSceneStateProducer.RenderMode,
            notes: []);
    }

    private async Task<AssetExtractOperationResult> ExtractPackedSceneAsync(
        PackedScene scene,
        AssetExtractRequestSnapshot request)
    {
        var instantiated = scene.Instantiate();
        if (instantiated is null)
        {
            return Failure(
                request,
                "scene",
                request.SourcePath,
                "PackedScene.instantiate() returned null.");
        }

        var notes = new List<string>();
        var renderMode = "flattened-merged";
        var warmupFrames = 1;
        var trimTransparentBounds = false;
        var normalizePreviewAlpha = false;
        var disableProcessDuringCapture = true;
        SceneRenderFrame? frameOverride = null;
        Action? afterAttach = null;

        var textureResult = await TryExtractSceneRootTextureAsync(instantiated, request);
        if (textureResult is not null)
        {
            return textureResult;
        }

        // SCENE-SUBTREE STILL (scene-subtree://<scene>?node=<relPath>, couch-coop's room-backdrop lane): swap
        // `instantiated` for ONLY the addressed subtree, detached from the never-tree-entered instantiation —
        // no _Ready/_EnterTree script ever runs (the merchant-room NMerchantHand crash class cannot fire), and
        // the remainder is freed immediately (never entered the tree, so a plain Free is safe). Framed like the
        // event-backdrop still: the caller's probed live frame (EventBackgroundFrame) — or identity when absent
        // — ½Δ-centered into the requested viewport, particles stabilized, spine on its deterministic first
        // frame, processing allowed during warmup.
        var subtreeConfigured = false;
        if (request.SceneSubtreeNodePath is { Length: > 0 } subtreeNodePath)
        {
            var subtree = instantiated.GetNodeOrNull(subtreeNodePath);
            if (subtree is null)
            {
                instantiated.Free();
                return Failure(
                    request,
                    "node",
                    subtreeNodePath,
                    $"The scene has no node at '{subtreeNodePath}'.");
            }

            subtree.GetParent()?.RemoveChild(subtree);
            instantiated.Free();
            instantiated = subtree;

            var subtreeViewport = ResolveRequestedViewportSize(request);
            var subtreeViewportSize = new Vector2I(Math.Max(1, subtreeViewport.X), Math.Max(1, subtreeViewport.Y));
            var subtreeFrame = Sts2EventBackgroundFrameMath.CenterFrame(
                Sts2EventBackgroundFrameMath.TryParseFrameSpec(request.EventBackgroundFrame)
                    ?? new Sts2EventBackgroundFrameMath.EventFrame(0f, 0f, 1f),
                subtreeViewportSize.X,
                subtreeViewportSize.Y);
            renderMode = "flattened-scene-subtree-backdrop";
            warmupFrames = 3;
            frameOverride = new SceneRenderFrame(
                subtreeViewportSize,
                new Vector2(subtreeFrame.PositionX, subtreeFrame.PositionY),
                new Vector2(subtreeFrame.Scale, subtreeFrame.Scale),
                Vector2.Zero,
                new Vector2(subtreeViewportSize.X, subtreeViewportSize.Y));
            notes.Add($"Rendered only the '{subtreeNodePath}' subtree, detached before any tree entry.");
            notes.AddRange(StabilizeBackgroundParticles(instantiated, "scene subtree backdrop"));
            if (TryConfigureSpineFirstFramePreview(instantiated, out var subtreeAnimation))
            {
                notes.Add($"Selected deterministic Spine preview animation '{subtreeAnimation}' for the subtree backdrop.");
            }

            notes.Add("Allowed the subtree backdrop to process during render warmup.");
            disableProcessDuringCapture = false;
            var subtreeRoot = instantiated;
            afterAttach = () =>
            {
                TryConfigureSpineFirstFramePreview(subtreeRoot, out _);
                QueueRedrawCanvasItems(subtreeRoot);
            };
            subtreeConfigured = true;
        }

        // Stage-A1 size-addressable event-backdrop renders (the couch-coop static-background policy): an
        // explicit RenderWidth/RenderHeight selects the MIRROR-CENTERED composition (the 16:9 reference frame
        // translated to the requested viewport's center — what the web mirror composes on a widened stage) and
        // stabilizes particles for a deterministic still. The size-less default path (recon stills) keeps the
        // live root viewport, the game's own aspect lerp, and its live particles — byte-identical to before.
        var explicitEventRenderSize = request is { RenderWidth: > 0, RenderHeight: > 0 };
        if (subtreeConfigured)
        {
            // Configured above; the family branches below must not re-touch the detached subtree (the spine arm
            // in particular would re-frame it as a tight-crop clip).
        }
        else if (TryResolveEventBackgroundFrame(
                request,
                ResolveRequestedViewportSize(request),
                mirrorCentered: explicitEventRenderSize,
                out var backgroundFrame))
        {
            renderMode = "flattened-full-scene-background";
            warmupFrames = 3;
            frameOverride = backgroundFrame;
            notes.Add("Rendered full-frame background scene with the in-game ancient background container transform.");
            if (explicitEventRenderSize)
            {
                notes.Add("Centered the 16:9 reference event-background frame in the requested capture viewport.");
                notes.AddRange(StabilizeBackgroundParticles(instantiated, "event background still"));
            }

            if (TryConfigureSpineFirstFramePreview(instantiated, out var animationName))
            {
                notes.Add(
                    $"Selected deterministic Spine preview animation '{animationName}' for the ancient event background.");
            }

            notes.Add("Allowed the ancient event background scene to process during render warmup.");
            disableProcessDuringCapture = false;
            afterAttach = () =>
            {
                TryConfigureSpineFirstFramePreview(instantiated, out _);
                QueueRedrawCanvasItems(instantiated);
            };
        }
        else if (TryResolveCharacterSelectBackgroundFrame(request, ResolveRootViewportSize(), out var characterSelectFrame))
        {
            renderMode = "flattened-character-select-background";
            warmupFrames = 3;
            frameOverride = characterSelectFrame;
            instantiated = CreateCharacterSelectBackgroundRenderRoot(instantiated, characterSelectFrame.ViewportSize);
            notes.Add("Rendered full-frame character-select background scene inside the in-game lobby background container transform.");
            if (TryConfigureSpineFirstFramePreview(instantiated, out var animationName))
            {
                notes.Add(
                    $"Selected deterministic Spine preview animation '{animationName}' for the character-select background.");
            }

            notes.Add("Allowed the character-select background scene to process during render warmup.");
            disableProcessDuringCapture = false;
            afterAttach = () =>
            {
                TryConfigureSpineFirstFramePreview(instantiated, out _);
                QueueRedrawCanvasItems(instantiated);
            };
        }
        else if (TryResolveCombatBackgroundFrame(request, ResolveRequestedViewportSize(request), out var combatBackgroundFrame))
        {
            TryPrepareCombatBackgroundLayers(
                instantiated,
                out var combatNotes,
                out _);
            renderMode = CombatBackgroundRenderMode;
            warmupFrames = 3;
            frameOverride = combatBackgroundFrame;
            notes.Add(
                $"Rendered literal combat background root scene from {NormalizeResourcePath(request.LoadPath)}. Use composed://combat-background/<id>/image for composed layer-stack exports.");
            notes.AddRange(combatNotes);
        }
        else if (TryConfigureSpineFirstFramePreview(instantiated, out var animationName))
        {
            renderMode = "flattened-spine-first-frame";
            warmupFrames = 3;
            trimTransparentBounds = true;
            // Let the Spine driver advance during warmup. Without processing, the
            // skeleton stays on its transparent setup pose and the capture flattens
            // to fully transparent pixels (e.g. monster creature_visuals scenes such
            // as nibbit/osty). Mirrors the event/character-select background Spine
            // paths, which enable processing + queue a redraw after attaching.
            disableProcessDuringCapture = false;
            afterAttach = () =>
            {
                TryConfigureSpineFirstFramePreview(instantiated, out _);
                QueueRedrawCanvasItems(instantiated);
            };
            notes.Add(
                $"Rendered a Spine-backed scene after selecting the deterministic preview animation '{animationName}' and waiting for the first visible frame.");
        }
        else if (TryConfigureVfxPreview(instantiated))
        {
            renderMode = "flattened-vfx-transparent-preview";
            warmupFrames = 8;
            trimTransparentBounds = true;
            normalizePreviewAlpha = true;
            notes.Add(
                "Rendered a representative VFX preview with transparent-background alpha normalization for additive effects.");
        }
        else
        {
            notes.Add("Rendered a flattened scene composition through a temporary SubViewport.");
        }

        return await RenderNodeResultAsync(
            instantiated,
            request,
            renderMode,
            notes,
            warmupFrames,
            trimTransparentBounds,
            normalizePreviewAlpha,
            frameOverride,
            afterAttach: afterAttach,
            disableProcessDuringCapture: disableProcessDuringCapture);
    }

    private async Task<AssetExtractOperationResult> ExtractCharacterSelectBgSpineStillAsync(
        Node sceneRoot,
        AssetExtractRequestSnapshot request)
    {
        var sourceScenePath = NormalizeResourcePath(request.LoadPath);
        var spineNode = FindFirstSpinePreviewNode(sceneRoot) as Node;
        if (spineNode is null)
        {
            sceneRoot.QueueFree();
            return AssetExtractOperationResult.Failure(
                requestId: request.RequestId,
                source: DataSourceKind.Live,
                provisional: false,
                code: AssetExtractFailureCode.RuntimeFailure,
                message: "The character-select background scene did not contain a renderable Spine preview node.",
                details:
                [
                    new AssetExtractDetail(
                        Field: "source_scene_path",
                        Value: sourceScenePath,
                        Note: "No SpineSprite or node exposing skeleton_data_res was found in the source scene."),
                ]);
        }

        var sourceNodePath = NodePathWithin(sceneRoot, spineNode);
        var particleCount = CountCpuParticlesOutside(sceneRoot, spineNode);
        var notes = new List<string>
        {
            "Rendered only the first Spine preview subtree from the character-select background scene while preserving its original scene hierarchy.",
            "Kept the character-select background scene hierarchy mounted and hid non-Spine sibling canvas items before capture.",
            "Allowed the character-select background Spine driver to process during render warmup.",
            CharacterSelectBgSpineStillAlphaNormalizationNote,
        };
        var notices = new List<AssetExtractNoticeSnapshot>();
        if (particleCount > 0)
        {
            notices.Add(new AssetExtractNoticeSnapshot(
                "asset-character-select-bg-spine-particles-skipped",
                "info",
                $"Skipped {particleCount} CPUParticles2D node(s) outside the captured Spine subtree.",
                sourceScenePath));
        }

        if (TryStartSpinePreviewAnimation(spineNode, out var animationName))
        {
            notes.Add(
                $"Started Spine preview animation '{animationName}' for the character-select background Spine still without forcing preview_time.");
        }

        TryResolveCharacterSelectBgSpineStillFrame(
            spineNode,
            out _,
            out var localBounds,
            out var globalBounds,
            out var boundsSource);
        if (boundsSource == "spine")
        {
            notices.Add(new AssetExtractNoticeSnapshot(
                "asset-character-select-bg-spine-bounds-inferred",
                "info",
                "Placement bounds were inferred from Spine/skeleton bounds because no authored Bounds node was found.",
                sourceNodePath));
        }
        else if (boundsSource == "diagnostic")
        {
            notices.Add(new AssetExtractNoticeSnapshot(
                "asset-character-select-bg-spine-bounds-diagnostic-fallback",
                "warning",
                "Placement bounds used a bounded diagnostic fallback because no authored or skeleton bounds were available.",
                sourceNodePath));
        }

        var result = await WarmUpCharacterSelectSceneThenRenderSpineSubtreeAsync(
            sceneRoot,
            spineNode,
            request,
            notes);

        if (result.Error is not null)
        {
            return result with { Notices = result.Notices.Concat(notices).ToArray() };
        }

        return result with
        {
            Notices = result.Notices.Concat(notices).ToArray(),
            PlacementMetadata = new AssetExtractPlacementMetadataSnapshot(
                SourceScenePath: sourceScenePath,
                SourceNodePath: sourceNodePath,
                LocalBounds: ToSnapshot(localBounds),
                GlobalBounds: ToSnapshot(globalBounds),
                OutputWidth: result.Width,
                OutputHeight: result.Height,
                RenderMode: CharacterSelectBgSpineStillRenderMode),
        };
    }

    private async Task<AssetExtractOperationResult> ExtractEventBackgroundSpineStillAsync(
        Node sceneRoot,
        AssetExtractRequestSnapshot request)
    {
        var sourceScenePath = NormalizeResourcePath(request.LoadPath);
        var spineNode = FindFirstSpinePreviewNode(sceneRoot) as Node;
        if (spineNode is null)
        {
            sceneRoot.QueueFree();
            return AssetExtractOperationResult.Failure(
                requestId: request.RequestId,
                source: DataSourceKind.Live,
                provisional: false,
                code: AssetExtractFailureCode.RuntimeFailure,
                message: "The event background scene did not contain a renderable Spine preview node.",
                details:
                [
                    new AssetExtractDetail(
                        Field: "source_scene_path",
                        Value: sourceScenePath,
                        Note: "No SpineSprite or node exposing skeleton_data_res was found in the source scene."),
                ]);
        }

        var notes = new List<string>
        {
            "Rendered only the first Spine preview subtree from the event background scene while preserving its authored placement.",
            "Hid canvas items outside the Spine preview subtree; the ancient background container transform is applied later by the presentation catalog.",
            EventBackgroundSpineStillAlphaNormalizationNote,
        };

        // Keep the SpineSprite at its authored scene position and capture the scene's
        // authored overscan extent (the cave layers' rect), not the bare viewport: the
        // waterfall Spine still reaches below the viewport bottom, so a viewport-clipped
        // capture would cut it. The catalog places the still at the same authored rect and
        // the AncientBgContainer 0.89 scale + offset then carries it to the viewport bottom,
        // matching the cave layers (no container scale is baked into the capture).
        HideNodesOutsideCapturedSubtree(sceneRoot, spineNode);

        // WS-NEOW still zero-placement fix: read the SpineSprite's node-relative transform (invariant to the
        // capture placement) BEFORE RenderNodeResultAsync repositions the root, so the captured rect can be
        // expressed node-locally and forwarded as clipPlacement (today the wrapped timeline shipped ClipLocal*=0,
        // and the web min/static tier — which draws the shared canvas AT clipPlacement — rendered scale(0) =
        // invisible).
        var tNode = ComputeNodeRelativeTransform(sceneRoot, spineNode);

        if (TryStartSpinePreviewAnimation(spineNode, out var animationName))
        {
            notes.Add($"Started Spine preview animation '{animationName}' for the event background Spine still without forcing preview_time.");
        }

        SceneRenderFrame frame;
        Rect2 captureRect;
        if (TryResolveBackgroundOverscanExtent(sceneRoot, out var overscanExtent))
        {
            frame = SpineStillOverscanFrame(overscanExtent);
            captureRect = overscanExtent;
            notes.Add(
                $"Captured the Spine still over the authored background overscan extent "
                + $"({overscanExtent.Position.X},{overscanExtent.Position.Y} "
                + $"{overscanExtent.Size.X}x{overscanExtent.Size.Y}) so it reaches the viewport bottom after the container scale.");
        }
        else
        {
            frame = AuthoredViewportFrame();
            var viewportSize = ResolveRootViewportSize();
            captureRect = new Rect2(0f, 0f, viewportSize.X, viewportSize.Y);
            notes.Add("No overscanning cave layers were found; captured the Spine still at the bare viewport extent.");
        }

        var result = await RenderNodeResultAsync(
            sceneRoot,
            request,
            EventBackgroundSpineStillRenderMode,
            notes,
            warmupFrames: 3,
            trimTransparentBounds: false,
            normalizePreviewAlpha: EventBackgroundSpineStillNormalizePreviewAlpha,
            frameOverride: frame,
            afterAttach: () =>
            {
                TryStartSpinePreviewAnimation(spineNode, out _);
                QueueRedrawCanvasItems(sceneRoot, makeVisible: false);
            },
            beforeCapture: () =>
            {
                QueueRedrawCanvasItems(sceneRoot, makeVisible: false);
            },
            disableProcessDuringCapture: false);

        if (result.Error is not null)
        {
            return result;
        }

        // Local = T_node^-1(captureRect). The still raster (result.Width x result.Height, no trim) IS the captured
        // rect at scale 1, so captureScale=1 and the cell is the raster size. A rotated/skewed node transform
        // yields null (char-select still keeps its documented latent null); the wrapper forwards whatever we set.
        var nodeAxisAligned = MathF.Abs(tNode.X.Y) < 1e-4f && MathF.Abs(tNode.Y.X) < 1e-4f;
        var placement = Sts2SpineEventBackgroundClip.ComposeCellPlacement(
            captureRect.Position.X,
            captureRect.Position.Y,
            tNode.Origin.X,
            tNode.Origin.Y,
            tNode.X.X,
            tNode.Y.Y,
            nodeAxisAligned,
            captureScale: 1d,
            cellWidth: result.Width,
            cellHeight: result.Height);
        if (placement is not { } cell)
        {
            return result;
        }

        return result with
        {
            ClipPlacement = new AssetExtractClipPlacement(cell.LocalX, cell.LocalY, cell.LocalWidth, cell.LocalHeight),
        };
    }

    private async Task<AssetExtractOperationResult> WarmUpCharacterSelectSceneThenRenderSpineSubtreeAsync(
        Node sceneRoot,
        Node spineNode,
        AssetExtractRequestSnapshot request,
        IReadOnlyList<string> notes)
    {
        var characterSelectFrame = TryResolveCharacterSelectBackgroundFrame(
            request,
            ResolveRootViewportSize(),
            out var resolvedFrame)
            ? resolvedFrame
            : AuthoredViewportFrame();
        // The render root is ALWAYS built at the ROOT viewport size: the synthetic AnimatedBg container's size is
        // `viewport + (640,120)`, and the background scene root is CENTER-anchored inside it, so feeding it an
        // overscan size would move the rig relative to the live screen. Only the CAPTURE FRAME widens below.
        var renderRoot = CreateCharacterSelectBackgroundRenderRoot(sceneRoot, characterSelectFrame.ViewportSize);
        HideNodesOutsideCapturedSubtree(renderRoot, spineNode);
        var hiddenEffectNodeCount = HideCharacterSelectBgSpineStillEffectNodes(renderRoot);
        var renderNotes = hiddenEffectNodeCount > 0
            ? notes.Concat([
                $"Disabled {hiddenEffectNodeCount} particle/light node(s) while rendering the isolated character-select background Spine still.",
            ]).ToArray()
            : notes;

        // R10 OVERSCAN. Capture over the authored container extent (~2816x1320 at a 1920x1080 root viewport)
        // instead of the bare viewport, so the raster actually CONTAINS the side art a widened mirror stage
        // re-centres onto. `SpineStillOverscanFrame` shifts the render root by -extent.Position and sizes the
        // SubViewport to the extent, so a render-root point p lands at capture pixel `p - extent.Position` —
        // which is why the placement composition below has to add extent.Position back to the reported trim
        // region (capture-pixel space) before differencing it against T_node (render-root space).
        var captureFrame = characterSelectFrame;
        var captureOrigin = Vector2.Zero;
        if (CharacterSelectStillOverscanEnabled)
        {
            var extent = CharacterSelectBackgroundOverscanExtent(characterSelectFrame.ViewportSize);
            captureFrame = SpineStillOverscanFrame(extent);
            captureOrigin = extent.Position;
            renderNotes = renderNotes.Concat([
                $"Captured the character-select background still over the authored container extent "
                + $"({extent.Position.X},{extent.Position.Y} {extent.Size.X}x{extent.Size.Y}) instead of the bare "
                + "root viewport, so the side art survives a wider-than-16:9 client stage.",
            ]).ToArray();
        }

        // WS5 CHAR-SELECT STILL PLACEMENT (the documented latent null). This lane returned NO ClipPlacement, so
        // WrapStillImageAsTimeline forwarded null, the wire shipped ClipLocalWidth=0, and the web client's
        // `scale = localWidth / canvasWidth` came out 0 — the char-select background painted at scale(0), i.e.
        // NOTHING (the menu backdrop showed through; Silent's scene looked "partial" because its plain
        // TextureRects are streamed as ordinary nodes and still rendered).
        //
        // Same shape as the event-background still (Local = T_node^-1(captureRect)) with two differences:
        //   * the capture frame is the SYNTHETIC char-select render root (viewport-sized, scale 1, at the origin),
        //     so T_node is read relative to THAT root — the synthetic AnimatedBg container (offset -388,-80,
        //     scale 1.1) is inside T_node and therefore cancels out of the node-local rect exactly, leaving the
        //     client to apply the REAL streamed container transform once;
        //   * this lane TRIMS transparent bounds, so the raster is NOT the capture rect — the placement is
        //     composed from the reported trim region instead (captureScale is still 1: the frame renders at
        //     scale 1, so one raster pixel is one render-root unit).
        // T_node is read at beforeCapture (layout settled, node in the tree), never from the un-mounted scene.
        var tNode = Transform2D.Identity;
        var haveNodeTransform = false;
        var captureRegion = new Rect2I();
        var haveCaptureRegion = false;

        var result = await RenderNodeResultAsync(
            renderRoot,
            request,
            CharacterSelectBgSpineStillRenderMode,
            renderNotes,
            warmupFrames: 3,
            trimTransparentBounds: true,
            normalizePreviewAlpha: CharacterSelectBgSpineStillNormalizePreviewAlpha,
            frameOverride: captureFrame,
            afterAttach: () =>
            {
                TryStartSpinePreviewAnimation(spineNode, out _);
                QueueRedrawCanvasItems(renderRoot);
            },
            beforeCapture: () =>
            {
                tNode = ComputeNodeRelativeTransform(renderRoot, spineNode);
                haveNodeTransform = true;
            },
            disableProcessDuringCapture: false,
            reportCaptureRegion: region =>
            {
                captureRegion = region;
                haveCaptureRegion = true;
            });

        if (result.Error is not null
            || !CharacterSelectStillPlacementEnabled
            || !haveNodeTransform
            || !haveCaptureRegion)
        {
            return result;
        }

        // The trim region is in CAPTURE-PIXEL space; T_node is in RENDER-ROOT space. They coincide only when the
        // capture frame sits at the origin (the pre-overscan case, captureOrigin = 0), so lift the region back
        // into render-root space before differencing.
        var nodeAxisAligned = MathF.Abs(tNode.X.Y) < 1e-4f && MathF.Abs(tNode.Y.X) < 1e-4f;
        var placement = Sts2SpineEventBackgroundClip.ComposeCellPlacement(
            captureRegion.Position.X + captureOrigin.X,
            captureRegion.Position.Y + captureOrigin.Y,
            tNode.Origin.X,
            tNode.Origin.Y,
            tNode.X.X,
            tNode.Y.Y,
            nodeAxisAligned,
            captureScale: 1d,
            cellWidth: result.Width,
            cellHeight: result.Height);
        if (placement is not { } cell)
        {
            // Rotated/skewed/degenerate node transform: no simple rect exists, so keep the old null placement
            // rather than shipping a wrong one (the client's zero-guard then draws it at scale 1).
            return result;
        }

        if (Sts2SpineDiagnostics.Current.Enabled)
        {
            Sts2SpineDiagnostics.Current.Log(
                $"charselect-still-placement node={NodePathWithin(renderRoot, spineNode)} "
            + $"tNode=origin({tNode.Origin.X:0.##},{tNode.Origin.Y:0.##}) scale({tNode.X.X:0.####},{tNode.Y.Y:0.####}) "
                + $"captureOrigin=({captureOrigin.X:0.##},{captureOrigin.Y:0.##}) "
                + $"capture=({captureRegion.Position.X},{captureRegion.Position.Y} {captureRegion.Size.X}x{captureRegion.Size.Y}) "
                + $"raster={result.Width}x{result.Height} "
                + $"local=({cell.LocalX:0.##},{cell.LocalY:0.##} {cell.LocalWidth:0.##}x{cell.LocalHeight:0.##})");
        }

        return result with
        {
            ClipPlacement = new AssetExtractClipPlacement(cell.LocalX, cell.LocalY, cell.LocalWidth, cell.LocalHeight),
        };
    }

    private async Task<AssetExtractOperationResult> ExtractCharacterSelectBackgroundAsync(
        PackedScene scene,
        AssetExtractRequestSnapshot request)
    {
        var instantiated = scene.Instantiate();
        if (instantiated is null)
        {
            return Failure(
                request,
                "scene",
                request.SourcePath,
                "PackedScene.instantiate() returned null.");
        }

        if (!TryResolveCharacterSelectBackgroundFrame(request, ResolveRootViewportSize(), out var frame))
        {
            instantiated.QueueFree();
            return Failure(
                request,
                "scene",
                request.SourcePath,
                "The requested resource path was not recognized as a character-select background scene.");
        }

        var renderRoot = CreateCharacterSelectBackgroundRenderRoot(instantiated, frame.ViewportSize);
        var notes = new List<string>
        {
            "Rendered full-frame character-select background scene inside the in-game lobby background container transform.",
        };

        if (TryConfigureSpineFirstFramePreview(renderRoot, out var animationName))
        {
            notes.Add(
                $"Selected deterministic Spine preview animation '{animationName}' for the character-select background.");
        }

        notes.Add("Allowed the character-select background scene to process during render warmup.");
        return await RenderNodeResultAsync(
            renderRoot,
            request,
            "flattened-character-select-background",
            notes,
            warmupFrames: 3,
            trimTransparentBounds: false,
            normalizePreviewAlpha: false,
            frameOverride: frame,
            afterAttach: () =>
            {
                TryConfigureSpineFirstFramePreview(renderRoot, out _);
                QueueRedrawCanvasItems(renderRoot);
            },
            disableProcessDuringCapture: false);
    }

    private async Task<AssetExtractOperationResult> ExtractComposedCombatBackgroundAsync(
        CombatBackgroundAliasRequest combatBackground,
        AssetExtractRequestSnapshot request)
    {
        Resource? resource;
        using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.SceneLoad))
        {
            resource = ResourceLoader.Load(combatBackground.RootScenePath);
        }

        if (resource is not PackedScene scene)
        {
            return Failure(
                request,
                "load_path",
                combatBackground.RootScenePath,
                "The live host could not load the requested combat background root scene.");
        }

        return await ExtractComposedCombatBackgroundSceneAsync(
            combatBackground,
            request,
            scene,
            DiscoverCombatBackgroundLayerPaths,
            LoadCombatBackgroundLayerScene);
    }

    private async Task<AssetExtractOperationResult> ExtractComposedCombatBackgroundSceneAsync(
        CombatBackgroundAliasRequest combatBackground,
        AssetExtractRequestSnapshot request,
        PackedScene scene,
        Func<string, IReadOnlyList<string>> discoverLayerPaths,
        Func<string, PackedScene?> loadLayerScene,
        Func<Node, IReadOnlyList<string>>? configureRoot = null,
        IReadOnlyList<string>? leadingNotes = null,
        string renderMode = ComposedCombatBackgroundRenderMode,
        SceneRenderFrame? frameOverride = null,
        EncounterRenderDiagnosticsBuilder? encounterDiagnostics = null)
    {
        Node? instantiated;
        using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.SceneInstantiate))
        {
            instantiated = scene.Instantiate();
        }

        if (instantiated is null)
        {
            return Failure(
                request,
                "scene",
                combatBackground.RootScenePath,
                "PackedScene.instantiate() returned null for the combat background root scene.");
        }

        encounterDiagnostics?.SetRootNode(instantiated);
        var placeholders = GetCombatBackgroundPlaceholderNames(instantiated);
        var layerSelection = Sts2CombatBackgroundLayerSelection.Parse(request.CompositionSelector);
        if (layerSelection.Error is not null)
        {
            instantiated.QueueFree();
            return Failure(
                request,
                Sts2CombatBackgroundLayerSelection.RequestField,
                request.CompositionSelector ?? string.Empty,
                layerSelection.Error);
        }

        IReadOnlyList<string> discoveredLayerPaths;
        using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.LayerDiscover))
        {
            discoveredLayerPaths = layerSelection.IsExplicit
                ? layerSelection.LayerPaths
                : discoverLayerPaths(combatBackground.BackgroundId);
        }

        var compositionPlan = BuildCombatBackgroundCompositionPlan(
            combatBackground.BackgroundId,
            placeholders,
            discoveredLayerPaths);
        if (layerSelection.IsExplicit)
        {
            var unhonoredLayerPaths = Sts2CombatBackgroundLayerSelection.FindUnhonoredLayerPaths(
                layerSelection.LayerPaths,
                compositionPlan.LayerGroups.Select(group => group.SelectedPath));
            if (unhonoredLayerPaths.Count > 0)
            {
                instantiated.QueueFree();
                return Failure(
                    request,
                    Sts2CombatBackgroundLayerSelection.RequestField,
                    string.Join(",", unhonoredLayerPaths),
                    "The explicit combat background layer selection could not be honored for these layer path(s): "
                        + "each entry must be a recognized layer scene (…_bg_NN_* / …_fg_*) matching exactly one root placeholder.");
            }

            foreach (var layerPath in layerSelection.LayerPaths)
            {
                using var layerScope = Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.LayerLoad);
                Sts2RenderPhaseProfile.Count(Sts2RenderPhaseProfile.Counter.LayerLoads);
                if (loadLayerScene(layerPath) is null)
                {
                    instantiated.QueueFree();
                    return Failure(
                        request,
                        Sts2CombatBackgroundLayerSelection.RequestField,
                        layerPath,
                        "The live host could not load an explicitly selected combat background layer scene.");
                }
            }
        }

        bool composed;
        IReadOnlyList<string> compositionNotes;
        using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.LayerCompose))
        {
            composed = TryComposeCombatBackgroundAliasLayers(
                instantiated,
                compositionPlan,
                loadLayerScene,
                out _,
                out _,
                out compositionNotes);
        }

        if (!composed)
        {
            instantiated.QueueFree();
            return Failure(
                request,
                "combat_background_layers",
                request.LoadPath,
                compositionNotes.FirstOrDefault()
                    ?? "No combat background layer scenes were composed for the requested alias.");
        }

        var notes = new List<string>();
        if (leadingNotes is not null)
        {
            notes.AddRange(leadingNotes);
        }

        notes.Add($"Rendered composed combat background alias '{request.LoadPath}' from {combatBackground.RootScenePath}.");
        notes.AddRange(configureRoot?.Invoke(instantiated) ?? []);
        notes.Add(layerSelection.IsExplicit
            ? $"Applied explicit combat background layer selection from the request ({layerSelection.LayerPaths.Count} layer path(s))."
            : $"Discovered deterministic combat background layer paths from res://scenes/backgrounds/{combatBackground.BackgroundId}/layers.");
        notes.AddRange(compositionNotes);
        using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.LayerStabilize))
        {
            notes.AddRange(StabilizeCombatBackgroundAliasPreview(instantiated));
        }

        notes.Add(
            "Rendered composed combat background at viewport framing using runtime BgContainer placement: centered, x-offset 23, scale 0.9, preserved root Control size.");

        using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.LayerPrepare))
        {
            TryPrepareCombatBackgroundLayers(
                instantiated,
                out var renderNotes,
                out _);
            notes.AddRange(renderNotes);
        }

        if (frameOverride is null)
        {
            using var frameScope = Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.FrameResolve);
            TryResolveCombatBackgroundFrame(
                new AssetExtractRequestSnapshot(
                    request.RequestId,
                    request.SourceRoot,
                    combatBackground.RootScenePath,
                    combatBackground.RootScenePath,
                    request.OutputFormat),
                ResolveRequestedViewportSize(request),
                out var resolvedFrame);
            frameOverride = resolvedFrame;
        }

        return await RenderNodeResultAsync(
            instantiated,
            request,
            renderMode,
            notes,
            warmupFrames: 3,
            trimTransparentBounds: false,
            normalizePreviewAlpha: false,
            frameOverride: frameOverride,
            encounterDiagnostics: encounterDiagnostics);
    }

    private AssetExtractOperationResult ExtractStyleBoxTexture(
        StyleBoxTexture styleBoxTexture,
        AssetExtractRequestSnapshot request)
    {
        var minimum = styleBoxTexture.GetMinimumSize();
        var size = new Vector2(
            Math.Max(1, minimum.X),
            Math.Max(1, minimum.Y));
        if (size.X <= 1 || size.Y <= 1)
        {
            size = new Vector2(DefaultSceneSize / 4f, DefaultSceneSize / 4f);
        }

        var panel = new Panel
        {
            CustomMinimumSize = size,
            Size = size,
        };
        panel.AddThemeStyleboxOverride("panel", styleBoxTexture);

        return RenderNodeResult(
            panel,
            request,
            renderMode: "flattened-stylebox",
            notes:
            [
                "Rendered a StyleBoxTexture through a temporary Panel container.",
            ]);
    }

    private AssetExtractOperationResult ExtractTheme(
        Theme theme,
        AssetExtractRequestSnapshot request)
    {
        var size = new Vector2(DefaultSceneSize / 2f, DefaultSceneSize / 3f);
        var panel = new Panel
        {
            Theme = theme,
            CustomMinimumSize = size,
            Size = size,
        };

        var title = new Label
        {
            Theme = theme,
            Text = "Theme Preview",
            Position = new Vector2(24, 20),
        };
        var button = new Button
        {
            Theme = theme,
            Text = "Sample Button",
            Position = new Vector2(24, 72),
            Size = new Vector2(size.X - 48, 44),
        };
        var caption = new Label
        {
            Theme = theme,
            Text = "Representative preview only.",
            Position = new Vector2(24, 130),
        };
        panel.AddChild(title);
        panel.AddChild(button);
        panel.AddChild(caption);

        return RenderNodeResult(
            panel,
            request,
            renderMode: "flattened-theme-sample",
            notes:
            [
                "Rendered a sampled Theme preview through a temporary Panel, Button, and Label composition.",
            ]);
    }

    private AssetExtractOperationResult ExtractTileSet(
        TileSet tileSet,
        AssetExtractRequestSnapshot request)
    {
#pragma warning disable CS0618 // TileMap support is retained for legacy TileSet preview compatibility.
        var tileMap = new TileMap
        {
            TileSet = tileSet,
            Scale = new Vector2(4, 4),
        };
#pragma warning restore CS0618

        if (!TryAssignDeterministicTile(tileSet, tileMap, out var tileNote))
        {
            return UnsupportedResourceTypeFailure(
                request,
                tileSet,
                "did not expose a deterministic atlas or scene tile that could be rendered in a temporary TileMap.");
        }

        return RenderNodeResult(
            tileMap,
            request,
            renderMode: "flattened-tileset-sample",
            notes:
            [
                tileNote,
            ]);
    }

    private AssetExtractOperationResult ExtractCanvasItemMaterial(
        Material material,
        AssetExtractRequestSnapshot request)
    {
        var size = new Vector2(DefaultSceneSize / 3f, DefaultSceneSize / 3f);
        var rect = new ColorRect
        {
            CustomMinimumSize = size,
            Size = size,
            Color = Colors.White,
            Material = material,
        };

        return RenderNodeResult(
            rect,
            request,
            renderMode: "flattened-material-sample",
            notes:
            [
                $"Rendered a sampled {material.GetType().Name} preview through a temporary ColorRect canvas item.",
            ]);
    }

    private AssetExtractOperationResult RenderNodeResult(
        Node node,
        AssetExtractRequestSnapshot request,
        string renderMode,
        IReadOnlyList<string> notes)
    {
        var rootViewport = (Engine.GetMainLoop() as SceneTree)?.Root;
        if (rootViewport is null)
        {
            return Failure(
                request,
                "viewport",
                "root",
                "Engine.GetMainLoop() did not expose a root viewport for scene rendering.");
        }

        var viewport = new SubViewport
        {
            // The marker a host's tree walk recognises an OFF-SCREEN EXTRACTION subtree by, so it can skip
            // it whole rather than freezing the detached rig this render is in the middle of posing.
            // See Sts2OffscreenExtraction.
            Name = Sts2OffscreenExtraction.SubViewportNodeName,
            TransparentBg = true,
            Size = ResolveViewportSize(node),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };

        PositionCanvasItem(node);
        rootViewport.AddChild(viewport);
        viewport.AddChild(node);

        try
        {
            RenderingServer.ForceDraw();
            var image = viewport.GetTexture()?.GetImage();
            if (image is null || image.IsEmpty())
            {
                return Failure(
                    request,
                    "scene",
                    request.SourcePath,
                    IsRenderingHeadless()
                        ? HeadlessRenderExplanation
                        : "The live bridge rendered an empty viewport while flattening the scene.");
            }

            if (!HasVisiblePixels(image))
            {
                return Failure(
                    request,
                    "scene",
                    request.SourcePath,
                    "The live bridge rendered only fully transparent pixels while flattening the scene.");
            }

            return EncodeImageResult(
                image,
                request,
                renderMode,
                notes);
        }
        finally
        {
            if (viewport.GetParent() is not null)
            {
                rootViewport.RemoveChild(viewport);
            }
            viewport.QueueFree();
        }
    }

    private async Task<AssetExtractOperationResult> RenderNodeResultAsync(
        Node node,
        AssetExtractRequestSnapshot request,
        string renderMode,
        IReadOnlyList<string> notes,
        int warmupFrames,
        bool trimTransparentBounds,
        bool normalizePreviewAlpha = false,
        SceneRenderFrame? frameOverride = null,
        Action? afterAttach = null,
        Action? beforeCapture = null,
        Action<Image>? postProcessImage = null,
        EncounterRenderDiagnosticsBuilder? encounterDiagnostics = null,
        bool disableProcessDuringCapture = true,
        // WS5: reports the region of the CAPTURED VIEWPORT (in viewport pixels) that the returned raster
        // actually covers — the transparent-trim rect when trimming is on, the whole image otherwise. A lane
        // that expresses its still as a node-local placement rect needs this, because with trimming the raster
        // is NOT the capture rect. Never invoked on a failure path.
        Action<Rect2I>? reportCaptureRegion = null)
    {
        var rootViewport = (Engine.GetMainLoop() as SceneTree)?.Root;
        if (rootViewport is null)
        {
            encounterDiagnostics?.AddTimingNote("Capture did not start because Engine.GetMainLoop() did not expose a root viewport.");
            return Failure(
                request,
                "viewport",
                "root",
                "Engine.GetMainLoop() did not expose a root viewport for scene rendering.",
                diagnostic: encounterDiagnostics?.Build().ToDictionary());
        }

        var frame = frameOverride ?? ResolveSceneFrame(node);
        encounterDiagnostics?.SetFrame(frame);
        encounterDiagnostics?.AddTimingNote($"Configured SubViewport with {Math.Max(0, warmupFrames)} warmup frame(s).");
        var viewport = new SubViewport
        {
            // The marker a host's tree walk recognises an OFF-SCREEN EXTRACTION subtree by, so it can skip
            // it whole rather than freezing the detached rig this render is in the middle of posing.
            // See Sts2OffscreenExtraction.
            Name = Sts2OffscreenExtraction.SubViewportNodeName,
            TransparentBg = true,
            Size = frame.ViewportSize,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };

        PositionCanvasItem(node, frame);
        if (disableProcessDuringCapture)
        {
            node.ProcessMode = Node.ProcessModeEnum.Disabled;
        }
        // Some Godot builds (including the "live host" validation gate) do not expose PhysicsProcessMode on Node.
        // Disabling regular ProcessMode is sufficient for our offscreen render capture.
        Sts2RenderPhaseProfile.Set(
            Sts2RenderPhaseProfile.Counter.ViewportPixels,
            (long)frame.ViewportSize.X * frame.ViewportSize.Y);
        // The captured image LEAVES the try/finally before it is encoded, so the SubViewport (a full-size render
        // target — 2520x1080 for a combat background) is released first instead of being pinned across a
        // half-second encode. GetImage() already handed back CPU-side pixels that own their own buffer.
        Image? captured = null;
        try
        {
            using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.ViewportAttach))
            {
                rootViewport.AddChild(viewport);
                encounterDiagnostics?.AddTimingNote("SubViewport attached to root viewport.");
                viewport.AddChild(node);
                encounterDiagnostics?.AddTimingNote("Render node attached to SubViewport.");
                afterAttach?.Invoke();
                encounterDiagnostics?.AddTimingNote("After-attach setup completed.");
            }

            await AwaitRenderWarmupFramesAsync(rootViewport, Math.Max(0, warmupFrames));
            encounterDiagnostics?.AddTimingNote("Warmup frames completed.");

            beforeCapture?.Invoke();
            encounterDiagnostics?.AddTimingNote("Before-capture setup completed.");
            using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.ForceDraw))
            {
                RenderingServer.ForceDraw();
            }

            encounterDiagnostics?.AddTimingNote("RenderingServer.ForceDraw completed before texture capture.");
            Image? image;
            using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.Readback))
            {
                image = viewport.GetTexture()?.GetImage();
            }

            if (image is null || image.IsEmpty())
            {
                encounterDiagnostics?.AddTimingNote("SubViewport texture capture returned null or empty image.");
                return Failure(
                    request,
                    "scene",
                    request.SourcePath,
                    "The live bridge rendered an empty viewport while flattening the scene.",
                    notes,
                    encounterDiagnostics?.Build().ToDictionary());
            }

            if (normalizePreviewAlpha)
            {
                using var normalizeScope = Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.VisibleScan);
                if (!HasVisiblePixels(image))
                {
                    encounterDiagnostics?.SetPixelEvidence(image, beforeAlphaNormalization: true);
                    var normalized = NormalizePreviewAlphaFromColor(image);
                    if (HasVisiblePixels(normalized))
                    {
                        encounterDiagnostics?.AddTimingNote("Preview alpha was normalized from nonzero RGB pixels in the captured image.");
                        image = normalized;
                    }
                }
            }

            postProcessImage?.Invoke(image);
            encounterDiagnostics?.SetPixelEvidence(image, beforeAlphaNormalization: false);

            bool anyVisiblePixels;
            using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.VisibleScan))
            {
                anyVisiblePixels = HasVisiblePixels(image);
            }

            if (!anyVisiblePixels)
            {
                encounterDiagnostics?.AddTimingNote("Captured image still had no visible alpha pixels after optional alpha normalization.");
                return Failure(
                    request,
                    "scene",
                    request.SourcePath,
                    "The live bridge rendered only fully transparent pixels while flattening the scene.",
                    notes,
                    encounterDiagnostics?.Build().ToDictionary());
            }

            var captureRegion = new Rect2I(0, 0, image.GetWidth(), image.GetHeight());
            if (ShouldTrimTransparentBounds(renderMode, trimTransparentBounds))
            {
                using var trimScope = Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.Trim);
                image = CropToVisiblePixels(image, ResolveTransparentCropPadding(renderMode), out captureRegion);
            }

            reportCaptureRegion?.Invoke(captureRegion);
            captured = image;
        }
        catch (Exception ex)
        {
            encounterDiagnostics?.AddTimingNote($"Rendering aborted with {ex.GetType().Name}: {ex.Message}");
            return Failure(
                request,
                "scene",
                request.SourcePath,
                $"The live bridge failed while configuring or rendering the scene: {ex.GetType().Name}: {ex.Message}",
                notes,
                encounterDiagnostics?.Build().ToDictionary());
        }
        finally
        {
            if (viewport.GetParent() is not null)
            {
                rootViewport.RemoveChild(viewport);
            }
            viewport.QueueFree();
        }

        if (captured is null)
        {
            // Unreachable today (every path out of the capture block either returns a Failure or sets `captured`),
            // and deliberately a clean failure rather than `captured!`: a future fall-through must say so, not NRE.
            return Failure(
                request,
                "scene",
                request.SourcePath,
                "The live bridge finished the capture without producing an image to encode.",
                notes,
                encounterDiagnostics?.Build().ToDictionary());
        }

        return await EncodeImageResultAsync(
            captured,
            request,
            renderMode,
            notes);
    }

    private async Task<(Image? Image, SceneRenderFrame Frame)> RenderNodeToImageAsync(
        Node node,
        SceneRenderFrame frame,
        int warmupFrames)
    {
        var rootViewport = (Engine.GetMainLoop() as SceneTree)?.Root;
        if (rootViewport is null)
        {
            return (null, frame);
        }

        var viewport = new SubViewport
        {
            // The marker a host's tree walk recognises an OFF-SCREEN EXTRACTION subtree by, so it can skip
            // it whole rather than freezing the detached rig this render is in the middle of posing.
            // See Sts2OffscreenExtraction.
            Name = Sts2OffscreenExtraction.SubViewportNodeName,
            TransparentBg = true,
            Size = frame.ViewportSize,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };

        PositionCanvasItem(node, frame);
        rootViewport.AddChild(viewport);
        viewport.AddChild(node);

        try
        {
            await AwaitRenderWarmupFramesAsync(rootViewport, Math.Max(0, warmupFrames));
            RenderingServer.ForceDraw();
            return (viewport.GetTexture()?.GetImage(), frame);
        }
        finally
        {
            if (viewport.GetParent() is not null)
            {
                rootViewport.RemoveChild(viewport);
            }

            if (node.GetParent() is not null)
            {
                node.GetParent().RemoveChild(node);
            }

            viewport.QueueFree();
        }
    }

    private AssetExtractOperationResult BuildTimelineResult(
        AssetExtractRequestSnapshot request,
        IReadOnlyList<AssetExtractFrame> frames,
        int durationMs,
        string renderMode,
        IReadOnlyList<string> notes,
        AssetExtractClipPlacement? clipPlacement = null)
    {
        if (frames.Count == 0)
        {
            return Failure(
                request,
                "frame_count",
                "0",
                "Timeline export did not produce any frames.");
        }

        var width = frames.Max(frame => frame.Width);
        var height = frames.Max(frame => frame.Height);
        _logStream.Write(
            BridgeLogLevel.Info,
            "bridge.assets",
            $"Extracted '{request.SourcePath}' as timeline ({frames.Count} frames, {durationMs}ms total) via {renderMode}.");

        return AssetExtractOperationResult.SuccessTimeline(
            requestId: request.RequestId,
            source: DataSourceKind.Live,
            provisional: false,
            format: frames[0].Format,
            width: width,
            height: height,
            renderMode: renderMode,
            durationMs: durationMs,
            frames: frames,
            notes: notes,
            clipPlacement: clipPlacement);
    }

    // Godot's PNG/WebP encoders route unknown formats through `detect_alpha()`,
    // which does not recognize float/half formats and reports ALPHA_NONE for them —
    // an RGBAH image (e.g. the card_frame_sdf.exr distance field, whose SDF lives
    // in the ALPHA channel) silently encodes as RGB and loses it. Convert any
    // non-8-bit format to RGBA8 ourselves so the encoder takes the
    // alpha-preserving path. 8-bit formats keep their existing (correct) handling.
    private static void NormalizeImageForEncode(Image image)
    {
        if (image.IsCompressed())
        {
            image.Decompress();
        }

        var format = image.GetFormat();
        if (format is not (Image.Format.L8 or Image.Format.La8 or Image.Format.Rgb8 or Image.Format.Rgba8))
        {
            image.Convert(Image.Format.Rgba8);
        }
    }

    private readonly record struct EncodedImage(byte[] Contents, string ActualFormat);

    private static Sts2ImageEncodePolicy.Plan ResolveEncodePlan(AssetExtractRequestSnapshot request)
        => Sts2ImageEncodePolicy.Resolve(request.OutputFormat, request.ImageQuality, request.ImageOpaque);

    /// <summary>
    /// Encode one image to bytes under an already-resolved <see cref="Sts2ImageEncodePolicy.Plan"/>. PURE CPU on an
    /// image nobody else holds, which is what lets <see cref="EncodeImageResultAsync"/> hand it to a worker thread;
    /// <paramref name="blocking"/> says which of those two it is, so the phase table reports a main-thread stall and
    /// a parked encode differently.
    /// </summary>
    private static EncodedImage EncodeImageBytes(
        Image image,
        Sts2ImageEncodePolicy.Plan plan,
        List<string> notes,
        bool blocking)
    {
        var actualFormat = plan.ActualFormat;
        using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.EncodeNormalize, blocking))
        {
            NormalizeImageForEncode(image);
            if (plan.Opaque)
            {
                // Only an image that HAS alpha is converted: "opaque" means drop the alpha channel, not "force RGB"
                // (promoting an L8 mask to RGB8 would make it bigger for nothing).
                var opaqueTarget = image.GetFormat() switch
                {
                    Image.Format.Rgba8 => Image.Format.Rgb8,
                    Image.Format.La8 => Image.Format.L8,
                    _ => (Image.Format?)null,
                };
                if (opaqueTarget is { } target)
                {
                    image.Convert(target);
                    notes.Add(plan.Codec == Sts2ImageEncodePolicy.ImageCodec.Jpeg
                        ? "The live bridge dropped this image's alpha channel before encoding: JPEG cannot carry alpha."
                        : "The live bridge dropped this image's alpha channel before encoding (opaque encode requested).");
                }
            }
        }

        byte[] contents;
        using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.EncodeSave, blocking))
        {
            contents = plan.Codec switch
            {
                Sts2ImageEncodePolicy.ImageCodec.Png => image.SavePngToBuffer(),
                // No quality => LOSSLESS webp, which is what every shipped caller gets today.
                Sts2ImageEncodePolicy.ImageCodec.Webp => plan.Quality is { } webpQuality
                    ? image.SaveWebpToBuffer(true, webpQuality)
                    : image.SaveWebpToBuffer(),
                Sts2ImageEncodePolicy.ImageCodec.Jpeg => image.SaveJpgToBuffer(plan.EffectiveQuality),
                _ => EncodePngFallback(image, notes, out actualFormat),
            };
        }

        return new EncodedImage(contents, actualFormat);
    }

    private AssetExtractOperationResult EncodeImageResult(
        Image image,
        AssetExtractRequestSnapshot request,
        string renderMode,
        IReadOnlyList<string> notes)
    {
        var encodedNotes = notes.ToList();
        var encoded = EncodeImageBytes(image, ResolveEncodePlan(request), encodedNotes, blocking: true);
        return BuildEncodedResult(image, request, renderMode, encodedNotes, encoded);
    }

    /// <summary>
    /// <see cref="EncodeImageResult"/> with the encoder on a WORKER thread, under the process-wide
    /// <see cref="Sts2RenderEncodeBudget"/>.
    /// </summary>
    /// <remarks>
    /// WHY: a 2520x1080 combat background measured 648ms of PNG encode — 87% of the render, and every millisecond
    /// of it held the Godot main thread, which is the hitch a player on the TV sees when a phone first asks for the
    /// static background. The captured <see cref="Image"/> is unshared CPU data after the readback (the same
    /// property the clip lane already relies on to encode its frames off-thread), so the encode does not need the
    /// main thread at all. The main thread's own await is recorded as the parked <c>encodeWait</c> phase — the
    /// render's wall time is unchanged, but it stops being a stall.
    /// <para>
    /// The <c>await</c> resumes on the Godot main thread by itself: <see cref="Sts2GodotSynchronizationContext"/>
    /// is installed on the main thread and re-set on every drain, so it is the captured context here.
    /// </para>
    /// </remarks>
    private async Task<AssetExtractOperationResult> EncodeImageResultAsync(
        Image image,
        AssetExtractRequestSnapshot request,
        string renderMode,
        IReadOnlyList<string> notes)
    {
        if (!Sts2RenderEncodeBudget.OffloadEnabled)
        {
            return EncodeImageResult(image, request, renderMode, notes);
        }

        var plan = ResolveEncodePlan(request);
        var encodedNotes = notes.ToList();
        EncodedImage encoded;
        // Same phase name the clip lane uses for exactly this shape (main thread parked on a worker encoder), so a
        // single vocabulary describes both lanes and a cross-lane comparison is not reading two different words.
        using (Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.EncodeWait, blocking: false))
        {
            using var encodeLease = await Sts2RenderEncodeBudget.AcquireAsync();
            // Plain Task.Run. Measured and rejected: a dedicated LongRunning thread, on the theory that the
            // FIRST render after boot (whose encodeWait runs ~550ms past its own encodeSave) was queued behind
            // ThreadPool injection. It is not — LongRunning moved that number not at all across three boots.
            // The extra wait is the CONTINUATION: at boot the host's main loop is ~10x slower (its 3 warmup
            // frames take ~300ms, not ~30ms), so the hop back onto the Godot context waits on a main thread
            // busy with the game's own startup. Which is the offload working: that time is the game running
            // instead of hitching. Warm, the same gap is 12-27ms.
            encoded = await Task.Run(() => EncodeImageBytes(image, plan, encodedNotes, blocking: false));
        }

        return BuildEncodedResult(image, request, renderMode, encodedNotes, encoded);
    }

    private AssetExtractOperationResult BuildEncodedResult(
        Image image,
        AssetExtractRequestSnapshot request,
        string renderMode,
        List<string> encodedNotes,
        EncodedImage encoded)
    {
        Sts2RenderPhaseProfile.Set(Sts2RenderPhaseProfile.Counter.OutputBytes, encoded.Contents.Length);
        _logStream.Write(
            BridgeLogLevel.Info,
            "bridge.assets",
            $"Extracted '{request.SourcePath}' as {encoded.ActualFormat} ({image.GetWidth()}x{image.GetHeight()}) via {renderMode}.");

        return AssetExtractOperationResult.Success(
            requestId: request.RequestId,
            source: DataSourceKind.Live,
            provisional: false,
            format: encoded.ActualFormat,
            width: image.GetWidth(),
            height: image.GetHeight(),
            contents: encoded.Contents,
            renderMode: renderMode,
            notes: encodedNotes);
    }

    private static byte[] EncodePngFallback(
        Image image,
        List<string> notes,
        out string actualFormat)
    {
        actualFormat = "png";
        notes.Add(
            "The live bridge encoded PNG bytes for this request; the CLI will transcode them to a supported requested output format when needed.");
        return image.SavePngToBuffer();
    }

    private AssetExtractFrame EncodeTimelineFrameFromImage(
        Image image,
        AssetExtractRequestSnapshot request,
        int index,
        int durationMs,
        int offsetX = 0,
        int offsetY = 0,
        int canvasWidth = 0,
        int canvasHeight = 0,
        string? codecOverride = null,
        bool webpLossy = false,
        float webpQuality = 0.85f)
    {
        // A codec override (the server-side clip size policy) wins over the request's OutputFormat; webp is
        // encoded LOSSY at webpQuality (~4× smaller than PNG — the whole point), PNG stays lossless.
        var requestedFormat = (codecOverride ?? request.OutputFormat).Trim().ToLowerInvariant();
        var actualFormat = requestedFormat;
        var notes = new List<string>();
        // One phase for the whole per-frame encode, marked NON-blocking: this runs on a worker thread (the clip
        // lane hands each sliced cell to Task.Run), so its cost is wall-clock the bake spends, never main-thread
        // time the game loses. Several of these overlap each other and the next batch's render.
        using var encodeScope = Sts2RenderPhaseProfile.Measure(Sts2RenderPhaseProfile.Phase.EncodeFrame, blocking: false);
        NormalizeImageForEncode(image);
        byte[] contents = requestedFormat switch
        {
            "png" => image.SavePngToBuffer(),
            "webp" => webpLossy ? image.SaveWebpToBuffer(true, webpQuality) : image.SaveWebpToBuffer(),
            _ => EncodePngFallback(image, notes, out actualFormat),
        };

        Sts2RenderPhaseProfile.Count(Sts2RenderPhaseProfile.Counter.OutputBytes, contents.Length);
        Sts2RenderPhaseProfile.Count(Sts2RenderPhaseProfile.Counter.Frames);
        return new AssetExtractFrame(
            Index: index,
            Format: actualFormat,
            ContentType: actualFormat == "webp" ? "image/webp" : "image/png",
            Width: image.GetWidth(),
            Height: image.GetHeight(),
            Contents: contents,
            DurationMs: durationMs,
            OffsetX: offsetX,
            OffsetY: offsetY,
            CanvasWidth: canvasWidth,
            CanvasHeight: canvasHeight);
    }

    private AssetExtractFrame? EncodeTimelineFrame(
        Texture2D texture,
        AssetExtractRequestSnapshot request,
        int index,
        int durationMs)
    {
        var image = texture.GetImage();
        if (image is null || image.IsEmpty())
        {
            return null;
        }

        var requestedFormat = request.OutputFormat.Trim().ToLowerInvariant();
        var actualFormat = requestedFormat;
        var notes = new List<string>();
        NormalizeImageForEncode(image);
        byte[] contents = requestedFormat switch
        {
            "png" => image.SavePngToBuffer(),
            "webp" => image.SaveWebpToBuffer(),
            _ => EncodePngFallback(image, notes, out actualFormat),
        };

        return new AssetExtractFrame(
            Index: index,
            Format: actualFormat,
            ContentType: actualFormat == "webp" ? "image/webp" : "image/png",
            Width: image.GetWidth(),
            Height: image.GetHeight(),
            Contents: contents,
            DurationMs: durationMs);
    }

}
