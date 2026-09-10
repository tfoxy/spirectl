using Spirectl.Sts2.Core.Models;
using Spirectl.Sts2.Core.Perspective;

namespace Spirectl.Sts2.Core.State;

public interface IStateProvider
{
    StateSnapshot Observe(PlayerPerspective perspective);
}

public sealed record StateSnapshot(
    string SchemaVersion,
    string? Language,
    string RootScene,
    StateCharacterSelectSnapshot? CharacterSelect,
    StateRunSnapshot? Run)
{
    public const string CurrentSchemaVersion = "spirectl.state/v0";
}

public sealed record StateCharacterSelectSnapshot(
    StateCharacterSelectLobbySnapshot Lobby,
    IReadOnlyList<StateCharacterButtonSnapshot> CharacterButtons,
    StateCharacterSelectViewSnapshot? View);

public sealed record StateCharacterSelectViewSnapshot(
    string? PlayerId,
    string? SelectedCharacterButtonId);

/// <summary>
/// Stock-game constants a lobby snapshot falls back to when the live value cannot be read. Separate from the
/// record because a record's own members are not in scope for its primary-constructor default values.
/// </summary>
public static class StateCharacterSelectLobbyDefaults
{
    /// <summary>The stock game's lobby player cap (<c>StartRunLobby.MaxPlayers</c>).</summary>
    public const int MaxPlayers = 4;
}

public sealed record StateCharacterSelectLobbySnapshot(
    string NetGameType,
    string? LocalPlayerId,
    string? HostPlayerId,
    uint ConnectingPlayerCount,
    int Ascension,
    int MaxAscension,
    string? Act1,
    string? Seed,
    IReadOnlyList<string> ModifierIds,
    IReadOnlyList<StateCharacterSelectPlayerSnapshot> Players,
    StateCharacterSelectSavedRunSnapshot? SavedRun = null,
    // The lobby's own player cap (StartRunLobby.MaxPlayers). 4 in the stock game, but the multiplayer
    // limit mods raise it — "Multiplayer Limit Break" writes 16 straight onto the lobby, "Unlimited"
    // overrides the maxPlayers argument InitializeMultiplayerAsHost builds it with — so a host that
    // sizes anything per player (seats, transports, layouts) must ASK rather than assume 4.
    // Trails the record with a stock default so every existing construction site stays valid.
    int MaxPlayers = StateCharacterSelectLobbyDefaults.MaxPlayers);

public sealed record StateCharacterSelectPlayerSnapshot(
    string Id,
    int SlotId,
    string? CharacterId,
    bool IsReady,
    int MaxMultiplayerAscensionUnlocked,
    string? DisplayName,
    bool IsConnected = true);

// Saved-run summary for the load-run lobby. Mirrors MegaCrit.Sts2.Core.Saves.SerializableRun;
// null for start-run lobbies (no save being resumed).
public sealed record StateCharacterSelectSavedRunSnapshot(
    int CurrentActIndex,
    int ActFloor,
    IReadOnlyList<StateCharacterSelectSavedRunPlayerSnapshot> Players);

// Mirrors MegaCrit.Sts2.Core.Saves.Runs.SerializablePlayer.
public sealed record StateCharacterSelectSavedRunPlayerSnapshot(
    string Id,
    int CurrentHp,
    int MaxHp,
    int Gold);

public sealed record StateCharacterButtonSnapshot(
    string Id,
    string CharacterId,
    bool IsLocked);

public sealed record StateRunSnapshot(
    string SourceType,
    string ManagerSourceType,
    string NetGameType,
    string GameMode,
    string Seed,
    int AscensionLevel,
    string? ActId,
    int CurrentActIndex,
    int ActFloor,
    int TotalFloor,
    string? BossEncounterId,
    string? SecondBossEncounterId,
    StateMapCoordSnapshot? CurrentMapCoord,
    string? CurrentMapPointId,
    IReadOnlyList<StateMapCoordSnapshot> VisitedMapCoords,
    IReadOnlyList<StateRunPlayerSnapshot> Players,
    StateRunMapSnapshot? Map,
    StateRunCurrentRoomSnapshot? CurrentRoom,
    IReadOnlyList<StateNoticeSnapshot> Notices,
    StateRunViewSnapshot? View = null);

