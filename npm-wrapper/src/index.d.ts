export type Sts2Mode = "normal" | "dangerous";
export type PerspectiveScope = "local" | "host" | "remote" | "omniscient" | "dev" | string;
export type RemoteOrchestrationCapability =
  | "unavailable"
  | "local-only-degraded"
  | "host-local-seat"
  | "host-mediated"
  | "configured-client"
  | "unsupported"
  | string;
export type LogLevel = "trace" | "debug" | "info" | "warn" | "error";
export type FailureArtifactsMode = "on-failure" | "never";
export type CodeLocateSubject = "type" | "method" | "symbol";
export type CodeResolveSubject = "type" | "method";
export type CodeDerivedSubject = "type";
export type Sts2McpTransport = "stdio" | "http";

export interface Sts2McpServerOptions {
  transport?: Sts2McpTransport;
  listen?: string;
  authToken?: string;
  acknowledgeNetworkRisk?: boolean;
}

export interface Sts2McpHttpServerMetadata {
  service: "sts2-mcp";
  version: string;
  transport: "http";
  sourceOfTruth: string;
  baseUrl: string;
  mcpUrl: string;
  listenAddress: string;
  auth: {
    mode: "bearer" | "none";
    required: boolean;
  };
}

export interface CreateSts2ClientOptions {
  binaryPath?: string;
  cwd?: string;
  configPath?: string;
  mode?: Sts2Mode;
  serviceUrl?: string;
  serviceToken?: string;
  mcp?: Sts2McpServerOptions;
  env?: Record<string, string | undefined>;
}

export interface RuntimeScreen {
  id: string;
  type?: string;
  screenInstanceId?: string;
  title?: string;
  source?: string | null;
  rawType?: string | null;
  className?: string | null;
  [key: string]: unknown;
}

export interface RuntimeChoice {
  id: string;
  kind: string;
  label?: string;
  description?: string | null;
  provisional?: boolean;
  playerId?: string | null;
  ownerPlayerId?: string | null;
  ownerRole?: string | null;
  choiceKind?: string | null;
  intentKind?: string | null;
  perspective?: string | null;
  preferredAction?: string | null;
  preferredActionRef?: RuntimeVisibleActionReference | null;
  arguments?: RuntimeActionArguments;
  enabled?: boolean;
  disabledReason?: string | null;
  legalityStatus?: RuntimeActionLegalityStatus;
  checkedHookPaths?: string[];
  [key: string]: unknown;
}

export type RuntimeActionLegalityStatus = "unknown" | "legal" | "illegal" | string;

export interface RuntimeActionArguments {
  playerId?: string | null;
  ownerPlayerId?: string | null;
  requestedPlayerId?: string | null;
  resolvedOwnerPlayerId?: string | null;
  rewardId?: string | null;
  cardId?: string | null;
  bundleId?: string | null;
  shopItemId?: string | null;
  relicId?: string | null;
  restOptionId?: string | null;
  eventOptionId?: string | null;
  controlId?: string | null;
  potionId?: string | null;
  targetId?: string | null;
  choiceId?: string | null;
  mapNodeId?: string | null;
  characterId?: string | null;
  [key: string]: unknown;
}

export type AvailableActionArguments = RuntimeActionArguments;

export interface AvailableAction {
  id: string;
  kind: string;
  summary?: string;
  provisional?: boolean;
  cliCommandHint?: string;
  arguments?: AvailableActionArguments;
  intentKind?: string | null;
  playerId?: string | null;
  ownerPlayerId?: string | null;
  ownerRole?: string | null;
  perspective?: string | null;
  remoteOrchestrationCapability?: RemoteOrchestrationCapability | null;
  capability?: RemoteOrchestrationCapability | string | null;
  preferredAction?: string | null;
  preferredActionRef?: RuntimeVisibleActionReference | null;
  legalityStatus?: RuntimeActionLegalityStatus;
  disabledReason?: string | null;
  checkedHookPaths?: string[];
  [key: string]: unknown;
}

export interface StateNotice {
  code?: string;
  message?: string;
  provisional?: boolean;
  path?: string | null;
  severity?: string | null;
  source?: string | null;
  stability?: string | null;
  perspective?: string | null;
  [key: string]: unknown;
}

export interface RuntimeVisibleActionReference {
  id: string;
  label?: string;
  description?: string | null;
  enabled?: boolean;
  playerId?: string | null;
  ownerPlayerId?: string | null;
  ownerRole?: string | null;
  kind?: string | null;
  intentKind?: string | null;
  arguments?: RuntimeActionArguments;
  legalityStatus?: RuntimeActionLegalityStatus;
  disabledReason?: string | null;
  perspective?: string | null;
  remoteOrchestrationCapability?: RemoteOrchestrationCapability | null;
  capability?: RemoteOrchestrationCapability | string | null;
  checkedHookPaths?: string[];
  provisional?: boolean;
  [key: string]: unknown;
}

export interface RuntimeVisibleControl {
  id: string;
  label?: string;
  description?: string | null;
  enabled?: boolean;
  playerId?: string | null;
  ownerPlayerId?: string | null;
  ownerRole?: string | null;
  intentKind?: string | null;
  perspective?: string | null;
  remoteOrchestrationCapability?: RemoteOrchestrationCapability | null;
  capability?: RemoteOrchestrationCapability | string | null;
  arguments?: RuntimeActionArguments;
  disabledReason?: string | null;
  preferredAction?: RuntimeVisibleActionReference | string | null;
  selected?: boolean;
  provisional?: boolean;
  [key: string]: unknown;
}

export interface RuntimeVisibleItem {
  id: string;
  label?: string;
  description?: string | null;
  playerId?: string | null;
  ownerPlayerId?: string | null;
  ownerRole?: string | null;
  isLocal?: boolean;
  isHost?: boolean;
  isRemote?: boolean;
  enabled?: boolean;
  selected?: boolean;
  provisional?: boolean;
  displayOrder?: number;
  position?: Record<string, unknown> | null;
  cost?: Record<string, unknown> | null;
  reward?: Record<string, unknown> | null;
  assetRefs?: RuntimeAssetReference[];
  intentKind?: string | null;
  perspective?: string | null;
  remoteOrchestrationCapability?: RemoteOrchestrationCapability | null;
  capability?: RemoteOrchestrationCapability | string | null;
  arguments?: RuntimeActionArguments;
  enabled?: boolean;
  disabledReason?: string | null;
  preferredAction?: RuntimeVisibleActionReference | string | null;
  [key: string]: unknown;
}

export interface RuntimeMapState {
  nodes?: RuntimeVisibleItem[];
  [key: string]: unknown;
}

export interface RuntimeRichLocalizedText {
  text?: string;
  rawText?: string | null;
  locTable?: string | null;
  locKey?: string | null;
  source?: "loc-string" | "mega-rich-text-label" | "mega-label" | "fallback" | string | null;
  provisional?: boolean;
  [key: string]: unknown;
}

export interface RuntimeAncientEventDialogueLineState {
  index?: number;
  text?: RuntimeRichLocalizedText | null;
  speaker?: string | null;
  current?: boolean;
  visible?: boolean;
  [key: string]: unknown;
}

export interface RuntimeAncientEventPageState {
  title?: RuntimeRichLocalizedText | null;
  bannerTitle?: RuntimeRichLocalizedText | null;
  epithet?: RuntimeRichLocalizedText | null;
  currentDialogue?: RuntimeRichLocalizedText | null;
  currentDialogueIndex?: number;
  dialogueLines?: RuntimeAncientEventDialogueLineState[];
  currentSpeaker?: string | null;
  nextButtonText?: RuntimeRichLocalizedText | null;
  textSource?: "ancient-event-model" | "ancient-layout-label" | "partial" | string | null;
  provisional?: boolean;
  [key: string]: unknown;
}

