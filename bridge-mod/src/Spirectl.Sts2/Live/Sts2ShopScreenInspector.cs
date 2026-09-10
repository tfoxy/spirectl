using System.Collections;
using Godot;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Live;

internal static class Sts2ShopScreenInspector
{
    private const string MerchantInventoryType = "MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantInventory";
    private const string FakeMerchantInventoryType = "MegaCrit.Sts2.Core.Nodes.Screens.Shops.NFakeMerchantInventory";

    public static object? ResolveActiveShopScreenObject(object? screenObject = null)
    {
        screenObject ??= Sts2LiveIntrospection.ResolveCurrentScreenObject();
        if (IsShopScreen(screenObject) && IsOpenShopScreen(screenObject, defaultWhenUnknown: true))
        {
            return screenObject;
        }

        return FindDescendant(screenObject, static child =>
            IsShopScreen(child)
            && ResolveVisible(child)
            && IsOpenShopScreen(child, defaultWhenUnknown: false));
    }

    public static bool IsShopScreen(object? screenObject)
        => Sts2LiveIntrospection.IsType(screenObject, MerchantInventoryType)
            || Sts2LiveIntrospection.IsType(screenObject, FakeMerchantInventoryType);

    // Locate the merchant inventory screen node regardless of its open/closed state.
    // ResolveActiveShopScreenObject is open-gated (it intentionally hides closed wares for the
    // actions path), so it cannot be used to report a definitive false. The wares-open flag lives
    // on this NMerchantInventory screen node, not on the MerchantInventory data entity.
    public static object? ResolveShopScreenObject(object? screenObject = null)
    {
        screenObject ??= Sts2LiveIntrospection.ResolveCurrentScreenObject();
        if (IsShopScreen(screenObject))
        {
            return screenObject;
        }

        return FindDescendant(screenObject, static child => IsShopScreen(child));
    }

    public static bool? TryResolveIsOpen(object? screenObject)
        => Sts2LiveIntrospection.GetMemberValue(screenObject, "IsOpen") as bool?;

    private static bool IsOpenShopScreen(object? screenObject, bool defaultWhenUnknown)
        => Sts2LiveIntrospection.GetMemberValue(screenObject, "IsOpen") switch
        {
            bool isOpen => isOpen,
            _ => defaultWhenUnknown,
        };