public sealed record StateLocRefSnapshot(string Table, string Key);

public sealed record StateRunViewSnapshot(
    string? PlayerId,
    StateRunCapstoneViewSnapshot? Capstone,
    StateSelectedPotionSnapshot? SelectedPotion = null,
    bool IsInCardSelection = false,
    StateSelectedCardSnapshot? SelectedCard = null,
    StateInspectRelicViewSnapshot? InspectRelic = null,
    StateHandSelectionViewSnapshot? HandSelection = null,
    StateGameOverViewSnapshot? GameOver = null);

// In-hand card selection mode (NPlayerHand SimpleSelect/UpgradeSelect), e.g.
// Survivor's "Discard 1 card." prompt. Local-client view state; card ids match
// run.players[].combat.hand.cards (selected cards stay in the hand pile while
// staged — only their UI holders move).
public sealed record StateHandSelectionViewSnapshot(
    string? PlayerId,
    string Mode,
    string PromptText,
    StateLocRefSnapshot? PromptLoc,
    int MinSelect,
    int MaxSelect,
    bool RequireManualConfirmation,
    bool CanConfirm,
    IReadOnlyList<string> SelectedCardIds,
    IReadOnlyList<string> SelectableCardIds,
    string? SourceCardId,
    string? SourceModelId,
    bool IsPeeking,
    StateCardSnapshot? PreviewCard = null);

// The relic-details overlay (NInspectRelicScreen) when open. It lives outside
// the capstone stack (NGame parents it under InspectionContainer), so it is its
// own local-client view field. Index/Count describe the browsed relic list and
// drive the screen's prev/next arrows.
public sealed record StateInspectRelicViewSnapshot(
    string RelicModelId,
    int Index,
    int Count);

// The run-terminal game-over / defeat overlay (NGameOverScreen) when open. Pushed
// onto NOverlayStack (capstone closed) at end of run; an IOverlayScreen, so it is a
// local-client view field like inspect_relic. BannerText/DeathQuote carry the LIVE
// resolved node text (both RNG-picked at runtime). Score/KilledByEncounterId are
// data-only (the score renders in the deferred post-Continue summary panel).
public sealed record StateGameOverViewSnapshot(
    bool Win,
    string BannerText,
    string DeathQuote,
    int Score,
    string? KilledByEncounterId);

public sealed record StateSelectedPotionSnapshot(
    string Mode,
    int SlotIndex);

public sealed record StateSelectedCardSnapshot(
    string CardId,
    string PlayerId);

public sealed record StateRunCapstoneViewSnapshot(
    string Scene,
    string SourceType,
    IReadOnlyList<StateRunStackEntrySnapshot> Stack,
    StateDeckViewSnapshot? DeckView = null,
    StateCardPileViewSnapshot? CardPileView = null);

public sealed record StateCardPileViewSnapshot(
    string PileType,
    string? PlayerId);

public sealed record StateRunStackEntrySnapshot(
    string? Scene,
    string SourceType);

public sealed record StateDeckViewSnapshot(
    IReadOnlyList<StateDeckViewSortSnapshot> Sort,
    bool ShowUpgrades);

public sealed record StateDeckViewSortSnapshot(
    string By,
    string Direction);

public sealed record StateRunCurrentRoomSnapshot(
    string SourceType,
    string RoomType,
    string Scene,
    string? ModelId,
    StateRunEventRoomSnapshot? Event,
    StateRunCombatRoomSnapshot? Combat = null,
    StateRunTreasureRoomSnapshot? Treasure = null,
    StateRunShopRoomSnapshot? Shop = null,
    StateRunRestSiteRoomSnapshot? RestSite = null,
    StateRunMapRoomSnapshot? MapRoom = null,
    int? Id = null);

public sealed record StateRunCombatRoomSnapshot(
    string? EncounterId,
    string? ParentEventId,
    double GoldProportion,
    bool IsPreFinished,
    bool ShouldCreateCombat,
    bool ShouldResumeParentEventAfterCombat,
    StateCombatStateSnapshot? CombatState,
    IReadOnlyList<StateNoticeSnapshot> Notices,
    StateCombatBackgroundSnapshot? Background = null);

