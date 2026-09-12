use super::*;

#[derive(Debug, Clone, Copy, Serialize, Deserialize, ValueEnum, PartialEq, Eq, Default)]
#[serde(rename_all = "kebab-case")]
pub enum Mode {
    #[default]
    Normal,
    Dangerous,
}

impl Display for Mode {
    fn fmt(&self, f: &mut Formatter<'_>) -> std::fmt::Result {
        let value = match self {
            Mode::Normal => "normal",
            Mode::Dangerous => "dangerous",
        };
        f.write_str(value)
    }
}

#[derive(Debug, Clone, Copy, Serialize, Deserialize, ValueEnum, PartialEq, Eq)]
#[serde(rename_all = "kebab-case")]
#[derive(Default)]
pub enum TransportKind {
    Mock,
    #[default]
    Ipc,
    Tcp,
}

impl Display for TransportKind {
    fn fmt(&self, f: &mut Formatter<'_>) -> std::fmt::Result {
        let value = match self {
            TransportKind::Mock => "mock",
            TransportKind::Ipc => "ipc",
            TransportKind::Tcp => "tcp",
        };
        f.write_str(value)
    }
}

#[derive(Debug, Clone, Copy, Serialize, Deserialize, ValueEnum, PartialEq, Eq, Default)]
#[serde(rename_all = "kebab-case")]
pub enum MouseButtonArg {
    #[default]
    Left,
    Right,
    Middle,
}

impl Display for MouseButtonArg {
    fn fmt(&self, f: &mut Formatter<'_>) -> std::fmt::Result {
        let value = match self {
            MouseButtonArg::Left => "left",
            MouseButtonArg::Right => "right",
            MouseButtonArg::Middle => "middle",
        };
        f.write_str(value)
    }
}

#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "kebab-case")]
#[derive(Default)]
pub enum MockScenario {
    #[default]
    MainMenu,
    Combat,
    Map,
    EventRoom,
    TreasureRoom,
    RelicSelection,
    RestSite,
    Shop,
    FakeMerchantPreOpen,
    CrystalSphere,
    CrystalSphereFinished,
    Rewards,
    CardSelection,
    SimpleCardSelection,
    DeckCardSelection,
    BundleSelection,
    CardOverlay,
    PassiveCardOverlay,
    Lobby,
    LobbyReady,
}

#[derive(Debug, Clone, Copy, Serialize, Deserialize, ValueEnum, PartialEq, Eq)]
pub enum PerspectiveScopeArg {
    Local,
    Omniscient,
}

#[derive(Debug, Clone, Copy, ValueEnum, PartialEq, Eq)]
pub enum LogLevelArg {
    Trace,
    Debug,
    Info,
    Warn,
    Error,
}

#[derive(Debug, Clone, Copy, Serialize, Deserialize, ValueEnum, PartialEq, Eq, Default)]
#[serde(rename_all = "kebab-case")]
pub enum FailureArtifactsMode {
    #[default]
    #[value(name = "on-failure")]
    OnFailure,
    Never,
}

impl Display for FailureArtifactsMode {
    fn fmt(&self, f: &mut Formatter<'_>) -> std::fmt::Result {
        let value = match self {
            FailureArtifactsMode::OnFailure => "on-failure",
            FailureArtifactsMode::Never => "never",
        };
        f.write_str(value)
    }
}

#[derive(Debug, Clone, Copy, Serialize, Deserialize, ValueEnum, PartialEq, Eq, Default)]
#[serde(rename_all = "kebab-case")]
pub enum ServiceJobStoreMode {
    #[default]
    Memory,
    Durable,
}

impl Display for ServiceJobStoreMode {
    fn fmt(&self, f: &mut Formatter<'_>) -> std::fmt::Result {
        let value = match self {
            ServiceJobStoreMode::Memory => "memory",
            ServiceJobStoreMode::Durable => "durable",
        };
        f.write_str(value)
    }
}

#[derive(Debug, Clone, Copy, Serialize, Deserialize, ValueEnum, PartialEq, Eq, Default)]
#[serde(rename_all = "kebab-case")]
pub enum ServiceMcpMode {
    #[default]
    Disabled,
    Network,
}

impl Display for ServiceMcpMode {
    fn fmt(&self, f: &mut Formatter<'_>) -> std::fmt::Result {
        let value = match self {
            ServiceMcpMode::Disabled => "disabled",
            ServiceMcpMode::Network => "network",
        };
        f.write_str(value)
    }
}

#[derive(Debug, Clone, Parser)]
#[command(
    name = "sts2",
    version,
    about = "Typed CLI scaffold for Slay the Spire 2 automation",
    subcommand_required = true,
    arg_required_else_help = true
)]
pub struct Cli {
    #[arg(long, global = true, help = "Emit structured JSON output")]
    pub json: bool,

    #[arg(
        long,
        global = true,
        help = "Stream newline-delimited JSON progress lines to stderr for long lifecycle commands (stdout is unchanged)"
    )]
    pub progress: bool,

    #[arg(long, global = true, help = "Path to the sts2 YAML config file")]
    pub config: Option<PathBuf>,

    #[arg(
        long,
        global = true,
        value_enum,
        default_value_t = Mode::Normal,
        help = "CLI mode for safety and behavior defaults"
    )]
    pub mode: Mode,

    #[arg(
        long,
        global = true,
        env = "SPIRECTL_INSTANCE",
        value_name = "NAME",
        help = "Target a named game instance (own bridge socket + Godot user-dir). Use 'auto' to allocate a random name."
    )]
    pub instance: Option<String>,

    #[arg(
        long,
        global = true,
        help = "With an active instance: give it its own game/mods mirror so it can run a different bridge build (Unix only)."
    )]
    pub isolated_build: bool,

    #[command(subcommand)]
    pub command: Commands,
}

impl Cli {
    pub fn parse() -> Self {
        <Self as Parser>::parse()
    }

    pub fn parse_from<I, T>(itr: I) -> Self
    where
        I: IntoIterator<Item = T>,
        T: Into<OsString> + Clone,
    {
        <Self as Parser>::parse_from(itr)
    }
}

#[derive(Debug, Clone, Subcommand)]
pub enum Commands {
    State(StateArgs),
    /// Stream transient combat events (floating damage numbers) as newline-delimited JSON.
    Events(EventsArgs),
    Act(ActCommand),
    Dev(DevCommand),
    Game(GameCommand),
    Config(ConfigCommand),
    Service(ServiceCommand),
    Toolchain(ToolchainCommand),
    Project(ProjectCommand),
    Code(CodeCommand),
    Assets(AssetCommand),
    Models(ModelCommand),
    /// Live combat inspection. `combat preview` returns the game's own damage/block
    /// numbers per hand card × target — the oracle that validates the client's
    /// status-modifier preview formula.
    Combat(CombatCommand),
    /// Player map drawings. `map drawings` returns the annotation strokes (quill/eraser) drawn
    /// on the map screen, transformed into the renderer's content space — the source the
    /// presentation pipeline captures into raw/drawings.json.
    Map(MapCommand),
    Reference(ReferenceCommand),
    Skill(SkillCommand),
    Completion(CompletionCommand),
    Inspect(InspectCommand),
    Test(TestCommand),
}

/// Read-only config surface. `config resolve` prints the install paths the CLI itself would use,
/// already absolute, so scripts never re-parse `sts2.local.yaml` by hand.
#[derive(Debug, Clone, Args)]
pub struct ConfigCommand {
    #[command(subcommand)]
    pub command: ConfigSubcommand,
}

#[derive(Debug, Clone, Subcommand)]
pub enum ConfigSubcommand {
    /// Resolve config into absolute install paths, with the source of each value.
    Resolve(ConfigResolveArgs),
}

#[derive(Debug, Clone, Args, Default)]
pub struct ConfigResolveArgs {
    /// Limit output to these keys: gamePath, assembliesDir, resourcesDir, modsDir,
    /// instancesDir, artifactsDir. Omit for all of them.
    #[arg(value_name = "KEY")]
    pub keys: Vec<String>,
}

#[derive(Debug, Clone, Args)]
pub struct ModelCommand {
    #[arg(
        long,
        help = "Resolve localized model text with a language code, or use 'auto' for the live game language; omit to return localization references"
    )]
    pub language: Option<String>,

    pub family: String,

    pub ids: Vec<String>,

    #[arg(long = "rpc-timeout-ms")]
    pub rpc_timeout_ms: Option<u64>,
}

#[derive(Debug, Clone, Args)]
pub struct CombatCommand {
    #[command(subcommand)]
    pub command: CombatSubcommand,
}

#[derive(Debug, Clone, Subcommand)]
pub enum CombatSubcommand {
    /// The game's own computed damage/block preview for every playable hand card
    /// against each candidate target — the oracle used to validate the client's
    /// status-modifier formula. Computed via the game's damage/block funnel, so all
    /// powers/relics/enchantments are reflected exactly as in-game.
    Preview(CombatPreviewArgs),
}

#[derive(Debug, Clone, Args)]
pub struct CombatPreviewArgs {
    #[arg(
        long = "player-id",
        help = "Perspective player whose hand to preview; omit for the local player"
    )]
    pub player_id: Option<String>,

    #[arg(long = "rpc-timeout-ms")]
    pub rpc_timeout_ms: Option<u64>,
}

#[derive(Debug, Clone, Args)]
pub struct MapCommand {
    #[command(subcommand)]
    pub command: MapSubcommand,
}

#[derive(Debug, Clone, Subcommand)]
pub enum MapSubcommand {
    /// The player annotation strokes (quill/eraser) currently drawn on the map screen, each
    /// transformed into the renderer's "TheMap content space". Empty when the map screen is closed.
    Drawings(MapDrawingsArgs),
    /// Draw (or erase) one annotation stroke on the local player's travel map — the write
    /// counterpart of `map drawings`. Points are in TheMap content space ("x,y;x,y;…"). Useful for
    /// seeding a stroke so the presentation pipeline captures `raw/drawings.json` for stroke rendering.
    /// Same action as `act draw-map-stroke`, surfaced under `map` next to the read path.
    #[command(name = "draw-stroke")]
    DrawStroke(DrawMapStrokeArgs),
}

