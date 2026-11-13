using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Runs;

namespace Spirectl.Sts2.Live.GameApi;

/// <summary>Model metadata whose name differs by lane.</summary>
internal static class GameApiMonster
{
    internal static bool IsHealthBarVisible(MonsterModel model) => model.IsInteractable;
}

#if !SPIRECTL_PROFILE_EMBEDDED
/// <summary>The overlay marker for a visible card-selection screen.</summary>
internal static class GameApiCardSelection
{
    internal static bool IsCardSelection(object? overlay) => overlay is ICardSelectionScreen;
}

/// <summary>The lane's cancel-end-turn entry point.</summary>
internal static class GameApiCombatActions
{
    internal static bool CanUndoReadyToEndTurn(CombatManager manager, Player player)
        => manager.CanUnEndTurn(player);

    internal static bool UndoReadyToEndTurn(CombatManager manager, Player player)
        => UndoReadyToEndTurn(
            () => manager.TryUndoReadyToEndTurn(player),
            () => manager.IsPlayerReadyToEndTurn(player));

    internal static bool UndoReadyToEndTurn(Action undo, Func<bool> isReady)
    {
        undo();
        return !isReady();
    }
}

/// <summary>The inventory model behind the active merchant room.</summary>
internal static class GameApiMerchant
{
    internal static object? RoomInventory(object currentRoom)
        => RoomInventory(currentRoom, () => RunManager.Instance?.MerchantSynchronizer?.GetLocalInventory());

    internal static object? RoomInventory(object currentRoom, Func<object?> localInventory)
    {
        _ = currentRoom;
        return localInventory();
    }

    internal static MerchantInventory? LocalInventory(NMerchantRoom? room)
        => room?.Inventory?.Inventory;
}

/// <summary>Lobby unlock state shared with synthetic and fixture seats.</summary>
internal static class GameApiLobbyPlayer
{
    internal const string AscensionMember = "maxStandardAscension";

    internal static object? MaxMultiplayerAscensionUnlocked(object player)
        => Sts2LiveIntrospection.GetMemberValue(player, AscensionMember);

    internal static bool RefreshChanged(object screen, GameLobbyPlayer player)
        => Sts2LiveIntrospection.TryInvokeMethod(screen, "PlayerChanged", player);

    internal static void CopyUnlockState(ref GameLobbyPlayer target, GameLobbyPlayer source)
    {
        target.unlockState = source.unlockState;
        target.maxStandardAscension = source.maxStandardAscension;
        target.maxBlitzAscension = source.maxBlitzAscension;
        target.isRotatingDread = source.isRotatingDread;
        target.isModded = source.isModded;
    }
}
#endif