    public static IReadOnlyList<ResolvedShopChoice> ResolveChoices(
        object shopScreen,
        string? defaultPlayerId,
        ICollection<StateNoticeSnapshot>? notices = null)
    {
        var choices = new List<ResolvedShopChoice>();
        var slots = Sts2LiveIntrospection.InvokeMethod(shopScreen, "GetAllSlots") as IEnumerable;
        var slotIndex = 0;
        foreach (var slot in slots ?? Array.Empty<object>())
        {
            if (slot is not Control control || !control.Visible)
            {
                slotIndex++;
                continue;
            }

            var entry = Sts2LiveIntrospection.GetMemberValue(slot, "Entry");
            if (entry is null)
            {
                slotIndex++;
                continue;
            }

            var kindKey = ResolveKindKey(slot);
            var playerId = ResolveOwnerPlayerId(slot, entry, defaultPlayerId, notices);
            var choiceId = kindKey == "card-removal"
                ? Sts2ShopIds.CardRemovalChoiceId(playerId, slotIndex)
                : Sts2ShopIds.ChoiceId(
                    playerId,
                    kindKey,
                    ResolveStableItemKey(slot, entry, kindKey, notices),
                    slotIndex);
            var isStocked = ResolveBool(entry, "IsStocked", defaultValue: true);
            var enoughGold = ResolveBool(entry, "EnoughGold", defaultValue: true);
            var enabled = isStocked && enoughGold;
            var preferredAction = ResolvePreferredAction(kindKey);
            var argumentValues = new Dictionary<string, string> { ["shopItemId"] = choiceId };
            if (kindKey == "relic")
            {
                argumentValues["relicId"] = choiceId;
            }
            var arguments = new ActionArgumentsSnapshot(
                playerId,
                kindKey == "card" ? choiceId : null,
                null,
                preferredAction == "choose" ? choiceId : null,
                null,
                null,
                kindKey == "potion" ? choiceId : null,
                IntentKind: preferredAction,
                Values: argumentValues);
            var checkedHookPaths = CheckedHookPathsForChoice(kindKey);
            var disabledReason = ResolveDisabledReason(isStocked, enoughGold);

            choices.Add(new ResolvedShopChoice(
                new ChoiceSnapshot(
                    Id: choiceId,
                    Label: ResolveLabel(slot, entry, kindKey),
                    Kind: ResolveChoiceKind(kindKey),
                    Provisional: false,
                    OwnerPlayerId: playerId,
                    ChoiceKind: ResolveChoiceKind(kindKey),
                    IntentKind: preferredAction,
                    Perspective: ResolvePerspective(playerId),
                    PreferredAction: preferredAction,
                    Arguments: arguments,
                    Enabled: enabled,
                    DisabledReason: disabledReason,
                    PreferredActionRef: new VisibleActionReferenceSnapshot(
                        Id: Sts2ActionIds.Intent("shop", preferredAction, choiceId),
                        Label: preferredAction,
                        Enabled: enabled,
                        OwnerPlayerId: playerId,
                        ActionKind: ResolveActionKind(preferredAction),
                        IntentKind: preferredAction,
                        Arguments: arguments,
                        LegalityStatus: enabled ? ActionLegalityKind.Legal : ActionLegalityKind.Illegal,
                        DisabledReason: disabledReason,
                        Perspective: ResolvePerspective(playerId),
                        CheckedHookPaths: checkedHookPaths),
                    LegalityStatus: enabled ? ActionLegalityKind.Legal : ActionLegalityKind.Illegal,
                    CheckedHookPaths: checkedHookPaths),
                Entry: entry,
                Control: control,
                PlayerId: playerId,
                IsExecutable: enabled,
                IsStocked: isStocked,
                EnoughGold: enoughGold,
                SlotKind: kindKey,
                ItemModelId: ResolveModelId(Sts2LiveIntrospection.GetMemberValue(entry, "Model"))
                    ?? ResolveModelId(Sts2LiveIntrospection.GetMemberValue(
                        Sts2LiveIntrospection.GetMemberValue(entry, "CreationResult"),
                        "Card")),
                Cost: TryResolveInt(Sts2LiveIntrospection.GetMemberValue(entry, "Price"))
                    ?? TryResolveInt(Sts2LiveIntrospection.GetMemberValue(entry, "Cost")),
                RequiresCancelableWrapper: kindKey == "card-removal",
                IsFlowChoice: false,
                PreferredAction: preferredAction));
            slotIndex++;
        }

        if (TryResolveLeaveChoice(shopScreen, defaultPlayerId, notices, out var leaveChoice))
        {
            choices.Add(leaveChoice);
        }

        return choices;
    }

    public static IReadOnlyList<ResolvedShopChoice> ResolveInventoryChoices(
        object? inventory,
        string? defaultPlayerId,
        ICollection<StateNoticeSnapshot>? notices = null)
    {
        if (inventory is null)
        {
            return [];
        }

        // Mint canonical inventory-model ids (shop:{collection}:{index}, per-collection index) so these
        // choices key by the SAME id the state snapshot (Sts2StateProvider.ResolveShopInventory) exposes
        // on run.currentRoom.shop.inventory.*Entries[].id — that is the id the browser sends back.
        var choices = new List<ResolvedShopChoice>();
        AddInventoryEntries(choices, inventory, "CharacterCardEntries", "card", Sts2ShopIds.CharacterCardCollection, defaultPlayerId);
        AddInventoryEntries(choices, inventory, "ColorlessCardEntries", "card", Sts2ShopIds.ColorlessCardCollection, defaultPlayerId);
        AddInventoryEntries(choices, inventory, "RelicEntries", "relic", Sts2ShopIds.RelicCollection, defaultPlayerId);
        AddInventoryEntries(choices, inventory, "PotionEntries", "potion", Sts2ShopIds.PotionCollection, defaultPlayerId);

        if (Sts2LiveIntrospection.GetMemberValue(inventory, "CardRemovalEntry") is { } cardRemovalEntry)
        {
            choices.Add(CreateInventoryChoice(
                cardRemovalEntry,
                defaultPlayerId,
                "card-removal",
                Sts2ShopIds.CardRemovalEntryIdValue,
                "Card Removal",
                "remove-card",
                isFlowChoice: false));
        }

        choices.Add(CreateFlowChoice(defaultPlayerId));
        if (choices.Count == 1)
        {
            AddNotice(
                notices,
                "shop-inventory-empty",
                "The live merchant inventory was present but did not expose any purchasable entries.");
        }

        return choices;
    }