export interface RuntimeEventRoomPageState {
  eventId?: string | null;
  eventType?: string | null;
  title?: RuntimeRichLocalizedText | null;
  description?: RuntimeRichLocalizedText | null;
  sharedLabel?: RuntimeRichLocalizedText | null;
  ancient?: RuntimeAncientEventPageState | null;
  textSource?: "event-model" | "layout-label" | "partial" | "ancient-event-model" | "ancient-layout-label" | string | null;
  provisional?: boolean;
  [key: string]: unknown;
}

export interface RuntimeEventRoomState {
  page?: RuntimeEventRoomPageState | null;
  options?: RuntimeVisibleItem[];
  [key: string]: unknown;
}

export interface RuntimeTreasureRoomState {
  relics?: RuntimeVisibleItem[];
  [key: string]: unknown;
}

export interface RuntimeRelicSelectionState {
  relics?: RuntimeVisibleItem[];
  [key: string]: unknown;
}

export interface RuntimeRestSiteState {
  controls?: RuntimeVisibleControl[];
  [key: string]: unknown;
}

export interface RuntimeShopState {
  purchasableItems?: RuntimeVisibleItem[];
  [key: string]: unknown;
}

export interface RuntimeRewardsState {
  rewards?: RuntimeVisibleItem[];
  [key: string]: unknown;
}

export interface RuntimeCardSelectionState {
  cards?: RuntimeVisibleItem[];
  [key: string]: unknown;
}

export interface RuntimeSimpleCardSelectionState {
  choices?: RuntimeVisibleItem[];
  [key: string]: unknown;
}

export interface RuntimeDeckCardSelectionState {
  deckCards?: RuntimeVisibleItem[];
  [key: string]: unknown;
}

export interface RuntimeBundleSelectionState {
  bundles?: RuntimeVisibleItem[];
  [key: string]: unknown;
}

export interface RuntimeMultiplayerLobbyState {
  players?: RuntimeVisibleItem[];
  actions?: RuntimeVisibleActionReference[];
  hostPlayerId?: string | null;
  localPlayerId?: string | null;
  localRole?: string | null;
  perspective?: string | null;
  remoteOrchestrationCapability?: RemoteOrchestrationCapability | null;
  [key: string]: unknown;
}

export interface RuntimeOverlayBreadcrumb {
  screenType: string;
  title?: string | null;
  screenInstanceId?: string | null;
  source?: string | null;
  rawType?: string | null;
  className?: string | null;
  ownerPlayerId?: string | null;
  perspective?: string | null;
  [key: string]: unknown;
}

export interface RuntimeOverlayAffordance {
  id: string;
  label?: string;
  enabled?: boolean;
  ownerPlayerId?: string | null;
  choiceKind?: string | null;
  intentKind?: string | null;
  preferredAction?: string | null;
  preferredActionRef?: RuntimeVisibleActionReference | null;
  perspective?: string | null;
  arguments?: RuntimeActionArguments;
  disabledReason?: string | null;
  provisional?: boolean;
  [key: string]: unknown;
}

export interface RuntimeAssetReference {
  kind: string;
  key: string;
  label?: string | null;
  provisional?: boolean;
  [key: string]: unknown;
}

export interface RuntimeCard {
  id: string;
  name?: string;
  cost?: number;
  ownerPlayerId?: string | null;
  playable?: boolean;
  unplayableReason?: string | null;
  targetIds?: string[];
  upgraded?: boolean;
  modelId?: string | null;
  description?: string | null;
  type?: string | null;
  rarity?: string | null;
  targetType?: string | null;
  upgradeLevel?: number;
  costLabel?: string | null;
  assetRefs?: RuntimeAssetReference[];
  [key: string]: unknown;
}

export interface RuntimeCardOverlayState {
  cards?: RuntimeCard[];
  previewText?: string | null;
  breadcrumbs?: RuntimeOverlayBreadcrumb[];
  sourceBreadcrumbs?: RuntimeOverlayBreadcrumb[];
  blocking?: boolean;
  passive?: boolean;
  overlayPolicy?: string | null;
  ownerPlayerId?: string | null;
  perspective?: string | null;
  close?: RuntimeOverlayAffordance | null;
  back?: RuntimeOverlayAffordance | null;
  followThroughControls?: RuntimeOverlayAffordance[];
  notices?: StateNotice[];
  provisional?: boolean;
  [key: string]: unknown;
}

export interface RuntimePotion {
  id: string;
  name?: string;
  ownerPlayerId?: string | null;
  slotIndex?: number;
  usable?: boolean;
  unusableReason?: string | null;
  targetIds?: string[];
  modelId?: string | null;
  description?: string | null;
  targetType?: string | null;
  requiresTarget?: boolean;
  assetRefs?: RuntimeAssetReference[];
  [key: string]: unknown;
}

export interface RuntimeCardPile {
  id: string;
  label?: string;
  ownerPlayerId?: string | null;
  count?: number;
  cards?: RuntimeCard[];
  cardsObservable?: boolean;
  orderObservable?: boolean;
  [key: string]: unknown;
}

export interface RuntimeRelic {
  id: string;
  modelId?: string;
  name?: string;
  description?: string | null;
  ownerPlayerId?: string | null;
  slotIndex?: number;
  hasCounter?: boolean;
  counter?: number;
  counterLabel?: string | null;
  assetRefs?: RuntimeAssetReference[];
  [key: string]: unknown;
}

export interface RuntimeStatusEffect {
  id: string;
  modelId?: string;
  name?: string;
  description?: string | null;
  ownerPlayerId?: string | null;
  stackCount?: number;
  stackLabel?: string | null;
  duration?: number;
  durationLabel?: string | null;
  type?: string | null;
  assetRefs?: RuntimeAssetReference[];
  [key: string]: unknown;
}

export interface RuntimeEnemyIntent {
  type?: string;
  damage?: number;
  hits?: number;
  totalDamage?: number;
  label?: string | null;
  description?: string | null;
  targetIds?: string[];
  assetRefs?: RuntimeAssetReference[];
  [key: string]: unknown;
}

export interface RuntimeEnemyVisual {
  assetKey?: string | null;
  encounterSlotId?: string | null;
  variantId?: string | null;
  screenSide?: string | null;
  encounterVisualPackageId?: string | null;
  assetRefs?: RuntimeAssetReference[];
  [key: string]: unknown;
}

export interface RuntimeEnemy {
  id: string;
  name?: string;
  hp?: number;
  maxHp?: number;
  block?: number;
  isAlive?: boolean;
  intent?: string;
  intents?: RuntimeEnemyIntent[];
  runtimeEntityId?: string | null;
  modelId?: string | null;
  statusEffects?: RuntimeStatusEffect[];
  assetRefs?: RuntimeAssetReference[];
  visual?: RuntimeEnemyVisual | null;
  [key: string]: unknown;
}

export interface RuntimePlayer {
  id: string;
  playerId?: string | null;
  character?: string;
  hp?: number;
  maxHp?: number;
  isLocal?: boolean;
  isHost?: boolean;
  isRemote?: boolean;
  localRole?: string | null;
  gold?: number;
  relics?: RuntimeRelic[];
  potions?: RuntimePotion[];
  masterDeck?: RuntimeCardPile | null;
  statusEffects?: RuntimeStatusEffect[];
  [key: string]: unknown;
}

export type RuntimePlayerState = RuntimePlayer;