#[derive(Debug, Clone, Args)]
pub struct MapDrawingsArgs {
    #[arg(
        long = "player-id",
        help = "Reserved for parity; drawings are returned for all players regardless"
    )]
    pub player_id: Option<String>,

    #[arg(long = "rpc-timeout-ms")]
    pub rpc_timeout_ms: Option<u64>,
}

/// Live game reference data that is constant at runtime but is not a game model
/// (cards, relics, …). Topic-addressed and extensible: new topics are added
/// server-side without changing this command. Built-in topics: `colors` (the
/// StsColors palette), `version` (game version, date, and modding summary), and
/// `randomCharacter` (the synthetic "Random Character" lobby entry — not a real
/// game model, so it never appears through `sts2 models characters`).
#[derive(Debug, Clone, Args)]
pub struct ReferenceCommand {
    #[arg(help = "Reference topic to fetch. Built-in topics: colors, version, randomCharacter.")]
    pub topic: String,

    #[arg(
        help = "Optional topic-specific keys to filter by (e.g. color names for the colors topic)."
    )]
    pub keys: Vec<String>,

    #[arg(long = "rpc-timeout-ms")]
    pub rpc_timeout_ms: Option<u64>,
}

#[derive(Debug, Clone, Subcommand)]
pub enum ServiceSubcommand {
    Serve(ServiceServeArgs),
}

#[derive(Debug, Clone, Args)]
pub struct ServiceCommand {
    #[command(subcommand)]
    pub command: ServiceSubcommand,
}

#[derive(Debug, Clone, Args)]
pub struct ServiceServeArgs {
    #[arg(long, default_value = "127.0.0.1:4317")]
    pub listen: std::net::SocketAddr,

    #[arg(long)]
    pub auth_token: Option<String>,

    #[arg(
        long = "mcp-mode",
        value_enum,
        help = "MCP service mode. The default local MCP path remains the stdio sts2-mcp adapter; network mode is explicit."
    )]
    pub mcp_mode: Option<ServiceMcpMode>,

    #[arg(
        long = "mcp-listen",
        help = "Network MCP bind address used only with --mcp-mode network"
    )]
    pub mcp_listen: Option<std::net::SocketAddr>,

    #[arg(
        long = "mcp-auth-token",
        help = "Bearer token for network MCP requests; required for non-loopback MCP binds"
    )]
    pub mcp_auth_token: Option<String>,

    #[arg(
        long = "acknowledge-non-loopback-mcp-threat-model",
        help = "Acknowledge operator responsibility for exposing network MCP beyond loopback"
    )]
    pub acknowledge_non_loopback_mcp_threat_model: bool,

    #[arg(long = "job-store", value_enum)]
    pub job_store: Option<ServiceJobStoreMode>,

    #[arg(
        long = "job-store-dir",
        help = "Durable job-store root under artifacts.dir; defaults to <artifacts.dir>/remote-jobs when durable mode is selected"
    )]
    pub job_store_dir: Option<PathBuf>,
}

pub(crate) const DEFAULT_BRIDGE_RPC_TIMEOUT_MS: u64 = 5_000;

#[derive(Debug, Clone, Args, Default)]
pub struct StateArgs {
    /// Optional view. `actions` resolves the available semantic actions from the
    /// current state envelope; omitted returns the full state snapshot.
    #[arg(value_enum)]
    pub view: Option<StateViewArg>,

    #[arg(long = "rpc-timeout-ms")]
    pub rpc_timeout_ms: Option<u64>,

    #[arg(long, value_enum)]
    pub perspective: Option<PerspectiveScopeArg>,

    #[arg(long)]
    pub player_id: Option<String>,

    #[arg(
        long,
        help = "Stream state change events as newline-delimited JSON instead of a single snapshot."
    )]
    pub watch: bool,

    #[arg(long = "max-events")]
    pub max_events: Option<u64>,

    #[arg(long = "timeout-ms")]
    pub timeout_ms: Option<u64>,

    #[arg(long = "poll-interval-ms", default_value_t = 250)]
    pub poll_interval_ms: u64,

    #[arg(long = "watch-mode", value_enum, default_value_t = StateWatchModeArg::Auto)]
    pub watch_mode: StateWatchModeArg,

    #[arg(long = "fail-fast")]
    pub fail_fast: bool,
}

#[derive(Debug, Clone, Args, Default)]
pub struct EventsArgs {
    #[arg(long = "rpc-timeout-ms")]
    pub rpc_timeout_ms: Option<u64>,

    #[arg(
        long,
        help = "Stream combat events as newline-delimited JSON. Combat events are push-only; this flag is required."
    )]
    pub watch: bool,

    #[arg(long = "max-events")]
    pub max_events: Option<u64>,

    #[arg(long = "timeout-ms")]
    pub timeout_ms: Option<u64>,

    /// Resume from this sequence: replay buffered events with a greater sequence, then stream live.
    /// The host only buffers while at least one watcher is attached, so a cold first watch starts from
    /// the events that follow it rather than from earlier in the combat.
    #[arg(long = "since-sequence", default_value_t = 0)]
    pub since_sequence: u64,

    #[arg(long = "fail-fast")]
    pub fail_fast: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, ValueEnum)]
pub enum StateViewArg {
    Actions,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, ValueEnum, Default)]
pub enum StateWatchModeArg {
    #[default]
    Auto,
    Reactive,
    Poll,
}

#[derive(Debug, Clone, Subcommand)]
pub enum ActSubcommand {
    #[command(name = "play-card")]
    PlayCard(PlayCardArgs),
    Choose(ChooseArgs),
    #[command(name = "confirm-selection")]
    ConfirmSelection(ConfirmSelectionArgs),
    #[command(name = "cancel-selection")]
    CancelSelection(PlayerScopedActionArgs),
    Mouse(MouseCommand),
    #[command(name = "use-potion")]
    UsePotion(UsePotionArgs),
    #[command(name = "open-potion-popup")]
    OpenPotionPopup(PotionActionArgs),
    #[command(name = "start-potion-targeting")]
    StartPotionTargeting(PotionActionArgs),
    #[command(name = "select-target")]
    SelectTarget(SelectTargetActionArgs),
    #[command(name = "discard-potion")]
    DiscardPotion(PotionActionArgs),
    #[command(name = "select-map-node")]
    SelectMapNode(SelectMapNodeArgs),
    #[command(name = "end-turn")]
    EndTurn(PlayerScopedActionArgs),
    #[command(name = "cancel-end-turn")]
    CancelEndTurn(PlayerScopedActionArgs),
    Ready(PlayerScopedActionArgs),
    Unready(PlayerScopedActionArgs),
    #[command(name = "select-character")]
    SelectCharacter(SelectCharacterArgs),
    #[command(name = "join-lobby-player")]
    JoinLobbyPlayer(JoinLobbyPlayerArgs),
    #[command(name = "leave-lobby-player")]
    LeaveLobbyPlayer(PlayerScopedActionArgs),
    #[command(name = "claim-reward")]
    ClaimReward(RewardActionArgs),
    #[command(name = "skip-rewards")]
    SkipRewards(PlayerScopedActionArgs),
    #[command(name = "select-card")]
    SelectCard(CardActionArgs),
    #[command(name = "skip-card-selection")]
    SkipCardSelection(PlayerScopedActionArgs),
    #[command(name = "select-bundle")]
    SelectBundle(BundleActionArgs),
    #[command(name = "buy-card")]
    BuyCard(ShopItemActionArgs),
    #[command(name = "buy-relic")]
    BuyRelic(ShopItemActionArgs),
    #[command(name = "buy-potion")]
    BuyPotion(ShopItemActionArgs),
    #[command(name = "remove-card")]
    RemoveCard(ShopItemActionArgs),
    #[command(name = "leave-shop")]
    LeaveShop(PlayerScopedActionArgs),
    #[command(name = "close-shop-inventory")]
    CloseShopInventory(PlayerScopedActionArgs),
    Rest(PlayerScopedActionArgs),
    Smith(SmithActionArgs),
    #[command(name = "use-rest-site-option")]
    UseRestSiteOption(RestSiteOptionActionArgs),
    #[command(name = "proceed-rest-site")]
    ProceedRestSite(PlayerScopedActionArgs),
    #[command(name = "open-chest")]
    OpenChest(PlayerScopedActionArgs),
    #[command(name = "take-relic")]
    TakeRelic(TakeRelicActionArgs),
    #[command(name = "proceed-treasure-room")]
    ProceedTreasureRoom(PlayerScopedActionArgs),
    #[command(name = "back-from-map")]
    BackFromMap(PlayerScopedActionArgs),
    #[command(name = "select-event-option")]
    SelectEventOption(EventOptionActionArgs),
    #[command(name = "open-event-shop")]
    OpenEventShop(EventOptionActionArgs),
    #[command(name = "use-crystal-sphere-control")]
    UseCrystalSphereControl(CrystalSphereControlActionArgs),
    #[command(name = "proceed-event")]
    ProceedEvent(PlayerScopedActionArgs),
    #[command(name = "toggle-map")]
    ToggleMap(PlayerScopedActionArgs),
    #[command(name = "toggle-deck")]
    ToggleDeck(PlayerScopedActionArgs),
    #[command(name = "toggle-settings")]
    ToggleSettings(PlayerScopedActionArgs),
    #[command(name = "sort-deck-view")]
    SortDeckView(SortDeckViewActionArgs),
    #[command(name = "toggle-deck-view-upgrades")]
    ToggleDeckViewUpgrades(PlayerScopedActionArgs),
    #[command(name = "view-draw-pile")]
    ViewDrawPile(PlayerScopedActionArgs),
    #[command(name = "view-discard-pile")]
    ViewDiscardPile(PlayerScopedActionArgs),
    #[command(name = "view-exhaust-pile")]
    ViewExhaustPile(PlayerScopedActionArgs),
    #[command(name = "inspect-relic")]
    InspectRelic(InspectRelicActionArgs),
    #[command(name = "close-inspect-relic")]
    CloseInspectRelic(PlayerScopedActionArgs),
    #[command(name = "select-hand-card")]
    SelectHandCard(CardActionArgs),
    #[command(name = "deselect-hand-card")]
    DeselectHandCard(CardActionArgs),
    #[command(name = "confirm-hand-selection")]
    ConfirmHandSelection(ConfirmHandSelectionArgs),
    /// Draw (or erase) one annotation stroke on the local player's travel map.
    #[command(name = "draw-map-stroke")]
    DrawMapStroke(DrawMapStrokeArgs),
    /// Clear the local player's travel-map drawings.
    #[command(name = "clear-map-drawings")]
    ClearMapDrawings(PlayerScopedActionArgs),
}

