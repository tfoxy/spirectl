using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Core.Actions;

public enum SemanticActionKind
{
    PlayCard,
    Choose,
    ConfirmSelection,
    CancelSelection,
    EndTurn,
    CancelEndTurn,
    UsePotion,
    Ready,
    Unready,
    SelectCharacter,
    SelectMapNode,
    MouseClick,
    ClaimReward,
    SkipRewards,
    SelectCard,
    SkipCardSelection,
    SelectBundle,
    BuyCard,
    BuyRelic,
    BuyPotion,
    RemoveCard,
    LeaveShop,
    CloseShopInventory,
    Rest,
    Smith,
    UseRestSiteOption,
    ProceedRestSite,
    OpenChest,
    TakeRelic,
    ProceedTreasureRoom,
    BackFromMap,
    SelectEventOption,
    OpenEventShop,
    UseCrystalSphereControl,
    ProceedEvent,
    JoinLobbyPlayer,
    LeaveLobbyPlayer,
    ToggleMap,
    ToggleDeck,
    ToggleSettings,
    SortDeckView,
    ToggleDeckViewUpgrades,
    OpenPotionPopup,
    StartPotionTargeting,
    SelectTarget,
    DiscardPotion,
    ViewDrawPile,
    ViewDiscardPile,
    ViewExhaustPile,
    InspectRelic,
    CloseInspectRelic,
    SelectHandCard,
    DeselectHandCard,
    ConfirmHandSelection,
    DebugApplyGodMode,
    DebugSetSeed,
    DebugStartRun,
    DebugAdvance,
    DebugPlayCombat,
    DebugTravel,
    DebugResolveEvent,
    DebugOpenShop,
    DebugUsePotions,
    DebugSpeedFast,
    // Appended at the end on purpose: the wire `kind` code equals the enum ordinal, so inserting in
    // the middle would renumber every later action. open-shop is the real (non-debug) human-equivalent
    // of clicking the merchant — it calls NMerchantRoom.OpenInventory() so the browser can shop without
    // the debug-open-shop crutch.
    OpenShop,
    // proceed-merchant-room is the human-equivalent of releasing the merchant room's Proceed button
    // (NMerchantRoom.HideScreen -> NMapScreen.Open()): leaving the shop inventory only closes the buy
    // panel, the ROOM is left via its proceed button, which opens the travel map. Appended at the end.
    ProceedMerchantRoom,
    // Map drawing (quill/eraser): write the LOCAL player's NMapDrawings strokes. Appended at the end on
    // purpose — the wire `kind` code equals the enum ordinal, so new kinds must stay last.
    DrawMapStroke,
    ClearMapDrawings,
    // Element-addressed pointer hover + keyboard input replay for browser-driven play (single controller).
    // Element clicks reuse MouseClick (it resolves ElementId → coordinate). Appended at the end on purpose —
    // the wire `kind` code equals the enum ordinal, so new kinds must stay last.
    HoverElement,
    KeyInput,
    // Host-side ENet peer eviction: force-disconnect a remote client by its netId so the host's net
    // server drops the dead peer and frees the netId for reuse (a SIGKILL'd headless leaves its peer
    // registered, and the next headless reusing that netId fails its ENet join). Appended at the end on
    // purpose — the wire `kind` code equals the enum ordinal, so new kinds must stay last.
    DisconnectClient,
    // Host-side display-name override for a real networked client by its netId (e.g. a couch-coop headless
    // ENet peer joining as 1002 should show the browser-chosen name instead of the raw netId). Registers a
    // name override consulted by the PlatformUtil.GetPlayerNameRaw hook + State name resolver, and refreshes
    // the live lobby nameplate. An empty DisplayName clears the override. Does NOT create a player or mark a
    // host-local seat. Appended at the end on purpose — the wire `kind` code equals the enum ordinal.
    SetClientName,
    // Dev-only host-side heal (`sts2 dev heal`): directly heals (or, per the game's
    // Heal/SetCurrentHp semantics, revives) ANY creature — player, ally, or enemy — via
    // CreatureCmd on the main thread. NOT a legal player action and never advertised in
    // availableActions; a live-rescue devtool. Host-direct mutation can desync real remote ENet
    // clients (couch co-op's one-real-player norm is fine). Targeting: TargetId `creature:<combatId>`
    // (same ids `sts2 state` shows); empty = the acting seat's player creature. Values: "amount"
    // (positive int) or "full" ("true" = set current HP to max). Appended at the end on purpose —
    // the wire `kind` code equals the enum ordinal, so new kinds must stay last.
    Heal,
    // ABSOLUTE SCROLL. Park a scrollable surface at a named container-local offset instead of asking for it in
    // quantised wheel ticks or a stream of drag deltas: `elementId` is the scroll container's live node instance
    // id (the same addressing select-map-node/hover-element use) and `values["offsetY"]` is the wanted Y. The
    // host clamps to the surface's own limits and reports the CLAMPED value back through the result's Values, so
    // a leading client can reconcile when the game refuses the full travel. Appended at the end on purpose — the
    // wire `kind` code equals the enum ordinal, so new kinds must stay last.
    SetScrollOffset,
    // ABSTRACT CONTROLLER INPUT. Replays one of the game's own controller inputs as an `InputEventAction`, which
    // is the mechanism the game's controller strategies already feed the input bus with. `controllerInput` is a
    // device-neutral browser token (faceSouth, dpadUp, leftBumper, …) that Sts2BrowserPadMap resolves to the
    // action name THIS game build registers, and `keyPressed` carries the edge (true=down, false=up, null=a full
    // press+release) — reused rather than adding a second "pressed" field, since a request is one or the other.
    // Deliberately bypasses the InputMap: the game's UI consumes these by action NAME, so an embedder that has
    // stripped a seat's joypad BINDINGS (couch-coop's headless seats) keeps that isolation while its browser
    // client can still drive the seat's controller mode. Appended at the end on purpose — the wire `kind` code
    // equals the enum ordinal, so new kinds must stay last.
    ControllerInput,
}

