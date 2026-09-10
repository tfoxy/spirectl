using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Core.Artifacts;

/// <summary>Blocking asset extraction. Live implementations marshal rendering to Godot's main thread.</summary>
public interface IAssetExtractor
{
    AssetExtractOperationResult Extract(AssetExtractRequestSnapshot request);
}

/// <summary>Blocking composition explanation. Live implementations marshal inspection to Godot's main thread.</summary>
public interface IAssetExplainer
{
    AssetExplainOperationResult Explain(AssetExplainRequestSnapshot request);
}

/// <summary>Blocking general asset catalog enumeration on the live main thread.</summary>
public interface IAssetCatalogProvider
{
    AssetCatalogOperationResult Catalog(AssetCatalogRequestSnapshot request);
}

/// <summary>Blocking Spine scene catalog enumeration on the live main thread.</summary>
public interface ISpineCatalogProvider
{
    SpineCatalogOperationResult CatalogSpines(SpineCatalogRequestSnapshot request);
}

/// <summary>Blocking geoclip baking. It must never be invoked from the game's main thread.</summary>
public interface ISpineGeoClipBaker
{
    SpineGeoClipBakeResultSnapshot BakeSpineGeoClip(SpineGeoClipBakeRequestSnapshot request);
}

/// <summary>Compatibility aggregate for bridge composition; consumers should request their narrow operation port.</summary>
public interface IAssetExtractProvider : IAssetExtractor, IAssetExplainer, IAssetCatalogProvider, ISpineCatalogProvider, ISpineGeoClipBaker;

public sealed record AssetExtractRequestSnapshot(
    string RequestId,
    string SourceRoot,
    string SourcePath,
    string LoadPath,
    string OutputFormat,
    TimeSpan? Timeout = null,
    // Optional model-resolved Spine skin to render in isolation (e.g. an act chest's
    // normal skin), set when model alias resolution knows the authoritative skin name.
    // When null, standalone-skeleton bakes fall back to a union of all skins.
    string? PreferredSkinName = null,
    // Requested output size (px). Combat-background SCENE renders treat it as the capture viewport;
    // TEXTURE extracts (plain Texture2D and AtlasTexture, R19) RESAMPLE to it after the region crop.
    // Both must be positive; null (or non-positive) => the source resolution / live root viewport size,
    // byte-identical to a request that omits them. See Sts2AssetExtractProvider.ApplyRequestedTextureResize.
    int? RenderWidth = null,
    int? RenderHeight = null,
    // Explicit combat-background layer selection for composed://combat-background/<id>/image:
    // comma-separated ordered res:// layer scene paths (see Sts2CombatBackgroundLayerSelection).
    // Null/whitespace => deterministic-first-sorted discovery, byte-identical to today.
    string? CompositionSelector = null,
    // Lossy encoder quality in 0..1, honoured by the single-image encoder only:
    //   webp + quality => SaveWebpToBuffer(lossy: true, quality); webp without one stays LOSSLESS,
    //   jpg/jpeg       => SaveJpgToBuffer(quality), defaulting to JpegDefaultQuality when absent,
    //   png            => ignored (PNG has no quality dial).
    // Null (or out of range) => the lossless/default call, byte-identical to a request that omits it.
    float? ImageQuality = null,
    // Encode without an alpha channel (RGBA8 -> RGB8 before the encoder sees it). Forced for jpg, which has
    // no alpha at all. False => today's behaviour exactly.
    bool ImageOpaque = false,
    // Event-backdrop FRAME override ("x,y,scale" in 1080-design units) for explicit-size event background
    // renders — see EmbeddableAssetRequest.EventBackgroundFrame. Null/malformed => the reference lerp.
    string? EventBackgroundFrame = null,
    // Scene-SUBTREE still: render ONLY the node at this scene-relative path (detached from a never-tree-entered
    // instantiation of the scene, so no _Ready/_EnterTree script runs — the merchant-room crash class cannot
    // fire). Set by the scene-subtree://<scene>?node=<relPath> key rewrite; null => whole-scene behaviour.
    string? SceneSubtreeNodePath = null);

public sealed record AssetExplainRequestSnapshot(
    string RequestId,
    string SourceRoot,
    string SourcePath,
    string LoadPath);

public sealed record AssetCatalogRequestSnapshot(string Family);

