using System.Collections;
using System.Text;
using Godot;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Live;

internal static class Sts2TreasureRoomScreenInspector
{
    public const string TreasureRoomType = "MegaCrit.Sts2.Core.Nodes.Rooms.NTreasureRoom";
    public const string TreasureRoomRelicCollectionType = "MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic.NTreasureRoomRelicCollection";
    private const string TreasureRoomRelicHolderType = "MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic.NTreasureRoomRelicHolder";

    public static bool IsSupportedScreen(object? screenObject)
        => Sts2LiveIntrospection.IsType(screenObject, TreasureRoomType)
            || Sts2LiveIntrospection.IsType(screenObject, TreasureRoomRelicCollectionType);

    public static object? ResolveActiveRoom(object? screenObject = null)
    {
        screenObject ??= Sts2LiveIntrospection.ResolveCurrentScreenObject();
        if (Sts2LiveIntrospection.IsType(screenObject, TreasureRoomType))
        {
            return screenObject;
        }

        if (screenObject is Node node)
        {
            for (Node? current = node; current is not null; current = current.GetParent())
            {
                if (Sts2LiveIntrospection.IsType(current, TreasureRoomType))
                {
                    return current;
                }
            }
        }

        // Browser mode: the located screen is the run screen and the treasure room is a DESCENDANT of the
        // Run node, not an ancestor of the located screen — so the up-walk above misses it. Search the
        // run tree downward as a fallback (same anchor the debug treasure resolver uses).
        return Sts2LiveIntrospection.FindRunTreeNodeOfType(TreasureRoomType);
    }

    public static object? ResolveActiveRelicCollection(object? screenObject = null, object? roomObject = null)
    {
        screenObject ??= Sts2LiveIntrospection.ResolveCurrentScreenObject();
        if (Sts2LiveIntrospection.IsType(screenObject, TreasureRoomRelicCollectionType))
        {
            return screenObject;
        }

        roomObject ??= ResolveActiveRoom(screenObject);
        var collection = Sts2LiveIntrospection.GetMemberValue(roomObject, "_relicCollection");
        return Sts2LiveIntrospection.IsType(collection, TreasureRoomRelicCollectionType) ? collection : null;
    }

    public static IReadOnlyList<ResolvedTreasureRoomChoice> ResolveChoices(
        object? roomObject,
        object? screenObject,
        string? defaultPlayerId,
        ICollection<StateNoticeSnapshot>? notices = null)
    {
        var choices = new List<ResolvedTreasureRoomChoice>();

        if (TryResolveOpenChestChoice(roomObject, defaultPlayerId, out var openChestChoice))
        {
            choices.Add(openChestChoice);
        }

        var relicCollection = ResolveActiveRelicCollection(screenObject, roomObject);
        choices.AddRange(ResolveRelicChoices(relicCollection, defaultPlayerId, notices));

        if (TryResolveProceedChoice(roomObject, defaultPlayerId, out var proceedChoice))
        {
            choices.Add(proceedChoice);
        }

        return choices;
    }

    private static bool TryResolveOpenChestChoice(
        object? roomObject,
        string? defaultPlayerId,
        out ResolvedTreasureRoomChoice choice)
    {
        if ((Sts2LiveIntrospection.GetMemberValue(roomObject, "_chestButton")
                ?? Sts2LiveIntrospection.GetMemberValue(roomObject, "ChestButton")) is not Control control
            || !control.Visible)
        {
            choice = default!;
            return false;
        }

        var choiceId = Sts2TreasureRoomIds.OpenChestChoiceId();
        var isExecutable = ResolveIsExecutable(control);
        choice = new ResolvedTreasureRoomChoice(
            new ChoiceSnapshot(
                Id: choiceId,
                Label: "Open Chest",
                Kind: "treasure-room-flow",
                Provisional: false,
                OwnerPlayerId: defaultPlayerId,
                ChoiceKind: "treasure-room-flow",
                IntentKind: "open-chest",
                Perspective: ResolvePerspective(defaultPlayerId),
                PreferredAction: "open-chest",
                Arguments: new ActionArgumentsSnapshot(defaultPlayerId, null, null, null, null, null, IntentKind: "open-chest"),
                Enabled: isExecutable,
                DisabledReason: isExecutable ? null : "not-enabled",
                PreferredActionRef: CreatePreferredActionRef("open-chest", SemanticActionKind.OpenChest, "Open Chest", defaultPlayerId, choiceId, null, isExecutable, isExecutable ? null : "not-enabled", ["treasureRoom.OnChestButtonReleased", "chestButton.OnRelease"]),
                LegalityStatus: isExecutable ? ActionLegalityKind.Legal : ActionLegalityKind.Illegal,
                CheckedHookPaths: ["treasureRoom.OnChestButtonReleased", "chestButton.OnRelease"]),
            control,
            TreasureRoomChoiceKind.OpenChest,
            isExecutable,
            PreferredAction: "open-chest");
        return true;
    }