public enum RawMouseButtonKind
{
    Left,
    Right,
    Middle,
    // Appended (wire codes = ordinals): mouse-wheel scroll ticks, injected as Godot WheelUp/WheelDown buttons.
    WheelUp,
    WheelDown,
}

public enum ActionImplementationStatus
{
    Implemented,
    Scaffolded,
}

public enum ActionFailureCode
{
    NotImplemented,
    InvalidAction,
    BridgeNotAttached,
    RuntimeFailure,
    WrongScreen,
    WrongPlayer,
    NotVisible,
    NotEnabled,
    MissingHook,
    AmbiguousHook,
    StaleId,
    UnsupportedPerspective,
    DangerousModeRequired,
}

public enum ActionArgumentValueKind
{
    Unspecified,
    String,
    StableId,
    PlayerId,
    CardId,
    PotionId,
    TargetId,
    ChoiceId,
    MapNodeId,
    CharacterId,
    RewardId,
    BundleId,
    ShopItemId,
    RelicId,
    RestOptionId,
    EventOptionId,
    ControlId,
    Int32,
    Enum,
    Bool,
}

public enum ActionPerspectiveBehaviorKind
{
    Unspecified,
    LocalOnly,
    OwnerOnly,
    OmniscientAllowed,
    Shared,
    DangerousViewport,
}

public enum ActionLegalityKind
{
    Unspecified,
    Legal,
    Illegal,
    Provisional,
    Unknown,
}

public sealed record ActionDescriptorSnapshot(
    string Id,
    SemanticActionKind Kind,
    string Summary,
    string CliCommandHint,
    bool Provisional,
    ActionImplementationStatus Status,
    IReadOnlyList<ActionParameterDescriptorSnapshot> Parameters,
    ActionKindDescriptorSnapshot? KindDescriptor = null,
    string? OwnerPlayerId = null,
    ActionPerspectiveBehaviorKind PerspectiveBehavior = ActionPerspectiveBehaviorKind.Unspecified,
    ActionLegalityKind LegalityStatus = ActionLegalityKind.Unspecified,
    IReadOnlyList<string>? CheckedHookPaths = null,
    IReadOnlyList<ActionArgumentSchemaSnapshot>? ArgumentSchema = null,
    IReadOnlyList<ActionFailureCode>? FailureReasonCodes = null,
    RemoteClientOrchestrationCapabilitySnapshot? RemoteOrchestration = null,
    MultiplayerRoleSnapshot OwnerRole = MultiplayerRoleSnapshot.Unspecified);