public sealed record AssetCatalogOperationResult(
    DataSourceKind Source,
    bool Provisional,
    string Family,
    IReadOnlyList<AssetCatalogEntrySnapshot> Entries,
    IReadOnlyList<string> Notes,
    AssetExtractFailure? Error)
{
    public static AssetCatalogOperationResult Success(
        DataSourceKind source,
        bool provisional,
        string family,
        IReadOnlyList<AssetCatalogEntrySnapshot> entries,
        IReadOnlyList<string> notes)
        => new(source, provisional, family, entries, notes, null);

    public static AssetCatalogOperationResult Failure(
        DataSourceKind source,
        bool provisional,
        string family,
        AssetExtractFailureCode code,
        string message,
        IReadOnlyList<AssetExtractDetail> details)
        => new(source, provisional, family, [], [], new AssetExtractFailure(code, message, details));
}

public sealed record AssetCatalogEntrySnapshot(
    string Key,
    string KeyPattern,
    string ResolutionKind,
    string? ModelType,
    string? ModelId,
    string? ModelProperty,
    string? SourcePath,
    string? RenderMode,
    IReadOnlyList<string> Notes);

public sealed record AssetExtractOperationResult(
    string RequestId,
    DataSourceKind Source,
    bool Provisional,
    string Format,
    string ContentType,
    int Width,
    int Height,
    byte[] Contents,
    string RenderMode,
    IReadOnlyList<string> Notes,
    AssetExtractProvenanceSnapshot Provenance,
    IReadOnlyList<AssetExtractNoticeSnapshot> Notices,
    AssetExtractArtifactKind ArtifactKind,
    int DurationMs,
    IReadOnlyList<AssetExtractFrame> Frames,
    AssetExtractFailure? Error,
    AssetExtractPlacementMetadataSnapshot? PlacementMetadata = null)
{
    public int ByteLength => Contents.Length;

    public int ExtractionMs { get; init; }

    // Timeline clips only: the node-LOCAL rect the shared frame canvas covers (a SpineSprite carries no
    // localRect, so the consumer aligns the clip by drawing this rect under the node's transform). Null otherwise.
    public AssetExtractClipPlacement? ClipPlacement { get; init; }

    public static AssetExtractOperationResult Success(
        string requestId,
        DataSourceKind source,
        bool provisional,
        string format,
        int width,
        int height,
        byte[] contents,
        string renderMode,
        IReadOnlyList<string> notes)
        => new(
            RequestId: requestId,
            Source: source,
            Provisional: provisional,
            Format: format,
            ContentType: format.ToLowerInvariant() switch
            {
                "png" => "image/png",
                "webp" => "image/webp",
                "gif" => "image/gif",
                "jpeg" or "jpg" => "image/jpeg",
                "avif" => "image/avif",
                _ => "application/octet-stream",
            },
            Width: width,
            Height: height,
            Contents: contents,
            RenderMode: renderMode,
            Notes: notes,
            Provenance: AssetExtractProvenanceSnapshot.FromRequest(requestId, string.Empty, string.Empty, string.Empty, renderMode, source),
            Notices: [],
            ArtifactKind: AssetExtractArtifactKind.Raster,
            DurationMs: 0,
            Frames: [],
            Error: null);

    public static AssetExtractOperationResult SuccessFont(
        string requestId,
        DataSourceKind source,
        bool provisional,
        string format,
        string contentType,
        byte[] contents,
        string renderMode,
        IReadOnlyList<string> notes)
        => new(
            RequestId: requestId,
            Source: source,
            Provisional: provisional,
            Format: format,
            ContentType: contentType,
            Width: 0,
            Height: 0,
            Contents: contents,
            RenderMode: renderMode,
            Notes: notes,
            Provenance: AssetExtractProvenanceSnapshot.FromRequest(requestId, string.Empty, string.Empty, string.Empty, renderMode, source),
            Notices: [],
            ArtifactKind: AssetExtractArtifactKind.Font,
            DurationMs: 0,
            Frames: [],
            Error: null);

    public static AssetExtractOperationResult SuccessRaw(
        string requestId,
        DataSourceKind source,
        bool provisional,
        string format,
        string contentType,
        byte[] contents,
        string renderMode,
        IReadOnlyList<string> notes)
        => new(
            RequestId: requestId,
            Source: source,
            Provisional: provisional,
            Format: format,
            ContentType: contentType,
            Width: 0,
            Height: 0,
            Contents: contents,
            RenderMode: renderMode,
            Notes: notes,
            Provenance: AssetExtractProvenanceSnapshot.FromRequest(requestId, string.Empty, string.Empty, string.Empty, renderMode, source),
            Notices: [],
            ArtifactKind: AssetExtractArtifactKind.Binary,
            DurationMs: 0,
            Frames: [],
            Error: null);

    public static AssetExtractOperationResult SuccessTimeline(
        string requestId,
        DataSourceKind source,
        bool provisional,
        string format,
        int width,
        int height,
        string renderMode,
        int durationMs,
        IReadOnlyList<AssetExtractFrame> frames,
        IReadOnlyList<string> notes,
        AssetExtractClipPlacement? clipPlacement = null)
        => new(
            RequestId: requestId,
            Source: source,
            Provisional: provisional,
            Format: format,
            ContentType: format.ToLowerInvariant() switch
            {
                "png" => "image/png",
                "webp" => "image/webp",
                "gif" => "image/gif",
                "jpeg" or "jpg" => "image/jpeg",
                "avif" => "image/avif",
                _ => "application/octet-stream",
            },
            Width: width,
            Height: height,
            Contents: [],
            RenderMode: renderMode,
            Notes: notes,
            Provenance: AssetExtractProvenanceSnapshot.FromRequest(requestId, string.Empty, string.Empty, string.Empty, renderMode, source),
            Notices: [],
            ArtifactKind: AssetExtractArtifactKind.Timeline,
            DurationMs: durationMs,
            Frames: frames,
            Error: null)
        {
            ClipPlacement = clipPlacement,
        };

    public static AssetExtractOperationResult Failure(
        string requestId,
        DataSourceKind source,
        bool provisional,
        AssetExtractFailureCode code,
        string message,
        IReadOnlyList<AssetExtractDetail> details)
        => new(
            RequestId: requestId,
            Source: source,
            Provisional: provisional,
            Format: string.Empty,
            ContentType: string.Empty,
            Width: 0,
            Height: 0,
            Contents: [],
            RenderMode: string.Empty,
            Notes: [],
            Provenance: AssetExtractProvenanceSnapshot.FromRequest(requestId, string.Empty, string.Empty, string.Empty, string.Empty, source),
            Notices: [],
            ArtifactKind: AssetExtractArtifactKind.Raster,
            DurationMs: 0,
            Frames: [],
            Error: new AssetExtractFailure(code, message, details));
}

