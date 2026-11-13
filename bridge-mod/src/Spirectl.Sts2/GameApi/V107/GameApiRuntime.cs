using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace Spirectl.Sts2.Live.GameApi;

/// <summary>Model metadata whose name differs by lane.</summary>
internal static class GameApiMonster
{
    internal static bool IsHealthBarVisible(MonsterModel model) => model.IsHealthBarVisible;
}

#if !SPIRECTL_PROFILE_EMBEDDED
/// <summary>The overlay marker for a visible card-selection screen.</summary>
internal static class GameApiCardSelection
{
    internal static bool IsCardSelection(object? overlay) => overlay is ICardSelector;
}

/// <summary>The lane's cancel-end-turn entry point.</summary>
internal static class GameApiCombatActions
{
    internal static bool CanUndoReadyToEndTurn(CombatManager manager, Player player)
        => true;

    internal static bool UndoReadyToEndTurn(CombatManager manager, Player player)
    {
        manager.UndoReadyToEndTurn(player);
        return true;
    }
}

/// <summary>The inventory model behind the active merchant room.</summary>
internal static class GameApiMerchant
{
    internal static object? RoomInventory(object currentRoom)
        => Sts2LiveIntrospection.InvokeMethod(currentRoom, "GetLocalInventory");

    internal static MerchantInventory? LocalInventory(NMerchantRoom? room)
        => room?.Room?.GetLocalInventory();
}

/// <summary>Lobby unlock state shared with synthetic and fixture seats.</summary>
internal static class GameApiLobbyPlayer
{
    internal static int? MaxBlitzAscensionUnlocked(object player) => null;

    internal const string AscensionMember = "maxMultiplayerAscensionUnlocked";

    internal static object? MaxMultiplayerAscensionUnlocked(object player)
        => Sts2LiveIntrospection.GetMemberValue(player, AscensionMember);

    internal static bool RefreshChanged(object screen, GameLobbyPlayer player)
        => Sts2LiveIntrospection.TryInvokeMethod(screen, "PlayerChanged", player, false);

    internal static void CopyUnlockState(ref GameLobbyPlayer target, GameLobbyPlayer source)
    {
        target.unlockState = source.unlockState;
        target.maxMultiplayerAscensionUnlocked = source.maxMultiplayerAscensionUnlocked;
    }
}
#endif