public sealed record ActionKindDescriptorSnapshot(
    SemanticActionKind Kind,
    string? IntentKind = null,
    string? CommandName = null,
    string? Summary = null,
    bool Fallback = false,
    bool Dangerous = false,
    IReadOnlyList<string>? Modes = null,
    IReadOnlyList<string>? ScreenTypes = null);

public sealed record ActionArgumentSchemaSnapshot(
    string Name,
    ActionArgumentValueKind ValueType,
    bool Required,
    string Summary,
    IReadOnlyList<string>? AllowedValues = null,
    bool StableId = false);

public sealed record ActionParameterDescriptorSnapshot(
    string Name,
    string ValueType,
    bool Required,
    string Summary);

public sealed record ActionFailureDetail(
    string Field,
    string Value,
    string Note,
    ActionFailureCode? ReasonCode = null,
    string? Screen = null,
    string? PlayerId = null,
    string? Perspective = null,
    IReadOnlyList<string>? CheckedHookPaths = null,
    IReadOnlyList<ActionFailureFieldDiagnostic>? FieldDiagnostics = null,
    string? RequestedPlayerId = null,
    string? ResolvedOwnerPlayerId = null,
    string? LocalPlayerId = null,
    string? HostPlayerId = null,
    MultiplayerRoleSnapshot LocalRole = MultiplayerRoleSnapshot.Unspecified,
    string? Action = null,
    RemoteClientOrchestrationCapabilitySnapshot? RemoteOrchestration = null);

public sealed record ActionFailureFieldDiagnostic(
    string Field,
    string Value,
    string Note);

public sealed record ActionFailure(
    ActionFailureCode Code,
    string Message,
    IReadOnlyList<ActionFailureDetail> Details,
    string? Screen = null,
    string? PlayerId = null,
    string? Perspective = null,
    IReadOnlyList<string>? CheckedHookPaths = null,
    IReadOnlyList<ActionFailureFieldDiagnostic>? FieldDiagnostics = null,
    string? RequestedPlayerId = null,
    string? ResolvedOwnerPlayerId = null,
    string? LocalPlayerId = null,
    string? HostPlayerId = null,
    MultiplayerRoleSnapshot LocalRole = MultiplayerRoleSnapshot.Unspecified,
    string? Action = null,
    RemoteClientOrchestrationCapabilitySnapshot? RemoteOrchestration = null);

public sealed record SemanticActionRequest(
    string RequestId,
    SemanticActionKind Kind,
    string? CardId,
    string? PotionId,
    string? TargetId,
    string? ChoiceId,
    string? CharacterId,
    string? MapNodeId,
    int? MouseX,
    int? MouseY,
    RawMouseButtonKind? MouseButton,
    PerspectiveSelection? Perspective,
    string? DisplayName = null,
    IReadOnlyDictionary<string, string>? Values = null,
    // Card ids to stage before confirming, for actions (confirm-hand-selection)
    // that fold staging + confirm into one call when the client kept selection
    // local. Null/empty means "confirm whatever is already staged".
    IReadOnlyList<string>? CardIds = null,
    // Map-draw stroke (draw-map-stroke): the polyline points in TheMap content space + the eraser flag.
    bool? IsEraser = null,
    IReadOnlyList<(double X, double Y)>? StrokePoints = null,
    // Element-addressed pointer input (hover-element / mouse-click): the target node's stable instance id and
    // an optional normalized 0..1 offset within its rect. The host resolves the id to the live node's global
    // rect and injects at rect.Position + offset*rect.Size (center when offset is null). Lets the browser drive
    // input by element identity, robust to client-side CSS scaling / non-16:9 viewports.
    string? ElementId = null,
    double? OffsetX = null,
    double? OffsetY = null,
    // Keyboard input (key-input): a browser KeyboardEvent.code (e.g. "KeyE", "Digit1", "Escape"), an optional
    // comma-separated modifier list ("ctrl,shift,alt,meta"), and whether this is a press (true), release
    // (false), or a full press+release (null).
    string? Key = null,
    string? KeyModifiers = null,
    bool? KeyPressed = null,
    // Mouse press state for mouse-click: true=button DOWN only (hold, starts a drag), false=button UP only
    // (release), null=a full click (down+up). While a button is held, the hover/move stream is injected as
    // drag-motion (the button mask is applied to the motion events).
    bool? MousePressed = null,
    // Abstract controller input (controller-input): a device-neutral browser pad token ("faceSouth", "dpadUp",
    // "leftBumper", …) that Sts2BrowserPadMap resolves to the action name this game build registers. The EDGE
    // rides on KeyPressed above rather than a field of its own (true=down, false=up, null=a full press+release):
    // a request is a key OR a pad input, never both, so a second "pressed" field would only create a way for the
    // two to disagree.
    string? ControllerInput = null);