public sealed record StateCombatBackgroundSnapshot(
    string? ScenePath,
    IReadOnlyList<CombatBackgroundLayerSnapshot> Layers);

public sealed record StateRunEventRoomSnapshot(
    string? Scene,
    string? CanonicalEventModelId,
    string? CanonicalSourceType,
    bool IsPreFinished,
    bool IsShared,
    IReadOnlyList<StateRunEventPlayerStateSnapshot> PlayerStates,
    IReadOnlyList<StateRunEventSharedVoteSnapshot> SharedVotes,
    IReadOnlyList<StateNoticeSnapshot> Notices);

public sealed record StateRunEventPlayerStateSnapshot(
    string PlayerId,
    string? EventModelId,
    string? CanonicalEventModelId,
    string SourceType,
    string? OwnerPlayerId,
    string LayoutType,
    bool IsFinished,
    StateLocRefSnapshot? DescriptionLoc,
    IReadOnlyList<StateRunEventOptionSnapshot> Options,
    StateRunEventAncientSnapshot? Ancient);

public sealed record StateRunEventOptionSnapshot(
    string Id,
    uint Index,
    string? TextKey,
    StateLocRefSnapshot? TitleLoc,
    StateLocRefSnapshot? DescriptionLoc,
    string? TitleText,
    string? DescriptionText,
    bool IsLocked,
    bool IsProceed,
    bool WasChosen,
    string? RelicId,
    bool ShouldSaveChoiceToHistory,
    bool ShouldSaveVariablesToHistory,
    IReadOnlyList<ModelHoverTipSnapshot>? HoverTips = null,
    StateCardSnapshot? PreviewedCard = null);

public sealed record StateRunEventSharedVoteSnapshot(
    string PlayerId,
    bool HasOptionIndex,
    uint OptionIndex);

public sealed record StateRunEventAncientSnapshot(
    int HealedAmount,
    StateRunEventAncientViewSnapshot? View);

public sealed record StateRunEventAncientViewSnapshot(StateRunEventAncientVisibleDialogueSnapshot? VisibleDialogue);

public sealed record StateRunEventAncientVisibleDialogueSnapshot(
    string SourceType,
    string? DialogueId,
    int CurrentLineIndex,
    string? CurrentLineLocKey,
    IReadOnlyList<string> LineLocKeys);

public sealed record StateRunTreasureRoomSnapshot(
    bool CurrentRelicsActive,
    IReadOnlyList<StateTreasureRelicSnapshot> CurrentRelics,
    IReadOnlyList<StateTreasurePlayerVoteSnapshot> PlayerVotes,
    IReadOnlyList<StateNoticeSnapshot> Notices,
    bool CanProceed = false);

public sealed record StateTreasureRelicSnapshot(string Id, string ModelId);

public sealed record StateTreasurePlayerVoteSnapshot(string PlayerId, int? Index, bool VoteReceived);

public sealed record StateRunShopRoomSnapshot(
    StateShopInventorySnapshot? Inventory,
    IReadOnlyList<StateNoticeSnapshot> Notices,
    StateShopViewSnapshot? View = null);

public sealed record StateShopViewSnapshot(bool IsOpen);

public sealed record StateShopInventorySnapshot(
    string SourceType,
    string? PlayerId,
    IReadOnlyList<StateShopCardEntrySnapshot> CharacterCardEntries,
    IReadOnlyList<StateShopCardEntrySnapshot> ColorlessCardEntries,
    IReadOnlyList<StateShopRelicEntrySnapshot> RelicEntries,
    IReadOnlyList<StateShopPotionEntrySnapshot> PotionEntries,
    StateShopCardRemovalEntrySnapshot? CardRemovalEntry);

public sealed record StateShopCardEntrySnapshot(
    string Id,
    string SourceType,
    int? Cost,
    bool IsOnSale,
    StateCardSnapshot? Card);

public sealed record StateShopRelicEntrySnapshot(string Id, string SourceType, int? Cost, string? ModelId);

public sealed record StateShopPotionEntrySnapshot(string Id, string SourceType, int? Cost, string? ModelId);

public sealed record StateShopCardRemovalEntrySnapshot(string Id, string SourceType, int? Cost, bool Used);

