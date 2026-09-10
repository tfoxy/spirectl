namespace Spirectl.Sts2.Embedding;

public sealed record EmbeddableAssetRequest(
    string Key,
    string Format = "auto",
    string? RequestId = null,
    TimeSpan? Timeout = null,
    // Requested output size (px), honored by two lanes:
    //   * combat-background SCENE renders — both the literal res://scenes/backgrounds/<id>/<id>_background.tscn
    //     render and the composed composed://combat-background/<id>/image alias re-center their runtime
    //     BgContainer framing on this CAPTURE VIEWPORT, so the requested pixels are rendered natively;
    //   * TEXTURE extracts (R19) — a plain Texture2D or an AtlasTexture is RESAMPLED (Lanczos) to this size
    //     after its region crop / margin frame, so a consumer that needs an exact pixel size (a PWA
    //     home-screen icon) does not have to ship the full-resolution source and scale it itself.
    // Both must be positive to take effect; null (or non-positive) => the source resolution for a texture
    // and the live root viewport size for a scene render, byte-identical to a request that omits them.
    int? RenderWidth = null,
    int? RenderHeight = null,
    // Explicit combat-background layer selection for composed://combat-background/<id>/image:
    // a comma-separated, ordered list of res:// layer scene paths (the layer sub-scenes actually
    // mounted in the live room; the filename convention <id>_bg_NN_*/<id>_fg_* maps each path to
    // its root placeholder). Null/whitespace => today's deterministic-first-sorted discovery,
    // byte-identical. A malformed selector or a layer path the render cannot honor fails cleanly.
    string? CompositionSelector = null,
    // Lossy encoder quality in 0..1 for a single-image render: `webp` with a quality encodes LOSSY (without
    // one it stays lossless), `jpg`/`jpeg` uses it directly. Ignored by PNG. Null => the lossless/default
    // encoder call, byte-identical to a request that omits it.
    float? ImageQuality = null,
    // Drop the alpha channel before encoding (RGBA8 -> RGB8). Implied by jpg, which cannot carry alpha.
    bool ImageOpaque = false,
    // Event-backdrop FRAME override for an explicit-size event background render: "x,y,scale" in 1080-design
    // units — the LIVE backdrop container transform the caller probed, which the render re-centers into the
    // requested viewport instead of predicting the game's placement from the recovered aspect lerp (the game's
    // real constants have drifted from it: measured container y 99.4 vs the lerp's 40 on the shipped Neow).
    // Only read by the explicit-size event-background branch. Null (or malformed) => the reference lerp,
    // byte-identical to a request that omits it.
    string? EventBackgroundFrame = null);

public sealed record EmbeddableAssetBatchRequest(IReadOnlyList<EmbeddableAssetRequest> Requests, bool FailFast = false);

public sealed record EmbeddableAssetResult(bool Success, EmbeddableAssetPayload? Payload, EmbeddableAssetError? Error);

public sealed record EmbeddableAssetBatchResult(string Status, IReadOnlyList<EmbeddableAssetResult> Results);

public sealed record EmbeddableAssetPayload(
    string RequestId,
    string Key,
    string ArtifactKind,
    string Format,
    string ContentType,
    int Width,
    int Height,
    byte[] Contents,
    IReadOnlyList<EmbeddableAssetFrame> Frames,
    EmbeddableAssetProvenance Provenance,
    IReadOnlyList<EmbeddableAssetNotice> Notices)
{
    public int ByteLength => Contents.Length;

    public int DurationMs { get; init; }

    public int ExtractionMs { get; init; }

    // Timeline clips only: the node-LOCAL rect the shared frame canvas covers (a SpineSprite has no localRect,
    // so the browser aligns the clip by drawing this rect under the node's transform). Zero size = absent.
    public double ClipLocalX { get; init; }

    public double ClipLocalY { get; init; }

    public double ClipLocalWidth { get; init; }

    public double ClipLocalHeight { get; init; }
}

// Placement (OffsetX/OffsetY within a shared CanvasWidth x CanvasHeight) lets a clip stream
// per-frame-tight images while a consumer composites them aligned; CanvasWidth/Height == 0 means
// "no shared canvas" (the frame IS the canvas). Mirrors AssetExtractFrame so the embeddable seam
// preserves the per-frame crop metadata the extractor produces (the couch-coop /spines/ stream needs it).
public sealed record EmbeddableAssetFrame(
    int Index,
    string Format,
    string ContentType,
    int Width,
    int Height,
    byte[] Contents,
    int DurationMs,
    int OffsetX = 0,
    int OffsetY = 0,
    int CanvasWidth = 0,
    int CanvasHeight = 0);

public sealed record EmbeddableAssetProvenance(string SourceRoot, string SourcePath, string LoadPath, string RenderMode, string SourceKind);

public sealed record EmbeddableAssetNotice(string Code, string Severity, string Message, string? Path = null);

public sealed record EmbeddableAssetError(string Code, string Message, string? Field = null, string? Value = null, IReadOnlyList<EmbeddableAssetNotice>? Notices = null);