public sealed record ActionExecutionResult(
    bool Accepted,
    string ActionInstanceId,
    SemanticActionKind Kind,
    string Message,
    bool Provisional,
    ActionFailure? Error,
    // What the action RESOLVED TO, for the handful of kinds whose caller needs the answer and not just "accepted".
    // TRAILING + DEFAULTED so every existing construction site and every existing consumer is untouched, and a
    // scalar string dictionary so it serialises through the embedding/browser envelopes with no schema of its own.
    // Today's only producer is set-scroll-offset, which reports the offset the game's own limits CLAMPED the
    // request to (see Sts2ScrollOffsetMath) — the fact a client leading the scroll locally cannot derive.
    IReadOnlyDictionary<string, string>? Values = null)
{
    public static ActionExecutionResult Success(
        string actionInstanceId,
        SemanticActionKind kind,
        string message,
        bool provisional = false,
        IReadOnlyDictionary<string, string>? values = null)
    {
        return new ActionExecutionResult(
            Accepted: true,
            ActionInstanceId: actionInstanceId,
            Kind: kind,
            Message: message,
            Provisional: provisional,
            Error: null,
            Values: values);
    }

    public static ActionExecutionResult Failure(
        SemanticActionKind kind,
        ActionFailureCode code,
        string message,
        IReadOnlyList<ActionFailureDetail>? details = null,
        string? actionInstanceId = null,
        string? screen = null,
        string? playerId = null,
        string? perspective = null,
        IReadOnlyList<string>? checkedHookPaths = null,
        IReadOnlyList<ActionFailureFieldDiagnostic>? fieldDiagnostics = null,
        string? requestedPlayerId = null,
        string? resolvedOwnerPlayerId = null,
        string? localPlayerId = null,
        string? hostPlayerId = null,
        MultiplayerRoleSnapshot localRole = MultiplayerRoleSnapshot.Unspecified,
        string? action = null,
        RemoteClientOrchestrationCapabilitySnapshot? remoteOrchestration = null)
    {
        return new ActionExecutionResult(
            Accepted: false,
            ActionInstanceId: actionInstanceId ?? $"action:{kind.ToString().ToLowerInvariant()}",
            Kind: kind,
            Message: message,
            Provisional: code == ActionFailureCode.NotImplemented,
            Error: new ActionFailure(
                Code: code,
                Message: message,
                Details: details ?? [],
                Screen: screen,
                PlayerId: playerId,
                Perspective: perspective,
                CheckedHookPaths: checkedHookPaths,
                FieldDiagnostics: fieldDiagnostics,
                RequestedPlayerId: requestedPlayerId,
                ResolvedOwnerPlayerId: resolvedOwnerPlayerId,
                LocalPlayerId: localPlayerId,
                HostPlayerId: hostPlayerId,
                LocalRole: localRole,
                Action: action,
                RemoteOrchestration: remoteOrchestration));
    }
}