    private static bool TryResolveProceedChoice(
        object? roomObject,
        string? defaultPlayerId,
        out ResolvedTreasureRoomChoice choice)
    {
        if ((Sts2LiveIntrospection.GetMemberValue(roomObject, "_proceedButton")
                ?? Sts2LiveIntrospection.GetMemberValue(roomObject, "ProceedButton")) is not Control control
            || !control.Visible)
        {
            choice = default!;
            return false;
        }

        var isExecutable = ResolveIsExecutable(control);
        choice = new ResolvedTreasureRoomChoice(
            new ChoiceSnapshot(
                Id: Sts2TreasureRoomIds.ProceedChoiceId(),
                Label: "Proceed",
                Kind: "treasure-room-flow",
                Provisional: false,
                OwnerPlayerId: defaultPlayerId,
                ChoiceKind: "treasure-room-flow",
                IntentKind: "proceed-treasure-room",
                Perspective: ResolvePerspective(defaultPlayerId),
                PreferredAction: "proceed-treasure-room",
                Arguments: new ActionArgumentsSnapshot(defaultPlayerId, null, null, null, null, null, IntentKind: "proceed-treasure-room"),
                Enabled: isExecutable,
                DisabledReason: isExecutable ? null : "not-enabled",
                PreferredActionRef: CreatePreferredActionRef("proceed-treasure-room", SemanticActionKind.ProceedTreasureRoom, "Proceed", defaultPlayerId, Sts2TreasureRoomIds.ProceedChoiceId(), null, isExecutable, isExecutable ? null : "not-enabled", ["treasureRoom.OnProceedButtonReleased", "proceedButton.OnRelease"]),
                LegalityStatus: isExecutable ? ActionLegalityKind.Legal : ActionLegalityKind.Illegal,
                CheckedHookPaths: ["treasureRoom.OnProceedButtonReleased", "proceedButton.OnRelease"]),
            control,
            TreasureRoomChoiceKind.Proceed,
            isExecutable,
            PreferredAction: "proceed-treasure-room");
        return true;
    }

