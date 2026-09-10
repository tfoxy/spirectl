namespace Spirectl.Sts2;

public static class Sts2SupportedScreenIds
{
    public const string GameNodeTypePrefix = "MegaCrit.Sts2.Core.Nodes.";

    public const string MainMenuScreenId = "Screens.MainMenu.NMainMenu";
    public const string StartRunLobbyScreenId = "Screens.CharacterSelect.NCharacterSelectScreen";
    public const string LoadRunLobbyScreenId = "Screens.CharacterSelect.NMultiplayerLoadGameScreen";
    public const string MapScreenId = "Screens.Map.NMapScreen";
    public const string CardRewardSelectionScreenId = "Screens.CardSelection.NCardRewardSelectionScreen";
    public const string ChooseACardSelectionScreenId = "Screens.CardSelection.NChooseACardSelectionScreen";
    public const string SimpleCardSelectionScreenId = "Screens.CardSelection.NSimpleCardSelectScreen";
    public const string DeckCardSelectionScreenId = "Screens.CardSelection.NDeckCardSelectScreen";
    public const string DeckUpgradeSelectionScreenId = "Screens.CardSelection.NDeckUpgradeSelectScreen";
    public const string DeckTransformSelectionScreenId = "Screens.CardSelection.NDeckTransformSelectScreen";
    public const string DeckEnchantSelectionScreenId = "Screens.CardSelection.NDeckEnchantSelectScreen";
    public const string BundleSelectionScreenId = "Screens.CardSelection.NChooseABundleSelectionScreen";
    public const string RelicSelectionScreenId = "Screens.NChooseARelicSelection";
    public const string RestSiteRoomScreenId = "Rooms.NRestSiteRoom";
    public const string EventRoomScreenId = "Rooms.NEventRoom";
    public const string TreasureRoomScreenId = "Rooms.NTreasureRoom";
    public const string TreasureRoomRelicCollectionScreenId = "Screens.TreasureRoomRelic.NTreasureRoomRelicCollection";
    public const string ShopScreenId = "Screens.Shops.NMerchantInventory";
    public const string FakeMerchantInventoryScreenId = "Screens.Shops.NFakeMerchantInventory";
    public const string RewardsScreenId = "Screens.NRewardsScreen";
    public const string CrystalSphereScreenId = "Events.Custom.CrystalSphere.NCrystalSphereScreen";
    public const string GameOverScreenId = "Screens.GameOverScreen.NGameOverScreen";

    private const string MainMenuScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.MainMenu.NMainMenu";
    private const string StartRunLobbyScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCharacterSelectScreen";
    private const string LoadRunLobbyScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NMultiplayerLoadGameScreen";
    private const string MapScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen";
    private const string CardRewardSelectionScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCardRewardSelectionScreen";
    private const string ChooseACardSelectionScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NChooseACardSelectionScreen";
    private const string SimpleCardSelectionScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NSimpleCardSelectScreen";
    private const string DeckCardSelectionScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckCardSelectScreen";
    private const string DeckUpgradeSelectionScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckUpgradeSelectScreen";
    private const string DeckTransformSelectionScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckTransformSelectScreen";
    private const string DeckEnchantSelectionScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckEnchantSelectScreen";
    private const string BundleSelectionScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NChooseABundleSelectionScreen";
    private const string RelicSelectionScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.NChooseARelicSelection";
    private const string RestSiteRoomType = "MegaCrit.Sts2.Core.Nodes.Rooms.NRestSiteRoom";
    private const string EventRoomType = "MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom";
    private const string TreasureRoomType = "MegaCrit.Sts2.Core.Nodes.Rooms.NTreasureRoom";
    private const string TreasureRoomRelicCollectionType = "MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic.NTreasureRoomRelicCollection";
    private const string ShopScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantInventory";
    private const string FakeMerchantInventoryType = "MegaCrit.Sts2.Core.Nodes.Screens.Shops.NFakeMerchantInventory";
    private const string RewardsScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen";
    private const string CrystalSphereScreenType = "MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen";
    private const string GameOverScreenType = "MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen.NGameOverScreen";

