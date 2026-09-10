#[derive(Debug, Clone, Subcommand)]
pub enum AssetSubcommand {
    #[command(about = "Search resource roots for asset exports with offline/live execution modes.")]
    Extract(AssetExtractArgs),
    #[command(about = "List supported semantic asset key families.")]
    Catalog(AssetCatalogArgs),
    #[command(about = "Resolve one semantic asset key into its provider provenance.")]
    Resolve(AssetResolveArgs),
    #[command(about = "Explain live asset composition decisions without writing artifacts.")]
    Explain(AssetExplainArgs),
    #[command(about = "Extract many assets from a generic JSON manifest.")]
    ExtractBatch(AssetExtractBatchArgs),
}

#[derive(Debug, Clone, Args)]
pub struct AssetCommand {
    #[command(subcommand)]
    pub command: AssetSubcommand,
}

#[derive(Debug, Clone, Copy, ValueEnum, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum AssetFormatArg {
    Auto,
    Webp,
    Png,
    /// Request the asset's structured document straight from the seam (a scene's
    /// `GodotSceneState` JSON, a localization table) and persist the bytes verbatim,
    /// rather than rendering a raster. Passed through to the bridge unchanged.
    Structure,
}

impl Display for AssetFormatArg {
    fn fmt(&self, f: &mut Formatter<'_>) -> std::fmt::Result {
        let value = match self {
            Self::Auto => "auto",
            Self::Webp => "webp",
            Self::Png => "png",
            Self::Structure => "structure",
        };
        f.write_str(value)
    }
}

impl AssetFormatArg {
    fn image_format(self) -> Option<ImageFormat> {
        match self {
            Self::Auto | Self::Structure => None,
            Self::Webp => Some(ImageFormat::WebP),
            Self::Png => Some(ImageFormat::Png),
        }
    }

    fn extension(self) -> Option<&'static str> {
        match self {
            Self::Auto => None,
            Self::Webp => Some("webp"),
            Self::Png => Some("png"),
            Self::Structure => Some("json"),
        }
    }

    fn live_format(self) -> AssetFormatArg {
        match self {
            // `auto` is the render tool's "best raster preview" default; `structure`
            // (and explicit raster formats) pass through to the seam unchanged.
            Self::Auto => Self::Png,
            other => other,
        }
    }
}

/// Map a bridge-reported raster format string back to a concrete output format.
pub(crate) fn parse_asset_output_format(raw: &str) -> Option<AssetFormatArg> {
    match raw.to_ascii_lowercase().as_str() {
        "png" => Some(AssetFormatArg::Png),
        "webp" => Some(AssetFormatArg::Webp),
        _ => None,
    }
}

#[derive(Debug, Clone, Copy, ValueEnum, PartialEq, Eq, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum AssetExecutionArg {
    Auto,
    Offline,
    Live,
}

impl AssetExecutionArg {
    fn as_str(self) -> &'static str {
        match self {
            Self::Auto => "auto",
            Self::Offline => "offline",
            Self::Live => "live",
        }
    }
}

impl Display for AssetExecutionArg {
    fn fmt(&self, f: &mut Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.as_str())
    }
}

#[derive(Debug, Clone, Args)]
pub struct AssetExtractArgs {
    #[arg(
        help = "Asset path, filename, substring, direct res:// path, or typed semantic asset key such as `model://characters/<id>/visuals` or `model://relics/<id>/bigIcon`."
    )]
    pub query: String,

    #[arg(
        long,
        value_enum,
        default_value_t = AssetExecutionArg::Auto,
        help = "Choose offline-only, live-only, or auto extraction. Auto exports readable configured resources offline first, then uses live IPC only when needed."
    )]
    pub execution: AssetExecutionArg,

    #[arg(
        long,
        value_enum,
        default_value_t = AssetFormatArg::Auto,
        help = "Output format for raster and live-preview exports. `auto` preserves offline raster bytes and uses PNG for live previews. `structure` requests the seam's structured document instead of a render (a scene's GodotSceneState JSON, a localization table) and writes it verbatim as .json."
    )]
    pub format: AssetFormatArg,

    #[command(flatten)]
    pub search_roots: AssetSearchRootArgs,
}

#[derive(Debug, Clone, Args)]
pub struct AssetCatalogArgs {
    #[arg(
        long,
        default_value = "all",
        help = "Catalog family to list: all, model, composed, or font."
    )]
    pub family: String,

    #[arg(
        long,
        value_enum,
        default_value_t = AssetExecutionArg::Live,
        help = "Execution strategy for catalog discovery. The current semantic catalog is live-oriented."
    )]
    pub execution: AssetExecutionArg,
}