    private static IReadOnlyList<ResolvedTreasureRoomChoice> ResolveRelicChoices(
        object? relicCollection,
        string? defaultPlayerId,
        ICollection<StateNoticeSnapshot>? notices)
    {
        if (relicCollection is null)
        {
            return [];
        }

        var holders = new List<object>();
        if (Sts2LiveIntrospection.GetMemberValue(relicCollection, "_holdersInUse") is IEnumerable activeHolders)
        {
            foreach (var holder in activeHolders)
            {
                if (holder is not null)
                {
                    holders.Add(holder);
                }
            }
        }

        if (holders.Count == 0
            && Sts2LiveIntrospection.GetMemberValue(relicCollection, "SingleplayerRelicHolder") is { } singleplayerHolder)
        {
            holders.Add(singleplayerHolder);
        }

        if (holders.Count == 0
            && Sts2LiveIntrospection.GetMemberValue(relicCollection, "_multiplayerHolders") is IEnumerable multiplayerHolders)
        {
            foreach (var holder in multiplayerHolders)
            {
                if (holder is not null)
                {
                    holders.Add(holder);
                }
            }
        }

        var choices = new List<ResolvedTreasureRoomChoice>();
        var seenChoiceIds = new HashSet<string>(StringComparer.Ordinal);
        var fallbackIndex = 0;
        foreach (var holder in holders)
        {
            if (holder is not Control control
                || !control.Visible
                || !Sts2LiveIntrospection.IsType(holder, TreasureRoomRelicHolderType))
            {
                fallbackIndex++;
                continue;
            }

            var relic = Sts2LiveIntrospection.GetMemberValue(holder, "Relic");
            if (relic is null)
            {
                fallbackIndex++;
                continue;
            }

            var relicIndex = TryResolveInt(Sts2LiveIntrospection.GetMemberValue(holder, "Index")) ?? fallbackIndex;
            var relicId = ResolveRelicId(relic, relicIndex, notices);
            var choiceId = Sts2TreasureRoomIds.RelicChoiceId(relicId, relicIndex);
            if (!seenChoiceIds.Add(choiceId))
            {
                fallbackIndex++;
                continue;
            }

            var isExecutable = ResolveIsExecutable(control);
            choices.Add(new ResolvedTreasureRoomChoice(
                new ChoiceSnapshot(
                    Id: choiceId,
                    Label: $"Take {ResolveRelicLabel(relic, relicId)}",
                    Kind: "treasure-room-relic",
                    Provisional: false,
                    OwnerPlayerId: defaultPlayerId,
                    ChoiceKind: "treasure-room-relic",
                    IntentKind: "take-relic",
                    Perspective: ResolvePerspective(defaultPlayerId),
                    PreferredAction: "take-relic",
                    Arguments: new ActionArgumentsSnapshot(defaultPlayerId, null, null, null, null, null, IntentKind: "take-relic", Values: new Dictionary<string, string> { ["relicId"] = relicId }),
                    Enabled: isExecutable,
                    DisabledReason: isExecutable ? null : "not-enabled",
                    PreferredActionRef: CreatePreferredActionRef("take-relic", SemanticActionKind.TakeRelic, $"Take {ResolveRelicLabel(relic, relicId)}", defaultPlayerId, choiceId, relicId, isExecutable, isExecutable ? null : "not-enabled", ["treasureRoomRelicCollection.PickRelic", "relicHolder.OnRelease"]),
                    LegalityStatus: isExecutable ? ActionLegalityKind.Legal : ActionLegalityKind.Illegal,
                    CheckedHookPaths: ["treasureRoomRelicCollection.PickRelic", "relicHolder.OnRelease"]),
                control,
                TreasureRoomChoiceKind.Relic,
                isExecutable,
                RelicId: relicId,
                PreferredAction: "take-relic"));
            fallbackIndex++;
        }

        return choices;
    }

    private static string ResolveRelicId(
        object relic,
        int relicIndex,
        ICollection<StateNoticeSnapshot>? notices)
    {
        var stableId = ResolveModelId(relic);
        if (!string.IsNullOrWhiteSpace(stableId))
        {
            return stableId!;
        }

        var slug = Slugify(ResolveOptionalRelicLabel(relic, fallback: null));
        if (!string.IsNullOrWhiteSpace(slug))
        {
            AddNotice(
                notices,
                "treasure-room-relic-id-label-fallback",
                "Treasure-room relic ids fell back to visible labels because the live relic model did not expose a stable id.");
            return slug!;
        }

        AddNotice(
            notices,
            "treasure-room-relic-id-order-fallback",
            "Treasure-room relic ids fell back to holder ordering because the live relic model did not expose a stable id or label.");
        return $"relic-{relicIndex}";
    }

    private static string ResolveRelicLabel(object relic, string fallback)
        => ResolveOptionalRelicLabel(relic, fallback) ?? fallback;

