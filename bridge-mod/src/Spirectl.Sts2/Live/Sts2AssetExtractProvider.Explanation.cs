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
    private delegate Task<(Image? Image, SceneRenderFrame Frame)> RenderExplanationFrame(
        Node node, SceneRenderFrame frame, int warmupFrames);

    /// <summary>Owns composition explanation while using the extractor only for its render primitive.</summary>
    private sealed class ExplanationRenderer(RenderExplanationFrame renderFrame) : IAssetExplanationRenderContext
    {
        public async Task<AssetExplainOperationResult> ExplainOnMainThreadAsync(AssetExplainRequestSnapshot request)
        {
            var extractRequest = new AssetExtractRequestSnapshot(
                request.RequestId,
                request.SourceRoot,
                request.SourcePath,
                request.LoadPath,
                "png");
            if (TryParseEncounterScenePackageRequest(extractRequest, out var encounterPackage))
            {
                return ExplainEncounterScenePackage(request, encounterPackage);
            }

            if (!TryParseCombatBackgroundAliasRequest(extractRequest, out var combatBackground))
            {
                return AssetExplainOperationResult.Failure(
                    requestId: request.RequestId,
                    source: DataSourceKind.Live,
                    provisional: false,
                    code: AssetExtractFailureCode.NotImplemented,
                    message: "asset composition explanation currently supports composed://combat-background/<id>/image and composed://encounters/<id>/scene-package only.",
                    details:
                    [
                        new AssetExtractDetail(
                            Field: "load_path",
                            Value: request.LoadPath,
                            Note: "Use a composed key such as composed://combat-background/overgrowth/image or composed://encounters/kaiser_crab_boss/scene-package."),
                    ]);
            }

            var resource = ResourceLoader.Load(combatBackground.RootScenePath);
            if (resource is not PackedScene scene)
            {
                return AssetExplainOperationResult.Failure(
                    requestId: request.RequestId,
                    source: DataSourceKind.Live,
                    provisional: false,
                    code: AssetExtractFailureCode.RuntimeFailure,
                    message: "The live host could not load the requested combat background root scene.",
                    details:
                    [
                        new AssetExtractDetail(
                            Field: "load_path",
                            Value: combatBackground.RootScenePath,
                            Note: "ResourceLoader.Load did not return a PackedScene."),
                    ]);
            }

            var instantiated = scene.Instantiate();
            if (instantiated is null)
            {
                return AssetExplainOperationResult.Failure(
                    requestId: request.RequestId,
                    source: DataSourceKind.Live,
                    provisional: false,
                    code: AssetExtractFailureCode.RuntimeFailure,
                    message: "The live host could not instantiate the combat background root scene.",
                    details:
                    [
                        new AssetExtractDetail(
                            Field: "scene",
                            Value: combatBackground.RootScenePath,
                            Note: "PackedScene.instantiate() returned null."),
                    ]);
            }

            try
            {
                var placeholderNames = GetCombatBackgroundPlaceholderNames(instantiated);
                var discoveredLayerPaths = DiscoverCombatBackgroundLayerPaths(combatBackground.BackgroundId);
                var plan = BuildCombatBackgroundCompositionPlan(
                    combatBackground.BackgroundId,
                    placeholderNames,
                    discoveredLayerPaths);
                var warnings = plan.Warnings.ToList();
                if (placeholderNames.Count == 0)
                {
                    warnings.Add(new AssetCompositionWarningSnapshot(
                        Code: "unsupported-placeholder",
                        Severity: "warning",
                        Message: "The root scene did not expose Layer_XX or Foreground placeholders.",
                        Details: new Dictionary<string, string>
                        {
                            ["rootScene"] = combatBackground.RootScenePath,
                        }));
                }

                var composeNotes = Array.Empty<string>() as IReadOnlyList<string>;
                if (!TryComposeCombatBackgroundAliasLayers(
                        instantiated,
                        plan,
                        LoadCombatBackgroundLayerScene,
                        out _,
                        out var selectedLayers,
                        out composeNotes))
                {
                    warnings.Add(new AssetCompositionWarningSnapshot(
                        Code: "missing-layer",
                        Severity: "warning",
                        Message: composeNotes.FirstOrDefault() ?? "No combat background layer scenes were composed for the requested alias.",
                        Details: new Dictionary<string, string>
                        {
                            ["loadPath"] = request.LoadPath,
                        }));
                }

                foreach (var missingLayer in selectedLayers.Where(layer => string.Equals(layer.LoadStatus, "missing", StringComparison.Ordinal)))
                {
                    warnings.Add(new AssetCompositionWarningSnapshot(
                        Code: "layer-load-failed",
                        Severity: "warning",
                        Message: $"Selected layer scene '{missingLayer.Path}' could not be loaded or instantiated.",
                        Details: new Dictionary<string, string>
                        {
                            ["placeholder"] = missingLayer.Placeholder,
                            ["path"] = missingLayer.Path,
                        }));
                }

                var renderNotes = composeNotes.ToList();
                renderNotes.Add(
                    "Rendered composed combat background at viewport framing using runtime BgContainer placement: centered, x-offset 23, scale 0.9, preserved root Control size.");
                renderNotes.AddRange(StabilizeCombatBackgroundAliasPreview(instantiated));
                TryPrepareCombatBackgroundLayers(
                    instantiated,
                    out var authoredLayerNotes,
                    out _);
                renderNotes.AddRange(authoredLayerNotes);

                TryResolveCombatBackgroundFrame(
                    new AssetExtractRequestSnapshot(
                        request.RequestId,
                        request.SourceRoot,
                        combatBackground.RootScenePath,
                        combatBackground.RootScenePath,
                        "png"),
                    ResolveRootViewportSize(),
                    out var frame);

                var render = await renderFrame(
                    instantiated,
                    frame,
                    warmupFrames: 3);
                var finalVisible = new AssetCompositionRectSnapshot(0, 0, 0, 0);
                var transparentPixelRatio = 1d;
                if (render.Image is not null && !render.Image.IsEmpty())
                {
                    var inspected = EnsureRgba8(render.Image);
                    var rgba = inspected.GetData();
                    transparentPixelRatio = ComputeTransparentPixelRatio(rgba, inspected.GetWidth(), inspected.GetHeight());
                    if (TryFindVisiblePixelBounds(
                            rgba,
                            inspected.GetWidth(),
                            inspected.GetHeight(),
                            ResolveTransparentCropPadding(ComposedCombatBackgroundRenderMode),
                            out var visibleRect))
                    {
                        finalVisible = ToSnapshot(visibleRect);
                    }
                    else
                    {
                        warnings.Add(new AssetCompositionWarningSnapshot(
                            Code: "empty-visible-bounds",
                            Severity: "warning",
                            Message: "No visible pixels were found in the composed diagnostic render.",
                            Details: new Dictionary<string, string>
                            {
                                ["renderMode"] = ComposedCombatBackgroundRenderMode,
                            }));
                    }
                }
                else
                {
                    warnings.Add(new AssetCompositionWarningSnapshot(
                        Code: "empty-visible-bounds",
                        Severity: "warning",
                        Message: "The composed diagnostic render did not produce an image.",
                        Details: new Dictionary<string, string>
                        {
                            ["renderMode"] = ComposedCombatBackgroundRenderMode,
                        }));
                }

                if (transparentPixelRatio >= 0.90d)
                {
                    warnings.Add(new AssetCompositionWarningSnapshot(
                        Code: "transparent-heavy",
                        Severity: "warning",
                        Message: "The composed diagnostic render is mostly transparent.",
                        Details: new Dictionary<string, string>
                        {
                            ["transparentPixelRatio"] = transparentPixelRatio.ToString("0.###"),
                        }));
                }

                var viewportBounds = new AssetCompositionRectSnapshot(
                    0,
                    0,
                    frame.ViewportSize.X,
                    frame.ViewportSize.Y);
                if (finalVisible.Width > 0
                    && finalVisible.Height > 0
                    && (finalVisible.Width < frame.ViewportSize.X * 0.25d
                        || finalVisible.Height < frame.ViewportSize.Y * 0.25d))
                {
                    warnings.Add(new AssetCompositionWarningSnapshot(
                        Code: "suspicious-bounds",
                        Severity: "warning",
                        Message: "The final visible bounds are much smaller than the render viewport.",
                        Details: new Dictionary<string, string>
                        {
                            ["viewport"] = $"{frame.ViewportSize.X}x{frame.ViewportSize.Y}",
                            ["visible"] = $"{finalVisible.Width:0.###}x{finalVisible.Height:0.###}",
                        }));
                }

                var selectedLayerPaths = selectedLayers
                    .Where(layer => string.Equals(layer.LoadStatus, "loaded", StringComparison.Ordinal))
                    .Select(layer => layer.Path)
                    .ToList();
                var activeScene = ObserveActiveCombatBackgroundScene(combatBackground, selectedLayerPaths);
                if (string.Equals(activeScene.Status, "different", StringComparison.Ordinal))
                {
                    warnings.Add(new AssetCompositionWarningSnapshot(
                        Code: "active-scene-differs",
                        Severity: "info",
                        Message: "The active runtime scene layer paths differ from the deterministic selected layer paths.",
                        Details: new Dictionary<string, string>
                        {
                            ["backgroundId"] = combatBackground.BackgroundId,
                        }));
                }

                return AssetExplainOperationResult.Success(
                    requestId: request.RequestId,
                    source: DataSourceKind.Live,
                    provisional: false,
                    rootScene: new AssetCompositionRootSceneSnapshot(
                        BackgroundId: combatBackground.BackgroundId,
                        Path: combatBackground.RootScenePath,
                        LoadSource: "live-host"),
                    placeholders: plan.Placeholders
                        .Select(placeholder => new AssetCompositionPlaceholderSnapshot(
                            placeholder.Name,
                            placeholder.NodePath,
                            placeholder.Order,
                            placeholder.Matched))
                        .ToList(),
                    layerGroups: plan.LayerGroups
                        .Select(group => new AssetCompositionLayerGroupSnapshot(
                            Name: group.Name,
                            Placeholder: group.Placeholder,
                            CandidateCount: group.Candidates.Count,
                            Candidates: group.Candidates,
                            SelectedPath: group.SelectedPath ?? string.Empty,
                            SelectionSource: group.SelectionSource,
                            Order: group.Order))
                        .ToList(),
                    selectedLayers: selectedLayers,
                    bounds: new AssetCompositionBoundsSnapshot(
                        Viewport: viewportBounds,
                        FinalComposed: viewportBounds,
                        FinalVisible: finalVisible,
                        TransparentPixelRatio: transparentPixelRatio),
                    render: new AssetCompositionRenderPlanSnapshot(
                        RenderMode: ComposedCombatBackgroundRenderMode,
                        WarmupFrames: 3,
                        TrimTransparentBounds: ShouldTrimTransparentBounds(ComposedCombatBackgroundRenderMode, false),
                        TransparentCropPadding: ResolveTransparentCropPadding(ComposedCombatBackgroundRenderMode),
                        Notes: renderNotes),
                    activeScene: activeScene,
                    warnings: warnings);
            }
            finally
            {
                instantiated.QueueFree();
            }
        }

    }

}