#[derive(Debug, Clone, Args)]
pub struct ActCommand {
    #[command(subcommand)]
    pub command: ActSubcommand,
}

#[derive(Debug, Clone, Args)]
pub struct PlayCardArgs {
    #[arg(long)]
    pub card: String,
    #[arg(long)]
    pub target: Option<String>,
    #[arg(long)]
    pub player_id: Option<String>,
}

#[derive(Debug, Clone, Args)]
pub struct ChooseArgs {
    #[arg(long)]
    pub choice: String,
    #[arg(long)]
    pub player_id: Option<String>,
}

#[derive(Debug, Clone, Args)]
pub struct SelectMapNodeArgs {
    #[arg(long)]
    pub node: String,
    #[arg(long)]
    pub player_id: Option<String>,
}

#[derive(Debug, Clone, Args)]
pub struct DrawMapStrokeArgs {
    /// Polyline points in TheMap content space as "x,y;x,y;…" (at least two points).
    #[arg(long)]
    pub points: String,
    /// Erase (clear-to-background) instead of drawing with the quill.
    #[arg(long)]
    pub eraser: bool,
    #[arg(long)]
    pub player_id: Option<String>,
}

#[derive(Debug, Clone, Args)]
pub struct UsePotionArgs {
    #[arg(long)]
    pub potion: String,

    #[arg(long)]
    pub target: Option<String>,

    #[arg(long)]
    pub player_id: Option<String>,
}

#[derive(Debug, Clone, Args)]
pub struct PotionActionArgs {
    #[arg(long)]
    pub potion: String,

    #[arg(long)]
    pub player_id: Option<String>,
}

#[derive(Debug, Clone, Args)]
pub struct SelectTargetActionArgs {
    #[arg(long)]
    pub target: String,

    #[arg(long)]
    pub player_id: Option<String>,
}

#[derive(Debug, Clone, Args)]
pub struct SelectCharacterArgs {
    #[arg(long)]
    pub character: String,
    #[arg(long)]
    pub player_id: Option<String>,
}

#[derive(Debug, Clone, Args)]
pub struct JoinLobbyPlayerArgs {
    #[arg(long = "display-name")]
    pub display_name: String,
}

#[derive(Debug, Clone, Args)]
pub struct RewardActionArgs {
    #[arg(long)]
    pub reward: String,
    #[arg(long)]
    pub player_id: Option<String>,
}

#[derive(Debug, Clone, Args)]
pub struct CardActionArgs {
    #[arg(long)]
    pub card: String,
    #[arg(long)]
    pub player_id: Option<String>,
    /// Optional reward id (reward:<player-id>:visible:<index>). When set, commits a
    /// per-player undoable card-CHOICE reward opened client-side: the bridge engages
    /// the owning player's reward and adds the chosen offered card to its deck.
    #[arg(long)]
    pub reward: Option<String>,
}

#[derive(Debug, Clone, Args)]
pub struct ConfirmHandSelectionArgs {
    /// Card id(s) to stage before confirming (repeatable `--card`); from
    /// state.run.view.handSelection.selectableCardIds. Omit to confirm whatever
    /// is already staged on the host.
    #[arg(long = "card")]
    pub cards: Vec<String>,
    #[arg(long)]
    pub player_id: Option<String>,
}

#[derive(Debug, Clone, Default, Args)]
pub struct ConfirmSelectionArgs {
    /// Card id(s) to stage before confirming (repeatable `--card`); from
    /// state.run.players[].overlays[].deckCardSelection.cards[].id. Used by the
    /// deck-card-removal preview to commit a client-staged selection in one call.
    /// Omit to confirm whatever is already staged on the host.
    #[arg(long = "card")]
    pub cards: Vec<String>,
    #[arg(long)]
    pub player_id: Option<String>,
    /// Optional reward id (reward:<player-id>:visible:<index>). When set, commits a
    /// per-player undoable card-REMOVAL reward opened client-side: the bridge engages
    /// the owning player's reward and removes the staged card from its deck.
    #[arg(long)]
    pub reward: Option<String>,
}

#[derive(Debug, Clone, Args)]
pub struct BundleActionArgs {
    #[arg(long)]
    pub bundle: String,
    #[arg(long)]
    pub player_id: Option<String>,
}

#[derive(Debug, Clone, Args)]
pub struct ShopItemActionArgs {
    #[arg(long = "shop-item")]
    pub shop_item: String,
    #[arg(long)]
    pub player_id: Option<String>,
}

#[derive(Debug, Clone, Args)]
pub struct RestSiteOptionActionArgs {
    #[arg(long = "rest-option", alias = "option")]
    pub option: String,
    #[arg(long)]
    pub player_id: Option<String>,
}

#[derive(Debug, Clone, Args)]
pub struct SmithActionArgs {
    #[arg(long)]
    pub card: Option<String>,
    #[arg(long)]
    pub player_id: Option<String>,
}

#[derive(Debug, Clone, Args)]
pub struct TakeRelicActionArgs {
    #[arg(long = "relic")]
    pub relic: String,
    #[arg(long)]
    pub player_id: Option<String>,
}

#[derive(Debug, Clone, Args)]
pub struct InspectRelicActionArgs {
    #[arg(long = "relic")]
    pub relic: String,
    #[arg(long)]
    pub player_id: Option<String>,
}

#[derive(Debug, Clone, Args)]
pub struct EventOptionActionArgs {
    #[arg(long = "event-option")]
    pub event_option: String,
    #[arg(long)]
    pub player_id: Option<String>,
}

#[derive(Debug, Clone, Args)]
pub struct CrystalSphereControlActionArgs {
    #[arg(long)]
    pub control: String,
    #[arg(long, value_parser = ["big", "small"])]
    pub selected_tool: Option<String>,
    #[arg(long)]
    pub player_id: Option<String>,
}

#[derive(Debug, Clone, Args)]
pub struct SortDeckViewActionArgs {
    #[arg(long, value_parser = ["obtained", "type", "cost", "alphabet"])]
    pub by: String,
    #[arg(long)]
    pub player_id: Option<String>,
}

#[derive(Debug, Clone, Default, Args)]
pub struct PlayerScopedActionArgs {
    #[arg(long)]
    pub player_id: Option<String>,
}

#[derive(Debug, Clone, Args)]
pub struct MouseCommand {
    #[command(subcommand)]
    pub command: MouseSubcommand,
}

#[derive(Debug, Clone, Subcommand)]
pub enum MouseSubcommand {
    Click(MouseClickArgs),
}

#[derive(Debug, Clone, Args)]
pub struct MouseClickArgs {
    #[arg(long)]
    pub x: i32,

    #[arg(long)]
    pub y: i32,

    #[arg(long, value_enum, default_value_t = MouseButtonArg::Left)]
    pub button: MouseButtonArg,
}

#[derive(Debug, Clone, Subcommand)]
pub enum DevSubcommand {
    Logs(LogsArgs),
    #[command(name = "log-health")]
    LogHealth(LogHealthArgs),
    Console(ConsoleArgs),
    Heal(DevHealArgs),
    Diagnostics(DiagnosticsArgs),
    Assert(AssertArgs),
    Http(DevHttpArgs),
    #[command(name = "http-wait")]
    HttpWait(DevHttpWaitArgs),
    Fetch(DevFetchArgs),
    Websocket(DevWebsocketArgs),
    #[command(name = "wait-for")]
    WaitFor(WaitForArgs),
    #[command(name = "wait-for-transitions")]
    WaitForTransitions(WaitForTransitionsArgs),
    Fixture(FixtureCommand),
    #[command(name = "load-fixture", hide = true)]
    LoadFixture(LoadFixtureArgs),
    Scenario(ScenarioCommand),
    Debug(DebugCommand),
    Breakpoint(BreakpointCommand),
    Screenshot(ScreenshotArgs),
    #[command(name = "screenshot-diff")]
    ScreenshotDiff(ScreenshotDiffArgs),
    Snapshot(SnapshotCommand),
    #[command(name = "mod-reload")]
    ModReload(ModReloadCommand),
    Scene(DevSceneCommand),
    #[command(name = "visual-preflight")]
    VisualPreflight(VisualPreflightArgs),
}

#[derive(Debug, Clone, Args)]
pub struct VisualPreflightArgs {
    #[arg(long, default_value = "kaiser_crab_boss")]
    pub encounter: String,
    #[arg(long = "repair-stale-endpoint")]
    pub repair_stale_endpoint: bool,
    #[arg(long)]
    pub attach: bool,
    #[arg(long)]
    pub launch: bool,
    #[arg(long = "print-only")]
    pub print_only: bool,
    #[arg(long, default_value_t = 30_000)]
    pub timeout_ms: u64,
    #[arg(long, default_value_t = 250)]
    pub interval_ms: u64,
    #[arg(long = "rpc-timeout-ms", default_value_t = 5_000)]
    pub rpc_timeout_ms: u64,
}

#[derive(Debug, Clone, Args)]
#[command(about = "Instantly heal a creature on the host (devtool, not a legal player action).")]
pub struct DevHealArgs {
    #[arg(
        long,
        help = "Target creature id (`creature:<combatId>` from `sts2 state` — players, allies, or enemies). Defaults to the acting seat's own player creature."
    )]
    pub target: Option<String>,

    #[arg(
        long,
        conflicts_with = "full",
        help = "HP to restore (clamped to max HP by the game). Healing a dead creature revives it."
    )]
    pub amount: Option<i32>,

    #[arg(
        long,
        help = "Set current HP to max HP instead of adding an amount. Also revives a dead creature. NOTE: host-direct mutation can desync real remote network clients; fine for couch co-op's single real player."
    )]
    pub full: bool,
}

