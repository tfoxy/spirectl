using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Core.Models;

public interface IModelCatalogProvider
{
    ModelCatalogOperationResult GetModels(ModelCatalogRequestSnapshot request)
        => ModelCatalogOperationResult.Failure(
            source: DataSourceKind.Stub,
            provisional: true,
            family: request.Family,
            language: null,
            status: ModelCatalogStatus.Unavailable,
            code: "model-catalog-unavailable",
            message: "Live game model catalog enumeration requires the live STS2 bridge host.",
            notices:
            [
                new ModelCatalogNoticeSnapshot(
                    "model-catalog-unavailable",
                    "warning",
                    "This runtime does not expose live game model metadata.",
                    "models"),
            ]);
}

public sealed record ModelCatalogRequestSnapshot(
    string Family,
    IReadOnlyList<string> Ids,
    string? Language = null);

public enum ModelCatalogStatus
{
    Ok,
    Partial,
    UnsupportedFamily,
    Unavailable,
}

public sealed record ModelCatalogOperationResult(
    DataSourceKind Source,
    bool Provisional,
    string Family,
    string? Language,
    ModelCatalogStatus Status,
    IReadOnlyList<GameModelSnapshot> Models,
    IReadOnlyList<string> MissingIds,
    IReadOnlyList<ModelCatalogNoticeSnapshot> Notices,
    ModelCatalogFailureSnapshot? Error)
{
    public static ModelCatalogOperationResult Success(
        DataSourceKind source,
        bool provisional,
        string family,
        string? language,
        ModelCatalogStatus status,
        IReadOnlyList<GameModelSnapshot> models,
        IReadOnlyList<string> missingIds,
        IReadOnlyList<ModelCatalogNoticeSnapshot> notices)
        => new(source, provisional, family, language, status, models, missingIds, notices, null);

    public static ModelCatalogOperationResult Failure(
        DataSourceKind source,
        bool provisional,
        string family,
        string? language,
        ModelCatalogStatus status,
        string code,
        string message,
        IReadOnlyList<ModelCatalogNoticeSnapshot> notices)
        => new(source, provisional, family, language, status, [], [], notices, new ModelCatalogFailureSnapshot(code, message));
}

public sealed record ModelCatalogFailureSnapshot(string Code, string Message);

public sealed record ModelCatalogNoticeSnapshot(string Code, string Severity, string Message, string? Path = null);

public sealed record ModelLocalizationRefSnapshot(string Table, string Key);

public abstract record GameModelSnapshot(
    string Family,
    string Id,
    string? TypeName = null,
    int CategorySortingId = 0,
    int EntrySortingId = 0,
    bool ShouldReceiveCombatHooks = false)
{
    public IReadOnlyDictionary<string, ModelLocalizationRefSnapshot> LocalizationRefs { get; init; } =
        new Dictionary<string, ModelLocalizationRefSnapshot>(StringComparer.Ordinal);
}

public sealed record CharacterGameModelSnapshot(
    string Id,
    string? Title,
    string? NameColor,
    int StartingHp,
    int StartingGold,
    int MaxEnergy,
    string? EnergyLabelOutlineColor,
    int BaseOrbSlotCount,
    bool ShouldAlwaysShowStarCounter,
    IReadOnlyList<string> StartingRelics,
    string? CharacterSelectTitle,
    string? CharacterSelectDesc,
    string? UnlockText,
    string? DialogueColor,
    string? SpeechBubbleColor,
    string? MapDrawingColor,
    string? VisualsAssetKey,
    string? IconAssetKey,
    string? IconOutlineAssetKey,
    string? EnergyCounterAssetKey,
    string? MerchantAnimAssetKey,
    string? RestSiteAnimAssetKey,
    string? CharacterSelectBgAssetKey,
    string? CharacterSelectBgSpineStillAssetKey,
    string? CharacterSelectIconAssetKey,
    string? CharacterSelectLockedIconAssetKey,
    string? MapMarkerAssetKey,
    string? IconPath,
    string? IconOutlinePath,
    string? EnergyCounterPath,
    string? MerchantAnimPath,
    string? RestSiteAnimPath,
    string? CharacterSelectBgPath,
    string? CharacterSelectIconPath,
    string? CharacterSelectLockedIconPath,
    string? MapMarkerPath,
    // The authored NCreatureVisuals/Bounds Control size (the combat-layout box),
    // derived offline from the creature scene; null when unavailable.
    ModelVector2Snapshot? VisualsBounds = null,
    // The authored IntentPos Marker2D position (creature-local px; the pivot
    // target the game pins the combat Intents container to); null when the
    // creature scene carries no IntentPos marker.
    ModelVector2Snapshot? IntentPos = null) : GameModelSnapshot("characters", Id);