export interface RuntimeCombatPlayer extends RuntimePlayer {
  block?: number;
  energy?: number;
  maxEnergy?: number;
  hand?: RuntimeCard[];
  drawPileCardIds?: string[];
  discardPileCardIds?: string[];
  exhaustPileCardIds?: string[];
  drawPile?: RuntimeCardPile | null;
  discardPile?: RuntimeCardPile | null;
  exhaustPile?: RuntimeCardPile | null;
}

export interface RuntimeEncounterVisualPartState {
  partId: string;
  actorId?: string;
  activeStateId?: string;
  screenSide?: string;
  anatomicalSide?: string;
  [key: string]: unknown;
}

export interface RuntimeEncounterVisualTransitionEvent {
  sequence?: number;
  packageId?: string;
  transitionId?: string;
  affectedPartIds?: string[];
  activeStateId?: string;
  sourceHook?: string;
  [key: string]: unknown;
}

export interface RuntimeEncounterVisuals {
  packageId: string;
  encounterId?: string;
  provisional?: boolean;
  visualParts?: RuntimeEncounterVisualPartState[];
  recentEvents?: RuntimeEncounterVisualTransitionEvent[];
  notices?: StateNotice[];
  [key: string]: unknown;
}

export interface RuntimeRun {
  seed?: string;
  floor?: number;
  act?: number;
  actLabel?: string | null;
  floorLabel?: string | null;
  encounterId?: string | null;
  encounterLabel?: string | null;
  roomId?: string | null;
  roomLabel?: string | null;
  bossId?: string | null;
  bossLabel?: string | null;
  players?: RuntimePlayer[];
  playersById?: Record<string, RuntimePlayer>;
  hostPlayerId?: string | null;
  localPlayerId?: string | null;
  localRole?: string | null;
  perspective?: string | null;
  remoteOrchestrationCapability?: RemoteOrchestrationCapability | null;
  [key: string]: unknown;
}

export interface RuntimeCombat {
  turn?: number;
  activePlayerId?: string | null;
  hostPlayerId?: string | null;
  localPlayerId?: string | null;
  localRole?: string | null;
  perspective?: string | null;
  remoteOrchestrationCapability?: RemoteOrchestrationCapability | null;
  isPlayerTurn?: boolean;
  encounterId?: string | null;
  encounterLabel?: string | null;
  hand?: RuntimeCard[];
  potions?: RuntimePotion[];
  drawPileCardIds?: string[];
  discardPileCardIds?: string[];
  exhaustPileCardIds?: string[];
  drawPile?: RuntimeCardPile | null;
  discardPile?: RuntimeCardPile | null;
  exhaustPile?: RuntimeCardPile | null;
  players?: RuntimeCombatPlayer[];
  playersById?: Record<string, RuntimeCombatPlayer>;
  enemies?: RuntimeEnemy[];
  encounterVisuals?: RuntimeEncounterVisuals | null;
  [key: string]: unknown;
}

export interface RuntimeDecision {
  phase: string;
  activePlayerId?: string | null;
  requiresAction: boolean;
  primaryActionKinds: string[];
  blockingReason?: string | null;
  actionCount: number;
  fallbackOnly: boolean;
  [key: string]: unknown;
}

export interface RuntimeStateAction {
  id: string;
  kind: string;
  label?: string | null;
  ownerPlayerId?: string | null;
  enabled: boolean;
  reason?: string | null;
  args: Record<string, unknown>;
  stateRefs: string[];
  provisional?: boolean;
  [key: string]: unknown;
}

export interface RuntimeSceneEntry {
  id: string;
  label?: string | null;
  description?: string | null;
  ownerPlayerId?: string | null;
  enabled?: boolean;
  reason?: string | null;
  selected?: boolean;
  provisional?: boolean;
  actionIds?: string[];
  [key: string]: unknown;
}

export interface RuntimeScene {
  type: string;
  id?: string | null;
  items: RuntimeSceneEntry[];
  controls: RuntimeSceneEntry[];
  [key: string]: unknown;
}

export interface RuntimeFallbackChoice {
  id: string;
  label?: string | null;
  kind?: string | null;
  enabled: boolean;
  reason?: string | null;
  classification: "generic" | "modded" | "unmodeled" | string;
  args: Record<string, unknown>;
  [key: string]: unknown;
}

/** `sts2 state actions` — the derived live action surface for the current state. */
export interface RuntimeStateActionsResult {
  actions: RuntimeStateAction[];
  diagnostics?: Array<Record<string, unknown>>;
  [key: string]: unknown;
}

// NOTE: this interface still describes the pre-cutover state envelope. The canonical
// `spirectl.state/v0` envelope the CLI emits today is `{ schemaVersion, language, rootScene,
// characterSelect, run }` — the top-level `screen`/`scene`/`actions`/`combat`/per-screen sections
// below were deleted by design (e2aa8028) and never appear. Every member is optional and the index
// signature is open, so this stays structurally valid; migrating it to the canonical shape is a
// separate, larger job.
export interface RuntimeState {
  schemaVersion: string;
  gameVersion: string;
  bridgeVersion: string;
  transportKind: string;
  attachmentState: string;
  source: string;
  provisional: boolean;
  screen: RuntimeScreen | null;
  perspective?: Record<string, unknown> | null;
  decision?: RuntimeDecision;
  scene?: RuntimeScene | null;
  actions?: RuntimeStateAction[];
  fallbackChoices?: RuntimeFallbackChoice[];
  resolvedPerspective?: Record<string, unknown> | null;
  playerId?: string | null;
  hostPlayerId?: string | null;
  localPlayerId?: string | null;
  localRole?: string | null;
  perspectiveLabel?: string | null;
  remoteOrchestrationCapability?: RemoteOrchestrationCapability | null;
  menu?: Record<string, unknown> | null;
  lobby?: Record<string, unknown> | null;
  map?: RuntimeMapState | null;
  eventRoom?: RuntimeEventRoomState | null;
  treasureRoom?: RuntimeTreasureRoomState | null;
  relicSelection?: RuntimeRelicSelectionState | null;
  restSite?: RuntimeRestSiteState | null;
  shop?: RuntimeShopState | null;
  rewards?: RuntimeRewardsState | null;
  cardSelection?: RuntimeCardSelectionState | null;
  simpleCardSelection?: RuntimeSimpleCardSelectionState | null;
  deckCardSelection?: RuntimeDeckCardSelectionState | null;
  bundleSelection?: RuntimeBundleSelectionState | null;
  multiplayerLobby?: RuntimeMultiplayerLobbyState | null;
  cardOverlay?: RuntimeCardOverlayState | null;
  run?: RuntimeRun | null;
  combat?: RuntimeCombat | null;
  choices?: RuntimeChoice[];
  availableActions?: AvailableAction[];
  notices: StateNotice[];
  debug?: Record<string, unknown> | null;
  [key: string]: unknown;
}

export interface LogEntry {
  cursor: number;
  timestamp?: string;
  level: string;
  target: string;
  message?: string;
  fields?: Record<string, unknown>;
  [key: string]: unknown;
}

export interface LogsResponse {
  source: string;
  provisional: boolean;
  nextCursor: number;
  entries: LogEntry[];
  [key: string]: unknown;
}

export interface ConsoleOptions {
  command: string;
  args?: string[];
  mode?: "dangerous";
}

export interface ConsoleNotice {
  code: string;
  message: string;
  provisional: boolean;
}

export interface ConsoleResult {
  requestId: string;
  command: string;
  args: string[];
  line: string;
  accepted: boolean;
  success: boolean;
  output: string;
  outputLines: string[];
  source: "stub" | "live" | string;
  provisional: boolean;
  notices: ConsoleNotice[];
}