#[derive(Debug, Clone, Args)]
pub struct ConsoleArgs {
    #[arg(help = "STS2 in-game developer console command name.")]
    pub command: String,

    #[arg(
        trailing_var_arg = true,
        allow_hyphen_values = true,
        help = "Arguments passed to the STS2 in-game developer console command."
    )]
    pub args: Vec<String>,
}

#[derive(Debug, Clone, Args)]
pub struct DevCommand {
    #[command(subcommand)]
    pub command: DevSubcommand,
}

#[derive(Debug, Clone, Args)]
pub struct ModReloadCommand {
    #[command(subcommand)]
    pub command: Option<ModReloadSubcommand>,

    #[arg(long)]
    pub project: Option<PathBuf>,

    #[arg(long)]
    pub build: bool,

    #[arg(long)]
    pub wait: bool,

    #[arg(long = "timeout-ms", default_value_t = 30_000)]
    pub timeout_ms: u64,

    #[arg(long = "interval-ms", default_value_t = 250)]
    pub interval_ms: u64,
}

#[derive(Debug, Clone, Subcommand)]
pub enum ModReloadSubcommand {
    Status(ModReloadStatusArgs),
}

#[derive(Debug, Clone, Args)]
pub struct ModReloadStatusArgs {
    #[arg(long)]
    pub project: PathBuf,
}

#[derive(Debug, Clone, Args)]
pub struct ScenarioCommand {
    #[command(subcommand)]
    pub command: ScenarioSubcommand,
}

#[derive(Debug, Clone, Args)]
pub struct FixtureCommand {
    #[command(subcommand)]
    pub command: FixtureSubcommand,
}

#[derive(Debug, Clone, Subcommand)]
pub enum FixtureSubcommand {
    #[command(about = "Record the current screen-entry fixture recipe into ignored local storage.")]
    Record,
    #[command(about = "Resume the ignored recorded screen-entry fixture through fixture loading.")]
    Resume(RecordedFixtureResumeArgs),
    #[command(about = "Show whether an ignored recorded screen-entry fixture exists.")]
    Status,
    #[command(about = "Clear the ignored recorded screen-entry fixture.")]
    Clear,
    #[command(about = "Load an authored sparse recipe fixture through the live bridge.")]
    Load(LoadFixtureArgs),
    #[command(
        name = "load-latest",
        about = "Reload the fixture last loaded in this worktree (recorded under .sts2/fixtures)."
    )]
    LoadLatest(LoadLatestFixtureArgs),
}

#[derive(Debug, Clone, Subcommand)]
pub enum ScenarioSubcommand {
    #[command(
        about = "Export a sparse spirectl.scenario/v0 artifact with field-level restoreSupport."
    )]
    Export(ScenarioExportArgs),
    #[command(
        about = "Load a spirectl.scenario/v0 artifact and report field-level restore validation."
    )]
    Load(ScenarioLoadArgs),
}

#[derive(Debug, Clone, Args)]
pub struct ScenarioExportArgs {
    /// Path where the shareable sparse .sts2.scenario.yaml artifact should be written.
    #[arg(long)]
    pub output: PathBuf,

    #[arg(
        long = "include-exact",
        help = "Also write an opaque exact sidecar when native save-backed or fixture-backed continuation data is available"
    )]
    pub include_exact: bool,
}

#[derive(Debug, Clone, Args)]
#[command(group(
    ArgGroup::new("path_input")
        .required(true)
        .multiple(false)
        .args(["path", "path_flag"])
))]
pub struct ScenarioLoadArgs {
    /// Path to one shareable sparse .sts2.scenario.yaml artifact.
    #[arg(value_name = "PATH")]
    pub path: Option<PathBuf>,

    /// Path to one shareable sparse .sts2.scenario.yaml artifact.
    #[arg(long = "path", value_name = "PATH")]
    pub path_flag: Option<PathBuf>,

    #[arg(long)]
    pub restart: bool,

    #[arg(long = "timeout-ms", default_value_t = 30_000)]
    pub timeout_ms: u64,

    #[arg(long = "interval-ms", default_value_t = 250)]
    pub interval_ms: u64,

    #[arg(
        long = "allow-degraded-local-multiplayer",
        help = "Allow active multiplayer artifacts to restore as degraded local-only state and report omitted remote clients"
    )]
    pub allow_degraded_local_multiplayer: bool,
}

impl ScenarioLoadArgs {
    pub fn path(&self) -> &Path {
        self.path
            .as_deref()
            .or(self.path_flag.as_deref())
            .expect("clap requires exactly one scenario load path")
    }

    pub fn from_path(path: PathBuf) -> Self {
        Self {
            path: Some(path),
            path_flag: None,
            restart: false,
            timeout_ms: 30_000,
            interval_ms: 250,
            allow_degraded_local_multiplayer: false,
        }
    }
}

#[derive(Debug, Clone, Args)]
pub struct RecordedFixtureResumeArgs {
    #[arg(long)]
    pub restart: bool,

    #[arg(long = "timeout-ms", default_value_t = 30_000)]
    pub timeout_ms: u64,

    #[arg(long = "interval-ms", default_value_t = 250)]
    pub interval_ms: u64,
}

#[derive(Debug, Clone, Args)]
pub struct LoadLatestFixtureArgs {
    #[arg(
        long,
        help = "Restart the game (stop + launch + attach) before reloading the latest fixture"
    )]
    pub restart: bool,

    #[arg(long = "timeout-ms", default_value_t = 30_000)]
    pub timeout_ms: u64,

    #[arg(long = "interval-ms", default_value_t = 250)]
    pub interval_ms: u64,
}

#[derive(Debug, Clone, Args)]
pub struct DebugCommand {
    #[command(subcommand)]
    pub command: DebugSubcommand,
}

#[derive(Debug, Clone, Subcommand)]
pub enum DebugSubcommand {
    Status(DebugStatusArgs),
    Session(DebugSessionCommand),
    Events(DebugEventsArgs),
    Pause(DebugSessionBoundArgs),
    Resume(DebugSessionBoundArgs),
    Step(DebugStepArgs),
    Wait(DebugWaitArgs),
}

#[derive(Debug, Clone, Copy, Serialize, Deserialize, ValueEnum, PartialEq, Eq)]
#[serde(rename_all = "kebab-case")]
pub enum DebugStepKindArg {
    Frame,
    Action,
}

#[derive(Debug, Clone, Copy, Serialize, Deserialize, ValueEnum, PartialEq, Eq)]
#[serde(rename_all = "kebab-case")]
pub enum DebugBreakpointKindArg {
    Match,
    Change,
}

#[derive(Debug, Clone, Copy, Serialize, Deserialize, ValueEnum, PartialEq, Eq)]
#[serde(rename_all = "kebab-case")]
pub enum DebugSessionRoleArg {
    Controller,
    Observer,
}

#[derive(Debug, Clone, Args)]
pub struct DebugStatusArgs {
    #[arg(long)]
    pub session: Option<String>,
}

#[derive(Debug, Clone, Args)]
pub struct DebugSessionBoundArgs {
    #[arg(long)]
    pub session: Option<String>,
}

#[derive(Debug, Clone, Args)]
pub struct DebugWaitArgs {
    #[arg(long)]
    pub session: String,

    #[arg(long = "timeout-ms", default_value_t = 5000)]
    pub timeout_ms: u32,
}

#[derive(Debug, Clone, Args)]
pub struct DebugSessionCommand {
    #[command(subcommand)]
    pub command: DebugSessionSubcommand,
}

#[derive(Debug, Clone, Subcommand)]
pub enum DebugSessionSubcommand {
    Start(DebugSessionStartArgs),
    Status(DebugSessionStatusArgs),
    End(DebugSessionEndArgs),
}

#[derive(Debug, Clone, Args)]
pub struct DebugSessionStartArgs {
    #[arg(long)]
    pub name: Option<String>,

    #[arg(long, value_enum, default_value_t = DebugSessionRoleArg::Controller)]
    pub role: DebugSessionRoleArg,

    #[arg(long)]
    pub pause: bool,

    #[arg(long = "lease-timeout-ms", default_value_t = 60_000)]
    pub lease_timeout_ms: u32,
}

#[derive(Debug, Clone, Args)]
pub struct DebugEventsArgs {
    #[arg(long)]
    pub session: Option<String>,

    #[arg(long = "from-sequence", default_value_t = 0)]
    pub from_sequence: u64,

    #[arg(long, default_value_t = 100)]
    pub limit: u32,

    #[arg(long)]
    pub follow: bool,

    #[arg(long = "timeout-ms", default_value_t = 5000)]
    pub timeout_ms: u32,
}

#[derive(Debug, Clone, Args)]
pub struct DebugSessionStatusArgs {
    #[arg(long)]
    pub id: String,
}

#[derive(Debug, Clone, Args)]
pub struct DebugSessionEndArgs {
    #[arg(long)]
    pub id: String,

    #[arg(long)]
    pub resume: bool,
}

#[derive(Debug, Clone, Args)]
pub struct DebugStepArgs {
    #[arg(long)]
    pub session: Option<String>,

    #[arg(long, value_enum)]
    pub kind: DebugStepKindArg,

    #[arg(long, default_value_t = 1)]
    pub count: u32,
}

#[derive(Debug, Clone, Args)]
pub struct BreakpointCommand {
    #[command(subcommand)]
    pub command: BreakpointSubcommand,
}

#[derive(Debug, Clone, Subcommand)]
#[allow(
    clippy::large_enum_variant,
    reason = "Clap subcommand variants preserve the public CLI argument type shape."
)]
pub enum BreakpointSubcommand {
    List(DebugSessionBoundArgs),
    Add(BreakpointAddArgs),
    Remove(BreakpointRemoveArgs),
}

#[derive(Debug, Clone, Args)]
pub struct BreakpointAddArgs {
    #[arg(long)]
    pub session: Option<String>,

    #[arg(long)]
    pub path: String,

    #[arg(long, value_enum, default_value_t = DebugBreakpointKindArg::Match)]
    pub kind: DebugBreakpointKindArg,

    #[command(flatten)]
    pub predicate: OptionalQueryPredicateArgs,

    #[arg(long)]
    pub name: Option<String>,