#[derive(Debug, Clone, Args)]
pub struct AssetResolveArgs {
    #[arg(help = "Semantic asset key to resolve.")]
    pub query: String,

    #[arg(
        long,
        value_enum,
        default_value_t = AssetExecutionArg::Live,
        help = "Execution strategy for key resolution. The current semantic resolver is live-oriented."
    )]
    pub execution: AssetExecutionArg,
}

#[derive(Debug, Clone, Args)]
pub struct AssetExplainArgs {
    #[arg(
        help = "Composed asset query to explain. Supports `composed://combat-background/<id>/image` and `composed://encounters/<id>/scene-package`."
    )]
    pub query: String,

    #[arg(
        long,
        value_enum,
        default_value_t = AssetExecutionArg::Live,
        help = "Execution strategy for explanation. Asset explain currently supports live only."
    )]
    pub execution: AssetExecutionArg,

    #[command(flatten)]
    pub search_roots: AssetSearchRootArgs,
}

#[derive(Debug, Clone, Args)]
pub struct AssetExtractBatchArgs {
    #[arg(
        long,
        help = "Path to a JSON asset batch manifest with version 0 and assets[]."
    )]
    pub manifest: PathBuf,

    #[arg(
        long,
        help = "Override the batch artifact output directory. Defaults to <artifacts.dir>/assets-batch."
    )]
    pub output: Option<PathBuf>,

    #[arg(
        long,
        value_enum,
        default_value_t = AssetExecutionArg::Auto,
        help = "Default extraction mode for requests that omit execution."
    )]
    pub execution: AssetExecutionArg,

    #[arg(
        long,
        value_enum,
        default_value_t = AssetFormatArg::Auto,
        help = "Default output format for requests that omit format. `auto` preserves offline raster bytes and uses PNG for live previews."
    )]
    pub format: AssetFormatArg,

    #[arg(
        long,
        help = "Stop after the first failed, no-match, or invalid request."
    )]
    pub fail_fast: bool,

    #[arg(
        long,
        help = "Validate the batch request surface without resolving roots, launching the game, or writing artifacts."
    )]
    pub dry_run: bool,

    #[command(flatten)]
    pub search_roots: AssetSearchRootArgs,
}

#[derive(Debug, Clone, Args, Default)]
pub struct AssetSearchRootArgs {
    #[arg(long, help = "Override the detected Slay the Spire 2 install root.")]
    pub game_path: Option<PathBuf>,