public enum AssetExtractArtifactKind
{
    Raster,
    Timeline,
    Metadata,
    Font,
    Binary,
}

public sealed record AssetExtractFrame(
    int Index,
    string Format,
    string ContentType,
    int Width,
    int Height,
    byte[] Contents,
    int DurationMs,
    // Placement of this (independently cropped) frame within the clip's fixed canvas. Lets a clip
    // stream per-frame-tight images while a consumer composites them aligned. CanvasWidth/Height == 0
    // means "no shared canvas" (the frame IS the canvas), for legacy full-frame timeline sources.
    int OffsetX = 0,
    int OffsetY = 0,
    int CanvasWidth = 0,
    int CanvasHeight = 0);

// The node-LOCAL rect a Timeline clip's shared frame canvas covers (in the rendered SpineSprite's local
// coordinates). A live SpineSprite has no localRect, so the browser aligns the clip by drawing this rect
// under the node's streamed transform; each frame's (OffsetX/Y, CanvasWidth/Height) then place it within.
public sealed record AssetExtractClipPlacement(
    double LocalX,
    double LocalY,
    double LocalWidth,
    double LocalHeight);

public sealed record AssetExtractProvenanceSnapshot(
    string SourceRoot,
    string SourcePath,
    string LoadPath,
    string RenderMode,
    string SourceKind)
{
    public static AssetExtractProvenanceSnapshot FromRequest(
        string requestId,
        string sourceRoot,
        string sourcePath,
        string loadPath,
        string renderMode,
        DataSourceKind source)
        => new(sourceRoot, sourcePath, loadPath, renderMode, source.ToString().ToLowerInvariant());
}

public sealed record AssetExtractNoticeSnapshot(
    string Code,
    string Severity,
    string Message,
    string? Path = null);