    private static bool TryResolveLeaveChoice(
        object shopScreen,
        string? defaultPlayerId,
        ICollection<StateNoticeSnapshot>? notices,
        out ResolvedShopChoice choice)
    {
        choice = default!;

        if (ResolveBackButton(shopScreen) is not Control control
            || !control.Visible
            || !ResolveIsExecutable(control, defaultValue: false))
        {
            return false;
        }

        var playerId = ResolveFlowOwnerPlayerId(defaultPlayerId, notices);
        var isEmbeddedInventory = Sts2LiveIntrospection.IsType(shopScreen, FakeMerchantInventoryType);
        var choiceId = isEmbeddedInventory
            ? Sts2ShopIds.CloseInventoryChoiceId()
            : Sts2ShopIds.LeaveChoiceId();
        var label = isEmbeddedInventory ? "Close Inventory" : "Leave Shop";
        var preferredAction = isEmbeddedInventory ? "close-shop-inventory" : "leave-shop";
        var arguments = new ActionArgumentsSnapshot(
            playerId,
            null,
            null,
            null,
            null,
            null,
            IntentKind: preferredAction);
        var checkedHookPaths = CheckedHookPathsForChoice(isEmbeddedInventory ? "close-inventory" : "leave");
        choice = new ResolvedShopChoice(
            new ChoiceSnapshot(
                Id: choiceId,
                Label: label,
                Kind: "shop-flow",
                Provisional: false,
                OwnerPlayerId: playerId,
                ChoiceKind: "shop-flow",
                IntentKind: preferredAction,
                Perspective: ResolvePerspective(playerId),
                PreferredAction: preferredAction,
                Arguments: arguments,
                Enabled: true,
                PreferredActionRef: new VisibleActionReferenceSnapshot(
                    Id: Sts2ActionIds.Intent("shop", preferredAction, choiceId),
                    Label: preferredAction,
                    Enabled: true,
                    OwnerPlayerId: playerId,
                    ActionKind: ResolveActionKind(preferredAction),
                    IntentKind: preferredAction,
                    Arguments: arguments,
                    LegalityStatus: ActionLegalityKind.Legal,
                    Perspective: ResolvePerspective(playerId),
                    CheckedHookPaths: checkedHookPaths),
                LegalityStatus: ActionLegalityKind.Legal,
                CheckedHookPaths: checkedHookPaths),
            Entry: null,
            Control: control,
            PlayerId: playerId,
            IsExecutable: true,
            IsStocked: true,
            EnoughGold: true,
            SlotKind: isEmbeddedInventory ? "close-inventory" : "leave",
            ItemModelId: null,
            Cost: null,
            RequiresCancelableWrapper: false,
            IsFlowChoice: true,
            PreferredAction: preferredAction);
        return true;
    }

    private static void AddInventoryEntries(
        List<ResolvedShopChoice> choices,
        object inventory,
        string memberName,
        string kindKey,
        string collection,
        string? defaultPlayerId)
    {
        var index = 0;
        foreach (var entry in EnumerateObjects(Sts2LiveIntrospection.GetMemberValue(inventory, memberName)))
        {
            var stableItemKey = ResolveInventoryEntryStableItemKey(entry, kindKey);
            var choiceId = Sts2ShopIds.InventoryEntryId(collection, index);
            choices.Add(CreateInventoryChoice(
                entry,
                defaultPlayerId,
                kindKey,
                choiceId,
                stableItemKey,
                ResolvePreferredAction(kindKey),
                isFlowChoice: false));
            index++;
        }
    }