    #[arg(long = "min-hit-count", default_value_t = 1)]
    pub min_hit_count: u32,

    #[arg(long = "auto-remove-on-hit")]
    pub auto_remove_on_hit: bool,
}

#[derive(Debug, Clone, Args, Default)]
#[command(group(
    ArgGroup::new("predicate")
        .required(false)
        .multiple(false)
        .args([
            "equals",
            "contains",
            "regex",
            "gt",
            "gte",
            "lt",
            "lte",
            "exists",
            "not_exists"
        ])
))]
pub struct OptionalQueryPredicateArgs {
    #[arg(long)]
    pub equals: Option<String>,

    #[arg(long)]
    pub contains: Option<String>,

    #[arg(long)]
    pub regex: Option<String>,

    #[arg(long)]
    pub gt: Option<String>,

    #[arg(long)]
    pub gte: Option<String>,

    #[arg(long)]
    pub lt: Option<String>,

    #[arg(long)]
    pub lte: Option<String>,

    #[arg(long)]
    pub exists: bool,

    #[arg(long = "not-exists")]
    pub not_exists: bool,
}

impl OptionalQueryPredicateArgs {
    pub(crate) fn to_predicate(&self) -> Result<Option<Predicate>, String> {
        if let Some(value) = &self.equals {
            Ok(Some(Predicate::from_raw_equals(value)))
        } else if let Some(value) = &self.contains {
            Ok(Some(Predicate::from_raw_contains(value)))
        } else if let Some(value) = &self.regex {
            Predicate::from_raw_regex(value).map(Some)
        } else if let Some(value) = &self.gt {
            Ok(Some(Predicate::from_raw_gt(value)))
        } else if let Some(value) = &self.gte {
            Ok(Some(Predicate::from_raw_gte(value)))
        } else if let Some(value) = &self.lt {
            Ok(Some(Predicate::from_raw_lt(value)))
        } else if let Some(value) = &self.lte {
            Ok(Some(Predicate::from_raw_lte(value)))
        } else if self.exists {
            Ok(Some(Predicate::Exists))
        } else if self.not_exists {
            Ok(Some(Predicate::NotExists))
        } else {
            Ok(None)
        }
    }
}

#[derive(Debug, Clone, Args)]
pub struct BreakpointRemoveArgs {
    #[arg(long)]
    pub session: Option<String>,

    #[arg(long)]
    pub id: String,
}

#[derive(Debug, Clone, Args)]
pub struct SnapshotCommand {
    #[command(subcommand)]
    pub command: SnapshotSubcommand,
}

#[derive(Debug, Clone, Subcommand)]
pub enum SnapshotSubcommand {
    Export(SnapshotExportArgs),
    Compare(SnapshotCompareArgs),
}

#[derive(Debug, Clone, Args)]
pub struct LogsArgs {
    /// Where to read from: the bridge's own log ring (default), the launched
    /// game's captured stdout/stderr, or both. `game-stdio` is the only source
    /// that survives a crash the bridge logger never saw.
    #[arg(long, value_enum, default_value_t = LogSourceArg::Bridge)]
    pub source: LogSourceArg,

    #[arg(long, default_value_t = 50)]
    pub limit: u32,

    #[arg(long)]
    pub tail: Option<u32>,

    #[arg(long)]
    pub after_cursor: Option<u64>,

    #[arg(long)]
    pub follow: bool,

    #[arg(long, value_enum)]
    pub level: Option<LogLevelArg>,

    #[arg(long)]
    pub target: Option<String>,
}

#[derive(Debug, Clone, Copy, ValueEnum, PartialEq, Eq, Default)]
pub enum LogSourceArg {
    #[default]
    Bridge,
    #[value(name = "game-stdio")]
    GameStdio,
    Both,
}

#[derive(Debug, Clone, Args)]
pub struct LogHealthArgs {
    #[arg(long, default_value_t = 50)]
    pub limit: u32,

    #[arg(long)]
    pub tail: Option<u32>,

    #[arg(long)]
    pub after_cursor: Option<u64>,

    #[arg(long, value_enum)]
    pub level: Option<LogLevelArg>,

    #[arg(long)]
    pub target: Option<String>,

    #[arg(long = "exclude-target")]
    pub exclude_targets: Vec<String>,

    #[arg(long = "exclude-message-regex")]
    pub exclude_message_regexes: Vec<String>,
}

#[derive(Debug, Clone, Args)]
pub struct DiagnosticsArgs {
    #[arg(long, default_value_t = 100)]
    pub limit: u32,

    #[arg(long)]
    pub tail: Option<u32>,

    #[arg(long)]
    pub after_cursor: Option<u64>,

    #[arg(long, value_enum)]
    pub level: Option<LogLevelArg>,

    #[arg(long)]
    pub target: Option<String>,

    #[arg(long = "exclude-target")]
    pub exclude_targets: Vec<String>,

    #[arg(long = "exclude-message-regex")]
    pub exclude_message_regexes: Vec<String>,

    #[arg(long)]
    pub preset: Option<String>,

    #[arg(long)]
    pub width: Option<u32>,

    #[arg(long)]
    pub height: Option<u32>,

    #[arg(long = "preset-catalog")]
    pub preset_catalogs: Vec<PathBuf>,

    #[arg(long = "hot-reload-project")]
    pub hot_reload_project: Option<PathBuf>,

    #[arg(long)]
    pub bundle_dir: Option<PathBuf>,
}

#[derive(Debug, Clone, Args)]
pub struct SnapshotExportArgs {
    #[arg(long)]
    pub spec: PathBuf,

    #[arg(long)]
    pub output: PathBuf,

    #[arg(long = "preset-catalog")]
    pub preset_catalogs: Vec<PathBuf>,
}

#[derive(Debug, Clone, Args)]
pub struct SnapshotCompareArgs {
    #[arg(long)]
    pub spec: PathBuf,

    #[arg(long)]
    pub baseline: PathBuf,

    #[arg(long = "preset-catalog")]
    pub preset_catalogs: Vec<PathBuf>,

    #[arg(long)]
    pub bundle_dir: Option<PathBuf>,
}

#[derive(Debug, Clone, Args)]
#[command(group(
    ArgGroup::new("predicate")
        .required(true)
        .multiple(false)
        .args([
            "equals",
            "contains",
            "regex",
            "gt",
            "gte",
            "lt",
            "lte",
            "exists",
            "not_exists"
        ])
))]
pub struct QueryPredicateArgs {
    #[arg(long)]
    pub equals: Option<String>,

    #[arg(long)]
    pub contains: Option<String>,

    #[arg(long)]
    pub regex: Option<String>,

    #[arg(long)]
    pub gt: Option<String>,

    #[arg(long)]
    pub gte: Option<String>,

    #[arg(long)]
    pub lt: Option<String>,

    #[arg(long)]
    pub lte: Option<String>,

    #[arg(long)]
    pub exists: bool,

    #[arg(long = "not-exists")]
    pub not_exists: bool,
}

impl QueryPredicateArgs {
    pub(crate) fn to_predicate(&self) -> Result<Predicate, String> {
        if let Some(value) = &self.equals {
            Ok(Predicate::from_raw_equals(value))
        } else if let Some(value) = &self.contains {
            Ok(Predicate::from_raw_contains(value))
        } else if let Some(value) = &self.regex {
            Predicate::from_raw_regex(value)
        } else if let Some(value) = &self.gt {
            Ok(Predicate::from_raw_gt(value))
        } else if let Some(value) = &self.gte {
            Ok(Predicate::from_raw_gte(value))
        } else if let Some(value) = &self.lt {
            Ok(Predicate::from_raw_lt(value))
        } else if let Some(value) = &self.lte {
            Ok(Predicate::from_raw_lte(value))
        } else if self.exists {
            Ok(Predicate::Exists)
        } else {
            Ok(Predicate::NotExists)
        }
    }
}

#[derive(Debug, Clone, Args)]
pub struct AssertArgs {
    pub path: String,

    #[command(flatten)]
    pub predicate: QueryPredicateArgs,

    /// Document to query. Omitted (or `state`) queries the plain state snapshot;
    /// `actions` queries the resolved `spirectl.state-actions/v0` envelope instead
    /// (the same document `sts2 state actions` returns).
    #[arg(long, value_enum)]
    pub source: Option<StateViewArg>,

    #[arg(long, value_enum)]
    pub perspective: Option<PerspectiveScopeArg>,

    #[arg(long)]
    pub player_id: Option<String>,
}

#[derive(Debug, Clone, Args)]
pub struct WaitForArgs {
    pub path: String,

    #[command(flatten)]
    pub predicate: QueryPredicateArgs,

    /// Document to query. Omitted (or `state`) queries the plain state snapshot;
    /// `actions` queries the resolved `spirectl.state-actions/v0` envelope instead
    /// (the same document `sts2 state actions` returns).
    #[arg(long, value_enum)]
    pub source: Option<StateViewArg>,

    #[arg(long, default_value_t = 5_000)]
    pub timeout_ms: u64,

    #[arg(long, default_value_t = 100)]
    pub interval_ms: u64,

    #[arg(long = "rpc-timeout-ms", default_value_t = DEFAULT_BRIDGE_RPC_TIMEOUT_MS)]
    pub rpc_timeout_ms: u64,

    #[arg(long, value_enum)]
    pub perspective: Option<PerspectiveScopeArg>,

    #[arg(long)]
    pub player_id: Option<String>,
}

#[derive(Debug, Clone, Args)]
pub struct WaitForTransitionsArgs {
    #[arg(long, default_value_t = 5_000)]
    pub timeout_ms: u64,

    #[arg(long, default_value_t = 50)]
    pub interval_ms: u64,

    #[arg(long, default_value_t = 3)]
    pub stable_samples: u32,

    #[arg(long = "rpc-timeout-ms", default_value_t = 1_000)]
    pub rpc_timeout_ms: u64,
}

#[derive(Debug, Clone, Args, Default)]
pub struct ProbeQueryPredicateArgs {
    #[arg(long)]
    pub query: Option<String>,

    #[arg(long)]
    pub equals: Option<String>,

    #[arg(long)]
    pub contains: Option<String>,