public sealed record AssetExtractPlacementMetadataSnapshot(
    string SourceScenePath,
    string SourceNodePath,
    AssetCompositionRectSnapshot LocalBounds,
    AssetCompositionRectSnapshot GlobalBounds,
    int OutputWidth,
    int OutputHeight,
    string RenderMode);

public enum AssetExtractFailureCode
{
    NotImplemented,
    BridgeNotAttached,
    RuntimeFailure,
}

public sealed record AssetExtractFailure(
    AssetExtractFailureCode Code,
    string Message,
    IReadOnlyList<AssetExtractDetail> Details);

public sealed record AssetExtractDetail(
    string Field,
    string Value,
    string Note,
    IReadOnlyDictionary<string, object?>? Diagnostic = null);

public sealed record AssetExplainOperationResult(
    string RequestId,
    DataSourceKind Source,
    bool Provisional,
    string SchemaVersion,
    string ExplanationKind,
    AssetCompositionRootSceneSnapshot? RootScene,
    IReadOnlyList<AssetCompositionPlaceholderSnapshot> Placeholders,
    IReadOnlyList<AssetCompositionLayerGroupSnapshot> LayerGroups,
    IReadOnlyList<AssetCompositionSelectedLayerSnapshot> SelectedLayers,
    AssetCompositionBoundsSnapshot? Bounds,
    AssetCompositionRenderPlanSnapshot? Render,
    AssetCompositionActiveSceneSnapshot? ActiveScene,
    IReadOnlyList<AssetCompositionWarningSnapshot> Warnings,
    AssetEncounterScenePackageSnapshot? EncounterScenePackage,
    AssetExtractFailure? Error)
{
    public static AssetExplainOperationResult Success(
        string requestId,
        DataSourceKind source,
        bool provisional,
        AssetCompositionRootSceneSnapshot rootScene,
        IReadOnlyList<AssetCompositionPlaceholderSnapshot> placeholders,
        IReadOnlyList<AssetCompositionLayerGroupSnapshot> layerGroups,
        IReadOnlyList<AssetCompositionSelectedLayerSnapshot> selectedLayers,
        AssetCompositionBoundsSnapshot bounds,
        AssetCompositionRenderPlanSnapshot render,
        AssetCompositionActiveSceneSnapshot activeScene,
        IReadOnlyList<AssetCompositionWarningSnapshot> warnings)
        => new(requestId, source, provisional, "1", "combat-background-composition", rootScene, placeholders, layerGroups, selectedLayers, bounds, render, activeScene, warnings, null, null);

    public static AssetExplainOperationResult SuccessEncounterScenePackage(
        string requestId,
        DataSourceKind source,
        bool provisional,
        AssetEncounterScenePackageSnapshot encounterScenePackage)
        => new(requestId, source, provisional, encounterScenePackage.SchemaVersion, "encounter-scene-package", null, [], [], [], null, null, null, [], encounterScenePackage, null);

    public static AssetExplainOperationResult Failure(
        string requestId,
        DataSourceKind source,
        bool provisional,
        AssetExtractFailureCode code,
        string message,
        IReadOnlyList<AssetExtractDetail> details)
        => new(requestId, source, provisional, string.Empty, string.Empty, null, [], [], [], null, null, null, [], null, new AssetExtractFailure(code, message, details));
}

public sealed record AssetCompositionRootSceneSnapshot(string BackgroundId, string Path, string LoadSource);

public sealed record AssetCompositionPlaceholderSnapshot(string Name, string NodePath, int Order, bool Matched);

public sealed record AssetCompositionLayerGroupSnapshot(
    string Name,
    string Placeholder,
    int CandidateCount,
    IReadOnlyList<string> Candidates,
    string SelectedPath,
    string SelectionSource,
    int Order);

public sealed record AssetCompositionSelectedLayerSnapshot(
    string Placeholder,
    string Path,
    int Order,
    string LoadStatus,
    IReadOnlyList<string> TextureRefs,
    AssetCompositionRectSnapshot? LocalBounds,
    AssetCompositionRectSnapshot? VisibleBounds);

public sealed record AssetCompositionBoundsSnapshot(
    AssetCompositionRectSnapshot Viewport,
    AssetCompositionRectSnapshot FinalComposed,
    AssetCompositionRectSnapshot FinalVisible,
    double TransparentPixelRatio);