public sealed record StateRunRestSiteRoomSnapshot(
    IReadOnlyList<StateRestSitePlayerStateSnapshot> PlayerStates,
    IReadOnlyList<StateNoticeSnapshot> Notices,
    StateRestSiteRoomViewSnapshot View);

// Machine-local-only rest-site UI state (the local seat), not tied to any player.
public sealed record StateRestSiteRoomViewSnapshot(bool CanProceed);

public sealed record StateRestSitePlayerStateSnapshot(
    string PlayerId,
    int? HoveredOptionIndex,
    int? ChosenOptionIndex,
    IReadOnlyList<StateRestSiteOptionSnapshot> Options);

public sealed record StateRestSiteOptionSnapshot(
    string Id,
    string SourceType,
    string OptionId,
    bool IsEnabled,
    int? SmithCount,
    int? LiftsLeft = null,
    string? DisabledReason = null);

public sealed record StateRunMapRoomSnapshot(IReadOnlyList<StateNoticeSnapshot> Notices);

public sealed record StateRunPlayerSnapshot(
    string Id,
    string SourceType,
    string? NetId,
    string? DisplayName,
    string CharacterId,
    bool IsLocal,
    bool IsHost,
    bool IsRemote,
    StateRunCreatureSnapshot? Creature,
    int Gold,
    StateCardPileSnapshot? Deck,
    IReadOnlyList<StateRelicSnapshot> Relics,
    bool InventoryComplete,
    IReadOnlyList<StateNoticeSnapshot> Notices,
    StateRunPlayerCombatSnapshot? Combat = null,
    IReadOnlyList<StateRunOverlaySnapshot>? Overlays = null,
    IReadOnlyList<StateCombatPotionSnapshot>? Potions = null,
    bool CanRemovePotions = true,
    // Whether this player's ENet peer is currently connected — the run-time counterpart of the lobby's
    // StateCharacterSelectPlayerSnapshot.IsConnected. Resolved from the host net service's live peer registry
    // (Sts2RunPlayerConnectivity). Defaults to TRUE and stays true whenever connectedness cannot be determined,
    // so a snapshot never reports a false "disconnected"; trailing + defaulted so existing construction sites
    // and fixtures are unchanged.
    bool IsConnected = true);

public sealed record StateRunOverlaySnapshot(
    string Id,
    string ScreenType,
    string ScreenId,
    string Scene,
    StateChooseACardOverlaySnapshot? ChooseACard,
    StateRewardsOverlaySnapshot? Rewards = null,
    StateDeckCardSelectionOverlaySnapshot? DeckCardSelection = null,
    StateSimpleGridCardSelectionOverlaySnapshot? SimpleGridCardSelection = null,
    StateBundleCardSelectionOverlaySnapshot? BundleCardSelection = null,
    StateCrystalSphereOverlaySnapshot? CrystalSphere = null);

public sealed record StateChooseACardOverlaySnapshot(
    bool CanSkip,
    IReadOnlyList<StateCardSnapshot> Cards);

public sealed record StateCrystalSphereOverlaySnapshot(
    int DivinationsRemaining,
    string SelectedTool,
    bool IsFinished,
    IReadOnlyList<StateCrystalSphereCellSnapshot> Cells,
    IReadOnlyList<StateCrystalSphereItemSnapshot>? RevealedItems = null);

public sealed record StateCrystalSphereCellSnapshot(
    string Id,
    int X,
    int Y,
    bool IsHidden,
    bool IsHighlighted,
    bool IsHovered,
    bool Enabled);

public sealed record StateCrystalSphereItemSnapshot(
    string Id,
    int X,
    int Y,
    int WidthCells,
    int HeightCells,
    string? IconAssetKey,
    bool ShowsCard,
    string? CardRarity = null,
    string? CardBannerMaterialKey = null,
    string? CardFrameMaterialKey = null);

public sealed record StateSimpleGridCardSelectionOverlaySnapshot(
    string PromptText,
    StateLocRefSnapshot? PromptLoc,
    int MinSelect,
    int MaxSelect,
    bool CanConfirm,
    bool CanCancel,
    bool PreviewActive,
    IReadOnlyList<string> SelectedCardIds,
    IReadOnlyList<StateCardSnapshot> Cards);

