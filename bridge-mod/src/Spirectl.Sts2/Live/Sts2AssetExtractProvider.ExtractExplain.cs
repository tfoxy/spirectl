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

public sealed partial class Sts2AssetExtractProvider
{
    private async Task<AssetExtractOperationResult> ExtractOnMainThreadAsync(AssetExtractRequestSnapshot request)
    {
        if (TryParseSpineClipRequest(request, out var spineClip))
        {
            return await ExtractSpineClipAsync(spineClip, request);
        }

        // scene-subtree://<res-scene>?node=<relPath> -> re-enter with the real res:// path plus the subtree
        // node path riding the snapshot; the PackedScene branch then detaches and renders only that subtree.
        if (TryParseSceneSubtreeRequest(request, out var subtreeRequest))
        {
            return await ExtractOnMainThreadAsync(subtreeRequest);
        }

        if (TryParseCharacterVisualRequest(request, out var characterVisual))
        {
            return await ExtractCharacterVisualAsync(characterVisual, request);
        }

        if (TryParseRelicVisualRequest(request, out var relicVisual))
        {
            return await ExtractRelicVisualAsync(relicVisual, request);
        }

        if (TryResolveModelResourceRequest(request, out var modelResourceRequest))
        {
            return await ExtractOnMainThreadAsync(modelResourceRequest);
        }

        if (TryParseCombatBackgroundAliasRequest(request, out var combatBackground))
        {
            return await ExtractComposedCombatBackgroundAsync(combatBackground, request);
        }

        if (TryParseEncounterRenderTargetRequest(request, out var encounterRenderTarget))
        {
            return await ExtractEncounterRenderTargetAsync(encounterRenderTarget, request);
        }

        var loadPath = ResolveLoadPath(request);
        if (TryExtractFontBytes(loadPath, request, out var fontResult))
        {
            return fontResult;
        }

        if (TryExtractRawResourceBytes(loadPath, request, out var rawResourceResult))
        {
            return rawResourceResult;
        }

        // Localization tables are plain JSON the renderer fetches directly (a browser
        // asking for res://localization/<lang>/<table>.json wants the table, not a
        // render). The engine won't load these as a renderable resource, so serve the
        // raw file bytes via Godot.FileAccess (path-faithful, like the loc inspector's
        // source — but unmerged, per-file).
        if (TryExtractLocalizationJson(loadPath, request, out var localizationResult))
        {
            return localizationResult;
        }

        if (IsUnresolvedVirtualAssetKey(request))
        {
            return Failure(
                request,
                "asset_key",
                request.LoadPath,
                "This virtual asset key is not supported by the live extractor. Use a direct res:// resource path or a documented virtual/composed key.");
        }

        var resource = ResourceLoader.Load(loadPath);
        if (resource is null)
        {
            if (TryLoadExternalImage(request.LoadPath, out var externalImage))
            {
                return EncodeImageResult(
                    externalImage,
                    request,
                    renderMode: "flattened-static",
                    notes: []);
            }

            return Failure(
                request,
                "load_path",
                loadPath,
                "The live host could not load the requested Godot resource path.");
        }

        if (resource is Image image)
        {
            if (image.IsEmpty())
            {
                return Failure(
                    request,
                    "image",
                    request.SourcePath,
                    "Image resource returned no pixels for the requested asset.");
            }

            return EncodeImageResult(
                image,
                request,
                renderMode: "flattened-static-image",
                notes: ["Encoded a loaded Godot Image resource as browser image bytes."]);
        }

#pragma warning disable CS0618 // AnimatedTexture support is retained for legacy STS2/Godot asset compatibility.
        if (resource is AnimatedTexture animated)
        {
            return ExtractAnimatedTexture(animated, request);
        }
#pragma warning restore CS0618

        if (resource is SpriteFrames spriteFrames)
        {
            return ExtractSpriteFrames(spriteFrames, request);
        }

        if (resource is StyleBoxTexture styleBoxTexture)
        {
            return ExtractStyleBoxTexture(styleBoxTexture, request);
        }

        // AtlasTexture sprites are served as a GodotResource JSON DOCUMENT (type + region/margin + `atlas`
        // ExtResource) under default/auto/structure formats, so the renderer resolves the UNDERLYING atlas and
        // crops the region itself — one stable image URL, no per-frame re-fetch/flicker for atlas-animated
        // sprites (intent icons). An explicit raster format still falls through to the cropped-PNG path below
        // (e.g. `sts2 assets extract --format png`). Must precede the Texture2D branch (AtlasTexture is one).
        if (resource is AtlasTexture atlasTextureResource
            && WantsSceneStructure(request.OutputFormat)
            && Sts2GodotResourceProducer.IsSupported(atlasTextureResource))
        {
            return WantsRawResourceBytes(request.OutputFormat)
                ? ExtractRawResourceDocument(atlasTextureResource, loadPath, request)
                : ExtractGodotResource(atlasTextureResource, request);
        }

        if (resource is Texture2D texture)
        {
            return await ExtractTextureAsync(texture, request, "flattened-static", []);
        }

        if (resource is PackedScene scene)
        {
            // Default/auto/structure -> faithful GodotSceneState JSON for the renderer;
            // explicit raster format -> PNG render (see WantsSceneStructure).
            return WantsSceneStructure(request.OutputFormat)
                ? ExtractPackedSceneStructure(scene, request)
                : await ExtractPackedSceneAsync(scene, request);
        }

        if (resource is Theme theme)
        {
            return ExtractTheme(theme, request);
        }

        if (IsFontResourcePath(loadPath))
        {
            return FontBytesUnavailable(request, loadPath);
        }

        // Font RESOURCES (FontVariation/FontFile loaded from a `.tres`) are served as a
        // GodotResource JSON DOCUMENT so the renderer can read `base_font` and resolve the
        // actual `.ttf` (whose bytes the font-path branch above already serves). Default/auto/
        // structure only; an explicit raster format falls through to the unsupported path.
        if (resource is Font fontResource
            && WantsSceneStructure(request.OutputFormat)
            && Sts2GodotResourceProducer.IsSupported(fontResource))
        {
            return WantsRawResourceBytes(request.OutputFormat)
                ? ExtractRawResourceDocument(fontResource, loadPath, request)
                : ExtractGodotResource(fontResource, request);
        }

        if (resource is TileSet tileSet)
        {
            return ExtractTileSet(tileSet, request);
        }

        // Shader SOURCE for the renderer's opt-in WebGL runtime: a `.gdshader` fetch
        // returns the raw shader text (`Shader.Code`) so the browser can transpile and
        // run it. Structure-style formats only; there is no raster rendering of a shader.
        if (resource is Shader shaderResource && WantsSceneStructure(request.OutputFormat))
        {
            return ExtractShaderSource(shaderResource, request);
        }

        // ShaderInclude SOURCE (`.gdshaderinc`) for BOTH the WebGL runtime and the native
        // mirror client: a `.gdshader` that `#include`s a util file needs the include's
        // body to compile/transpile. A Godot 4 ShaderInclude exposes `.Code` exactly like
        // Shader (it is a Resource, NOT a Shader subclass, so it needs its own branch or
        // it falls through to UnsupportedResourceTypeFailure — the original "white wash"
        // bug: the client's shader compile fails silently on the unresolved #include and
        // paints the raw base fill). Same raw-text shape/route as the Shader branch above,
        // so ONE fix feeds both clients (they request includes over the same /res route).
        if (resource is ShaderInclude shaderInclude && WantsSceneStructure(request.OutputFormat))
        {
            return ExtractShaderIncludeSource(shaderInclude, request);
        }

        // Canvas-item materials are served as GodotResource JSON DOCUMENTS under the
        // default/auto/structure formats — the renderer reads the material TYPE, the
        // `shader` ref and the LIVE `shader_parameter/*` values to pick its render
        // strategy (additive compositing / SVG ripple / hsv color matrix / WebGL).
        // An explicit raster format keeps the sampled ColorRect preview.
        if (resource is CanvasItemMaterial or ShaderMaterial
            && WantsSceneStructure(request.OutputFormat))
        {
            return WantsRawResourceBytes(request.OutputFormat)
                ? ExtractRawResourceDocument(resource, loadPath, request)
                : ExtractGodotResource((Material)resource, request);
        }

        if (resource is CanvasItemMaterial canvasItemMaterial)
        {
            return ExtractCanvasItemMaterial(canvasItemMaterial, request);
        }

        if (resource is ShaderMaterial shaderMaterial)
        {
            return ExtractCanvasItemMaterial(shaderMaterial, request);
        }

        if (resource is Material material)
        {
            return UnsupportedResourceTypeFailure(
                request,
                material,
                "requires a deterministic 2D canvas preview context and is not a supported CanvasItem/Shader material preview.");
        }

        // Route to the standalone-skeleton bake either by resource-path convention OR when the
        // loaded resource is actually a Spine skeleton-data resource. The path convention misses
        // scene-authored spines whose filenames don't carry a _skel suffix (e.g. the merchant
        // background's shop_merchant_bottom.tres); GDExtension types surface as Godot.Resource in
        // C#, so GetClass() is the reliable discriminator.
        if (LooksLikeStandaloneSkeletonData(request) || IsSpineSkeletonDataResource(resource))
        {
            return await ExtractStandaloneSkeletonDataAsync(resource, request);
        }

        return UnsupportedResourceTypeFailure(
            request,
            resource,
            "does not have a deterministic live preview path yet.");
    }