// Spine animations are no longer surfaced on the character model. They are addressed per SpineSprite
// node via the spine:// scheme (the producer streams each node's canonical key + animation list); see
// `sts2 spine list`. The catalog stays a thin model index with no Spine-clip duplication.

public sealed record RelicGameModelSnapshot(
    string Id,
    string? Title,
    string? Flavor,
    string? Description,
    string? IconPath,
    string? IconOutlinePath,
    string? BigIconPath,
    string? Rarity,
    string? IconAssetKey,
    string? IconOutlineAssetKey,
    string? BigIconAssetKey,
    string? PoolId,
    bool IsTradable,
    bool IsAllowedInShops,
    bool HasUponPickupEffect,
    bool SpawnsPets,
    bool AddsPet,
    bool IsStackable,
    int MerchantCost,
    bool ShowCounter,
    string? FlashSfx,
    IReadOnlyDictionary<string, int>? DynamicVars = null,
    IReadOnlyList<ModelHoverTipSnapshot>? HoverTips = null) : GameModelSnapshot("relics", Id);

// One resolved hover tip in a model's hover-tip stack (mirrors a MegaCrit
// HoverTip: the model's own tip plus one per keyword/power it references). Text
// is fully resolved; IsDebuff marks the reddish status/power tips. Same shape
// as PotionHoverTipSnapshot, which predates this shared record.
public sealed record ModelHoverTipSnapshot(
    string? Title,
    string? Description,
    bool IsDebuff,
    string? IconAssetKey);

public sealed record CardGameModelSnapshot(
    string Id,
    string? Title,
    string? Description,
    string? Type,
    string? Rarity,
    string? TargetType,
    string? PoolId,
    string? VisualPoolId,
    int EnergyCost,
    bool IsEnergyXCost,
    int StarCost,
    bool IsStarXCost,
    int ReplayCount,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> Tags,
    int MaxUpgradeLevel,
    bool Upgradable,
    string? UpgradePreviewDescription,
    bool CanBeGeneratedInCombat,
    bool CanBeGeneratedByModifiers,
    string? MultiplayerConstraint,
    bool ShouldShowInCardLibrary,
    bool GainsBlock,
    string? OrbEvokeType,
    bool HasBuiltInOverlay,
    string? ImageAssetKey,
    string? ImagePath,
    string? BetaImagePath,
    string? OverlayAssetKey,
    string? OverlayPath,
    IReadOnlyDictionary<string, int>? DynamicVars = null,
    CardGameModelUpgradeSnapshot? Upgrade = null,
    // The card's FULLY pile-rendered body (GetDescriptionForPile(None, Normal)): the game resolves its
    // own keyword/enchant/affliction lines + dynamic-var substitution, distinct from the bare
    // Description LocString. Nulled in reference mode (language-agnostic). UpgradePreviewDescription
    // already carries the equivalent GetDescriptionForPile(None, Upgrade) render.
    string? RenderedDescription = null) : GameModelSnapshot("cards", Id);

public sealed record CardGameModelUpgradeSnapshot(
    int? EnergyCost = null,
    IReadOnlyDictionary<string, int>? DynamicVars = null);

public sealed record PotionGameModelSnapshot(
    string Id,
    string? Title,
    string? Description,
    string? SelectionScreenPrompt,
    string? Rarity,
    string? Usage,
    string? TargetType,
    string? PoolId,
    bool CanBeGeneratedInCombat,
    bool PassesCustomUsabilityCheck,
    string? IconAssetKey,
    string? IconPath,
    string? OutlineAssetKey,
    string? OutlinePath,
    IReadOnlyList<PotionHoverTipSnapshot>? HoverTips = null) : GameModelSnapshot("potions", Id);