public sealed record StateBundleCardSelectionOverlaySnapshot(
    IReadOnlyList<StateCardBundleSnapshot> Bundles,
    bool CanConfirm,
    bool PreviewActive,
    string SelectedBundleId);

public sealed record StateCardBundleSnapshot(
    string Id,
    IReadOnlyList<StateCardSnapshot> Cards);

public sealed record StateDeckCardSelectionOverlaySnapshot(
    string Kind,
    bool CanSkip,
    bool CanConfirm,
    bool PreviewActive,
    IReadOnlyList<StateCardSnapshot> Cards,
    string PromptText = "",
    StateLocRefSnapshot? PromptLoc = null,
    int MinSelect = 0,
    int MaxSelect = 0,
    IReadOnlyList<string>? SelectedCardIds = null,
    bool CanCancel = false,
    string? EnchantmentTitle = null,
    string? EnchantmentDescription = null,
    string? EnchantmentIconPath = null,
    string? EnchantmentExtraCardText = null);

public sealed record StateRewardsOverlaySnapshot(
    StateRewardFlowSnapshot? Flow,
    IReadOnlyList<StateRewardItemSnapshot> Items,
    IReadOnlyList<StateNoticeSnapshot> Notices);

public sealed record StateRewardFlowSnapshot(string Mode, bool Enabled);

public sealed record StateRewardItemSnapshot(
    string Id,
    string SourceType,
    StateLocRefSnapshot? Description,
    int? Gold = null,
    string? Relic = null,
    string? Potion = null,
    StateCardSnapshot? Card = null,
    StateCardRewardSnapshot? CardReward = null,
    bool CardRemoval = false,
    IReadOnlyList<StateRewardItemSnapshot>? Linked = null,
    string? IconAssetKey = null,
    IReadOnlyDictionary<string, string>? DescriptionArgs = null);

public sealed record StateCardRewardSnapshot(
    IReadOnlyList<StateCardSnapshot> Cards,
    bool CanReroll,
    bool CanSkip);

public sealed record StateRunCreatureSnapshot(
    string SourceType,
    int CurrentHp,
    int MaxHp,
    string? Id = null,
    string? ModelId = null,
    string? Side = null,
    string? SlotName = null,
    int? Block = null,
    bool? IsHittable = null,
    IReadOnlyList<StateCombatPowerInstanceSnapshot>? PowerInstances = null,
    StateCombatNextMoveSnapshot? NextMove = null,
    StateCardSnapshot? PreviewedCard = null);

public sealed record StateCombatNextMoveSnapshot(
    string Id,
    IReadOnlyList<StateCombatIntentSnapshot> Intents);

public sealed record StateCombatIntentSnapshot(
    string Type,
    StateCombatAttackIntentSnapshot? Attack,
    int? CardCount = null);

public sealed record StateCombatAttackIntentSnapshot(
    int Damage,
    int Hits,
    int Repeats);

public sealed record StateCombatPotionSnapshot(
    int Id,
    string? ModelId,
    bool IsQueued,
    bool PassesUsabilityCheck);

public sealed record StateRunPlayerCombatSnapshot(
    string SourceType,
    int Energy,
    int MaxEnergy,
    int Stars,
    StateCombatCardPileSnapshot? Hand,
    StateCombatCardPileSnapshot? DrawPile,
    StateCombatCardPileSnapshot? DiscardPile,
    StateCombatCardPileSnapshot? ExhaustPile,
    StateCombatCardPileSnapshot? PlayPile,
    IReadOnlyList<StateRunCreatureSnapshot> PetCreatures,
    StateCombatOrbQueueSnapshot? OrbQueue,
    IReadOnlyList<StateNoticeSnapshot> Notices,
    bool HasEndedTurn = false);

public sealed record StateCombatCardPileSnapshot(
    string SourceType,
    string Id,
    string Type,
    IReadOnlyList<StateCombatCardSnapshot> Cards);

