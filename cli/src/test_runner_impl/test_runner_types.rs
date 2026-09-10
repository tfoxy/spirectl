#[derive(Debug, Clone)]
struct DiscoveredScenario {
    source: ScenarioSource,
    display_path: String,
}

impl DiscoveredScenario {
    fn source_label(&self) -> String {
        match &self.source {
            ScenarioSource::File(path) => path.display().to_string(),
            ScenarioSource::Inline(_) => self.display_path.clone(),
        }
    }
}

#[derive(Debug, Clone)]
enum ScenarioSource {
    File(PathBuf),
    Inline(String),
}

#[derive(Debug, Clone)]
struct LoadedScenario {
    path: String,
    name: String,
    description: Option<String>,
    tags: Vec<String>,
    live_validation: Option<LiveValidationMetadata>,
    base_dir: Option<PathBuf>,
    steps: Vec<ScenarioStep>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct LiveValidationMetadata {
    status: String,
    #[serde(default)]
    reason: String,
}

#[derive(Debug, Clone)]
struct ScenarioStep {
    requested_id: String,
    canonical_id: String,
    input: Value,
    execution: StepExecution,
}

#[derive(Debug, Clone)]
enum StepExecution {
    GameInfo,
    GameDeploy(DeployArgs),
    DevLogs(LogsArgs),
    DevLogHealth(LogHealthArgs),
    DevConsole(ConsoleStepExecution),
    DevDiagnostics(DiagnosticsArgs),
    DevHotReload(HotReloadStepExecution),
    DevDelay(DelayStepExecution),
    DevAssert(AssertExecution),
    DevWaitFor(WaitForExecution),
    DevLoadFixture(LoadFixtureArgs),
    DevLoadScenario(ScenarioLoadArgs),
    DevHttp(HttpProbe),
    DevHttpWait(HttpWaitProbe),
    DevFetch(FetchProbe),
    DevWebsocket(WebsocketProbe),
    DevDebugEvents(DebugEventsStepExecution),
    ProjectHook(ProjectHookExecution),
    DevScreenshot(ScreenshotArgs),
    DevScreenshotDiff(ScreenshotDiffArgs),
    DevSnapshotCompare(SnapshotCompareArgs),
    ActPlayCard(PlayCardArgs),
    ActUsePotion(UsePotionArgs),
    ActChoose(ChooseArgs),
    ActConfirmSelection,
    ActCancelSelection,
    ActSelectMapNode(SelectMapNodeArgs),
    ActEndTurn,
    ActReady(PlayerScopedActionArgs),
    ActUnready(PlayerScopedActionArgs),
    ActSelectCharacter(SelectCharacterArgs),
    ActClaimReward(RewardActionArgs),
    ActSkipRewards,
    ActSelectCard(CardActionArgs),
    ActSkipCardSelection,
    ActSelectBundle(BundleActionArgs),
    ActBuyCard(ShopItemActionArgs),
    ActBuyRelic(ShopItemActionArgs),
    ActBuyPotion(ShopItemActionArgs),
    ActRemoveCard(ShopItemActionArgs),
    ActLeaveShop,
    ActCloseShopInventory,
    ActRest,
    ActSmith(SmithActionArgs),
    ActUseRestSiteOption(RestSiteOptionActionArgs),
    ActProceedRestSite,
    ActOpenChest,
    ActTakeRelic(TakeRelicActionArgs),
    ActProceedTreasureRoom,
    ActBackFromMap,
    ActSelectEventOption(EventOptionActionArgs),
    ActOpenEventShop(EventOptionActionArgs),
    ActUseCrystalSphereControl(CrystalSphereControlActionArgs),
    ActProceedEvent,
}

#[derive(Debug, Clone)]
struct HotReloadStepExecution {
    project: PathBuf,
    build: bool,
    wait: bool,
    timeout_ms: u64,
    interval_ms: u64,
    expect_generation_changed: bool,
}

#[derive(Debug, Clone)]
struct DelayStepExecution {
    ms: u64,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
pub struct ConsoleStepExecution {
    pub command: String,
    #[serde(default)]
    pub args: Vec<String>,
}

#[derive(Debug, Clone)]
struct AssertExecution {
    path: String,
    predicate: Predicate,
    source: Option<StateViewArg>,
    perspective: Option<PerspectiveScopeArg>,
    player_id: Option<String>,
}

#[derive(Debug, Clone)]
struct WaitForExecution {
    path: String,
    predicate: Predicate,
    source: Option<StateViewArg>,
    perspective: Option<PerspectiveScopeArg>,
    player_id: Option<String>,
    timeout_ms: u64,
    interval_ms: u64,
    rpc_timeout_ms: u64,
}

#[derive(Debug, Clone)]
struct ParsedAssertionArgs {
    input: Value,
    assert: AssertExecution,
}

#[derive(Debug, Clone)]
struct ProjectHookExecution {
    name: String,
    input: Option<Value>,
}

#[derive(Debug, Clone)]
struct StepLoadError {
    requested_id: String,
    canonical_id: String,
    input: Value,
    error: Value,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct RawScenarioFile {
    name: String,
    #[serde(default)]
    description: Option<String>,
    #[serde(default)]
    tags: Vec<String>,
    #[serde(default)]
    live_validation: Option<LiveValidationMetadata>,
    steps: Vec<RawStep>,
}

#[derive(Debug, Deserialize)]
#[serde(untagged)]
enum RawStep {
    Bare(String),
    Map(BTreeMap<String, serde_yaml::Value>),
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct LogsStepArgs {
    #[serde(default)]
    limit: Option<u32>,
    #[serde(default)]
    tail: Option<u32>,
    #[serde(default)]
    after_cursor: Option<u64>,
    #[serde(default)]
    level: Option<String>,
    #[serde(default)]
    target: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct LogHealthStepArgs {
    #[serde(default)]
    limit: Option<u32>,
    #[serde(default)]
    tail: Option<u32>,
    #[serde(default)]
    after_cursor: Option<u64>,
    #[serde(default)]
    level: Option<String>,
    #[serde(default)]
    target: Option<String>,
    #[serde(default)]
    exclude_targets: Option<Vec<String>>,
    #[serde(default)]
    exclude_message_regexes: Option<Vec<String>>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct DiagnosticsStepArgs {
    #[serde(default)]
    limit: Option<u32>,
    #[serde(default)]
    tail: Option<u32>,
    #[serde(default)]
    after_cursor: Option<u64>,
    #[serde(default)]
    level: Option<String>,
    #[serde(default)]
    target: Option<String>,
    #[serde(default)]
    exclude_targets: Option<Vec<String>>,
    #[serde(default)]
    exclude_message_regexes: Option<Vec<String>>,
    #[serde(default)]
    preset: Option<String>,
    #[serde(default)]
    width: Option<u32>,
    #[serde(default)]
    height: Option<u32>,
    #[serde(default)]
    preset_catalogs: Option<Vec<PathBuf>>,
    #[serde(default)]
    bundle_dir: Option<PathBuf>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct DelayStepArgs {
    ms: u64,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct PlayerScopedStepArgs {
    #[serde(default)]
    player_id: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct AssertStepArgs {
    path: String,
    #[serde(default)]
    equals: Option<serde_yaml::Value>,
    #[serde(default)]
    contains: Option<serde_yaml::Value>,
    #[serde(default)]
    regex: Option<String>,
    #[serde(default)]
    gt: Option<serde_yaml::Value>,
    #[serde(default)]
    gte: Option<serde_yaml::Value>,
    #[serde(default)]
    lt: Option<serde_yaml::Value>,
    #[serde(default)]
    lte: Option<serde_yaml::Value>,
    #[serde(default)]
    exists: bool,
    #[serde(default)]
    not_exists: bool,
    /// Document to query. Omitted (or `state`) queries the plain state snapshot;
    /// `actions` queries the resolved `spirectl.state-actions/v0` envelope instead.
    #[serde(default)]
    source: Option<String>,
    #[serde(default)]
    perspective: Option<String>,
    #[serde(default)]
    player_id: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct WaitForStepArgs {
    #[serde(flatten)]
    assert: AssertStepArgs,
    #[serde(default)]
    timeout_ms: Option<u64>,
    #[serde(default)]
    interval_ms: Option<u64>,
    #[serde(default)]
    rpc_timeout_ms: Option<u64>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ProbeQueryStepArgs {
    #[serde(default)]
    query: Option<String>,
    #[serde(default)]
    equals: Option<serde_yaml::Value>,
    #[serde(default)]
    contains: Option<serde_yaml::Value>,
    #[serde(default)]
    regex: Option<String>,
    #[serde(default)]
    gt: Option<serde_yaml::Value>,
    #[serde(default)]
    gte: Option<serde_yaml::Value>,
    #[serde(default)]
    lt: Option<serde_yaml::Value>,
    #[serde(default)]
    lte: Option<serde_yaml::Value>,
    #[serde(default)]
    exists: bool,
    #[serde(default)]
    not_exists: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct HttpStepArgs {
    url: String,
    #[serde(default)]
    method: Option<String>,
    #[serde(default)]
    headers: Option<Vec<String>>,
    #[serde(default)]
    body: Option<String>,
    #[serde(default)]
    timeout_ms: Option<u64>,
    #[serde(default)]
    expect_status: Option<u16>,
    #[serde(default)]
    expect_headers: Option<Vec<String>>,
    #[serde(flatten)]
    query: ProbeQueryStepArgs,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct HttpWaitStepArgs {
    url: String,
    #[serde(default)]
    method: Option<String>,
    #[serde(default)]
    headers: Option<Vec<String>>,
    #[serde(default)]
    body: Option<String>,
    #[serde(default)]
    timeout_ms: Option<u64>,
    #[serde(default)]
    expect_status: Option<u16>,
    #[serde(default)]
    expect_headers: Option<Vec<String>>,
    #[serde(flatten)]
    query: ProbeQueryStepArgs,
    #[serde(default)]
    interval_ms: Option<u64>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FetchStepArgs {
    source: PathBuf,
    #[serde(default)]
    output: Option<PathBuf>,
    #[serde(default)]
    expect_sha256: Option<String>,
    #[serde(flatten)]
    query: ProbeQueryStepArgs,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct WebsocketStepArgs {
    url: String,
    #[serde(default)]
    headers: Option<Vec<String>>,
    #[serde(default)]
    send_text: Option<Vec<String>>,
    #[serde(default)]
    expect_text: Option<Vec<String>>,
    #[serde(default)]
    timeout_ms: Option<u64>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct DebugEventsStepArgs {
    #[serde(default)]
    session: Option<String>,
    #[serde(default)]
    from_sequence: Option<u64>,
    #[serde(default)]
    limit: Option<u32>,
    #[serde(default)]
    follow: bool,
    #[serde(default)]
    timeout_ms: Option<u32>,
    #[serde(default)]
    expect_event_kind: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ProjectHookStepArgs {
    name: String,
    #[serde(default)]
    input: Option<Value>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ScreenshotStepArgs {
    #[serde(default)]
    preset: Option<String>,
    #[serde(default)]
    width: Option<u32>,
    #[serde(default)]
    height: Option<u32>,
    #[serde(default)]
    preset_catalogs: Option<Vec<PathBuf>>,
    #[serde(default)]
    output: Option<PathBuf>,
    #[serde(default)]
    rpc_timeout_ms: Option<u64>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ScreenshotDiffStepArgs {
    baseline: PathBuf,
    #[serde(default)]
    actual: Option<PathBuf>,
    #[serde(default)]
    preset: Option<String>,
    #[serde(default)]
    width: Option<u32>,
    #[serde(default)]
    height: Option<u32>,
    #[serde(default)]
    preset_catalogs: Option<Vec<PathBuf>>,
    #[serde(default)]
    bundle_dir: Option<PathBuf>,
    #[serde(default)]
    max_diff_pixels: Option<u64>,
    #[serde(default)]
    max_diff_ratio: Option<f64>,
    #[serde(default)]
    pixel_tolerance: Option<u8>,
    #[serde(default)]
    ignore_alpha: Option<bool>,
    #[serde(default)]
    mask: Option<PathBuf>,
    #[serde(default)]
    regions: Option<PathBuf>,
    #[serde(default)]
    foreground_max_diff_ratio: Option<f64>,
    #[serde(default)]
    roi_max_diff_ratio: Option<f64>,
    #[serde(default)]
    required_comparisons: Option<Vec<String>>,
    #[serde(default)]
    rpc_timeout_ms: Option<u64>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct LoadFixtureStepArgs {
    path: PathBuf,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct LoadScenarioStepArgs {
    path: PathBuf,
    #[serde(default)]
    restart: bool,
    #[serde(default)]
    timeout_ms: Option<u64>,
    #[serde(default)]
    interval_ms: Option<u64>,
    #[serde(default)]
    allow_degraded_local_multiplayer: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct HotReloadStepArgs {
    project: PathBuf,
    #[serde(default)]
    build: bool,
    #[serde(default)]
    wait: bool,
    #[serde(default)]
    timeout_ms: Option<u64>,
    #[serde(default)]
    interval_ms: Option<u64>,
    #[serde(default)]
    expect_generation_changed: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct DeployStepArgs {
    path: PathBuf,
    #[serde(default)]
    build: bool,
    #[serde(default)]
    restart: bool,
    #[serde(default)]
    verify: bool,
    #[serde(default)]
    timeout_ms: Option<u64>,
    #[serde(default)]
    interval_ms: Option<u64>,
    #[serde(default)]
    rpc_timeout_ms: Option<u64>,
    #[serde(default)]
    verify_stable_ms: Option<u64>,
    #[serde(default)]
    allow_stale_build: bool,
    #[serde(default)]
    wait_quiescent_ms: Option<u64>,
    #[serde(default)]
    quiescent_stable_samples: Option<u32>,
    #[serde(default)]
    require_quiescent: bool,
    #[serde(default)]
    launch_args: Vec<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ChooseStepArgs {
    choice: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct PlayCardStepArgs {
    card: String,
    #[serde(default)]
    target: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct UsePotionStepArgs {
    potion: String,
    #[serde(default)]
    target: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SelectCharacterStepArgs {
    character: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SelectMapNodeStepArgs {
    node: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct RewardStepArgs {
    reward: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct CardStepArgs {
    card: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct OptionalCardStepArgs {
    #[serde(default)]
    card: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct BundleStepArgs {
    bundle: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ShopItemStepArgs {
    shop_item: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct RestSiteOptionStepArgs {
    rest_option: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct RelicStepArgs {
    relic: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct EventOptionStepArgs {
    event_option: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct CrystalSphereControlStepArgs {
    control: String,
    selected_tool: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct SnapshotCompareStepArgs {
    spec: PathBuf,
    baseline: PathBuf,
    #[serde(default)]
    preset_catalogs: Option<Vec<PathBuf>>,
    #[serde(default)]
    bundle_dir: Option<PathBuf>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct TestRunReport {
    status: &'static str,
    root: String,
    scenario_count: usize,
    passed_scenario_count: usize,
    failed_scenario_count: usize,
    invalid_scenario_count: usize,
    step_count: usize,
    passed_step_count: usize,
    failed_step_count: usize,
    invalid_step_count: usize,
    elapsed_ms: u128,
    exit_code: i32,
    scenarios: Vec<ScenarioReport>,
    #[serde(skip_serializing_if = "Option::is_none")]
    profile: Option<TestRunProfileSummary>,
    #[serde(skip_serializing_if = "Option::is_none")]
    preflight: Option<Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    cleanup: Option<Value>,
    artifacts: TestRunArtifacts,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    errors: Vec<Value>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct TestRunProfileSummary {
    name: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    description: Option<String>,
    paths: Vec<PathBuf>,
    include_tags: Vec<String>,
    exclude_tags: Vec<String>,
    require_matching_scenario: bool,
    allow_not_applicable: bool,
    require_live_transport: bool,
}

impl TestRunProfileSummary {
    fn from_config(name: &str, config: &TestProfileConfig) -> Self {
        Self {
            name: name.to_string(),
            description: config.description.clone(),
            paths: config.paths.clone(),
            include_tags: config.include_tags.clone(),
            exclude_tags: config.exclude_tags.clone(),
            require_matching_scenario: config.gate.require_matching_scenario,
            allow_not_applicable: config.gate.allow_not_applicable,
            require_live_transport: config.gate.require_live_transport,
        }
    }

    fn unknown(name: &str) -> Self {
        Self {
            name: name.to_string(),
            description: None,
            paths: Vec::new(),
            include_tags: Vec::new(),
            exclude_tags: Vec::new(),
            require_matching_scenario: false,
            allow_not_applicable: false,
            require_live_transport: false,
        }
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ScenarioReport {
    path: String,
    name: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    description: Option<String>,
    status: &'static str,
    elapsed_ms: u128,
    exit_code: i32,
    steps: Vec<StepReport>,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    failure: Option<ScenarioFailure>,
    #[serde(skip_serializing_if = "Option::is_none")]
    recovery_attempt: Option<Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    artifacts: Option<ScenarioArtifacts>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct StepReport {
    index: usize,
    requested_id: String,
    canonical_id: String,
    status: &'static str,
    input: Value,
    #[serde(skip_serializing_if = "Option::is_none")]
    output: Option<Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<Value>,
    exit_code: i32,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct TestRunArtifacts {
    root_dir: String,
    summary_path: String,
    failure_artifacts: FailureArtifactsMode,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    errors: Vec<ArtifactIssue>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ScenarioArtifacts {
    scenario_dir: String,
    result_path: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    steps_dir: Option<String>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    step_artifacts: Vec<StepArtifact>,
    #[serde(skip_serializing_if = "Option::is_none")]
    failure: Option<FailureArtifactPaths>,
    #[serde(skip_serializing_if = "Vec::is_empty")]
    errors: Vec<ArtifactIssue>,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
struct FailureArtifactPaths {
    #[serde(skip_serializing_if = "Option::is_none")]
    summary_path: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    evidence_dir: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    diagnostics_path: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct StepArtifact {
    step_index: usize,
    step_canonical_id: String,
    path: String,
    kind: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ScenarioFailure {
    phase: &'static str,
    step_index: usize,
    step_requested_id: String,
    step_canonical_id: String,
    exit_code: i32,
    error: Value,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ArtifactIssue {
    artifact: String,
    operation: String,
    path: String,
    error: Value,
}