// One resolved hover tip in a potion's hover-tip stack (mirrors a MegaCrit
// HoverTip: the potion's own tip plus one per status/power it applies). Text is
// fully resolved; IsDebuff marks the reddish status/power tips.
public sealed record PotionHoverTipSnapshot(
    string? Title,
    string? Description,
    bool IsDebuff,
    string? IconAssetKey);

public sealed record EventGameModelSnapshot(
    string Id,
    string Kind,
    string? Title,
    string? InitialDescription,
    string? LayoutType,
    bool IsShared,
    bool IsDeterministic,
    bool HasVfx,
    string? CanonicalEncounterId,
    IReadOnlyList<string> GameInfoOptions,
    string? BackgroundSceneAssetKey,
    string? BackgroundScenePath,
    string? BackgroundSpineStillAssetKey,
    string? BackgroundSpineStillPath,
    string? InitialPortraitAssetKey,
    string? InitialPortraitPath,
    string? VfxAssetKey,
    string? VfxPath,
    string? Epithet,
    string? DialogueColor,
    string? ButtonColor,
    string? AmbientBgm,
    bool HasAmbientBgm,
    IReadOnlyList<string> AnyCharacterDialogueBlacklistIds,
    string? MapIconAssetKey,
    string? MapIconPath,
    string? MapIconOutlineAssetKey,
    string? MapIconOutlinePath,
    string? RunHistoryIconAssetKey,
    string? RunHistoryIconPath,
    string? RunHistoryIconOutlineAssetKey,
    string? RunHistoryIconOutlinePath) : GameModelSnapshot("events", Id);

public sealed record CombatBackgroundLayerSnapshot(
    string AssetKey,
    string ResPath,
    bool IsForeground,
    string BgGroupKey);

public sealed record ActGameModelSnapshot(
    string Id,
    string? Title,
    int DefaultOrder,
    int RoomCount,
    int MultiplayerRoomCount,
    int FloorCount,
    int MultiplayerFloorCount,
    IReadOnlyList<string> BossEncounterIds,
    IReadOnlyList<string> EventIds,
    IReadOnlyList<string> AncientEventIds,
    IReadOnlyList<string> WeakEncounterIds,
    IReadOnlyList<string> RegularEncounterIds,
    IReadOnlyList<string> EliteEncounterIds,
    IReadOnlyList<string> MonsterIds,
    IReadOnlyList<string> BgMusicOptions,
    IReadOnlyList<string> MusicBankPaths,
    string? AmbientSfx,
    string? ChestOpenSfx,
    string? MapTraveledColor,
    string? MapUntraveledColor,
    string? MapBgColor,
    string? BackgroundSceneAssetKey,
    string? BackgroundScenePath,
    string? RestSiteBackgroundAssetKey,
    string? RestSiteBackgroundPath,
    string? MapTopBgAssetKey,
    string? MapTopBgPath,
    string? MapMidBgAssetKey,
    string? MapMidBgPath,
    string? MapBotBgAssetKey,
    string? MapBotBgPath,
    string? ChestSpineAssetKey,
    string? ChestSpineResourcePath,
    IReadOnlyList<CombatBackgroundLayerSnapshot>? CombatBackgroundLayers = null) : GameModelSnapshot("acts", Id);

public sealed record ModelVector2Snapshot(double X, double Y);

public sealed record EncounterMonsterSlotSnapshot(string MonsterId, string Slot);

// A scene encounter's enemy slot position (EnemyContainer-local px), keyed by slot
// name. Derived offline from the encounter scene's slot Marker2D nodes.
public sealed record EncounterSlotPositionSnapshot(string Slot, ModelVector2Snapshot Position);