// A card's applied enchantment (CardModel.Enchantment). Display strings are game-resolved
// (DynamicDescription/DynamicExtraCardText already substitute amount + card vars + energy
// icons), mirroring the deck-enchant overlay's resolved-strings projection.
public sealed record StateCardEnchantmentSnapshot(
    string ModelId,
    string Title,
    string Description,
    string ExtraCardText,
    string IconPath,
    int DisplayAmount,
    bool ShowAmount,
    string Status,
    // EnchantmentModel.ExtraHoverTips (the "second tip": Replay/Weak/Eternal/Retain), resolved
    // like option/power tips. The renderer stacks these beside the enchantment's own tip.
    IReadOnlyList<ModelHoverTipSnapshot>? ExtraHoverTips = null,
    // Keyword IDs the enchantment adds to the card (card.Keywords - CanonicalKeywords), UPPER.
    IReadOnlyList<string>? AddedKeywords = null,
    // card.GetEnchantedReplayCount() — drives the gold "Rejugada {n}" card-body line; 0 = none.
    int ReplayCount = 0);

public sealed record StateCombatCardSnapshot(
    string Id,
    string ModelId,
    int UpgradeLevel,
    string? CurrentTargetCreatureId,
    bool ExhaustOnNextPlay,
    bool HasSingleTurnRetain,
    bool HasSingleTurnSly,
    bool ShouldRetainThisTurn,
    int EnergyCost,
    int StarCost,
    IReadOnlyList<string> UnplayableReason,
    bool ShouldGlowGold,
    bool ShouldGlowRed,
    string? AfflictionModelId = null,
    int AfflictionAmount = 0,
    StateCardEnchantmentSnapshot? Enchantment = null,
    // Host-computed card values (hand cards only; other piles leave these null). The game renders
    // its own description with sentinel slots where target-varying numbers go, so the browser never
    // re-implements the LocString pipeline or the damage-modifier rules.
    //
    // DescriptionTemplate: the pile-rendered body with `<damage>`/`<block>` tokens in place of the
    //   per-target numbers. Null when the card is not templatable (see RenderedByTarget).
    // DescriptionText: the verbatim no-target render (also the fallback when no slot was substituted).
    // Preview: the game-truth post-modifier matrix (per-target damage, block, self-damage) + the slot
    //   descriptors the frontend fills, keyed by the same creature ids state uses elsewhere.
    string? DescriptionTemplate = null,
    string? DescriptionText = null,
    StateCombatCardPreviewSnapshot? Preview = null);

// The game's own post-modifier numbers for one hand card (mirrors the ICombatPreviewProvider oracle,
// computed here for every player's hand). Block/SelfDamage are the no-target values; DamageByTarget is
// the per-hittable-enemy final damage keyed by creature id. Slots describe how DescriptionTemplate's
// tokens are filled. RenderedByTarget is the pathological-card fallback: a fully game-rendered body per
// target when the description could not be reduced to a template.
public sealed record StateCombatCardPreviewSnapshot(
    int Block,
    int SelfDamage,
    IReadOnlyDictionary<string, int> DamageByTarget,
    IReadOnlyList<StateCombatCardSlotSnapshot> Slots,
    IReadOnlyDictionary<string, string>? RenderedByTarget = null);

// One sentinel slot in DescriptionTemplate. Token is the literal placeholder (e.g. "<damage>"); Kind
// is "damage" | "block" | "selfDamage"; Baseline is the enchanted no-combat-modifier value (the game's
// own diff-highlight comparison basis); Inverse mirrors HighlightDifferencesInverse coloring (green for
// lower). The frontend substitutes the matrix value per hovered target and colors it against Baseline.
public sealed record StateCombatCardSlotSnapshot(
    string Token,
    string Kind,
    int Baseline,
    bool Inverse = false);

public sealed record StateCombatPowerInstanceSnapshot(
    string Id,
    string ModelId,
    string SourceType,
    int Amount,
    int DisplayAmount,
    int AmountOnTurnStart,
    string Type,
    string TypeForCurrentAmount,
    string StackType,
    bool IsVisible,
    bool SkipNextDurationTick,
    string? AmountLabelColor,
    string? OwnerCreatureId,
    string? TargetCreatureId,
    string? ApplierCreatureId,
    IReadOnlyList<ModelHoverTipSnapshot>? HoverTips = null,
    StateCardSnapshot? PreviewedCard = null);

public sealed record StateCombatOrbQueueSnapshot(
    string SourceType,
    int Capacity,
    IReadOnlyList<StateCombatOrbSnapshot> Orbs);

