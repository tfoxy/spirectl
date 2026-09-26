using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Core.State;

public sealed record GameStateQuery(
    PerspectiveSelection? RequestedPerspective,
    bool IncludeDebug,
    IReadOnlySet<string>? IncludeSections = null,
    TimeSpan? Timeout = null);

public sealed record AvailableActionSnapshot(
    string Id,
    SemanticActionKind Kind,
    string Summary,
    string CliCommandHint,
    bool Provisional,
    ActionArgumentsSnapshot? Arguments,
    string? IntentKind = null,
    string? OwnerPlayerId = null,
    string? Perspective = null,
    string? PreferredAction = null,
    ActionLegalityKind LegalityStatus = ActionLegalityKind.Unspecified,
    string? DisabledReason = null,
    IReadOnlyList<string>? CheckedHookPaths = null,
    VisibleActionReferenceSnapshot? PreferredActionRef = null,
    RemoteClientOrchestrationCapabilitySnapshot? RemoteOrchestration = null);

public sealed record VisibleControlStateSnapshot(
    string Id,
    string Label,
    bool Enabled,
    string? OwnerPlayerId = null,
    string? Description = null,
    string? ChoiceKind = null,
    string? IntentKind = null,
    string? Perspective = null,
    ActionArgumentsSnapshot? Arguments = null,
    string? DisabledReason = null,
    VisibleActionReferenceSnapshot? PreferredAction = null,
    string? PlayerId = null,
    bool IsLocal = false,
    bool IsHost = false,
    bool IsRemote = false,
    bool IsHostLocalSeat = false,
    string? HostPlayerId = null,
    RemoteClientOrchestrationCapabilitySnapshot? RemoteOrchestration = null);

public sealed record VisibleItemStateSnapshot(
    string Id,
    string Label,
    string? Description = null,
    string? OwnerPlayerId = null,
    bool Selected = false,
    bool Provisional = false,
    string? ChoiceKind = null,
    string? IntentKind = null,
    string? Perspective = null,
    ActionArgumentsSnapshot? Arguments = null,
    bool Enabled = true,
    string? DisabledReason = null,
    VisibleActionReferenceSnapshot? PreferredAction = null,
    string? PlayerId = null,
    bool IsLocal = false,
    bool IsHost = false,
    bool IsRemote = false,
    bool IsHostLocalSeat = false,
    string? HostPlayerId = null,
    RemoteClientOrchestrationCapabilitySnapshot? RemoteOrchestration = null);

public sealed record MapStateSnapshot(IReadOnlyList<VisibleItemStateSnapshot> Nodes);
public sealed record EventRoomStateSnapshot(
    IReadOnlyList<VisibleItemStateSnapshot> Options,
    EventRoomPageStateSnapshot? Page = null);
public sealed record TreasureRoomStateSnapshot(IReadOnlyList<VisibleItemStateSnapshot> Relics);
public sealed record RelicSelectionStateSnapshot(IReadOnlyList<VisibleItemStateSnapshot> Relics);
public sealed record RestSiteStateSnapshot(IReadOnlyList<VisibleControlStateSnapshot> Controls);
public sealed record ShopStateSnapshot(IReadOnlyList<VisibleItemStateSnapshot> PurchasableItems);
public sealed record RewardsStateSnapshot(IReadOnlyList<VisibleItemStateSnapshot> Rewards);
public sealed record CardSelectionStateSnapshot(IReadOnlyList<VisibleItemStateSnapshot> Cards);
public sealed record SimpleCardSelectionStateSnapshot(IReadOnlyList<VisibleItemStateSnapshot> Choices);
public sealed record DeckCardSelectionStateSnapshot(IReadOnlyList<VisibleItemStateSnapshot> DeckCards);
public sealed record BundleSelectionStateSnapshot(IReadOnlyList<VisibleItemStateSnapshot> Bundles);
public sealed record MultiplayerLobbyStateSnapshot(
    IReadOnlyList<VisibleItemStateSnapshot> Players,
    IReadOnlyList<VisibleActionReferenceSnapshot> Actions,
    string? LocalPlayerId = null,
    string? HostPlayerId = null,
    string? Perspective = null,
    bool IsLocal = false,
    bool IsHost = false,
    bool IsRemote = false,
    RemoteClientOrchestrationCapabilitySnapshot? RemoteOrchestration = null);
