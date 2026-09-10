using System.Globalization;

namespace Spirectl.Sts2;

public static class Sts2ShopIds
{
    public const string LeaveChoiceIdValue = "shop:leave";
    public const string CloseInventoryChoiceIdValue = "shop:close-inventory";

    // Canonical, browser-facing shop-item id. This is the single source of shop-item identity: it is
    // minted from the inventory MODEL (collection + per-collection index) by both the state snapshot
    // (Sts2StateProvider.ResolveShopInventory) and ResolveInventoryChoices, so the id the browser reads
    // off state.run.currentRoom.shop.inventory.*Entries[].id is exactly the id the action handler
    // resolves. Distinct from the 5-part screen-slot ChoiceId below, which the action/availableActions
    // world derives from live screen slots (it cannot know the inventory collection or per-collection
    // index, so it cannot mint this id).
    public const string CardRemovalEntryIdValue = "shop:card-removal";

    public const string CharacterCardCollection = "character-card";
    public const string ColorlessCardCollection = "colorless-card";
    public const string RelicCollection = "relic";
    public const string PotionCollection = "potion";
    public const string CardRemovalCollection = "card-removal";

    public static string InventoryEntryId(string collection, int index)
        => string.Create(CultureInfo.InvariantCulture, $"shop:{collection}:{index}");

    // Map an inventory collection prefix to the action handler's slot kind / expectedSlotKind
    // (both character and colorless cards buy through the "card" kind).
    public static string KindForCollection(string collection)
        => collection switch
        {
            CharacterCardCollection or ColorlessCardCollection => "card",
            RelicCollection => "relic",
            PotionCollection => "potion",
            CardRemovalCollection => "card-removal",
            _ => collection,
        };

    public static bool TryParseInventoryEntryId(string? id, out string collection, out int index)
    {
        collection = string.Empty;
        index = -1;

        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        if (string.Equals(id, CardRemovalEntryIdValue, StringComparison.Ordinal))
        {
            collection = CardRemovalCollection;
            index = 0;
            return true;
        }

        var parts = id.Split(':');
        if (parts.Length != 3
            || !string.Equals(parts[0], "shop", StringComparison.Ordinal)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out index))
        {
            return false;
        }

        collection = parts[1];
        return IsInventoryCollection(collection);
    }

    public static bool IsInventoryCollection(string collection)
        => string.Equals(collection, CharacterCardCollection, StringComparison.Ordinal)
           || string.Equals(collection, ColorlessCardCollection, StringComparison.Ordinal)
           || string.Equals(collection, RelicCollection, StringComparison.Ordinal)
           || string.Equals(collection, PotionCollection, StringComparison.Ordinal);

    public static string ChoiceId(string playerId, string kind, string stableId, int slotIndex)
        => string.Create(CultureInfo.InvariantCulture, $"shop:{playerId}:{kind}:{stableId}:{slotIndex}");

    public static string CardRemovalChoiceId(string playerId, int slotIndex)
        => string.Create(CultureInfo.InvariantCulture, $"shop:{playerId}:card-removal:{slotIndex}");

    public static string LeaveChoiceId()
        => LeaveChoiceIdValue;

    public static string CloseInventoryChoiceId()
        => CloseInventoryChoiceIdValue;

    public static bool IsLeaveChoiceId(string? choiceId)
        => string.Equals(choiceId, LeaveChoiceIdValue, StringComparison.Ordinal);

    public static bool IsCloseInventoryChoiceId(string? choiceId)
        => string.Equals(choiceId, CloseInventoryChoiceIdValue, StringComparison.Ordinal);

    public static bool TryParseShopItemChoiceId(
        string? choiceId,
        out string playerId,
        out string kind,
        out string stableId,
        out int slotIndex)
    {
        playerId = string.Empty;
        kind = string.Empty;
        stableId = string.Empty;
        slotIndex = -1;

        if (string.IsNullOrWhiteSpace(choiceId))
        {
            return false;
        }

        var parts = choiceId.Split(':');
        if (parts.Length != 5
            || !string.Equals(parts[0], "shop", StringComparison.Ordinal)
            || !int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out slotIndex))
        {
            return false;
        }

        playerId = parts[1];
        kind = parts[2];
        stableId = parts[3];
        return IsItemKind(kind)
            && !string.IsNullOrWhiteSpace(playerId)
            && !string.IsNullOrWhiteSpace(stableId);
    }

    public static bool TryParseCardRemovalChoiceId(
        string? choiceId,
        out string playerId,
        out int slotIndex)
    {
        playerId = string.Empty;
        slotIndex = -1;

        if (string.IsNullOrWhiteSpace(choiceId))
        {
            return false;
        }

        var parts = choiceId.Split(':');
        if (parts.Length != 4
            || !string.Equals(parts[0], "shop", StringComparison.Ordinal)
            || !string.Equals(parts[2], "card-removal", StringComparison.Ordinal)
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out slotIndex))
        {
            return false;
        }

        playerId = parts[1];
        return !string.IsNullOrWhiteSpace(playerId);
    }

    public static bool IsShopChoiceId(string? choiceId)
        => IsLeaveChoiceId(choiceId)
           || IsCloseInventoryChoiceId(choiceId)
           || TryParseShopItemChoiceId(choiceId, out _, out _, out _, out _)
           || TryParseCardRemovalChoiceId(choiceId, out _, out _)
           || TryParseInventoryEntryId(choiceId, out _, out _);

    private static bool IsItemKind(string kind)
        => string.Equals(kind, "card", StringComparison.Ordinal)
           || string.Equals(kind, "relic", StringComparison.Ordinal)
           || string.Equals(kind, "potion", StringComparison.Ordinal);
}