    #[arg(long)]
    pub regex: Option<String>,

    #[arg(long)]
    pub gt: Option<String>,

    #[arg(long)]
    pub gte: Option<String>,

    #[arg(long)]
    pub lt: Option<String>,

    #[arg(long)]
    pub lte: Option<String>,

    #[arg(long)]
    pub exists: bool,

    #[arg(long = "not-exists")]
    pub not_exists: bool,
}

impl ProbeQueryPredicateArgs {
    pub(crate) fn to_probe_query(&self) -> Result<Option<ProbeQuery>, String> {
        let mut predicates = Vec::new();
        if let Some(value) = &self.equals {
            predicates.push(Predicate::from_raw_equals(value));
        }
        if let Some(value) = &self.contains {
            predicates.push(Predicate::from_raw_contains(value));
        }
        if let Some(value) = &self.regex {
            predicates.push(Predicate::from_raw_regex(value)?);
        }
        if let Some(value) = &self.gt {
            predicates.push(Predicate::from_raw_gt(value));
        }
        if let Some(value) = &self.gte {
            predicates.push(Predicate::from_raw_gte(value));
        }
        if let Some(value) = &self.lt {
            predicates.push(Predicate::from_raw_lt(value));
        }
        if let Some(value) = &self.lte {
            predicates.push(Predicate::from_raw_lte(value));
        }
        if self.exists {
            predicates.push(Predicate::Exists);
        }
        if self.not_exists {
            predicates.push(Predicate::NotExists);
        }

        if predicates.is_empty() {
            if self.query.is_some() {
                return Err("Probe queries require exactly one predicate option.".to_string());
            }
            return Ok(None);
        }
        if predicates.len() != 1 {
            return Err("Probe queries require exactly one predicate option.".to_string());
        }
        let path = self
            .query
            .clone()
            .ok_or_else(|| "Probe predicates require --query <path>.".to_string())?;
        Ok(Some(ProbeQuery {
            path,
            predicate: predicates.remove(0),
        }))
    }
}

#[derive(Debug, Clone, Args)]
pub struct DevHttpArgs {
    #[arg(long)]
    pub url: String,

    #[arg(long, default_value = "GET")]
    pub method: String,

    #[arg(long = "header")]
    pub headers: Vec<String>,

    #[arg(long)]
    pub body: Option<String>,

    #[arg(long, default_value_t = 5_000)]
    pub timeout_ms: u64,

    #[arg(long)]
    pub expect_status: Option<u16>,

    #[arg(long = "expect-header")]
    pub expect_headers: Vec<String>,

    #[command(flatten)]
    pub query: ProbeQueryPredicateArgs,
}

#[derive(Debug, Clone, Args)]
pub struct DevHttpWaitArgs {
    #[command(flatten)]
    pub http: DevHttpArgs,

    #[arg(long, default_value_t = 100)]
    pub interval_ms: u64,
}

#[derive(Debug, Clone, Args)]
pub struct DevFetchArgs {
    #[arg(long)]
    pub source: PathBuf,

    #[arg(long)]
    pub output: Option<PathBuf>,

    #[arg(long)]
    pub expect_sha256: Option<String>,

    #[command(flatten)]
    pub query: ProbeQueryPredicateArgs,
}

#[derive(Debug, Clone, Args)]
pub struct DevWebsocketArgs {
    #[arg(long)]
    pub url: String,

    #[arg(long = "header")]
    pub headers: Vec<String>,

    #[arg(long = "send-text")]
    pub send_text: Vec<String>,

    #[arg(long = "expect-text")]
    pub expect_text: Vec<String>,

    #[arg(long, default_value_t = 5_000)]
    pub timeout_ms: u64,
}

#[derive(Debug, Clone, Args)]
#[command(group(
    ArgGroup::new("path_input")
        .required(true)
        .multiple(false)
        .args(["path", "path_flag"])
))]
pub struct LoadFixtureArgs {
    #[arg(value_name = "PATH")]
    pub path: Option<PathBuf>,

    #[arg(long = "path", value_name = "PATH")]
    pub path_flag: Option<PathBuf>,
}

impl LoadFixtureArgs {
    pub fn path(&self) -> &Path {
        self.path
            .as_deref()
            .or(self.path_flag.as_deref())
            .expect("clap requires exactly one fixture path")
    }

    pub fn from_path(path: PathBuf) -> Self {
        Self {
            path: Some(path),
            path_flag: None,
        }
    }
}

#[derive(Debug, Clone, Args)]
pub struct ScreenshotArgs {
    #[arg(long)]
    pub preset: Option<String>,

    #[arg(long)]
    pub width: Option<u32>,

    #[arg(long)]
    pub height: Option<u32>,

    #[arg(long = "preset-catalog")]
    pub preset_catalogs: Vec<PathBuf>,

    #[arg(long)]
    pub output: Option<PathBuf>,

    #[arg(long = "rpc-timeout-ms", default_value_t = DEFAULT_BRIDGE_RPC_TIMEOUT_MS)]
    pub rpc_timeout_ms: u64,
}

#[derive(Debug, Clone, Args)]
pub struct ScreenshotDiffArgs {
    #[arg(long)]
    pub baseline: PathBuf,

    #[arg(long)]
    pub actual: Option<PathBuf>,

    #[arg(long)]
    pub preset: Option<String>,

    #[arg(long)]
    pub width: Option<u32>,

    #[arg(long)]
    pub height: Option<u32>,

    #[arg(long = "preset-catalog")]
    pub preset_catalogs: Vec<PathBuf>,

    #[arg(long)]
    pub bundle_dir: Option<PathBuf>,

    #[arg(long)]
    pub max_diff_pixels: Option<u64>,

    #[arg(long)]
    pub max_diff_ratio: Option<f64>,

    #[arg(
        long = "pixel-tolerance",
        value_parser = clap::value_parser!(u8),
        help = "Per-channel delta (0-255) that still counts as an identical pixel; absorbs anti-aliasing jitter without loosening --max-diff-pixels/--max-diff-ratio. Default 0 = exact RGBA"
    )]
    pub pixel_tolerance: Option<u8>,

    #[arg(
        long = "ignore-alpha",
        help = "Compare RGB only, ignoring the alpha channel"
    )]
    pub ignore_alpha: bool,

    #[arg(long)]
    pub mask: Option<PathBuf>,

    #[arg(long)]
    pub regions: Option<PathBuf>,

    #[arg(long)]
    pub foreground_max_diff_ratio: Option<f64>,

    #[arg(long)]
    pub roi_max_diff_ratio: Option<f64>,

    #[arg(long = "required-comparison")]
    pub required_comparisons: Vec<String>,

    #[arg(long = "rpc-timeout-ms", default_value_t = DEFAULT_BRIDGE_RPC_TIMEOUT_MS)]
    pub rpc_timeout_ms: u64,
}

#[derive(Debug, Clone, Args)]
pub struct DevSceneCommand {
    #[command(subcommand)]
    pub command: DevSceneSubcommand,
}

#[derive(Debug, Clone, Subcommand)]
pub enum DevSceneSubcommand {
    Tree(DevSceneTreeArgs),
    Node(DevSceneNodeArgs),
    Children(DevSceneChildrenArgs),
    Hover(DevSceneHoverArgs),
    Unhover(DevSceneUnhoverArgs),
    #[command(name = "set-visible")]
    SetVisible(DevSceneSetVisibleArgs),
}

#[derive(Debug, Clone, Args)]
pub struct DevSceneTreeArgs {
    pub node_path: Option<String>,

    #[arg(long)]
    pub properties: bool,

    #[arg(long = "computed-transform")]
    pub computed_transform: bool,

    #[arg(long = "rpc-timeout-ms", default_value_t = DEFAULT_BRIDGE_RPC_TIMEOUT_MS)]
    pub rpc_timeout_ms: u64,
}

#[derive(Debug, Clone, Args)]
pub struct DevSceneNodeArgs {
    pub node_path: String,

    #[arg(long)]
    pub properties: bool,

    #[arg(long = "computed-transform")]
    pub computed_transform: bool,

    #[arg(long = "measure-text-ink")]
    pub measure_text_ink: bool,

    #[arg(long = "rpc-timeout-ms", default_value_t = DEFAULT_BRIDGE_RPC_TIMEOUT_MS)]
    pub rpc_timeout_ms: u64,
}

#[derive(Debug, Clone, Args)]
pub struct DevSceneChildrenArgs {
    pub node_path: String,

    #[arg(long)]
    pub properties: bool,

    #[arg(long = "rpc-timeout-ms", default_value_t = DEFAULT_BRIDGE_RPC_TIMEOUT_MS)]
    pub rpc_timeout_ms: u64,
}

#[derive(Debug, Clone, Args)]
#[command(group(ArgGroup::new("target").required(true).args(["node_path", "element_id"])))]
pub struct DevSceneHoverArgs {
    #[arg(long = "path")]
    pub node_path: Option<String>,

    #[arg(long = "element-id")]
    pub element_id: Option<String>,

    #[arg(long = "hover-tip")]
    pub hover_tip: bool,

    #[arg(long = "settle-ms", default_value_t = 0)]
    pub settle_ms: u32,

    #[arg(long = "rpc-timeout-ms", default_value_t = DEFAULT_BRIDGE_RPC_TIMEOUT_MS)]
    pub rpc_timeout_ms: u64,
}

// Reverts a hover. The target is optional: with no --path/--element-id this is a global clear
// (move the pointer away so the game drops any active hover/tooltip); with a target it also invokes
// that control's unfocus hook.
#[derive(Debug, Clone, Args)]
pub struct DevSceneUnhoverArgs {
    #[arg(long = "path")]
    pub node_path: Option<String>,

    #[arg(long = "element-id")]
    pub element_id: Option<String>,

    #[arg(long = "rpc-timeout-ms", default_value_t = DEFAULT_BRIDGE_RPC_TIMEOUT_MS)]
    pub rpc_timeout_ms: u64,
}

#[derive(Debug, Clone, Args)]
#[command(group(ArgGroup::new("visibility").required(true).args(["visible", "hidden"])))]
pub struct DevSceneSetVisibleArgs {
    pub node_path: String,

    #[arg(long)]
    pub visible: bool,