public sealed record MonsterGameModelSnapshot(
    string Id,
    string? TypeName,
    int CategorySortingId,
    int EntrySortingId,
    bool ShouldReceiveCombatHooks,
    string? Title,
    int MinInitialHp,
    int MaxInitialHp,
    IReadOnlyList<string> MoveNames,
    IReadOnlyList<string> AssetPaths,
    string? VisualsAssetKey,
    string? VisualsPath,
    string? BestiaryAttackAnimId,
    bool CanChangeScale,
    bool IsHealthBarVisible,
    double DeathAnimLengthOverride,
    bool HasDeathAnimLengthOverride,
    bool HasDeathSfx,
    string? DeathSfx,
    bool HasHurtSfx,
    string? HurtSfx,
    string? TakeDamageSfx,
    string? TakeDamageSfxType,
    bool ShouldFadeAfterDeath,
    bool ShouldDisappearFromDoom,
    double HpBarSizeReduction,
    ModelVector2Snapshot? ExtraDeathVfxPadding,
    // The authored NCreatureVisuals/Bounds Control size (the combat-layout box),
    // derived offline from the creature scene; null when unavailable.
    ModelVector2Snapshot? VisualsBounds = null,
    // The authored IntentPos Marker2D position (creature-local px; the pivot
    // target the game pins the combat Intents container to); null when the
    // creature scene carries no IntentPos marker.
    ModelVector2Snapshot? IntentPos = null) : GameModelSnapshot("monsters", Id, TypeName, CategorySortingId, EntrySortingId, ShouldReceiveCombatHooks);

public sealed record EncounterGameModelSnapshot(
    string Id,
    string? TypeName,
    int CategorySortingId,
    int EntrySortingId,
    bool ShouldReceiveCombatHooks,
    string? Title,
    string? RoomType,
    bool IsWeak,
    bool IsDebugEncounter,
    IReadOnlyList<string> MonsterIds,
    IReadOnlyList<EncounterMonsterSlotSnapshot> MonstersWithSlots,
    IReadOnlyList<string> Slots,
    IReadOnlyList<string> Tags,
    int MinGoldReward,
    int MaxGoldReward,
    bool ShouldGiveRewards,
    bool HasBgm,
    string? CustomBgm,
    bool HasAmbientSfx,
    string? AmbientSfx,
    bool HasScene,
    string? SceneAssetKey,
    string? ScenePath,
    string? BossNodePath,
    IReadOnlyList<string> MapNodeAssetPaths,
    IReadOnlyList<string> ExtraAssetPaths,
    string? CustomRewardDescription,
    bool FullyCenterPlayers,
    ModelVector2Snapshot? CameraOffset,
    double CameraScaling,
    bool HasCustomBackground = false,
    IReadOnlyList<CombatBackgroundLayerSnapshot>? CombatBackgroundLayers = null,
    string? BackgroundSceneAssetKey = null,
    string? BackgroundScenePath = null,
    IReadOnlyList<EncounterSlotPositionSnapshot>? SlotPositions = null,
    string? BackgroundSpineStillAssetKey = null,
    string? BackgroundSpineStillPath = null,
    ModelVector2Snapshot? BackgroundSpinePosition = null) : GameModelSnapshot("encounters", Id, TypeName, CategorySortingId, EntrySortingId, ShouldReceiveCombatHooks);

public sealed record PowerGameModelSnapshot(
    string Id,
    string? TypeName,
    int CategorySortingId,
    int EntrySortingId,
    bool ShouldReceiveCombatHooks,
    string? Title,
    string? Description,
    string? SmartDescription,
    string? RemoteDescription,
    string? Type,
    string? StackType,
    int Amount,
    int DisplayAmount,
    int AmountOnTurnStart,
    bool AllowNegative,
    bool IsVisible,
    bool IsInstanced,
    bool HasSmartDescription,
    bool HasRemoteDescription,
    bool ShouldPlayVfx,
    bool ShouldScaleInMultiplayer,
    string? AmountLabelColor,
    string? IconAssetKey,
    string? IconPath,
    string? PackedIconPath,
    string? BigIconAssetKey,
    string? ResolvedBigIconPath) : GameModelSnapshot("powers", Id, TypeName, CategorySortingId, EntrySortingId, ShouldReceiveCombatHooks);

public sealed record OrbGameModelSnapshot(
    string Id,
    string? TypeName,
    int CategorySortingId,
    int EntrySortingId,
    bool ShouldReceiveCombatHooks,
    string? Title,
    string? Description,
    string? SmartDescription,
    bool HasSmartDescription,
    double PassiveVal,
    double EvokeVal,
    string? DarkenedColor,
    IReadOnlyList<string> AssetPaths,
    string? IconAssetKey,
    string? IconPath,
    string? SpriteAssetKey,
    string? SpritePath) : GameModelSnapshot("orbs", Id, TypeName, CategorySortingId, EntrySortingId, ShouldReceiveCombatHooks);