    public static bool TryResolve(object? screenObject, out SupportedScreenIdMatch? match)
    {
        match = null;
        if (screenObject is null)
        {
            return false;
        }

        if (IsTypeOrSubtype(screenObject, MainMenuScreenType))
        {
            match = new SupportedScreenIdMatch(ScreenIdFor(screenObject), "Main Menu");
            return true;
        }

        if (IsStartRunLobbyScreen(screenObject))
        {
            match = new SupportedScreenIdMatch(ScreenIdFor(screenObject), "Character Select");
            return true;
        }

        if (IsLoadRunLobbyScreen(screenObject))
        {
            match = new SupportedScreenIdMatch(ScreenIdFor(screenObject), "Load Run Lobby");
            return true;
        }

        if (IsTypeOrSubtype(screenObject, MapScreenType))
        {
            match = new SupportedScreenIdMatch(ScreenIdFor(screenObject), "Map");
            return true;
        }

        if (IsTypeOrSubtype(screenObject, SimpleCardSelectionScreenType))
        {
            match = new SupportedScreenIdMatch(ScreenIdFor(screenObject), "Simple Card Selection");
            return true;
        }

        if (IsTypeOrSubtype(screenObject, DeckCardSelectionScreenType)
            || IsTypeOrSubtype(screenObject, DeckUpgradeSelectionScreenType)
            || IsTypeOrSubtype(screenObject, DeckTransformSelectionScreenType)
            || IsTypeOrSubtype(screenObject, DeckEnchantSelectionScreenType))
        {
            match = new SupportedScreenIdMatch(ScreenIdFor(screenObject), "Deck Card Selection");
            return true;
        }

        if (IsTypeOrSubtype(screenObject, BundleSelectionScreenType))
        {
            match = new SupportedScreenIdMatch(ScreenIdFor(screenObject), "Choose a Bundle");
            return true;
        }

        if (IsTypeOrSubtype(screenObject, RelicSelectionScreenType))
        {
            match = new SupportedScreenIdMatch(ScreenIdFor(screenObject), "Choose a Relic");
            return true;
        }

        if (IsCardSelectionScreen(screenObject))
        {
            match = new SupportedScreenIdMatch(ScreenIdFor(screenObject), "Card Selection");
            return true;
        }

        if (IsTypeOrSubtype(screenObject, RestSiteRoomType))
        {
            match = new SupportedScreenIdMatch(ScreenIdFor(screenObject), "Rest Site");
            return true;
        }

        if (IsTypeOrSubtype(screenObject, EventRoomType))
        {
            match = new SupportedScreenIdMatch(ScreenIdFor(screenObject), "Event");
            return true;
        }

        if (IsTypeOrSubtype(screenObject, TreasureRoomType)
            || IsTypeOrSubtype(screenObject, TreasureRoomRelicCollectionType))
        {
            match = new SupportedScreenIdMatch(ScreenIdFor(screenObject), "Treasure Room");
            return true;
        }

        if (IsTypeOrSubtype(screenObject, ShopScreenType)
            || IsTypeOrSubtype(screenObject, FakeMerchantInventoryType))
        {
            match = new SupportedScreenIdMatch(ScreenIdFor(screenObject), "Shop");
            return true;
        }

        if (IsTypeOrSubtype(screenObject, RewardsScreenType))
        {
            match = new SupportedScreenIdMatch(ScreenIdFor(screenObject), "Rewards");
            return true;
        }

        if (IsTypeOrSubtype(screenObject, CrystalSphereScreenType))
        {
            match = new SupportedScreenIdMatch(ScreenIdFor(screenObject), "Crystal Sphere");
            return true;
        }

        // Run-terminal defeat/victory overlay. Pushed onto NOverlayStack, so the
        // screen locator's overlay path (which peeks the stack — the actual trigger)
        // and the active-screen path both resolve it here.
        if (IsTypeOrSubtype(screenObject, GameOverScreenType))
        {
            match = new SupportedScreenIdMatch(ScreenIdFor(screenObject), "Game Over");
            return true;
        }

        return false;
    }

    public static bool IsStartRunLobbyScreen(object? screenObject)
        => IsTypeOrSubtype(screenObject, StartRunLobbyScreenType);

    public static bool IsLoadRunLobbyScreen(object? screenObject)
        => IsTypeOrSubtype(screenObject, LoadRunLobbyScreenType);

    public static bool IsLobbyScreenType(string? screenType)
        => string.Equals(screenType, StartRunLobbyScreenId, StringComparison.Ordinal)
            || string.Equals(screenType, LoadRunLobbyScreenId, StringComparison.Ordinal);

    public static bool IsStartRunLobbyScreenType(string? screenType)
        => string.Equals(screenType, StartRunLobbyScreenId, StringComparison.Ordinal);

    public static bool IsLoadRunLobbyScreenType(string? screenType)
        => string.Equals(screenType, LoadRunLobbyScreenId, StringComparison.Ordinal);

    public static bool IsCardSelectionScreen(object? screenObject)
        => IsTypeOrSubtype(screenObject, CardRewardSelectionScreenType)
            || IsTypeOrSubtype(screenObject, ChooseACardSelectionScreenType)
            || IsTypeOrSubtype(screenObject, SimpleCardSelectionScreenType)
            || IsTypeOrSubtype(screenObject, DeckCardSelectionScreenType)
            || IsTypeOrSubtype(screenObject, DeckUpgradeSelectionScreenType)
            || IsTypeOrSubtype(screenObject, DeckTransformSelectionScreenType)
            || IsTypeOrSubtype(screenObject, DeckEnchantSelectionScreenType)
            || IsTypeOrSubtype(screenObject, BundleSelectionScreenType)
            || IsTypeOrSubtype(screenObject, RelicSelectionScreenType);