    private static ResolvedShopChoice CreateInventoryChoice(
        object entry,
        string? playerId,
        string kindKey,
        string choiceId,
        string label,
        string preferredAction,
        bool isFlowChoice)
    {
        var isStocked = ResolveBool(entry, "IsStocked", defaultValue: true);
        var enoughGold = ResolveBool(entry, "EnoughGold", defaultValue: true);
        var enabled = isStocked && enoughGold;
        var disabledReason = ResolveDisabledReason(isStocked, enoughGold);
        var values = new Dictionary<string, string> { ["shopItemId"] = choiceId };
        if (kindKey == "relic")
        {
            values["relicId"] = choiceId;
        }

        var arguments = new ActionArgumentsSnapshot(
            playerId,
            kindKey == "card" ? choiceId : null,
            null,
            preferredAction == "choose" ? choiceId : null,
            null,
            null,
            kindKey == "potion" ? choiceId : null,
            IntentKind: preferredAction,
            Values: values);

        return new ResolvedShopChoice(
            new ChoiceSnapshot(
                Id: choiceId,
                Label: label,
                Kind: ResolveChoiceKind(kindKey),
                Provisional: false,
                OwnerPlayerId: playerId,
                ChoiceKind: ResolveChoiceKind(kindKey),
                IntentKind: preferredAction,
                Perspective: ResolvePerspective(playerId),
                PreferredAction: preferredAction,
                Arguments: arguments,
                Enabled: enabled,
                DisabledReason: disabledReason,
                LegalityStatus: enabled ? ActionLegalityKind.Legal : ActionLegalityKind.Illegal),
            Entry: entry,
            Control: null,
            PlayerId: playerId,
            IsExecutable: enabled,
            IsStocked: isStocked,
            EnoughGold: enoughGold,
            SlotKind: kindKey,
            ItemModelId: ResolveInventoryEntryStableItemKey(entry, kindKey),
            Cost: TryResolveInt(Sts2LiveIntrospection.GetMemberValue(entry, "Price"))
                ?? TryResolveInt(Sts2LiveIntrospection.GetMemberValue(entry, "Cost")),
            RequiresCancelableWrapper: kindKey == "card-removal",
            IsFlowChoice: isFlowChoice,
            PreferredAction: preferredAction);
    }

    private static ResolvedShopChoice CreateFlowChoice(string? playerId)
    {
        var choiceId = Sts2ShopIds.LeaveChoiceId();
        var preferredAction = "leave-shop";
        var arguments = new ActionArgumentsSnapshot(playerId, null, null, null, null, null, IntentKind: preferredAction);
        return new ResolvedShopChoice(
            new ChoiceSnapshot(
                Id: choiceId,
                Label: "Leave Shop",
                Kind: "shop-flow",
                Provisional: false,
                OwnerPlayerId: playerId,
                ChoiceKind: "shop-flow",
                IntentKind: preferredAction,
                Perspective: ResolvePerspective(playerId),
                PreferredAction: preferredAction,
                Arguments: arguments,
                Enabled: true,
                LegalityStatus: ActionLegalityKind.Legal),
            Entry: null,
            Control: null,
            PlayerId: playerId,
            IsExecutable: true,
            IsStocked: true,
            EnoughGold: true,
            SlotKind: "leave",
            ItemModelId: null,
            Cost: null,
            RequiresCancelableWrapper: false,
            IsFlowChoice: true,
            PreferredAction: preferredAction);
    }

    private static string ResolveInventoryEntryStableItemKey(object entry, string kindKey)
    {
        return ResolveModelId(Sts2LiveIntrospection.GetMemberValue(entry, "Model"))
            ?? ResolveModelId(Sts2LiveIntrospection.GetMemberValue(
                Sts2LiveIntrospection.GetMemberValue(entry, "CreationResult"),
                "Card"))
            ?? kindKey;
    }

    private static object? ResolveBackButton(object shopScreen)
    {
        var memberButton = Sts2LiveIntrospection.GetMemberValue(shopScreen, "_backButton");
        if (memberButton is not null)
        {
            return memberButton;
        }

        return FindDescendant(shopScreen, static child =>
            string.Equals(Sts2LiveIntrospection.GetMemberValue(child, "Name")?.ToString(), "BackButton", StringComparison.Ordinal)
            || Sts2LiveIntrospection.IsType(child, "MegaCrit.Sts2.Core.Nodes.CommonUi.NBackButton"));
    }