export interface GameInfo {
  repository: string;
  executable: string;
  transport: Record<string, unknown>;
  bridge: Record<string, unknown>;
  config: Record<string, unknown>;
  notes: string[];
  [key: string]: unknown;
}

export interface GameDetectResult {
  status: string;
  configuredPath?: string | null;
  detectedPath?: string | null;
  liveBridge?: Record<string, unknown>;
  notes: string[];
  [key: string]: unknown;
}

export interface LifecycleWaitOptions {
  timeoutMs?: number;
  intervalMs?: number;
}

export interface GameLaunchOptions extends LifecycleWaitOptions {
  launchArgs?: string[];
}

export interface LifecycleWaitResult {
  gamePath?: string;
  assembliesDir?: string;
  modsDir?: string;
  modDir?: string;
  endpoint?: Record<string, unknown>;
  socketPath?: string | null;
  pipeName?: string | null;
  tcpAddress?: string | null;
  attempts?: number;
  elapsedMs?: number;
  gameInfo?: GameInfo;
  screen?: Record<string, unknown>;
  [key: string]: unknown;
}

export interface LifecycleStopNotice {
  code?: string;
  message?: string;
  [key: string]: unknown;
}

export interface LifecycleStopEndpoint {
  kind?: string;
  socketPath?: string | null;
  pipeName?: string | null;
  address?: string | null;
  [key: string]: unknown;
}

export interface LifecycleStopResult {
  command: string;
  strategy: string;
  matchedPids: number[];
  stoppedPids: number[];
  remainingPids: number[];
  endpoint?: LifecycleStopEndpoint;
  elapsedMs: number;
  timeoutMs: number;
  success: boolean;
  bridgeEndpointDisappeared?: boolean;
  notices: LifecycleStopNotice[];
  [key: string]: unknown;
}

export interface GameLaunchResult {
  launch?: Record<string, unknown>;
  attachment?: LifecycleWaitResult;
  [key: string]: unknown;
}

export interface GameDeployOptions extends LifecycleWaitOptions {
  path: string;
  build?: boolean;
  restart?: boolean;
  verify?: boolean;
}

export interface GameDeployResult {
  bridge?: Record<string, unknown>;
  mod?: Record<string, unknown>;
  build?: Record<string, unknown> | null;
  restart?: Record<string, unknown>;
  verify?: Record<string, unknown>;
  [key: string]: unknown;
}

export interface ToolchainInfoResult {
  roots: Record<string, unknown>;
  tools: Array<Record<string, unknown>>;
  [key: string]: unknown;
}

export interface AiToolCatalog {
  source: string;
  adapter: Record<string, unknown>;
  tools: AiToolDefinition[];
  [key: string]: unknown;
}

export interface AiToolDefinition {
  name: string;
  summary: string;
  status: string;
  readOnly: boolean;
  mapsTo: string[];
  inputSchema: Record<string, unknown>;
  outputShape: Record<string, unknown>;
  limitations: string[];
  recommendedUsage: string[];
  [key: string]: unknown;
}

export interface ActionResult {
  requestId: string;
  actionInstanceId: string;
  kind: string;
  accepted: boolean;
  provisional: boolean;
  message: string;
  playerId?: string | null;
  ownerPlayerId?: string | null;
  requestedPlayerId?: string | null;
  resolvedOwnerPlayerId?: string | null;
  localPlayerId?: string | null;
  localRole?: string | null;
  ownerRole?: string | null;
  action?: string | null;
  reasonCode?: string | null;
  perspective?: string | null;
  remoteOrchestrationCapability?: RemoteOrchestrationCapability | null;
  checkedHookPaths?: string[];
  failureReasonCodes?: string[];
  [key: string]: unknown;
}

export interface StructuredActionFailure {
  reasonCode?: string;
  actionReasonCode?: string;
  screen?: RuntimeScreen | string | null;
  playerId?: string | null;
  requestedPlayerId?: string | null;
  resolvedOwnerPlayerId?: string | null;
  localPlayerId?: string | null;
  localRole?: string | null;
  ownerRole?: string | null;
  action?: string | null;
  perspective?: string | null;
  remoteOrchestrationCapability?: RemoteOrchestrationCapability | null;
  capability?: RemoteOrchestrationCapability | string | null;
  checkedHookPaths?: string[];
  details?: unknown;
  [key: string]: unknown;
}

export interface StructuredBridgeErrorDetail {
  field?: string | null;
  value?: unknown;
  note?: string | null;
  actionReasonCode?: string | null;
  checkedHookPaths?: string[];
  screen?: RuntimeScreen | string | null;
  playerId?: string | null;
  requestedPlayerId?: string | null;
  resolvedOwnerPlayerId?: string | null;
  localPlayerId?: string | null;
  localRole?: string | null;
  action?: string | null;
  reasonCode?: string | null;
  perspective?: string | null;
  fieldDiagnostics?: unknown[];
  [key: string]: unknown;
}

export interface StructuredBridgeErrorPayload {
  code?: string;
  message?: string;
  actionFailure?: StructuredActionFailure | null;
  actionReasonCode?: string | null;
  details?: StructuredBridgeErrorDetail[];
  [key: string]: unknown;
}

export interface Sts2ErrorPayload {
  exitCode?: number;
  error?: StructuredBridgeErrorPayload;
  [key: string]: unknown;
}

export interface AssertionResult {
  matched: boolean;
  path: string;
  operator: string;
  expected: unknown;
  actual: unknown;
  pathExists: boolean;
  [key: string]: unknown;
}

export interface WaitForResult extends AssertionResult {
  attempts: number;
  elapsedMs: number;
}

export interface StaticInspectionResponse {
  command: string;
  status: string;
  [key: string]: unknown;
}

export interface TestRunResult {
  status: string;
  scenarioCount: number;
  passedScenarioCount: number;
  failedScenarioCount: number;
  invalidScenarioCount: number;
  stepCount: number;
  passedStepCount: number;
  failedStepCount: number;
  invalidStepCount: number;
  elapsedMs: number;
  exitCode: number;
  scenarios: Array<Record<string, unknown>>;
  artifacts?: Record<string, unknown>;
  remoteArtifacts?: RemoteTestRunArtifact[];
  artifactIndex?: RemoteTestRunArtifactIndex;
  [key: string]: unknown;
}

export type RemoteTestRunStatus =
  | "queued"
  | "running"
  | "completed"
  | "failed"
  | "canceled"
  | "orphaned"
  | "unknown"
  | string;

export interface RemoteTestRunArtifact {
  relativePath: string;
  url?: string;
  downloadUrl?: string;
  contentType?: string | null;
  sizeBytes?: number | null;
  sha256?: string | null;
  [key: string]: unknown;
}

export interface RemoteTestRunArtifactIndex {
  runId?: string;
  artifacts: RemoteTestRunArtifact[];
  [key: string]: unknown;
}

export interface RemoteTestRunArtifactDownload {
  contentType: string;
  body: unknown;
}

export interface RemoteTestRunStatusResult {
  runId: string;
  status: RemoteTestRunStatus;
  submittedAt?: string | null;
  startedAt?: string | null;
  updatedAt?: string | null;
  completedAt?: string | null;
  summary?: TestRunResult;
  error?: Record<string, unknown>;
  recovery?: Record<string, unknown>;
  [key: string]: unknown;
}