    public static bool IsMapScreenType(string? screenType)
        => string.Equals(screenType, MapScreenId, StringComparison.Ordinal);

    public static bool IsEventRoomScreenType(string? screenType)
        => string.Equals(screenType, EventRoomScreenId, StringComparison.Ordinal);

    public static bool IsCrystalSphereScreenType(string? screenType)
        => string.Equals(screenType, CrystalSphereScreenId, StringComparison.Ordinal);

    public static bool IsRestSiteScreenType(string? screenType)
        => string.Equals(screenType, RestSiteRoomScreenId, StringComparison.Ordinal);

    public static bool IsShopScreenType(string? screenType)
        => string.Equals(screenType, ShopScreenId, StringComparison.Ordinal)
            || string.Equals(screenType, FakeMerchantInventoryScreenId, StringComparison.Ordinal);

    public static bool IsRewardsScreenType(string? screenType)
        => string.Equals(screenType, RewardsScreenId, StringComparison.Ordinal);

    public static bool IsGameOverScreenType(string? screenType)
        => string.Equals(screenType, GameOverScreenId, StringComparison.Ordinal);

    public static bool IsCardSelectionFamily(string? screenType)
        => string.Equals(screenType, CardRewardSelectionScreenId, StringComparison.Ordinal)
            || string.Equals(screenType, ChooseACardSelectionScreenId, StringComparison.Ordinal)
            || string.Equals(screenType, SimpleCardSelectionScreenId, StringComparison.Ordinal)
            || string.Equals(screenType, DeckCardSelectionScreenId, StringComparison.Ordinal)
            || string.Equals(screenType, DeckUpgradeSelectionScreenId, StringComparison.Ordinal)
            || string.Equals(screenType, DeckTransformSelectionScreenId, StringComparison.Ordinal)
            || string.Equals(screenType, DeckEnchantSelectionScreenId, StringComparison.Ordinal)
            || string.Equals(screenType, BundleSelectionScreenId, StringComparison.Ordinal);

    public static bool IsSimpleCardSelectionScreenType(string? screenType)
        => string.Equals(screenType, SimpleCardSelectionScreenId, StringComparison.Ordinal);

    public static bool IsDeckCardSelectionScreenType(string? screenType)
        => string.Equals(screenType, DeckCardSelectionScreenId, StringComparison.Ordinal)
            || string.Equals(screenType, DeckUpgradeSelectionScreenId, StringComparison.Ordinal)
            || string.Equals(screenType, DeckTransformSelectionScreenId, StringComparison.Ordinal)
            || string.Equals(screenType, DeckEnchantSelectionScreenId, StringComparison.Ordinal);

    public static bool IsBundleSelectionScreenType(string? screenType)
        => string.Equals(screenType, BundleSelectionScreenId, StringComparison.Ordinal);

    public static bool IsTreasureRoomFamily(string? screenType)
        => string.Equals(screenType, TreasureRoomScreenId, StringComparison.Ordinal)
            || string.Equals(screenType, TreasureRoomRelicCollectionScreenId, StringComparison.Ordinal)
            || string.Equals(screenType, RelicSelectionScreenId, StringComparison.Ordinal);

    public static bool IsTreasureRoomScreenType(string? screenType)
        => string.Equals(screenType, TreasureRoomScreenId, StringComparison.Ordinal);

    public static bool IsRelicSelectionScreenType(string? screenType)
        => string.Equals(screenType, RelicSelectionScreenId, StringComparison.Ordinal)
            || string.Equals(screenType, TreasureRoomRelicCollectionScreenId, StringComparison.Ordinal);

    public static string ScreenIdFor(object screenObject)
        => ScreenIdFor(screenObject.GetType());

    public static string ScreenIdFor(Type type)
        => TypeNameChain(type)
            .Select(TryScreenIdFromFullTypeName)
            .FirstOrDefault(static id => !string.IsNullOrWhiteSpace(id))
            ?? type.Name;

    public static string? TryScreenIdFromFullTypeName(string? fullTypeName)
    {
        if (string.IsNullOrWhiteSpace(fullTypeName))
        {
            return null;
        }

        return fullTypeName.StartsWith(GameNodeTypePrefix, StringComparison.Ordinal)
            ? fullTypeName[GameNodeTypePrefix.Length..]
            : null;
    }

    private static bool IsTypeOrSubtype(object? target, string fullTypeName)
    {
        return target is not null && TypeNameChain(target.GetType()).Any(name => string.Equals(name, fullTypeName, StringComparison.Ordinal));
    }

    private static IEnumerable<string> TypeNameChain(Type? type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            yield return current.FullName ?? current.Name;
        }
    }
}

public sealed record SupportedScreenIdMatch(
    string ScreenType,
    string ScreenTitle);