public sealed record OverlayBreadcrumbSnapshot(
    string ScreenType,
    string? Title = null,
    string? ScreenInstanceId = null,
    string? Source = null,
    string? RawType = null,
    string? ClassName = null,
    string? OwnerPlayerId = null,
    string? Perspective = null);
public sealed record OverlayAffordanceSnapshot(
    string Id,
    string Label,
    bool Enabled,
    string? OwnerPlayerId = null,
    string? ChoiceKind = null,
    string? IntentKind = null,
    string? PreferredAction = null,
    string? Perspective = null,
    bool Provisional = false,
    string? Description = null,
    ActionArgumentsSnapshot? Arguments = null,
    string? DisabledReason = null,
    VisibleActionReferenceSnapshot? PreferredActionRef = null);
public sealed record CardOverlayStateSnapshot(
    IReadOnlyList<CardStateSnapshot> Cards,
    string? PreviewText = null,
    IReadOnlyList<OverlayBreadcrumbSnapshot>? Breadcrumbs = null,
    bool Blocking = false,
    bool Passive = false,
    string? OverlayPolicy = null,
    string? OwnerPlayerId = null,
    string? Perspective = null,
    OverlayAffordanceSnapshot? Close = null,
    OverlayAffordanceSnapshot? Back = null,
    IReadOnlyList<OverlayAffordanceSnapshot>? FollowThroughControls = null,
    bool Provisional = false);

public sealed record MenuStateSnapshot(
    string MenuId,
    string Title);

public sealed record EnemyIntentSnapshot(
    string Type,
    int Damage,
    int Hits,
    int TotalDamage,
    string? Label = null,
    string? Description = null,
    IReadOnlyList<string>? TargetIds = null,
    IReadOnlyList<AssetReferenceSnapshot>? AssetRefs = null);

public sealed record EnemyVisualMetadataSnapshot(
    string? AssetKey,
    string? EncounterSlotId,
    string? VariantId,
    string? ScreenSide,
    string? EncounterVisualPackageId,
    IReadOnlyList<AssetReferenceSnapshot>? AssetRefs = null);

public sealed record EnemyStateSnapshot(
    string Id,
    string Name,
    int Hp,
    string Intent,
    int MaxHp,
    int Block,
    bool IsAlive,
    IReadOnlyList<EnemyIntentSnapshot> Intents,
    string? RuntimeEntityId = null,
    string? ModelId = null,
    IReadOnlyList<StatusEffectStateSnapshot>? StatusEffects = null,
    IReadOnlyList<AssetReferenceSnapshot>? AssetRefs = null,
    EnemyVisualMetadataSnapshot? Visual = null);

public sealed record CombatPlayerStateSnapshot(
    string Id,
    string Character,
    int Hp,
    int MaxHp,
    int Block,
    int Energy,
    int MaxEnergy,
    IReadOnlyList<CardStateSnapshot> Hand,
    IReadOnlyList<PotionStateSnapshot>? Potions = null,
    bool IsLocal = false,
    bool IsHost = false,
    bool IsRemote = false,
    bool IsHostLocalSeat = false,
    IReadOnlyList<string>? DrawPileCardIds = null,
    IReadOnlyList<string>? DiscardPileCardIds = null,
    IReadOnlyList<string>? ExhaustPileCardIds = null,
    CardPileStateSnapshot? DrawPile = null,
    CardPileStateSnapshot? DiscardPile = null,
    CardPileStateSnapshot? ExhaustPile = null,
    int Gold = 0,
    IReadOnlyList<RelicStateSnapshot>? Relics = null,
    IReadOnlyList<StatusEffectStateSnapshot>? StatusEffects = null,
    bool CanRemovePotions = true);

public sealed record EncounterVisualsStateSnapshot(
    string PackageId,
    string EncounterId,
    bool Provisional,
    IReadOnlyList<EncounterVisualPartStateSnapshot> VisualParts,
    IReadOnlyList<EncounterVisualTransitionEventSnapshot> RecentEvents,
    IReadOnlyList<StateNoticeSnapshot>? Notices = null);

public sealed record EncounterVisualPartStateSnapshot(
    string PartId,
    string ActorId,
    string ActiveStateId,
    string ScreenSide,
    string AnatomicalSide);

public sealed record EncounterVisualTransitionEventSnapshot(
    ulong Sequence,
    string PackageId,
    string TransitionId,
    IReadOnlyList<string> AffectedPartIds,
    string ActiveStateId,
    string SourceHook);