    private async Task<AssetExtractOperationResult> ExtractEncounterRenderTargetAsync(
        EncounterRenderTargetRequest target,
        AssetExtractRequestSnapshot request)
    {
        var catalog = Sts2EncounterVisualCatalog.LoadBaseGame();
        if (!catalog.TryGetPackage(target.EncounterId, out var package))
        {
            return Failure(
                request,
                "encounter_id",
                target.EncounterId,
                "No checked-in base-game encounter visual package exists for this encounter yet.");
        }

        return target.Kind switch
        {
            "background" => await ExtractEncounterBackgroundAsync(package, request),
            "overlay" => await ExtractEncounterSpecialVisualAsync(package, target, request, EncounterVisualOverlayRenderMode),
            "part" => await ExtractEncounterSpecialVisualAsync(package, target, request, EncounterVisualPartRenderMode),
            _ => Failure(
                request,
                "encounter_render_target",
                target.Kind,
                "Unsupported encounter render target kind."),
        };
    }

    private async Task<AssetExtractOperationResult> ExtractEncounterBackgroundAsync(
        Sts2EncounterVisualPackageDefinition package,
        AssetExtractRequestSnapshot request)
    {
        var combatBackground = new CombatBackgroundAliasRequest(
            package.EncounterId,
            package.Background.SourceScene);
        var resource = ResourceLoader.Load(combatBackground.RootScenePath);
        if (resource is not PackedScene scene)
        {
            return Failure(
                request,
                "load_path",
                combatBackground.RootScenePath,
                "The live host could not load the encounter background scene from the visual package catalog.");
        }

        var camera = ResolveEncounterCameraForRender(package);
        var diagnostics = new EncounterRenderDiagnosticsBuilder(
            request.RequestId,
            "background",
            EncounterBackgroundRenderMode);
        var backgroundFrame = EncounterViewportFrame(camera);
        diagnostics.SetFrame(backgroundFrame);
        diagnostics.AddTimingNote("Encounter background diagnostics initialized before composed background rendering.");
        return await ExtractComposedCombatBackgroundSceneAsync(
            combatBackground,
            request,
            scene,
            discoverLayerPaths: DiscoverCombatBackgroundLayerPaths,
            loadLayerScene: LoadCombatBackgroundLayerScene,
            configureRoot: node => RemoveEncounterSpecialVisualNodes(node, package),
            leadingNotes:
            [
                $"Rendered encounter background scene '{package.Background.SourceScene}' for package '{package.PackageId}'.",
                $"Encounter camera resolved with scale {camera.Scale:0.###}, offset ({camera.Offset.X:0.###}, {camera.Offset.Y:0.###}) from {camera.Provenance}.",
                "The render target is framed in game viewport coordinates; catalog special visual roots are removed from this background target.",
            ],
            renderMode: EncounterBackgroundRenderMode,
            frameOverride: backgroundFrame,
            encounterDiagnostics: diagnostics);
    }