    private static string ResolveOwnerPlayerId(
        object slot,
        object entry,
        string? defaultPlayerId,
        ICollection<StateNoticeSnapshot>? notices)
    {
        var player = Sts2LiveIntrospection.GetMemberValue(slot, "Player")
            ?? Sts2LiveIntrospection.GetMemberValue(entry, "_player");
        if (player is not null)
        {
            return Sts2CombatIds.PlayerId(player);
        }

        if (!string.IsNullOrWhiteSpace(defaultPlayerId))
        {
            AddNotice(
                notices,
                "shop-choice-player-fallback",
                "Shop choice owners fall back to the local player id when the merchant slot does not expose a player.");
            return defaultPlayerId!;
        }

        AddNotice(
            notices,
            "shop-choice-player-fallback",
            "Shop choice owners fall back to an unknown player id when the merchant slot does not expose a player.");
        return "p:unknown";
    }

    private static string ResolveFlowOwnerPlayerId(
        string? defaultPlayerId,
        ICollection<StateNoticeSnapshot>? notices)
    {
        if (!string.IsNullOrWhiteSpace(defaultPlayerId))
        {
            return defaultPlayerId!;
        }

        AddNotice(
            notices,
            "shop-choice-player-fallback",
            "Shop choice owners fall back to an unknown player id when the active shop screen does not expose a local player.");
        return "p:unknown";
    }

    private static string ResolveStableItemKey(
        object slot,
        object entry,
        string kindKey,
        ICollection<StateNoticeSnapshot>? notices)
    {
        string? stableId = kindKey switch
        {
            "card" => ResolveModelId(Sts2LiveIntrospection.GetMemberValue(
                Sts2LiveIntrospection.GetMemberValue(entry, "CreationResult"),
                "Card")),
            "potion" => ResolveModelId(Sts2LiveIntrospection.GetMemberValue(entry, "Model")),
            "relic" => ResolveModelId(Sts2LiveIntrospection.GetMemberValue(entry, "Model")),
            _ => null,
        };

        if (!string.IsNullOrWhiteSpace(stableId))
        {
            return stableId!;
        }

        AddNotice(
            notices,
            "shop-choice-id-fallback",
            "Shop choice ids fall back to slot-type stability when merchant models do not expose stable ids.");
        return slot.GetType().Name.ToLowerInvariant();
    }

    private static string ResolveLabel(object slot, object entry, string kindKey)
    {
        var name = kindKey switch
        {
            "card" => ResolveModelTitle(Sts2LiveIntrospection.GetMemberValue(
                Sts2LiveIntrospection.GetMemberValue(entry, "CreationResult"),
                "Card")),
            "potion" => ResolveModelTitle(Sts2LiveIntrospection.GetMemberValue(entry, "Model")),
            "relic" => ResolveModelTitle(Sts2LiveIntrospection.GetMemberValue(entry, "Model")),
            "card-removal" => "Remove a Card",
            _ => slot.GetType().Name,
        };
        var cost = TryResolveInt(Sts2LiveIntrospection.GetMemberValue(entry, "Cost"));
        if (kindKey == "card-removal")
        {
            return cost.HasValue ? $"{name} ({cost.Value} gold)" : name;
        }

        return cost.HasValue ? $"Buy {name} ({cost.Value} gold)" : $"Buy {name}";
    }

    private static string ResolveChoiceKind(string kindKey)
        => kindKey switch
        {
            "card-removal" => "shop-card-removal",
            _ => $"shop-{kindKey}",
        };

    private static string ResolvePreferredAction(string kindKey)
        => kindKey switch
        {
            "card" => "buy-card",
            "relic" => "buy-relic",
            "potion" => "buy-potion",
            "card-removal" => "remove-card",
            _ => "choose",
        };

    private static SemanticActionKind ResolveActionKind(string action)
        => action switch
        {
            "buy-card" => SemanticActionKind.BuyCard,
            "buy-relic" => SemanticActionKind.BuyRelic,
            "buy-potion" => SemanticActionKind.BuyPotion,
            "remove-card" => SemanticActionKind.RemoveCard,
            "leave-shop" => SemanticActionKind.LeaveShop,
            "close-shop-inventory" => SemanticActionKind.CloseShopInventory,
            _ => SemanticActionKind.Choose,
        };