export interface LoadFixtureResult {
  status?: string;
  requestedPath?: string;
  resolvedPath?: string;
  fixture?: {
    schemaVersion?: string;
    name?: string;
    screen?: string;
    [key: string]: unknown;
  };
  source?: string;
  provisional?: boolean;
  recipeReport?: FixtureRecipeReport;
  loaded?: {
    screen?: RuntimeScreen;
    resolvedPerspective?: Record<string, unknown>;
    notices?: StateNotice[];
    recipeReport?: FixtureRecipeReport;
    [key: string]: unknown;
  };
  [key: string]: unknown;
}

export interface FixtureRecipeFieldReport {
  fieldPath: string;
  valueSummary?: string;
  reasonCode: string;
  message?: string;
  [key: string]: unknown;
}

export interface FixtureBridgeValidationResult {
  status: "passed" | "failed" | "warning" | "unspecified" | string;
  details: FixtureRecipeFieldReport[];
  [key: string]: unknown;
}

export interface FixtureRecipeReport {
  recipeName: string;
  appliedFields: FixtureRecipeFieldReport[];
  inferredFields: FixtureRecipeFieldReport[];
  omittedFields: FixtureRecipeFieldReport[];
  unsupportedFields: FixtureRecipeFieldReport[];
  degradedMultiplayerFields: FixtureRecipeFieldReport[];
  bridgeValidation: FixtureBridgeValidationResult;
  [key: string]: unknown;
}

export interface HotReloadResult {
  status?: string;
  requestId?: string;
  project?: Record<string, unknown>;
  build?: Record<string, unknown>;
  shell?: Record<string, unknown>;
  reload?: Record<string, unknown>;
  notices?: Array<Record<string, unknown>>;
  [key: string]: unknown;
}

export interface HotReloadStatusResult {
  project?: Record<string, unknown>;
  shell?: Record<string, unknown>;
  notices?: Array<Record<string, unknown>>;
  [key: string]: unknown;
}

export interface ScenarioExportResult {
  path?: string;
  exactBundlePath?: string | null;
  exactBundleKind?: "save-backed" | "fixture-backed" | null;
  restoreQuality?: string;
  restoreSupport?: Record<string, unknown>;
  [key: string]: unknown;
}

export interface ScenarioLoadResult {
  path?: string;
  restoreQuality?: string;
  exactBundleUsed?: boolean;
  sparseFallbackUsed?: boolean;
  multiplayerRestore?: Record<string, unknown>;
  validation?: Record<string, unknown>;
  bridgeVerification?: Record<string, unknown> | null;
  [key: string]: unknown;
}

export interface ProbeResult {
  status?: string;
  [key: string]: unknown;
}

export interface LogHealthResult {
  status: string;
  unhealthyEntryCount?: number;
  excludedEntryCount?: number;
  inspectedEntryCount?: number;
  entries?: Array<Record<string, unknown>>;
  [key: string]: unknown;
}

export interface DiagnosticsResult {
  status?: string;
  logHealth?: Record<string, unknown> | null;
  captures?: Record<string, unknown>;
  bundle?: Record<string, unknown>;
  errors?: Array<Record<string, unknown>>;
  [key: string]: unknown;
}

export interface DebugNotice {
  code?: string;
  message?: string;
  [key: string]: unknown;
}

export interface DebugPredicate {
  operator?: string;
  expected?: string | null;
  [key: string]: unknown;
}

export interface DebugBreakpoint {
  id: string;
  name?: string | null;
  queryPath: string;
  predicate: DebugPredicate;
  enabled: boolean;
  provisional: boolean;
  [key: string]: unknown;
}

export interface DebugBreakpointHit {
  breakpointId: string;
  breakpointName?: string | null;
  queryPath: string;
  kind?: string;
  predicate?: DebugPredicate | null;
  actual?: string | null;
  screenType?: string | null;
  screenInstanceId?: string | null;
  hitCount?: number;
  [key: string]: unknown;
}

export interface DebugSessionInfo {
  id: string;
  name?: string | null;
  role?: DebugSessionRole;
  leaseTimeoutMs: number;
  leaseExpiresAtUnixMs: number;
  [key: string]: unknown;
}

export interface DebugStatusResult {
  supported: boolean;
  executionState: string;
  pauseReason: string;
  pauseReasonDetail?: string | null;
  canPause: boolean;
  canResume: boolean;
  supportedStepKinds: string[];
  breakpoints: DebugBreakpoint[];
  lastBreakpointHit?: DebugBreakpointHit | null;
  notices: DebugNotice[];
  breakpointManagementSupported: boolean;
  breakpointEvaluationSupported: boolean;
  sessionOwnership?: string;
  activeSession?: DebugSessionInfo | null;
  [key: string]: unknown;
}

export interface DebugOperationResult {
  applied: boolean;
  status: DebugStatusResult;
  notices: DebugNotice[];
  [key: string]: unknown;
}

export interface DebugStepResult extends DebugOperationResult {
  kind: string;
  requestedKind: string;
  count: number;
}

export interface DebugSessionStartResult {
  started: boolean;
  session?: DebugSessionInfo | null;
  status: DebugStatusResult;
  notices: DebugNotice[];
  [key: string]: unknown;
}

export interface DebugSessionStatusResult {
  found: boolean;
  session?: DebugSessionInfo | null;
  status: DebugStatusResult;
  notices: DebugNotice[];
  [key: string]: unknown;
}

export interface DebugSessionEndResult {
  ended: boolean;
  id: string;
  status: DebugStatusResult;
  notices: DebugNotice[];
  [key: string]: unknown;
}

export interface DebugWaitResult {
  completed: boolean;
  timedOut: boolean;
  status: DebugStatusResult;
  notices: DebugNotice[];
  [key: string]: unknown;
}

export type DebugEventKind =
  | "status-changed"
  | "breakpoint-hit"
  | "paused"
  | "resumed"
  | "stepped"
  | "lease-changed"
  | "observer-attached"
  | "observer-detached"
  | "disconnected"
  | "client-dropped"
  | "state-changed"
  | string;

export interface DebugEventRecord {
  sequence: number;
  unixTimeMs: number;
  kind: DebugEventKind;
  sessionId?: string | null;
  sessionRole?: DebugSessionRole;
  notices: DebugNotice[];
  detail?: Record<string, unknown> | null;
  [key: string]: unknown;
}

export interface DebugEventRetention {
  oldestSequence: number;
  newestSequence: number;
  limit: number;
  [key: string]: unknown;
}

export interface DebugEventsResult {
  events: DebugEventRecord[];
  fromSequence: number;
  nextSequence: number;
  oldestRetainedSequence: number;
  newestSequence: number;
  retention: DebugEventRetention;
  expired: boolean;
  overflow: boolean;
  follow: boolean;
  timedOut: boolean;
  timeoutMs: number;
  notices: DebugNotice[];
  [key: string]: unknown;
}

export interface BreakpointListResult {
  breakpoints: DebugBreakpoint[];
  status: DebugStatusResult;
  notices: DebugNotice[];
  [key: string]: unknown;
}

export interface BreakpointAddResult {
  added: boolean;
  breakpoint?: DebugBreakpoint | null;
  status: DebugStatusResult;
  notices: DebugNotice[];
  [key: string]: unknown;
}

export interface BreakpointRemoveResult {
  removed: boolean;
  id: string;
  status: DebugStatusResult;
  notices: DebugNotice[];
  [key: string]: unknown;
}

export interface ScreenshotDiffResult {
  matched?: boolean;
  diffPixels?: number;
  diffRatio?: number;
  bundle?: Record<string, unknown>;
  [key: string]: unknown;
}

export interface ProjectProfileResult {
  name?: string;
  sourcePath?: string;
  profile?: Record<string, unknown>;
  steps?: Array<Record<string, unknown>>;
  [key: string]: unknown;
}