    #[arg(long)]
    pub hidden: bool,

    #[arg(long = "computed-transform")]
    pub computed_transform: bool,

    #[arg(long = "verify-after-ms")]
    pub verify_after_ms: Option<u64>,
}

#[derive(Debug, Clone, Subcommand)]
pub enum GameSubcommand {
    Detect,
    Info,
    #[command(name = "bridge-health")]
    BridgeHealth(BridgeHealthArgs),
    #[command(name = "install-bridge")]
    InstallBridge(GameInstallBridgeArgs),
    Launch(GameLaunchArgs),
    Attach(LifecycleWaitArgs),
    Close(LifecycleWaitArgs),
    Kill(LifecycleWaitArgs),
    Deploy(DeployArgs),
    Mods(GameModsCommand),
    Instances(GameInstancesCommand),
}

#[derive(Debug, Clone, Args)]
pub struct GameInstallBridgeArgs {
    /// Install from a verified cached/release bridge archive instead of rebuilding.
    /// A source checkout may also stage its existing host publish output; this
    /// option never invokes .NET.
    #[arg(long = "no-build")]
    pub no_build: bool,

    /// Copy into the mods folder even when the staged bridge is byte-identical
    /// to the installed one.
    #[arg(long)]
    pub force: bool,

    /// Restart the game after installing.
    #[arg(long)]
    pub restart: bool,

    #[arg(long = "timeout-ms", default_value_t = 30_000)]
    pub timeout_ms: u64,

    #[arg(long = "interval-ms", default_value_t = 250)]
    pub interval_ms: u64,

    #[arg(long = "rpc-timeout-ms", default_value_t = 10_000)]
    pub rpc_timeout_ms: u64,

    /// Wait for the game's runtime transitions to settle (0 = off).
    #[arg(long = "wait-quiescent-ms", default_value_t = 0)]
    pub wait_quiescent_ms: u64,

    /// Consecutive quiescent samples required by --wait-quiescent-ms.
    #[arg(long = "quiescent-stable-samples", default_value_t = 3)]
    pub quiescent_stable_samples: u32,

    /// Fail (exit 4, `quiescence_timeout`) instead of reporting a non-fatal
    /// notice when the --wait-quiescent-ms budget runs out.
    #[arg(long = "require-quiescent")]
    pub require_quiescent: bool,
}

impl Default for GameInstallBridgeArgs {
    fn default() -> Self {
        Self {
            no_build: false,
            force: false,
            restart: false,
            timeout_ms: 30_000,
            interval_ms: 250,
            rpc_timeout_ms: 10_000,
            wait_quiescent_ms: 0,
            quiescent_stable_samples: 3,
            require_quiescent: false,
        }
    }
}

#[derive(Debug, Clone, Args)]
pub struct GameCommand {
    #[command(subcommand)]
    pub command: GameSubcommand,
}

#[derive(Debug, Clone, Args)]
pub struct GameInstancesCommand {
    #[command(subcommand)]
    pub command: Option<GameInstancesSubcommand>,
}

#[derive(Debug, Clone, Subcommand)]
pub enum GameInstancesSubcommand {
    /// List known game instances with liveness and socket status (default).
    List,
    /// Remove registry entries for instances that are no longer running.
    Prune,
}

#[derive(Debug, Clone, Args)]
pub struct GameModsCommand {
    #[command(subcommand)]
    pub command: GameModsSubcommand,
}

#[derive(Debug, Clone, Subcommand)]
pub enum GameModsSubcommand {
    Settings(GameModsSettingsArgs),
    Active,
}

#[derive(Debug, Clone, Args)]
pub struct GameModsSettingsArgs {
    #[arg(long = "settings-file")]
    pub settings_file: Option<PathBuf>,
}

#[derive(Debug, Clone, Args)]
pub struct BridgeHealthArgs {
    #[arg(
        long = "non-mutating",
        help = "Compatibility no-op; bridge-health is non-mutating unless --repair-stale-endpoint is used"
    )]
    pub non_mutating: bool,

    #[arg(
        long = "repair-stale-endpoint",
        help = "Remove a safe stale local endpoint before reporting health"
    )]
    pub repair_stale_endpoint: bool,

    #[arg(long = "verbose", help = "Emit the full diagnostic payload")]
    pub verbose: bool,

    #[arg(
        long = "check-source",
        help = "Walk every bridge/proto source file for the freshness timestamp instead of statting sentinels (implied by --verbose)"
    )]
    pub check_source: bool,

    #[arg(long = "rpc-timeout-ms", default_value_t = 1_000)]
    pub rpc_timeout_ms: u64,
}

#[derive(Debug, Clone, Args)]
pub struct LifecycleWaitArgs {
    #[arg(long, default_value_t = 30_000)]
    pub timeout_ms: u64,

    #[arg(long, default_value_t = 250)]
    pub interval_ms: u64,

    #[arg(long = "rpc-timeout-ms", default_value_t = 10_000)]
    pub rpc_timeout_ms: u64,
}

#[derive(Debug, Clone, Args)]
pub struct GameLaunchArgs {
    #[arg(long, default_value_t = 30_000)]
    pub timeout_ms: u64,

    #[arg(long, default_value_t = 250)]
    pub interval_ms: u64,

    #[arg(long = "rpc-timeout-ms", default_value_t = 10_000)]
    pub rpc_timeout_ms: u64,

    #[arg(long = "verify-stable-ms", default_value_t = 0)]
    pub verify_stable_ms: u64,

    /// Keep the launched game in the CLI process session instead of detaching it.
    #[arg(long = "no-detach-session")]
    pub no_detach_session: bool,

    /// Keep this instance responsive while its window is backgrounded: the bridge
    /// mod skips the game's background FPS limit and pins vertical sync on, which
    /// stops the engine parking its main loop in the swapchain acquire while the
    /// surface is hidden, so RPCs, state streaming, and queued actions keep flowing.
    /// Also settable once via `game.disableBackgroundThrottle`.
    #[arg(long = "disable-background-throttle")]
    pub disable_background_throttle: bool,

    #[arg(long)]
    pub verbose: bool,

    /// Wait for the game's runtime transitions to settle after attach (0 = off).
    /// A reachable bridge is not a settled screen: boot flows keep pushing
    /// overlays for seconds afterwards.
    #[arg(long = "wait-quiescent-ms", default_value_t = 0)]
    pub wait_quiescent_ms: u64,

    /// Consecutive quiescent samples required by --wait-quiescent-ms.
    #[arg(long = "quiescent-stable-samples", default_value_t = 3)]
    pub quiescent_stable_samples: u32,

    /// Fail (exit 4, `quiescence_timeout`) instead of reporting a non-fatal
    /// notice when the --wait-quiescent-ms budget runs out.
    #[arg(long = "require-quiescent")]
    pub require_quiescent: bool,

    #[arg(last = true)]
    pub launch_args: Vec<String>,
}

#[derive(Debug, Clone, Args)]
pub struct DeployArgs {
    pub path: PathBuf,
    #[arg(long)]
    pub build: bool,
    #[arg(long)]
    pub restart: bool,
    #[arg(long)]
    pub verify: bool,
    #[arg(long, default_value_t = 30_000)]
    pub timeout_ms: u64,
    #[arg(long, default_value_t = 250)]
    pub interval_ms: u64,
    #[arg(long = "rpc-timeout-ms", default_value_t = 5_000)]
    pub rpc_timeout_ms: u64,
    #[arg(long = "verify-stable-ms", default_value_t = 0)]
    pub verify_stable_ms: u64,

    /// Accept a deploy output directory the build did not refresh instead of
    /// failing with `deploy_build_output_stale`.
    #[arg(long = "allow-stale-build")]
    pub allow_stale_build: bool,

    /// Wait for the game's runtime transitions to settle after attach (0 = off).
    /// A reachable bridge is not a settled screen: boot flows keep pushing
    /// overlays for seconds afterwards.
    #[arg(long = "wait-quiescent-ms", default_value_t = 0)]
    pub wait_quiescent_ms: u64,

    /// Consecutive quiescent samples required by --wait-quiescent-ms.
    #[arg(long = "quiescent-stable-samples", default_value_t = 3)]
    pub quiescent_stable_samples: u32,

    /// Fail (exit 4, `quiescence_timeout`) instead of reporting a non-fatal
    /// notice when the --wait-quiescent-ms budget runs out.
    #[arg(long = "require-quiescent")]
    pub require_quiescent: bool,

    /// Arguments passed straight through to the game executable on --restart.
    #[arg(last = true)]
    pub launch_args: Vec<String>,
}

#[derive(Debug, Clone, Subcommand)]
pub enum CodeSubcommand {
    Locate(CodeLocateArgs),
    Describe(CodeResolveArgs),
    Decompile(CodeDecompileArgs),
    Refs(CodeLocateArgs),
    Derived(CodeDerivedArgs),
    Hooks(CodeHooksArgs),
    #[command(name = "hook-info")]
    HookInfo(CodeHookInfoArgs),
    #[command(name = "verify-references")]
    VerifyReferences(CodeVerifyReferencesArgs),
    #[command(name = "scene-search")]
    SceneSearch(CodeSceneSearchArgs),
    #[command(name = "scene-tree")]
    SceneTree(CodeSceneResolveArgs),
    #[command(name = "scene-node")]
    SceneNode(CodeSceneNodeArgs),
}

#[derive(Debug, Clone, Args)]
pub struct CodeCommand {
    #[command(subcommand)]
    pub command: CodeSubcommand,
}

#[derive(Debug, Clone, Copy, ValueEnum, PartialEq, Eq)]
pub enum CodeLocateSubject {
    Type,
    Method,
    Symbol,
}

impl Display for CodeLocateSubject {
    fn fmt(&self, f: &mut Formatter<'_>) -> std::fmt::Result {
        let value = match self {
            Self::Type => "type",
            Self::Method => "method",
            Self::Symbol => "symbol",
        };
        f.write_str(value)
    }
}

#[derive(Debug, Clone, Copy, ValueEnum, PartialEq, Eq)]
pub enum CodeResolveSubject {
    Type,
    Method,
}