    private async Task<AssetExtractOperationResult> ExtractEncounterSpecialVisualAsync(
        Sts2EncounterVisualPackageDefinition package,
        EncounterRenderTargetRequest target,
        AssetExtractRequestSnapshot request,
        string renderMode)
    {
        if (!package.States.Any(state => string.Equals(state.StateId, target.StateId, StringComparison.Ordinal)))
        {
            return Failure(
                request,
                "state_id",
                target.StateId ?? string.Empty,
                "The requested encounter visual state is not defined in the visual package catalog.");
        }

        if (string.Equals(target.Kind, "part", StringComparison.Ordinal)
            && !package.VisualParts.Any(part => string.Equals(part.PartId, target.PartId, StringComparison.Ordinal)))
        {
            return Failure(
                request,
                "part_id",
                target.PartId ?? string.Empty,
                "The requested encounter visual part is not defined in the visual package catalog.");
        }

        var resource = ResourceLoader.Load(package.SpecialVisual.SourceScene);
        if (resource is not PackedScene scene)
        {
            return Failure(
                request,
                "load_path",
                package.SpecialVisual.SourceScene,
                "The live host could not load the encounter special visual scene from the visual package catalog.");
        }

        var node = scene.Instantiate();
        if (node is null)
        {
            return Failure(
                request,
                "scene",
                package.SpecialVisual.SourceScene,
                "PackedScene.instantiate() returned null for the encounter special visual scene.");
        }

        node = ResolveEncounterSpecialVisualRoot(node, package);

        var camera = ResolveEncounterCameraForRender(package);
        var diagnostics = new EncounterRenderDiagnosticsBuilder(
            request.RequestId,
            RenderTargetDiagnosticId(target),
            renderMode,
            node);
        var notes = new List<string>
        {
            $"Rendered encounter special visual scene '{package.SpecialVisual.SourceScene}' for package '{package.PackageId}'.",
            $"Requested state '{target.StateId}'.",
            $"Encounter camera resolved with scale {camera.Scale:0.###}, offset ({camera.Offset.X:0.###}, {camera.Offset.Y:0.###}) from {camera.Provenance}.",
            "Special visual render targets are framed through the encounter camera viewport placement.",
        };
        if (string.Equals(target.Kind, "part", StringComparison.Ordinal))
        {
            notes.Add($"Requested visual part '{target.PartId}'.");
        }

        if (string.Equals(target.Kind, "part", StringComparison.Ordinal))
        {
            notes.Add("Per-part rendering removes any non-catalog nodes from the special visual scene before rendering.");
        }

        return await RenderNodeResultAsync(
            node,
            request,
            renderMode,
            notes,
            warmupFrames: 3,
            trimTransparentBounds: false,
            normalizePreviewAlpha: true,
            frameOverride: EncounterViewportFrame(camera),
            afterAttach: () =>
            {
                var initializedCatalogRoot = TryInvokeEncounterVisualHook(node, "_Ready");
                diagnostics.ReadyHookRan = initializedCatalogRoot;
                notes.Add(initializedCatalogRoot
                    ? "Initialized the encounter special visual root after attaching it to the render viewport."
                    : "No callable _Ready hook was available after render viewport attach; selectors rely on instantiated scene defaults.");
                ConfigureEncounterSpecialVisualNode(node, package, target, notes, diagnostics, out var renderTargetDecision);
                notes.Add($"Encounter render target decision '{renderTargetDecision.Decision}' for '{renderTargetDecision.TargetId}': {renderTargetDecision.Reason}");
                if (TryConfigureSpineFirstFramePreview(node, out var animationName))
                {
                    diagnostics.SpinePreviewSetupRan = true;
                    notes.Add($"Selected deterministic Spine preview animation '{animationName}' for the special visual scene.");
                }
            },
            encounterDiagnostics: diagnostics);
    }

}