public sealed record CombatStateSnapshot(
    int Turn,
    string? ActivePlayerId,
    bool IsPlayerTurn,
    IReadOnlyList<CardStateSnapshot> Hand,
    IReadOnlyList<CombatPlayerStateSnapshot> Players,
    IReadOnlyList<EnemyStateSnapshot> Enemies,
    IReadOnlyDictionary<string, CombatPlayerStateSnapshot> PlayersById,
    IReadOnlyList<PotionStateSnapshot>? Potions = null,
    IReadOnlyList<string>? DrawPileCardIds = null,
    IReadOnlyList<string>? DiscardPileCardIds = null,
    IReadOnlyList<string>? ExhaustPileCardIds = null,
    string? EncounterId = null,
    string? EncounterLabel = null,
    CardPileStateSnapshot? DrawPile = null,
    CardPileStateSnapshot? DiscardPile = null,
    CardPileStateSnapshot? ExhaustPile = null,
    EncounterVisualsStateSnapshot? EncounterVisuals = null,
    StateSelectedPotionSnapshot? SelectedPotion = null);

public sealed record DebugStateSnapshot(
    IReadOnlyList<string> Notes);

public sealed record GameStateSnapshot(
    string SchemaVersion,
    string GameVersion,
    string BridgeVersion,
    DataSourceKind Source,
    bool Provisional,
    string ScreenType,
    string ScreenTitle,
    string ScreenInstanceId,
    PlayerPerspective ResolvedPerspective,
    MenuStateSnapshot? Menu,
    LobbyStateSnapshot? Lobby,
    RunStateSnapshot? Run,
    CombatStateSnapshot? Combat,
    MapStateSnapshot? Map,
    EventRoomStateSnapshot? EventRoom,
    TreasureRoomStateSnapshot? TreasureRoom,
    RelicSelectionStateSnapshot? RelicSelection,
    RestSiteStateSnapshot? RestSite,
    ShopStateSnapshot? Shop,
    RewardsStateSnapshot? Rewards,
    CardSelectionStateSnapshot? CardSelection,
    SimpleCardSelectionStateSnapshot? SimpleCardSelection,
    DeckCardSelectionStateSnapshot? DeckCardSelection,
    BundleSelectionStateSnapshot? BundleSelection,
    MultiplayerLobbyStateSnapshot? MultiplayerLobby,
    IReadOnlyList<ChoiceSnapshot> Choices,
    IReadOnlyList<AvailableActionSnapshot> AvailableActions,
    IReadOnlyList<StateNoticeSnapshot> Notices,
    DebugStateSnapshot? Debug,
    string? ScreenSource = null,
    string? ScreenRawType = null,
    string? ScreenClassName = null,
    CardOverlayStateSnapshot? CardOverlay = null,
    string? PlayerId = null,
    bool IsLocal = false,
    bool IsHost = false,
    bool IsRemote = false,
    string? HostPlayerId = null,
    string? Perspective = null,
    RemoteClientOrchestrationCapabilitySnapshot? RemoteOrchestration = null,
    string? Language = null)
{
    public GameStateSnapshot(
        string SchemaVersion,
        string GameVersion,
        string BridgeVersion,
        DataSourceKind Source,
        bool Provisional,
        string ScreenType,
        string ScreenTitle,
        string ScreenInstanceId,
        PlayerPerspective ResolvedPerspective,
        MenuStateSnapshot? Menu,
        LobbyStateSnapshot? Lobby,
        RunStateSnapshot? Run,
        CombatStateSnapshot? Combat,
        IReadOnlyList<ChoiceSnapshot> Choices,
        IReadOnlyList<AvailableActionSnapshot> AvailableActions,
        IReadOnlyList<StateNoticeSnapshot> Notices,
        DebugStateSnapshot? Debug,
        string? ScreenSource = null,
        string? ScreenRawType = null,
        string? ScreenClassName = null,
        string? Language = null)
        : this(
            SchemaVersion,
            GameVersion,
            BridgeVersion,
            Source,
            Provisional,
            ScreenType,
            ScreenTitle,
            ScreenInstanceId,
            ResolvedPerspective,
            Menu,
            Lobby,
            Run,
            Combat,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            Choices,
            AvailableActions,
            Notices,
            Debug,
            ScreenSource,
            ScreenRawType,
            ScreenClassName,
            null,
            null,
            false,
            false,
            false,
            null,
            null,
            null,
            Language)
    {
    }
}