public sealed record AfflictionGameModelSnapshot(
    string Id,
    string? TypeName,
    int CategorySortingId,
    int EntrySortingId,
    bool ShouldReceiveCombatHooks,
    string? Title,
    string? Description,
    string? ExtraCardText,
    int Amount,
    bool IsStackable,
    bool CanAfflictUnplayableCards,
    bool HasExtraCardText,
    bool HasOverlay,
    string? OverlayAssetKey,
    string? OverlayPath) : GameModelSnapshot("afflictions", Id, TypeName, CategorySortingId, EntrySortingId, ShouldReceiveCombatHooks);

public sealed record EnchantmentGameModelSnapshot(
    string Id,
    string? TypeName,
    int CategorySortingId,
    int EntrySortingId,
    bool ShouldReceiveCombatHooks,
    string? Title,
    string? Description,
    string? ExtraCardText,
    int Amount,
    int DisplayAmount,
    bool ShowAmount,
    string? Status,
    bool IsStackable,
    bool HasExtraCardText,
    bool PreviewOutsideOfCombat,
    bool ShouldGlowGold,
    bool ShouldGlowRed,
    bool ShouldStartAtBottomOfDrawPile,
    string? IconAssetKey,
    string? IconPath,
    string? IntendedIconPath,
    string? MissingIconPath) : GameModelSnapshot("enchantments", Id, TypeName, CategorySortingId, EntrySortingId, ShouldReceiveCombatHooks);

public sealed record CardPoolGameModelSnapshot(
    string Id,
    string? TypeName,
    int CategorySortingId,
    int EntrySortingId,
    bool ShouldReceiveCombatHooks,
    string? Title,
    IReadOnlyList<string> CardIds,
    bool IsColorless,
    string? EnergyColorName,
    string? EnergyOutlineColor,
    string? DeckEntryCardColor,
    string? EnergyIconAssetKey,
    string? EnergyIconPath,
    string? FrameMaterialAssetKey,
    string? FrameMaterialPath,
    string? CardFrameMaterialPath) : GameModelSnapshot("card-pools", Id, TypeName, CategorySortingId, EntrySortingId, ShouldReceiveCombatHooks);

public sealed record RelicPoolGameModelSnapshot(
    string Id,
    string? TypeName,
    int CategorySortingId,
    int EntrySortingId,
    bool ShouldReceiveCombatHooks,
    IReadOnlyList<string> RelicIds,
    string? EnergyColorName,
    string? LabOutlineColor) : GameModelSnapshot("relic-pools", Id, TypeName, CategorySortingId, EntrySortingId, ShouldReceiveCombatHooks);

public sealed record PotionPoolGameModelSnapshot(
    string Id,
    string? TypeName,
    int CategorySortingId,
    int EntrySortingId,
    bool ShouldReceiveCombatHooks,
    IReadOnlyList<string> PotionIds,
    string? EnergyColorName,
    string? LabOutlineColor) : GameModelSnapshot("potion-pools", Id, TypeName, CategorySortingId, EntrySortingId, ShouldReceiveCombatHooks);

public sealed record ModifierGameModelSnapshot(
    string Id,
    string? TypeName,
    int CategorySortingId,
    int EntrySortingId,
    bool ShouldReceiveCombatHooks,
    string? Title,
    string? Description,
    string? NeowOptionTitle,
    string? NeowOptionDescription,
    bool ClearsPlayerDeck,
    string? Polarity,
    IReadOnlyList<string> MutuallyExclusiveModifierIds,
    string? IconAssetKey,
    string? IconPath) : GameModelSnapshot("modifiers", Id, TypeName, CategorySortingId, EntrySortingId, ShouldReceiveCombatHooks);

public sealed record AchievementGameModelSnapshot(
    string Id,
    string? TypeName,
    int CategorySortingId,
    int EntrySortingId,
    bool ShouldReceiveCombatHooks) : GameModelSnapshot("achievements", Id, TypeName, CategorySortingId, EntrySortingId, ShouldReceiveCombatHooks);