export interface ProjectProfileListResult {
  sourcePath?: string;
  profiles?: Array<Record<string, unknown>>;
  [key: string]: unknown;
}

export interface InspectViewportPresetsResult {
  source?: string;
  presets?: Array<Record<string, unknown>>;
  [key: string]: unknown;
}

export interface InspectReferenceTopicsResult {
  source?: string;
  topics?: Array<Record<string, unknown>>;
  [key: string]: unknown;
}

export type CodeHookSource = "game" | "mod";
export type CodeHookForm =
  | "managed-prefix"
  | "managed-postfix"
  | "managed-override"
  | "managed-interface-contract";
export type CodeHookSort = "relevance" | "name" | "reference-count" | "assembly";

export interface CodeHooksOptions extends CodeSearchRootOptions {
  query?: string;
  limit?: number;
  offset?: number;
  source?: CodeHookSource;
  assembly?: string;
  form?: CodeHookForm[];
  hasScript?: boolean;
  sort?: CodeHookSort;
}

export interface CodeHookInfoOptions extends CodeSearchRootOptions {
  query: string;
}

export interface CodeHooksResult extends StaticInspectionResponse {
  totalCount?: number;
  returnedCount?: number;
  matchCount?: number;
  limit?: number;
  offset?: number;
  nextOffset?: number;
  facets?: Record<string, Array<Record<string, unknown>>>;
  matches?: Array<Record<string, unknown>>;
}

export interface CodeHookInfoResult extends StaticInspectionResponse {
  id?: string;
  signature?: string;
  hookForms?: string[];
  suggestedNextCommands?: string[];
}

export interface ProjectHookResult {
  status?: string;
  [key: string]: unknown;
}

export interface AssetsExtractResult {
  command: string;
  status: string;
  query: string;
  format?: string;
  executionMode?: string;
  outputDir?: string;
  exportCount?: number;
  exports?: Array<Record<string, unknown>>;
  [key: string]: unknown;
}

export interface AssetsExplainResult {
  command: string;
  status: string;
  query: string;
  executionMode?: string;
  sourceRoot?: string;
  sourcePath?: string;
  assetKind?: string;
  storageKind?: string;
  explanation?: Record<string, unknown>;
  [key: string]: unknown;
}

export interface AssetsExtractBatchRequestResult {
  id: string;
  query: string;
  status: string;
  executionMode?: string;
  format?: string;
  outputDir?: string;
  matchCount?: number;
  exportCount?: number;
  metadata?: unknown;
  exports?: Array<Record<string, unknown>>;
  error?: Record<string, unknown>;
  [key: string]: unknown;
}

export interface AssetsExtractBatchResult {
  command: string;
  status: string;
  manifestPath?: string;
  outputDir?: string;
  executionMode?: string;
  format?: string;
  requestCount?: number;
  successCount?: number;
  failureCount?: number;
  skippedCount?: number;
  exportCount?: number;
  results?: AssetsExtractBatchRequestResult[];
  [key: string]: unknown;
}

export interface SkillInstallResult {
  name?: string;
  path?: string;
  pathSource?: string;
  skillDir?: string;
  source?: string;
  sourcePath?: string;
  [key: string]: unknown;
}

export interface ScreenshotResult {
  outputPath?: string;
  [key: string]: unknown;
}

export interface StateOptions {
  perspective?: PerspectiveScope;
  playerId?: string;
}

export interface PresentationElement {
  id: string;
  kind: string;
  ownerPlayerId?: string | null;
  stateRef?: string | null;
  zIndex?: number;
  actionRefs?: PresentationElementActionReference[];
  assetRefs?: Array<Record<string, unknown>>;
  interaction?: PresentationElementInteraction | null;
  notices?: StateNotice[];
  [key: string]: unknown;
}

export interface PresentationElementActionReference {
  actionId: string;
  kind: string;
  enabled?: boolean;
  ownerPlayerId?: string | null;
  ownerRole?: string | null;
  label?: string | null;
  [key: string]: unknown;
}

export type PresentationInteractionState =
  | "enabled"
  | "disabled"
  | "selected"
  | "staged"
  | "hover-detail-only"
  | "passive"
  | "targetable"
  | string;

export type PresentationInteractionGesture =
  | "click"
  | "tap"
  | "drag"
  | "hover"
  | "keyboard-activate"
  | string;

export type PresentationTargetSelectionRequirement =
  | "none"
  | "any"
  | "enemy"
  | "player"
  | "card"
  | "map-node"
  | "potion"
  | string;

export interface PresentationElementInteraction {
  state?: PresentationInteractionState;
  hitTargets?: PresentationHitTarget[];
  supportedGestures?: PresentationInteractionGesture[];
  targetSelection?: PresentationTargetSelectionRequirement;
  disabledReasons?: PresentationDisabledReason[];
  actionRefIds?: string[];
  stateRefs?: string[];
  accessibilityLabel?: string | null;
  [key: string]: unknown;
}

export interface PresentationHitTarget {
  id: string;
  role?: string | null;
  rect?: PresentationRect | null;
  accessibilityLabel?: string | null;
  stability?: string | null;
  actionRefIds?: string[];
  stateRefs?: string[];
  [key: string]: unknown;
}

export interface PresentationRect {
  x?: number;
  y?: number;
  width?: number;
  height?: number;
  normalized?: PresentationNormalizedRect | null;
  [key: string]: unknown;
}

export interface PresentationNormalizedRect {
  x?: number;
  y?: number;
  width?: number;
  height?: number;
  centerX?: number;
  centerY?: number;
  unit?: string | null;
  origin?: string | null;
  [key: string]: unknown;
}

export interface PresentationDisabledReason {
  code?: string;
  message?: string;
  actionRefId?: string | null;
  stateRef?: string | null;
  [key: string]: unknown;
}

export interface LogsOptions {
  limit?: number;
  tail?: number;
  afterCursor?: number;
  level?: LogLevel;
  target?: string;
}

export interface LogHealthOptions extends LogsOptions {
  excludeTargets?: string[];
  excludeMessageRegexes?: string[];
}

export interface ViewportOptions {
  preset?: string;
  width?: number;
  height?: number;
  presetCatalogs?: string[];
}

export interface InspectViewportPresetsOptions {
  presetCatalogs?: string[];
}

export interface DiagnosticsOptions extends LogHealthOptions, ViewportOptions {
  bundleDir?: string;
}

export interface AssetsExtractOptions extends CodeSearchRootOptions {
  query: string;
  execution?: "auto" | "offline" | "live";
  format?: "auto" | "png" | "webp";
}

export interface AssetsExplainOptions extends CodeSearchRootOptions {
  query: string;
  execution?: "live";
}

export interface AssetsExtractBatchOptions extends CodeSearchRootOptions {
  manifest: string;
  output?: string;
  execution?: "auto" | "offline" | "live";
  format?: "auto" | "png" | "webp";
  failFast?: boolean;
}

export interface LoadFixtureOptions {
  path: string;
}

export interface HotReloadOptions extends LifecycleWaitOptions {
  project: string;
  build?: boolean;
  wait?: boolean;
}

export interface HotReloadStatusOptions {
  project: string;
}

export interface ScenarioExportOptions {
  output: string;
  includeExact?: boolean;
}

export interface ScenarioLoadOptions extends LifecycleWaitOptions {
  path: string;
  restart?: boolean;
  allowDegradedLocalMultiplayer?: boolean;
}

export interface ProbePredicateOptions extends QueryPredicateOptions {
  query?: string;
}