    private static string? ResolveDisabledReason(bool isStocked, bool enoughGold)
        => !isStocked
            ? "out-of-stock"
            : !enoughGold
                ? "not-enough-gold"
                : null;

    private static string? ResolvePerspective(string? playerId)
        => string.IsNullOrWhiteSpace(playerId) ? null : "local";

    private static IReadOnlyList<string> CheckedHookPathsForChoice(string kindKey)
        => kindKey switch
        {
            "leave" or "close-inventory" => ["shopScreen.Close", "backButton.OnRelease"],
            "card-removal" => ["merchantEntry.OnTryPurchaseWrapper(inventory, false, true)"],
            "card" or "relic" or "potion" => ["merchantEntry.OnTryPurchaseWrapper(inventory, false)"],
            _ => ["visible_choices"],
        };

    private static string ResolveKindKey(object slot)
    {
        return slot.GetType().Name switch
        {
            "NMerchantCard" => "card",
            "NMerchantPotion" => "potion",
            "NMerchantRelic" => "relic",
            "NMerchantCardRemoval" => "card-removal",
            _ => "slot",
        };
    }

    private static string ResolveModelTitle(object? model)
    {
        var title = Normalize(Sts2LiveIntrospection.GetMemberValue(model, "Title")?.ToString());
        if (!string.IsNullOrWhiteSpace(title))
        {
            return title!;
        }

        var modelId = ResolveModelId(model);
        if (!string.IsNullOrWhiteSpace(modelId))
        {
            return modelId!;
        }

        return model?.GetType().Name ?? "unknown";
    }

    private static string? ResolveModelId(object? model)
    {
        var id = Sts2LiveIntrospection.GetMemberValue(model, "Id");
        return Normalize(Sts2LiveIntrospection.GetMemberValue(id, "Entry")?.ToString())
            ?? Normalize(id?.ToString());
    }

    private static bool ResolveBool(object target, string memberName, bool defaultValue)
    {
        return Sts2LiveIntrospection.GetMemberValue(target, memberName) switch
        {
            bool value => value,
            _ => defaultValue,
        };
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

    private static bool ResolveVisible(object control)
        => Sts2LiveIntrospection.GetMemberValue(control, "Visible") switch
        {
            bool visible => visible,
            _ => true,
        };

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

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.ReplaceLineEndings(" ").Trim();

    private static void AddNotice(
        ICollection<StateNoticeSnapshot>? notices,
        string code,
        string message)
    {
        if (notices is null || notices.Any(notice => notice.Code == code))
        {
            return;
        }

        Sts2StateNotice.AddPartialOnce(notices, code, message, "choices", nameof(Sts2ShopScreenInspector));
    }

    private static object? FindDescendant(object? root, Func<object, bool> predicate)
    {
        if (root is null)
        {
            return null;
        }

        foreach (var child in EnumerateChildren(root))
        {
            if (predicate(child))
            {
                return child;
            }

            var nested = FindDescendant(child, predicate);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static IEnumerable<object> EnumerateObjects(object? source)
    {
        if (source is not IEnumerable entries)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            if (entry is not null)
            {
                yield return entry;
            }
        }
    }

    private static IEnumerable<object> EnumerateChildren(object target)
    {
        var children = Sts2LiveIntrospection.GetMemberValue(target, "Children") as IEnumerable;
        if (children is not null)
        {
            foreach (var child in children)
            {
                if (child is not null)
                {
                    yield return child;
                }
            }

            yield break;
        }

        if (target is Node node)
        {
            foreach (var child in node.GetChildren())
            {
                if (child is not null)
                {
                    yield return child;
                }
            }
        }
    }
}

internal sealed record ResolvedShopChoice(
    ChoiceSnapshot Snapshot,
    object? Entry,
    object? Control,
    string? PlayerId,
    bool IsExecutable,
    bool IsStocked,
    bool EnoughGold,
    string SlotKind,
    string? ItemModelId,
    int? Cost,
    bool RequiresCancelableWrapper,
    bool IsFlowChoice,
    string PreferredAction);
