using Spirectl.Sts2.Core.Actions;

namespace Spirectl.Sts2.Core.State;

public sealed record ChoiceSnapshot(
    string Id,
    string Label,
    string Kind,
    bool Provisional,
    string? OwnerPlayerId = null,
    string? ChoiceKind = null,
    string? IntentKind = null,
    string? Perspective = null,
    string? PreferredAction = null,
    string? Description = null,
    ActionArgumentsSnapshot? Arguments = null,
    bool Enabled = true,
    string? DisabledReason = null,
    VisibleActionReferenceSnapshot? PreferredActionRef = null,
    ActionLegalityKind LegalityStatus = ActionLegalityKind.Unspecified,
    IReadOnlyList<string>? CheckedHookPaths = null);

public sealed record ActionArgumentsSnapshot(
    string? PlayerId,
    string? CardId,
    string? TargetId,
    string? ChoiceId,
    string? CharacterId,
    string? MapNodeId,
    string? PotionId = null,
    string? IntentKind = null,
    IReadOnlyDictionary<string, string>? Values = null);

public sealed record StateNoticeSnapshot(
    string Code,
    string Message,
    bool Provisional,
    string? Path = null,
    string? Severity = null,
    string? Source = null,
    string? Stability = null,
    string? Perspective = null);

public sealed record VisibleActionReferenceSnapshot(
    string Id,
    string Label,
    bool Enabled,
    string? OwnerPlayerId = null,
    SemanticActionKind? ActionKind = null,
    string? IntentKind = null,
    ActionArgumentsSnapshot? Arguments = null,
    ActionLegalityKind LegalityStatus = ActionLegalityKind.Unspecified,
    string? DisabledReason = null,
    string? Perspective = null,
    IReadOnlyList<string>? CheckedHookPaths = null);

public sealed record RichLocalizedTextSnapshot(
    string Text,
    string? RawText = null,
    string? LocTable = null,
    string? LocKey = null,
    string? Source = null,
    bool Provisional = false);

public sealed record EventRoomPageStateSnapshot(
    string? EventId,
    string? EventType,
    RichLocalizedTextSnapshot? Title,
    RichLocalizedTextSnapshot? Description,
    RichLocalizedTextSnapshot? SharedLabel,
    string? TextSource,
    bool Provisional = false,
    AncientEventPageStateSnapshot? Ancient = null);

public sealed record AncientEventDialogueLineStateSnapshot(
    int Index,
    RichLocalizedTextSnapshot? Text,
    string? Speaker,
    bool Current = false,
    bool Visible = true);

public sealed record AncientEventPageStateSnapshot(
    RichLocalizedTextSnapshot? Title,
    RichLocalizedTextSnapshot? BannerTitle,
    RichLocalizedTextSnapshot? Epithet,
    RichLocalizedTextSnapshot? CurrentDialogue,
    int CurrentDialogueIndex,
    IReadOnlyList<AncientEventDialogueLineStateSnapshot> DialogueLines,
    string? CurrentSpeaker,
    RichLocalizedTextSnapshot? NextButtonText,
    string? TextSource,
    bool Provisional = false);

public sealed record LobbyPlayerSnapshot(
    string Id,
    string Status,
    string? Name,
    string? SelectedCharacterId,
    bool IsReady,
    int SlotId,
    bool IsLocal,
    bool IsHost,
    bool IsRemote,
    bool IsHostLocalSeat = false);

public sealed record LobbyCharacterSnapshot(
    string Id,
    string Name,
    bool IsUnlocked,
    string? NameKey = null,
    string? Description = null,
    int? StartingHp = null,
    int? StartingGold = null,
    string? PassiveId = null,
    string? PassiveName = null,
    string? PassiveDescription = null,
    string? PassiveIconAssetKey = null,
    string? PortraitAssetKey = null,
    string? IconAssetKey = null,
    string? SelectBackgroundAssetKey = null,
    string? NameColor = null);

public sealed record LobbyPresentationGeometrySnapshot(
    IReadOnlyDictionary<string, LobbyPresentationRectSnapshot> RectsByKey,
    IReadOnlyList<LobbyPresentationMissingGeometrySnapshot> Missing,
    IReadOnlyDictionary<string, LobbyPresentationRectSnapshot>? EntriesByKey = null)
{
    public IReadOnlyDictionary<string, LobbyPresentationRectSnapshot> EntriesByKey { get; init; } = EntriesByKey ?? RectsByKey;
}