export interface HttpOptions extends ProbePredicateOptions {
  url: string;
  method?: string;
  headers?: string[];
  body?: string;
  timeoutMs?: number;
  expectStatus?: number;
  expectHeaders?: string[];
}

export interface HttpWaitOptions extends HttpOptions {
  intervalMs?: number;
}

export interface FetchOptions extends ProbePredicateOptions {
  source: string;
  output?: string;
  expectSha256?: string;
}

export interface WebsocketOptions {
  url: string;
  headers?: string[];
  sendText?: string[];
  expectText?: string[];
  timeoutMs?: number;
}

export interface ProjectHookShowOptions {
  name: string;
}

export interface ProjectProfileShowOptions {
  name: string;
}

export interface ProjectProfileRunOptions {
  name: string;
}

export interface ProjectHookRunOptions {
  name: string;
  input?: string | Record<string, unknown> | Array<unknown>;
}

export interface SkillInstallOptions {
  path?: string;
}

export interface ScreenshotOptions extends ViewportOptions {
  output?: string;
  rpcTimeoutMs?: number;
}

export interface ScreenshotDiffOptions extends ViewportOptions {
  baseline: string;
  actual?: string;
  bundleDir?: string;
  maxDiffPixels?: number;
  maxDiffRatio?: number;
  rpcTimeoutMs?: number;
}

export interface SnapshotExportOptions {
  spec: string;
  output: string;
  presetCatalogs?: string[];
}

export interface SnapshotCompareOptions {
  spec: string;
  baseline: string;
  bundleDir?: string;
  presetCatalogs?: string[];
}

export interface SnapshotExportResult {
  [key: string]: unknown;
}

export interface SnapshotCompareResult {
  [key: string]: unknown;
}

export interface TestStressOptions {
  path: string;
  artifactsDir?: string;
  failureArtifacts?: FailureArtifactsMode;
  iterations?: number;
  durationMs?: number;
  maxFailures?: number;
  cooldownMs?: number;
}

export interface TestStressResult {
  status: string;
  completedIterations: number;
  passedIterations: number;
  failedIterations: number;
  [key: string]: unknown;
}

export interface QueryPredicateOptions {
  equals?: string | number;
  contains?: string | number;
  regex?: string;
  gt?: string | number;
  gte?: string | number;
  lt?: string | number;
  lte?: string | number;
  exists?: true;
  notExists?: true;
}

export type DebugStepKind = "frame" | "action";
export type DebugBreakpointKind = "match" | "change";
export type DebugSessionRole = "controller" | "observer" | string;

export interface DebugStatusOptions {
  session?: string;
}

export interface DebugSessionBoundOptions {
  session?: string;
}

export interface DebugSessionStartOptions {
  name?: string;
  role?: DebugSessionRole;
  pause?: boolean;
  leaseTimeoutMs?: number;
}

export interface DebugSessionStatusOptions {
  sessionId: string;
}

export interface DebugSessionEndOptions {
  sessionId: string;
  resume?: boolean;
}

export interface DebugWaitOptions {
  sessionId: string;
  timeoutMs?: number;
}

export interface DebugEventsOptions {
  sessionId: string;
  fromSequence?: number;
  limit?: number;
  follow?: boolean;
  timeoutMs?: number;
}

export interface DebugStepOptions {
  session?: string;
  kind: DebugStepKind;
  count?: number;
}

export interface BreakpointAddOptions extends QueryPredicateOptions {
  session?: string;
  path: string;
  kind?: DebugBreakpointKind;
  name?: string;
  minHitCount?: number;
  autoRemoveOnHit?: boolean;
}

export interface BreakpointRemoveOptions {
  session?: string;
  id: string;
}

export interface QueryOptions extends QueryPredicateOptions {
  path: string;
  perspective?: PerspectiveScope;
  playerId?: string;
}

export interface WaitForOptions extends QueryOptions {
  timeoutMs?: number;
  intervalMs?: number;
}

export interface CodeSearchRootOptions {
  gamePath?: string;
  assembliesDir?: string;
  resourcesDir?: string;
  modsDir?: string;
  includeMods?: boolean;
  includeDependencies?: boolean;
}

export interface CodeLocateOptions extends CodeSearchRootOptions {
  subject: CodeLocateSubject;
  query: string;
  limit?: number;
}

export interface CodeResolveOptions extends CodeSearchRootOptions {
  subject: CodeResolveSubject;
  query: string;
}

export interface CodeDecompileOptions extends CodeResolveOptions {
  full?: boolean;
}

export interface CodeDerivedOptions extends CodeSearchRootOptions {
  subject: CodeDerivedSubject;
  query: string;
  limit?: number;
}

export interface CodeSceneSearchOptions extends CodeSearchRootOptions {
  query: string;
  limit?: number;
}

export interface CodeSceneTreeOptions extends CodeSearchRootOptions {
  scene: string;
}

export interface CodeSceneNodeOptions extends CodeSearchRootOptions {
  scene: string;
  nodePath: string;
}

export type TestRunScenario = Record<string, unknown> | Array<unknown>;

export interface TestRunCommonOptions {
  profile?: string;
  tag?: string;
  tags?: string[];
  artifactsDir?: string;
  failureArtifacts?: FailureArtifactsMode;
  /** Service-backed test runs only; ignored by the local CLI runner. */
  durable?: boolean;
}

export type TestRunOptions =
  | (TestRunCommonOptions & {
      path: string;
      inline?: never;
      scenario?: never;
    })
  | (TestRunCommonOptions & {
      inline: string;
      path?: never;
      scenario?: never;
    })
  | (TestRunCommonOptions & {
      scenario: TestRunScenario;
      path?: never;
      inline?: never;
    })
  | (TestRunCommonOptions & {
      profile: string;
      path?: never;
      inline?: never;
      scenario?: never;
    });

export type PlayerScopedActionOption = { playerId?: string };
export type PreferredActionOption = {
  preferredAction?: RuntimeVisibleActionReference | null;
  preferredActionRef?: RuntimeVisibleActionReference | null;
  arguments?: RuntimeActionArguments;
};

export type ActOptions =
  | (PreferredActionOption & {
      kind?: never;
    })
  | (PlayerScopedActionOption &
      PreferredActionOption &
      (
        | { kind: "choose"; choice?: string; choiceId?: string }
        | { kind: "confirm-selection" }
        | { kind: "cancel-selection" }
        | { kind: "select-map-node"; node?: string; mapNodeId?: string }
        | { kind: "end-turn" }
        | { kind: "ready" }
        | { kind: "unready" }
        | { kind: "select-character"; character?: string; characterId?: string }
        | { kind: "claim-reward"; reward?: string; rewardId?: string }
        | { kind: "skip-rewards" }
        | { kind: "select-card"; card?: string; cardId?: string }
        | { kind: "skip-card-selection" }
        | { kind: "select-bundle"; bundle?: string; bundleId?: string }
        | { kind: "buy-card"; shopItem?: string; shopItemId?: string }
        | { kind: "buy-relic"; shopItem?: string; shopItemId?: string }
        | { kind: "buy-potion"; shopItem?: string; shopItemId?: string }
        | { kind: "remove-card"; shopItem?: string; shopItemId?: string }
        | { kind: "leave-shop" }
        | { kind: "close-shop-inventory" }
        | { kind: "rest" }
        | { kind: "smith"; card?: string; cardId?: string }
        | { kind: "use-rest-site-option"; restOption?: string; restOptionId?: string }
        | { kind: "proceed-rest-site" }
        | { kind: "open-chest" }
        | { kind: "take-relic"; relic?: string; relicId?: string }
        | { kind: "proceed-treasure-room" }
        | { kind: "back-from-map" }
        | { kind: "select-event-option"; eventOption?: string; eventOptionId?: string }
        | { kind: "open-event-shop"; eventOption?: string; eventOptionId?: string }
        | { kind: "use-crystal-sphere-control"; control?: string; controlId?: string }
        | { kind: "proceed-event" }
        | { kind: "toggle-map" }
        | { kind: "toggle-deck" }
        | { kind: "toggle-settings" }
        | { kind: "play-card"; card?: string; cardId?: string; target?: string; targetId?: string }
        | { kind: "use-potion"; potion?: string; potionId?: string; target?: string; targetId?: string }
      ))
  | { kind: "mouse-click"; x: number; y: number; button?: "left" | "right" | "middle" };