public sealed record AssetCompositionRenderPlanSnapshot(
    string RenderMode,
    int WarmupFrames,
    bool TrimTransparentBounds,
    int TransparentCropPadding,
    IReadOnlyList<string> Notes);

public sealed record AssetCompositionActiveSceneSnapshot(
    string Status,
    bool MatchedRoot,
    IReadOnlyList<string> ObservedLayerPaths,
    IReadOnlyList<string> Differences);

public sealed record AssetCompositionWarningSnapshot(
    string Code,
    string Severity,
    string Message,
    IReadOnlyDictionary<string, string> Details);

public sealed record AssetCompositionRectSnapshot(double X, double Y, double Width, double Height);

public sealed record AssetExplainNoticeSnapshot(
    string Code,
    string Severity,
    string Path,
    string Message,
    bool Provisional);

public sealed record AssetVector2Snapshot(double X, double Y);

public sealed record AssetEncounterScenePackageSnapshot(
    string SchemaVersion,
    string EncounterId,
    AssetEncounterViewportSnapshot Viewport,
    AssetEncounterCameraSnapshot Camera,
    AssetEncounterBackgroundSnapshot Background,
    IReadOnlyList<AssetEncounterLogicalActorSnapshot> LogicalActors,
    IReadOnlyList<AssetEncounterVisualPartSnapshot> VisualParts,
    IReadOnlyList<AssetEncounterVisualStateSnapshot> States,
    IReadOnlyList<AssetEncounterVisualTransitionSnapshot> Transitions,
    IReadOnlyList<AssetEncounterRenderTargetSnapshot> RenderTargets,
    IReadOnlyList<AssetExplainNoticeSnapshot> Notices,
    IReadOnlyList<AssetEncounterSelectorDiagnosticSnapshot>? SelectorDiagnostics = null);

public sealed record AssetEncounterViewportSnapshot(int Width, int Height, string CoordinateSpace);

public sealed record AssetEncounterCameraSnapshot(
    double Scale,
    AssetVector2Snapshot Offset,
    string Source,
    string Provenance);

public sealed record AssetEncounterBackgroundSnapshot(
    string SourceScene,
    string SourceQuery,
    string RenderQuery);

public sealed record AssetEncounterLogicalActorSnapshot(
    string ActorId,
    string SlotId,
    AssetCompositionRectSnapshot? TargetRect,
    IReadOnlyList<string> StatePartIds);

public sealed record AssetEncounterVisualPartSnapshot(
    string PartId,
    string ActorId,
    string ScreenSide,
    string AnatomicalSide,
    int Layer,
    AssetCompositionRectSnapshot? ViewportRect,
    AssetEncounterSelectorDiagnosticSnapshot? SelectorDiagnostic = null);

public sealed record AssetEncounterVisualStateSnapshot(
    string StateId,
    IReadOnlyList<string> AffectedPartIds);

public sealed record AssetEncounterVisualTransitionSnapshot(
    string TransitionId,
    string Hook,
    IReadOnlyList<string> AffectedPartIds,
    string ActiveStateId);

public sealed record AssetEncounterRenderTargetSnapshot(
    string TargetId,
    string Kind,
    string Query,
    AssetEncounterRenderTargetDecisionSnapshot? Decision = null);

public sealed record AssetEncounterSelectorDiagnosticSnapshot(
    string PartId,
    string Selector,
    IReadOnlyList<string> NormalizedSelectors,
    IReadOnlyList<AssetEncounterSelectorCandidateSnapshot> Candidates,
    AssetEncounterResolvedNodeSnapshot? ResolvedNode,
    AssetCompositionRectSnapshot? LocalBounds,
    AssetCompositionRectSnapshot? VisibleBounds,
    string Status,
    string TargetStateId,
    string RenderTargetId);

public sealed record AssetEncounterSelectorCandidateSnapshot(
    string Path,
    string Selector,
    string Source,
    string Status);

public sealed record AssetEncounterResolvedNodeSnapshot(
    string Path,
    string Name,
    string Type);

public sealed record AssetEncounterRenderTargetDecisionSnapshot(
    string TargetId,
    string Kind,
    string StateId,
    string PartId,
    string Decision,
    string Reason,
    IReadOnlyList<string> AffectedPartIds);