public sealed record LobbyPresentationRectSnapshot(
    string Key,
    PresentationRectSnapshot Rect,
    string Source,
    string? NodePath,
    string? MemberPath,
    IReadOnlyList<string> CheckedPaths,
    string? SemanticRole = null,
    string? SourceKind = null,
    string? DisplayedText = null,
    string? TextSource = null,
    string? AssetSourceKind = null,
    string? AssetResourcePath = null,
    string? AssetMemberPath = null,
    string? AssetTintColor = null,
    LobbyPresentationNinePatchSnapshot? NinePatch = null,
    PresentationVisualHintSnapshot? VisualHint = null,
    bool? DrawBehindParent = null)
{
    public string? NodeType { get; init; }
    public LobbyPresentationAnchorsSnapshot? Anchors { get; init; }
    public double? ObservedGapX { get; init; }
    public double? ObservedGapY { get; init; }
    public string? DrawOrder { get; init; }
    public IReadOnlyDictionary<string, string> VisualCatalogHints { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyList<LobbyPresentationChildLayoutSnapshot> Children { get; init; } = [];
}

public sealed record LobbyPresentationAnchorsSnapshot(
    double Left,
    double Top,
    double Right,
    double Bottom);

public sealed record LobbyPresentationChildLayoutSnapshot(
    string GeometryKey,
    string? StateId,
    string? NodePath,
    PresentationRectSnapshot Rect,
    uint Order,
    IReadOnlyDictionary<string, string>? Hints = null);

public sealed record LobbyPresentationPatchMarginsSnapshot(
    double Left,
    double Top,
    double Right,
    double Bottom);

public sealed record LobbyPresentationNinePatchSnapshot(
    string? Texture,
    bool DrawCenter,
    LobbyPresentationPatchMarginsSnapshot PatchMargins,
    string AxisStretchHorizontal,
    string AxisStretchVertical,
    string EffectiveModulate);

public enum PresentationSourceKindSnapshot
{
    Unspecified,
    RuntimeState,
    RuntimeUi,
    AssetMetadata,
    Inferred,
    Unsupported,
}

public enum PresentationStabilitySnapshot
{
    Unspecified,
    Stable,
    ScreenInstance,
    Unstable,
    Inferred,
}

public sealed record PresentationVisualHintSnapshot(
    string? SourceNodePath,
    string? NodeType,
    PresentationVisualTextureSnapshot? Texture,
    string? EffectiveModulate,
    bool? ClipContents,
    PresentationTextureRectHintSnapshot? TextureRect,
    PresentationSourceKindSnapshot SourceKind,
    PresentationStabilitySnapshot Stability,
    string? Note,
    string? InheritedEffectiveModulate = null,
    string? ModulateMode = null,
    PresentationVisualMaterialHintSnapshot? Material = null,
    string? CompositeMode = null,
    PresentationColorAdjustHintSnapshot? ColorAdjust = null,
    PresentationVisualMaskHintSnapshot? Mask = null);

public sealed record PresentationVisualTextureSnapshot(
    string? Field,
    string? ResourcePath,
    string? ResourceType,
    string? ResourceName);

public sealed record PresentationTextureRectHintSnapshot(
    string? StretchMode,
    string? ExpandMode,
    bool FlipH,
    bool FlipV);

public sealed record PresentationVisualMaterialHintSnapshot(
    PresentationVisualTextureSnapshot? Material,
    bool? UseParentMaterial,
    PresentationVisualTextureSnapshot? Shader,
    IReadOnlyList<PresentationVisualShaderParameterSnapshot> ShaderParameters,
    string? BlendMode = null);

public sealed record PresentationVisualShaderParameterSnapshot(
    string Name,
    string ValueKind,
    string? StringValue = null,
    double? NumberValue = null,
    bool? BoolValue = null,
    string? ColorValue = null,
    double? Vector2X = null,
    double? Vector2Y = null,
    PresentationVisualTextureSnapshot? ResourceValue = null);

public sealed record PresentationColorAdjustHintSnapshot(
    double? Hue,
    double? Saturation,
    double? Value,
    string? Source = null);

public sealed record PresentationVisualMaskHintSnapshot(
    string? SourceNodePath,
    PresentationVisualTextureSnapshot? Texture,
    PresentationRectSnapshot? Rect,
    PresentationTextureRectHintSnapshot? TextureRect,
    string? EffectiveModulate = null);

public sealed record PresentationRectSnapshot(
    double X,
    double Y,
    double Width,
    double Height,
    PresentationNormalizedRectSnapshot? Normalized = null);

public sealed record PresentationNormalizedRectSnapshot(
    double X,
    double Y,
    double Width,
    double Height,
    double CenterX,
    double CenterY,
    string Unit,
    string Origin);

public sealed record LobbyPresentationMissingGeometrySnapshot(
    string Key,
    IReadOnlyList<string> CheckedPaths,
    string Reason,
    string? ScreenType = null,
    string? ScreenRawType = null,
    string? ScreenClassName = null,
    string? RequestedPlayerId = null,
    string? Perspective = null,
    string? SourceKind = null);

public sealed record LobbyStateSnapshot(
    string LobbyId,
    string Phase,
    IReadOnlyList<LobbyPlayerSnapshot> Players,
    IReadOnlyList<LobbyCharacterSnapshot> AvailableCharacters,
    string? LocalPlayerId,
    string? HostPlayerId,
    string? LocalPlayerRole,
    IReadOnlyDictionary<string, LobbyPlayerSnapshot> PlayersById,
    IReadOnlyDictionary<string, LobbyCharacterSnapshot> AvailableCharactersById,
    string? WaitingText = null,
    LobbyPresentationGeometrySnapshot? PresentationGeometry = null);

public sealed record PlayerStateSnapshot(
    string Id,
    string Character,
    int Hp,
    int MaxHp,
    bool IsLocal = false,
    bool IsHost = false,
    bool IsRemote = false,
    bool IsHostLocalSeat = false,
    int Gold = 0,
    IReadOnlyList<RelicStateSnapshot>? Relics = null,
    IReadOnlyList<PotionStateSnapshot>? Potions = null,
    CardPileStateSnapshot? MasterDeck = null,
    IReadOnlyList<StatusEffectStateSnapshot>? StatusEffects = null);

public sealed record RunStateSnapshot(
    string Seed,
    int Floor,
    int Act,
    IReadOnlyList<PlayerStateSnapshot> Players,
    IReadOnlyDictionary<string, PlayerStateSnapshot> PlayersById,
    string? ActLabel = null,
    string? FloorLabel = null,
    string? EncounterId = null,
    string? EncounterLabel = null,
    string? RoomId = null,
    string? RoomLabel = null,
    string? BossId = null,
    string? BossLabel = null);

public sealed record AssetReferenceSnapshot(
    string Kind,
    string Key,
    string? Label,
    bool Provisional);

public sealed record CardPileStateSnapshot(
    string Id,
    string Label,
    string? OwnerPlayerId,
    int Count,
    IReadOnlyList<CardStateSnapshot> Cards,
    bool CardsObservable,
    bool OrderObservable);

public sealed record RelicStateSnapshot(
    string Id,
    string ModelId,
    string Name,
    string? Description,
    string? OwnerPlayerId,
    int SlotIndex,
    bool HasCounter,
    int Counter,
    string? CounterLabel,
    IReadOnlyList<AssetReferenceSnapshot>? AssetRefs = null);

public sealed record StatusEffectStateSnapshot(
    string Id,
    string ModelId,
    string Name,
    string? Description,
    string? OwnerPlayerId,
    int StackCount,
    string? StackLabel,
    int Duration,
    string? DurationLabel,
    string? Type,
    IReadOnlyList<AssetReferenceSnapshot>? AssetRefs = null);

public sealed record CardStateSnapshot(
    string Id,
    string Name,
    int Cost,
    string? OwnerPlayerId,
    bool Playable,
    string? UnplayableReason,
    IReadOnlyList<string> TargetIds,
    bool Upgraded,
    string? ModelId = null,
    string? Description = null,
    string? Type = null,
    string? Rarity = null,
    string? TargetType = null,
    int UpgradeLevel = 0,
    string? CostLabel = null,
    string? AfflictionModelId = null,
    int AfflictionAmount = 0,
    IReadOnlyList<AssetReferenceSnapshot>? AssetRefs = null);

public sealed record PotionStateSnapshot(
    string Id,
    string Name,
    string? OwnerPlayerId,
    int SlotIndex,
    bool Usable,
    string? UnusableReason,
    IReadOnlyList<string> TargetIds,
    string? ModelId = null,
    string? Description = null,
    string? TargetType = null,
    bool RequiresTarget = false,
    IReadOnlyList<AssetReferenceSnapshot>? AssetRefs = null);