public sealed record StateCombatOrbSnapshot(
    string Id,
    string ModelId,
    double PassiveVal,
    double EvokeVal,
    string? OwnerPlayerId,
    bool HasBeenRemovedFromState);

public sealed record StateCombatStateSnapshot(
    string SourceType,
    string CurrentSide,
    int RoundNumber,
    IReadOnlyList<string> ModifierIds,
    IReadOnlyList<string> EscapedCreatureIds,
    IReadOnlyList<StateRunCreatureSnapshot> Enemies,
    bool PlayerActionsDisabled = false,
    IReadOnlyList<StateCombatTransientEffectSnapshot>? TransientEffects = null);

// Transient, time-bound combat VFX (floating damage numbers; cardUpgrade previews).
// Normally synthesized by a host from the combat-event stream; the bridge populates it only
// when a fixture authors it directly. Empty list / null when idle.
public sealed record StateCombatTransientEffectSnapshot(
    string Id,
    string AnchorCreatureId,
    string Kind,
    int Amount,
    long SpawnedAtMs,
    string? CardModelId = null,
    string? CardId = null,
    string? SourceRelicModelId = null,
    // Native VFX backbone: the res://-relative scene to mount for Kind == "vfx".
    string? ScenePath = null);

public sealed record StateCardPileSnapshot(
    string SourceType,
    string Id,
    string Type,
    int Count,
    IReadOnlyList<StateCardSnapshot> Cards,
    bool OrderObservable);

public sealed record StateCardSnapshot(
    string Id,
    string ModelId,
    int UpgradeLevel,
    IReadOnlyDictionary<string, int>? DynamicVars = null,
    IReadOnlyDictionary<string, int>? NextDynamicVars = null,
    string? AfflictionModelId = null,
    int AfflictionAmount = 0,
    StateCardEnchantmentSnapshot? Enchantment = null,
    // Modifier-applied energy cost (e.g. TEZCATARAS_EMBER -> 0); -1 = unknown (renderer falls
    // back to the model cost). Mirrors StateCombatCardSnapshot.EnergyCost for the deck viewer.
    int EnergyCost = -1);

public sealed record StateRelicSnapshot(
    string Id,
    string ModelId,
    int SlotIndex,
    bool HasCounter,
    int Counter);

public sealed record StateRunMapSnapshot(
    string SourceType,
    int RowCount,
    int ColumnCount,
    string? StartingMapPointId,
    string? BossMapPointId,
    string? SecondBossMapPointId,
    IReadOnlyList<StateMapPointHistoryActSnapshot> MapPointHistory,
    IReadOnlyList<StateMapPointSnapshot> Points,
    StateRunMapViewSnapshot? View = null,
    IReadOnlyList<StateMapVoteSnapshot>? Votes = null);

// IsAcceptingVotes: the host is currently presenting the map for selection (travel
// enabled). Browser seats may only vote when this is true; the bridge validates against it.
public sealed record StateRunMapViewSnapshot(bool IsOpen, bool IsAcceptingVotes = false);

// One per run player: which node (if any) that player has voted to travel to. Map travel
// is a run-global shared vote — all players must vote before the party moves.
public sealed record StateMapVoteSnapshot(string PlayerId, StateMapCoordSnapshot? Coord);

public sealed record StateMapCoordSnapshot(int Row, int Col);

public sealed record StateMapPointHistoryActSnapshot(
    IReadOnlyList<StateMapPointHistoryEntrySnapshot> Entries);

public sealed record StateMapPointHistoryEntrySnapshot(
    int Floor,
    StateMapCoordSnapshot? Coord,
    string MapPointType);

public sealed record StateMapPointSnapshot(
    string Id,
    string SourceType,
    StateMapCoordSnapshot Coord,
    string PointType,
    bool CanBeModified,
    IReadOnlyList<string> ParentIds,
    IReadOnlyList<string> ChildIds,
    // Travelable: reachable from the party's current position right now (the selectable
    // next nodes). Visited: already traveled. Both run-global (the map is shared).
    bool Travelable = false,
    bool Visited = false,
    // For a VISITED "Unknown" (`?`) node, the resolved room type behind it (the game reveals an
    // Unknown node's category once travelled). One of the RoomType enum names; empty otherwise.
    string RevealedRoomType = "");