impl Display for CodeResolveSubject {
    fn fmt(&self, f: &mut Formatter<'_>) -> std::fmt::Result {
        let value = match self {
            Self::Type => "type",
            Self::Method => "method",
        };
        f.write_str(value)
    }
}

#[derive(Debug, Clone, Args, Default)]
pub struct CodeSearchRootArgs {
    #[arg(long, help = "Override the detected Slay the Spire 2 install root.")]
    pub game_path: Option<PathBuf>,

    #[arg(
        long,
        help = "Override the managed assemblies directory used for symbol-aware lookups."
    )]
    pub assemblies_dir: Option<PathBuf>,

    #[arg(
        long,
        help = "Override the root directory used for static scene/resource search."
    )]
    pub resources_dir: Option<PathBuf>,

    #[arg(long, help = "Override the mod root used when --include-mods is set.")]
    pub mods_dir: Option<PathBuf>,

    #[arg(
        long,
        help = "Include mod-root assets in addition to the base resources root."
    )]
    pub include_mods: bool,

    #[arg(
        long,
        help = "Include managed dependency DLLs in static inspection; dependencies are excluded by default."
    )]
    pub include_dependencies: bool,
}

#[derive(Debug, Clone, Args)]
pub struct CodeLocateArgs {
    #[arg(value_enum)]
    pub subject: CodeLocateSubject,

    pub query: String,

    #[arg(long, default_value_t = 20)]
    pub limit: u32,

    #[command(flatten)]
    pub search_roots: CodeSearchRootArgs,
}

#[derive(Debug, Clone, Args)]
pub struct CodeResolveArgs {
    #[arg(value_enum)]
    pub subject: CodeResolveSubject,

    pub query: String,

    #[command(flatten)]
    pub search_roots: CodeSearchRootArgs,
}

#[derive(Debug, Clone, Args)]
pub struct CodeDecompileArgs {
    #[arg(value_enum)]
    pub subject: CodeResolveSubject,

    pub query: String,

    #[arg(
        long,
        help = "Use the embedded ILSpy backend instead of the default metadata summary renderer."
    )]
    pub full: bool,

    #[command(flatten)]
    pub search_roots: CodeSearchRootArgs,
}

#[derive(Debug, Clone, Copy, ValueEnum, PartialEq, Eq)]
pub enum CodeDerivedSubject {
    Type,
}

impl Display for CodeDerivedSubject {
    fn fmt(&self, f: &mut Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Type => f.write_str("type"),
        }
    }
}

#[derive(Debug, Clone, Args)]
pub struct CodeDerivedArgs {
    #[arg(value_enum)]
    pub subject: CodeDerivedSubject,

    pub query: String,

    #[arg(long, default_value_t = 20)]
    pub limit: u32,

    #[command(flatten)]
    pub search_roots: CodeSearchRootArgs,
}

#[derive(Debug, Clone, Copy, ValueEnum, PartialEq, Eq)]
pub enum CodeHookSource {
    Game,
    Mod,
}

impl Display for CodeHookSource {
    fn fmt(&self, f: &mut Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Game => f.write_str("game"),
            Self::Mod => f.write_str("mod"),
        }
    }
}

#[derive(Debug, Clone, Copy, ValueEnum, PartialEq, Eq)]
pub enum CodeHookForm {
    #[value(name = "managed-prefix")]
    ManagedPrefix,
    #[value(name = "managed-postfix")]
    ManagedPostfix,
    #[value(name = "managed-override")]
    ManagedOverride,
    #[value(name = "managed-interface-contract")]
    ManagedInterfaceContract,
}

impl Display for CodeHookForm {
    fn fmt(&self, f: &mut Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::ManagedPrefix => f.write_str("managed-prefix"),
            Self::ManagedPostfix => f.write_str("managed-postfix"),
            Self::ManagedOverride => f.write_str("managed-override"),
            Self::ManagedInterfaceContract => f.write_str("managed-interface-contract"),
        }
    }
}

#[derive(Debug, Clone, Copy, ValueEnum, PartialEq, Eq)]
pub enum CodeHookSort {
    Relevance,
    Name,
    #[value(name = "reference-count")]
    ReferenceCount,
    Assembly,
}

impl Display for CodeHookSort {
    fn fmt(&self, f: &mut Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Relevance => f.write_str("relevance"),
            Self::Name => f.write_str("name"),
            Self::ReferenceCount => f.write_str("reference-count"),
            Self::Assembly => f.write_str("assembly"),
        }
    }
}

#[derive(Debug, Clone, Args)]
pub struct CodeHooksArgs {
    pub query: Option<String>,

    #[arg(long, default_value_t = 100)]
    pub limit: usize,

    #[arg(long, default_value_t = 0)]
    pub offset: usize,

    #[arg(long, value_enum)]
    pub source: Option<CodeHookSource>,

    #[arg(long)]
    pub assembly: Option<String>,

    #[arg(long = "form", value_enum)]
    pub forms: Vec<CodeHookForm>,

    #[arg(long = "has-script")]
    pub has_script: bool,

    #[arg(long, value_enum, default_value_t = CodeHookSort::Relevance)]
    pub sort: CodeHookSort,

    #[command(flatten)]
    pub search_roots: CodeSearchRootArgs,
}

#[derive(Debug, Clone, Args)]
pub struct CodeHookInfoArgs {
    pub query: String,

    #[command(flatten)]
    pub search_roots: CodeSearchRootArgs,
}

#[derive(Debug, Clone, Args)]
pub struct CodeVerifyReferencesArgs {
    #[arg(
        help = "Built consumer assemblies to check, comma-separated. These are read as shipped binaries; no source or matching reference package is needed."
    )]
    pub assemblies: String,

    #[arg(
        long = "control-assemblies-dir",
        help = "A game build the consumers are known to bind against. Required to report reshaped signatures, and the run fails if the control itself leaves a binding unresolved."
    )]
    pub control_assemblies_dir: Option<PathBuf>,

    #[arg(long, help = "Override the detected Slay the Spire 2 install root.")]
    pub game_path: Option<PathBuf>,

    #[arg(
        long,
        help = "The candidate managed assemblies directory to resolve the consumers' game bindings against."
    )]
    pub assemblies_dir: Option<PathBuf>,
}

impl CodeVerifyReferencesArgs {
    /// Reference verification reads game assemblies straight out of one directory, so it takes only
    /// the two roots that resolution actually uses rather than the whole shared search-root set.
    pub(crate) fn search_roots(&self) -> CodeSearchRootArgs {
        CodeSearchRootArgs {
            game_path: self.game_path.clone(),
            assemblies_dir: self.assemblies_dir.clone(),
            ..CodeSearchRootArgs::default()
        }
    }
}

#[derive(Debug, Clone, Args)]
pub struct CodeSceneSearchArgs {
    pub query: String,

    #[arg(long, default_value_t = 20)]
    pub limit: u32,

    #[command(flatten)]
    pub search_roots: CodeSearchRootArgs,
}

#[derive(Debug, Clone, Args)]
pub struct CodeSceneResolveArgs {
    pub scene: String,

    #[command(flatten)]
    pub search_roots: CodeSearchRootArgs,
}

#[derive(Debug, Clone, Args)]
pub struct CodeSceneNodeArgs {
    pub scene: String,

    pub node_path: String,

    #[command(flatten)]
    pub search_roots: CodeSearchRootArgs,
}

#[derive(Debug, Clone, Subcommand)]
pub enum InspectSubcommand {
    Commands,
    Examples(InspectExamplesArgs),
    Actions(InspectActionsArgs),
    #[command(name = "state-schema")]
    StateSchema,
    #[command(name = "reference-topics")]
    ReferenceTopics,
    #[command(name = "viewport-presets")]
    ViewportPresets(InspectViewportPresetsArgs),
    #[command(name = "ai-tools")]
    AiTools,
}

#[derive(Debug, Clone, Args)]
pub struct InspectCommand {
    #[command(subcommand)]
    pub command: InspectSubcommand,
}

#[derive(Debug, Clone, Args)]
pub struct InspectExamplesArgs {
    #[arg(long)]
    pub command: Option<String>,
}

#[derive(Debug, Clone, Args, Default)]
pub struct InspectActionsArgs {
    /// Render static action metadata without connecting to the live bridge.
    #[arg(long)]
    pub offline: bool,
}

#[derive(Debug, Clone, Args, Default)]
pub struct InspectViewportPresetsArgs {
    #[arg(long = "preset-catalog")]
    pub preset_catalogs: Vec<PathBuf>,
}

#[derive(Debug, Clone, Subcommand)]
pub enum TestSubcommand {
    Run(TestRunArgs),
    Stress(TestStressArgs),
}

#[derive(Debug, Clone, Args)]
pub struct TestCommand {
    #[command(subcommand)]
    pub command: TestSubcommand,
}

#[derive(Debug, Clone, Args)]
#[command(group(
    ArgGroup::new("input")
        .required(false)
        .multiple(false)
        .args(["path", "inline"])
))]
pub struct TestRunArgs {
    #[arg(long)]
    pub profile: Option<String>,

    #[arg(long = "tag")]
    pub tags: Vec<String>,

    #[arg(long)]
    pub artifacts_dir: Option<PathBuf>,

    #[arg(long, value_enum, default_value_t = FailureArtifactsMode::OnFailure)]
    pub failure_artifacts: FailureArtifactsMode,

    #[arg(long, value_name = "YAML")]
    pub inline: Option<String>,

    pub path: Option<PathBuf>,
}

#[derive(Debug, Clone, Args)]
#[command(group(
    ArgGroup::new("limit")
        .required(true)
        .multiple(false)
        .args(["iterations", "duration_ms"])
))]
pub struct TestStressArgs {
    #[arg(long)]
    pub artifacts_dir: Option<PathBuf>,

    #[arg(long, value_enum, default_value_t = FailureArtifactsMode::OnFailure)]
    pub failure_artifacts: FailureArtifactsMode,

    #[arg(long)]
    pub iterations: Option<u32>,

    #[arg(long)]
    pub duration_ms: Option<u64>,

    #[arg(long, default_value_t = 0)]
    pub max_failures: u32,

    #[arg(long, default_value_t = 0)]
    pub cooldown_ms: u64,

    pub path: PathBuf,
}