export class Sts2CliError extends Error {
  constructor(args: {
    exitCode: number;
    argv: string[];
    stdout: string;
    stderr: string;
    payload: unknown | null;
  });

  exitCode: number;
  argv: string[];
  stdout: string;
  stderr: string;
  payload: Sts2ErrorPayload | null;
}

export class Sts2ServiceError extends Error {
  constructor(args: {
    statusCode: number;
    payload?: unknown;
    url?: string;
    method?: string;
  });

  statusCode: number;
  payload: unknown;
  url?: string;
  method?: string;
}

export interface Sts2Client {
  gameDetect(): Promise<GameDetectResult>;
  gameInfo(): Promise<GameInfo>;
  toolchainInfo(): Promise<ToolchainInfoResult>;
  gameLaunch(options?: GameLaunchOptions): Promise<GameLaunchResult>;
  gameAttach(options?: LifecycleWaitOptions): Promise<LifecycleWaitResult>;
  gameClose(options?: LifecycleWaitOptions): Promise<LifecycleStopResult>;
  gameKill(options?: LifecycleWaitOptions): Promise<LifecycleStopResult>;
  gameDeploy(options: GameDeployOptions): Promise<GameDeployResult>;
  inspectActions(): Promise<Record<string, unknown>>;
  inspectAiTools(): Promise<AiToolCatalog>;
  inspectViewportPresets(options?: InspectViewportPresetsOptions): Promise<InspectViewportPresetsResult>;
  inspectReferenceTopics(): Promise<InspectReferenceTopicsResult>;
  state(options?: StateOptions): Promise<RuntimeState>;
  stateActions(options?: StateOptions): Promise<RuntimeStateActionsResult>;
  logs(options?: LogsOptions): Promise<LogsResponse>;
  logHealth(options?: LogHealthOptions): Promise<LogHealthResult>;
  diagnostics(options?: DiagnosticsOptions): Promise<DiagnosticsResult>;
  assetsExtract(options: AssetsExtractOptions): Promise<AssetsExtractResult>;
  assetsExplain(options: AssetsExplainOptions): Promise<AssetsExplainResult>;
  assetsExtractBatch(options: AssetsExtractBatchOptions): Promise<AssetsExtractBatchResult>;
  loadFixture(options: LoadFixtureOptions): Promise<LoadFixtureResult>;
  hotReload(options: HotReloadOptions): Promise<HotReloadResult>;
  hotReloadStatus(options: HotReloadStatusOptions): Promise<HotReloadStatusResult>;
  scenarioExport(options: ScenarioExportOptions): Promise<ScenarioExportResult>;
  scenarioLoad(options: ScenarioLoadOptions): Promise<ScenarioLoadResult>;
  debugStatus(options?: DebugStatusOptions): Promise<DebugStatusResult>;
  debugSessionStart(options?: DebugSessionStartOptions): Promise<DebugSessionStartResult>;
  debugSessionStatus(options: DebugSessionStatusOptions): Promise<DebugSessionStatusResult>;
  debugSessionEnd(options: DebugSessionEndOptions): Promise<DebugSessionEndResult>;
  debugEvents(options: DebugEventsOptions): Promise<DebugEventsResult>;
  debugPause(options?: DebugSessionBoundOptions): Promise<DebugOperationResult>;
  debugResume(options?: DebugSessionBoundOptions): Promise<DebugOperationResult>;
  debugStep(options: DebugStepOptions): Promise<DebugStepResult>;
  debugWait(options: DebugWaitOptions): Promise<DebugWaitResult>;
  breakpointList(options?: DebugSessionBoundOptions): Promise<BreakpointListResult>;
  breakpointAdd(options: BreakpointAddOptions): Promise<BreakpointAddResult>;
  breakpointRemove(options: BreakpointRemoveOptions): Promise<BreakpointRemoveResult>;
  http(options: HttpOptions): Promise<ProbeResult>;
  httpWait(options: HttpWaitOptions): Promise<ProbeResult>;
  fetch(options: FetchOptions): Promise<ProbeResult>;
  websocket(options: WebsocketOptions): Promise<ProbeResult>;
  projectProfileList(): Promise<ProjectProfileListResult>;
  projectProfileShow(options: ProjectProfileShowOptions): Promise<ProjectProfileResult>;
  projectProfileRun(options: ProjectProfileRunOptions): Promise<ProjectProfileResult>;
  projectHookList(): Promise<ProjectHookResult>;
  projectHookShow(options: ProjectHookShowOptions): Promise<ProjectHookResult>;
  projectHookRun(options: ProjectHookRunOptions): Promise<ProjectHookResult>;
  skillInstall(options?: SkillInstallOptions): Promise<SkillInstallResult>;
  screenshot(options?: ScreenshotOptions): Promise<ScreenshotResult>;
  screenshotDiff(options: ScreenshotDiffOptions): Promise<ScreenshotDiffResult>;
  snapshotExport(options: SnapshotExportOptions): Promise<SnapshotExportResult>;
  snapshotCompare(options: SnapshotCompareOptions): Promise<SnapshotCompareResult>;
  waitFor(options: WaitForOptions): Promise<WaitForResult>;
  assert(options: QueryOptions): Promise<AssertionResult>;
  codeLocate(options: CodeLocateOptions): Promise<StaticInspectionResponse>;
  codeDescribe(options: CodeResolveOptions): Promise<StaticInspectionResponse>;
  codeRefs(options: CodeLocateOptions): Promise<StaticInspectionResponse>;
  codeDerived(options: CodeDerivedOptions): Promise<StaticInspectionResponse>;
  codeDecompile(options: CodeDecompileOptions): Promise<StaticInspectionResponse>;
  codeHooks(options?: CodeHooksOptions): Promise<CodeHooksResult>;
  codeHookInfo(options: CodeHookInfoOptions): Promise<CodeHookInfoResult>;
  codeSceneSearch(options: CodeSceneSearchOptions): Promise<StaticInspectionResponse>;
  codeSceneTree(options: CodeSceneTreeOptions): Promise<StaticInspectionResponse>;
  codeSceneNode(options: CodeSceneNodeOptions): Promise<StaticInspectionResponse>;
  testRun(options: TestRunOptions): Promise<TestRunResult>;
  submitTestRun(options: TestRunOptions): Promise<RemoteTestRunStatusResult>;
  waitForTestRun(runId: string): Promise<RemoteTestRunStatusResult>;
  listTestRunArtifacts(runId: string): Promise<RemoteTestRunArtifactIndex>;
  downloadTestRunArtifact(runId: string, relativePath: string): Promise<RemoteTestRunArtifactDownload>;
  testStress(options: TestStressOptions): Promise<TestStressResult>;
  devConsole(options: ConsoleOptions): Promise<ConsoleResult>;
  act(options: ActOptions): Promise<ActionResult>;
}

export function createSts2Client(options?: CreateSts2ClientOptions): Sts2Client;