    private static string? ResolveOptionalRelicLabel(object relic, string? fallback)
    {
        return Normalize(Sts2LiveIntrospection.GetMemberValue(relic, "Title")?.ToString())
            ?? Normalize(Sts2LiveIntrospection.GetMemberValue(relic, "Name")?.ToString())
            ?? Normalize(Sts2LiveIntrospection.GetMemberValue(relic, "DisplayName")?.ToString())
            ?? Normalize(ResolveModelId(relic))
            ?? fallback;
    }

    private static string? ResolveModelId(object? model)
    {
        var id = Sts2LiveIntrospection.GetMemberValue(model, "Id")
            ?? Sts2LiveIntrospection.GetMemberValue(model, "Key");
        return Normalize(Sts2LiveIntrospection.GetMemberValue(id, "Entry")?.ToString())
            ?? Normalize(id?.ToString());
    }

    private static bool ResolveIsExecutable(object control, bool defaultValue = true)
    {
        return Sts2LiveIntrospection.GetMemberValue(control, "Disabled") switch
        {
            bool disabled => !disabled,
            _ => Sts2LiveIntrospection.GetMemberValue(control, "IsEnabled") switch
            {
                bool isEnabled => isEnabled,
                _ => Sts2LiveIntrospection.GetMemberValue(control, "IsDisabled") switch
                {
                    bool isDisabled => !isDisabled,
                    _ => defaultValue,
                },
            },
        };
    }

    private static int? TryResolveInt(object? value)
    {
        return value switch
        {
            int intValue => intValue,
            long longValue when longValue is >= int.MinValue and <= int.MaxValue => (int)longValue,
            short shortValue => shortValue,
            byte byteValue => byteValue,
            string stringValue when int.TryParse(stringValue, out var parsed) => parsed,
            _ => null,
        };
    }

    private static string? Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        var previousWasSeparator = false;
        foreach (var ch in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
                previousWasSeparator = false;
                continue;
            }

            if (ch is '-' or '_')
            {
                builder.Append(ch);
                previousWasSeparator = false;
                continue;
            }

            if (previousWasSeparator)
            {
                continue;
            }

            builder.Append('-');
            previousWasSeparator = true;
        }

        var normalized = builder.ToString().Trim('-', '_');
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.ReplaceLineEndings(" ").Trim();

    private static VisibleActionReferenceSnapshot CreatePreferredActionRef(
        string preferredAction,
        SemanticActionKind kind,
        string label,
        string? playerId,
        string choiceId,
        string? relicId,
        bool enabled,
        string? disabledReason,
        IReadOnlyList<string> checkedHookPaths)
        => new(
            Sts2ActionIds.Intent("treasure-room", preferredAction, choiceId),
            label,
            Enabled: enabled,
            OwnerPlayerId: playerId,
            ActionKind: kind,
            IntentKind: preferredAction,
            Arguments: new ActionArgumentsSnapshot(
                playerId,
                null,
                null,
                null,
                null,
                null,
                IntentKind: preferredAction,
                Values: relicId is null ? null : new Dictionary<string, string> { ["relicId"] = relicId }),
            LegalityStatus: enabled ? ActionLegalityKind.Legal : ActionLegalityKind.Illegal,
            DisabledReason: disabledReason,
            Perspective: ResolvePerspective(playerId),
            CheckedHookPaths: checkedHookPaths);

    private static string? ResolvePerspective(string? playerId)
        => string.IsNullOrWhiteSpace(playerId) ? null : $"player:{playerId}";

    private static void AddNotice(
        ICollection<StateNoticeSnapshot>? notices,
        string code,
        string message)
    {
        if (notices is null || notices.Any(notice => notice.Code == code))
        {
            return;
        }

        Sts2StateNotice.AddPartialOnce(notices, code, message, "choices", nameof(Sts2TreasureRoomScreenInspector));
    }
}

internal sealed record ResolvedTreasureRoomChoice(
    ChoiceSnapshot Snapshot,
    object Control,
    TreasureRoomChoiceKind Kind,
    bool IsExecutable,
    string? RelicId = null,
    int DisplayOrder = 0,
    string PreferredAction = "choose");

internal enum TreasureRoomChoiceKind
{
    OpenChest,
    Relic,
    Proceed,
}