    #[arg(
        long,
        help = "Override the root directory used for static scene/resource search. Recovered project files are used only when this points at them explicitly."
    )]
    pub resources_dir: Option<PathBuf>,

    #[arg(long, help = "Override the mod root used when --include-mods is set.")]
    pub mods_dir: Option<PathBuf>,

    #[arg(
        long,
        help = "Include mod-root assets in addition to the base resources root."
    )]
    pub include_mods: bool,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct AssetBatchManifest {
    version: u32,
    #[serde(default)]
    assets: Vec<AssetBatchManifestRequest>,
    #[serde(default)]
    requests: Vec<serde_json::Value>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct AssetBatchManifestRequest {
    id: String,
    query: String,
    execution: Option<AssetExecutionArg>,
    format: Option<AssetFormatArg>,
    #[serde(default)]
    metadata: Option<serde_json::Value>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct AssetExtractResponse {
    command: &'static str,
    status: &'static str,
    query: String,
    format: String,
    execution_mode: &'static str,
    output_dir: String,
    match_count: usize,
    export_count: usize,
    exports: Vec<AssetExportRecord>,
}

#[derive(Debug, Clone)]
struct AssetExtractRequestSpec {
    command: &'static str,
    query: String,
    execution: AssetExecutionArg,
    format: AssetFormatArg,
    output_dir: PathBuf,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct AssetRequestError {
    code: String,
    message: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    details: Option<serde_json::Value>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct AssetBatchExtractResponse {
    command: &'static str,
    status: &'static str,
    dry_run: bool,
    manifest_path: String,
    output_dir: String,
    execution_mode: &'static str,
    format: String,
    fail_fast: bool,
    request_count: usize,
    success_count: usize,
    failure_count: usize,
    skipped_count: usize,
    export_count: usize,
    timing: AssetBatchTiming,
    results: Vec<AssetBatchRequestResult>,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
struct AssetBatchTiming {
    total_ms: u64,
    index_ms: u64,
    resolve_ms: u64,
    live_setup_ms: u64,
    export_ms: u64,
}

#[derive(Debug, Clone, Default)]
struct AssetRequestTiming {
    resolve_ms: u64,
    live_setup_ms: u64,
    export_ms: u64,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct AssetBatchRequestResult {
    id: String,
    query: String,
    status: &'static str,
    execution_mode: &'static str,
    format: String,
    output_dir: String,
    match_count: usize,
    export_count: usize,
    resolve_ms: u64,
    live_setup_ms: u64,
    export_ms: u64,
    #[serde(skip_serializing_if = "Option::is_none")]
    metadata: Option<serde_json::Value>,
    exports: Vec<AssetExportRecord>,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<AssetRequestError>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct AssetExportRecord {
    source_id: String,
    source_path: String,
    source_root: String,
    asset_kind: String,
    storage_kind: String,
    container_path: Option<String>,
    resource_type: Option<String>,
    status: &'static str,
    execution_mode: &'static str,
    artifact_kind: Option<&'static str>,
    content_type: Option<String>,
    actual_format: Option<String>,
    render_mode: Option<String>,
    width: Option<u32>,
    height: Option<u32>,
    output_path: Option<String>,
    frames_dir: Option<String>,
    frame_count: Option<u32>,
    duration_ms: Option<u32>,
    extraction_ms: u64,
    provenance: Option<AssetExtractProvenanceRecord>,
    #[serde(skip_serializing_if = "Option::is_none")]
    placement_metadata: Option<AssetExtractPlacementMetadataRecord>,
    notices: Vec<AssetExtractNoticeRecord>,
    notes: Vec<String>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    artifact_checks: Vec<serde_json::Value>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    render_diagnostics: Vec<serde_json::Value>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct AssetExtractProvenanceRecord {
    source_root: String,
    source_path: String,
    load_path: String,
    render_mode: String,
    source_kind: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct AssetExtractNoticeRecord {
    code: String,
    severity: String,
    path: String,
    message: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct AssetExtractPlacementMetadataRecord {
    source_scene_path: String,
    source_node_path: String,
    local_bounds: AssetCompositionRectRecord,
    global_bounds: AssetCompositionRectRecord,
    output_width: u32,
    output_height: u32,
    render_mode: String,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
struct AssetCompositionRectRecord {
    x: f64,
    y: f64,
    width: f64,
    height: f64,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct AssetTimelineManifest {
    source_id: String,
    source_path: String,
    source_root: String,
    asset_kind: String,
    artifact_kind: &'static str,
    actual_format: String,
    render_mode: String,
    width: u32,
    height: u32,
    frame_count: u32,
    duration_ms: u32,
    frames_dir: String,
    frames: Vec<AssetTimelineFrameManifest>,
    notes: Vec<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct AssetTimelineFrameManifest {
    index: u32,
    path: String,
    format: String,
    width: u32,
    height: u32,
    duration_ms: u32,
    // Placement of this independently-cropped frame within the clip's fixed canvas. canvas_width/height
    // == 0 means the frame is the whole canvas (legacy full-frame timeline sources).
    offset_x: i32,
    offset_y: i32,
    canvas_width: u32,
    canvas_height: u32,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct AssetExplainResponse {
    command: &'static str,
    status: &'static str,
    query: String,
    execution_mode: &'static str,
    source_root: String,
    source_path: String,
    asset_kind: String,
    storage_kind: String,
    container_path: Option<String>,
    resource_type: Option<String>,
    explanation_kind: String,
    explanation: serde_json::Value,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct AssetCatalogResponse {
    command: &'static str,
    status: &'static str,
    execution_mode: &'static str,
    source: Option<String>,
    provisional: bool,
    family: String,
    entries: Vec<AssetCatalogEntry>,
    notes: Vec<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct AssetCatalogEntry {
    #[serde(skip_serializing_if = "Option::is_none")]
    key: Option<String>,
    key_pattern: &'static str,
    resolution_kind: &'static str,
    model_type: Option<&'static str>,
    #[serde(skip_serializing_if = "Option::is_none")]
    model_id: Option<String>,
    model_property: Option<&'static str>,
    #[serde(skip_serializing_if = "Option::is_none")]
    source_path: Option<String>,
    render_mode: Option<&'static str>,
    notes: Vec<&'static str>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct AssetResolveResponse {
    command: &'static str,
    status: &'static str,
    query: String,
    key: Option<String>,
    resolution_kind: Option<&'static str>,
    model_type: Option<&'static str>,
    model_id: Option<String>,
    model_property: Option<&'static str>,
    source_path: Option<String>,
    load_path: Option<String>,
    render_mode: Option<&'static str>,
    notes: Vec<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct AssetExplainPayload {
    request_id: String,
    source: String,
    provisional: bool,
    root_scene: AssetExplainRootScene,
    placeholders: Vec<AssetExplainPlaceholder>,
    layer_groups: Vec<AssetExplainLayerGroup>,
    selected_layers: Vec<AssetExplainSelectedLayer>,
    bounds: AssetExplainBounds,
    render: AssetExplainRenderPlan,
    active_scene: AssetExplainActiveScene,
    warnings: Vec<AssetExplainWarning>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct AssetExplainRootScene {
    background_id: String,
    path: String,
    load_source: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct AssetExplainPlaceholder {
    name: String,
    node_path: String,
    order: u32,
    matched: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct AssetExplainLayerGroup {
    name: String,
    placeholder: String,
    candidate_count: u32,
    candidates: Vec<String>,
    selected_path: String,
    selection_source: String,
    order: u32,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct AssetExplainSelectedLayer {
    placeholder: String,
    path: String,
    order: u32,
    load_status: String,
    texture_refs: Vec<String>,
    local_bounds: Option<AssetExplainRect>,
    visible_bounds: Option<AssetExplainRect>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct AssetExplainBounds {
    viewport: AssetExplainRect,
    final_composed: AssetExplainRect,
    final_visible: AssetExplainRect,
    transparent_pixel_ratio: f64,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct AssetExplainRenderPlan {
    render_mode: String,
    warmup_frames: u32,
    trim_transparent_bounds: bool,
    transparent_crop_padding: u32,
    notes: Vec<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct AssetExplainActiveScene {
    status: String,
    matched_root: bool,
    observed_layer_paths: Vec<String>,
    differences: Vec<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct AssetExplainWarning {
    code: String,
    severity: String,
    message: String,
    details: BTreeMap<String, String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct AssetExplainRect {
    x: f64,
    y: f64,
    width: f64,
    height: f64,
}

#[derive(Debug, Clone)]
struct AssetCandidate {
    source_root: String,
    logical_path: String,
    source_path: String,
    relative_path: PathBuf,
    asset_kind: AssetKind,
    storage_kind: String,
    load_path: String,
    file_path: Option<PathBuf>,
    container_path: Option<PathBuf>,
    offline_readable: bool,
    resource_type: Option<String>,
}

#[derive(Debug, Clone)]
struct AssetDiscoveryIndex {
    candidates: Vec<AssetCandidate>,
}

impl AssetCandidate {
    fn source_id(&self) -> String {
        let normalized_source = normalize_path_fragment(&self.source_path);
        if normalized_source.starts_with("model://") || normalized_source.starts_with("composed://")
        {
            return normalized_source;
        }

        format!("{}:{}", self.source_root, normalized_source)
    }

    fn source_path_text(&self) -> String {
        self.source_path.clone()
    }
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct HelperAssetSearchResponse {
    matches: Vec<HelperAssetMatch>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct HelperAssetMatch {
    logical_path: String,
    source_path: String,
    source_root: String,
    asset_kind: String,
    storage_kind: String,
    load_path: String,
    readable_offline: bool,
    container_path: Option<String>,
    resource_type: Option<String>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct HelperAssetReadResponse {
    contents_base64: String,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum AssetKind {
    Image,
    Scene,
    Resource,
    Font,
    CharacterVisual,
    EncounterScenePackage,
    EncounterVisual,
    Other,
}

impl AssetKind {
    fn as_str(self) -> &'static str {
        match self {
            Self::Image => "image",
            Self::Scene => "scene",
            Self::Resource => "resource",
            Self::Font => "font",
            Self::CharacterVisual => "character-visual",
            Self::EncounterScenePackage => "encounter-scene-package",
            Self::EncounterVisual => "encounter-visual",
            Self::Other => "other",
        }
    }

    fn is_reportable(self) -> bool {
        !matches!(self, Self::Other)
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct VirtualCharacterVisualQuery {
    character_id: String,
    variant: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
enum EncounterAssetQuery {
    ScenePackage {
        encounter_id: String,
    },
    Background {
        encounter_id: String,
    },
    VisualStateOverlay {
        encounter_id: String,
        state_id: String,
    },
    VisualPartState {
        encounter_id: String,
        part_id: String,
        state_id: String,
    },
}
