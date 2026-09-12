using System.Reflection;
using MegaCrit.Sts2.Core.Bindings.MegaSpine;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace Spirectl.Sts2.Live.GameApi;

/// <summary>Which API lane these files are, for diagnostics and refusal messages.</summary>
internal static class GameApiLane
{
    internal const string Name = "v111";
}

/// <summary>Per-player members that differ by lane.</summary>
internal static class GameApiPlayer
{
    /// <summary>Whether this player may take a potion out of a slot right now.</summary>
    internal static bool CanRemoveOrUsePotions(Player player) => player.CanUseOrRemovePotions;
}

/// <summary>Run-lobby roster members that differ by lane.</summary>
internal static class GameApiLobby
{
    /// <summary>The net ids the in-run lobby currently holds.</summary>
    internal static IEnumerable<ulong> ConnectedPlayerIds(RunLobby lobby) => lobby.PlayerIds;
}

/// <summary>Encounter-model members that differ by lane.</summary>
internal static class GameApiEncounter
{
    /// <summary>
    /// False: this game build carries no debug-encounter flag at all, and there is no successor member to
    /// read. Callers must say so on their notice channel rather than reporting the constant as game truth.
    /// </summary>
    internal const bool IsDebugEncounterSupported = false;

    internal static bool IsDebugEncounter(EncounterModel model)
    {
        _ = model;
        return false;
    }
}

/// <summary>
/// Game member names the bridge reads BY NAME (through <see cref="Sts2LiveIntrospection"/>) rather than by
/// binding. A rename here is invisible to the compiler, so the names are pinned per lane and checked at
/// startup by <see cref="Sts2GameApiProbe"/> instead of falling back to a plausible default.
/// </summary>
internal static class GameApiNames
{
    /// <summary>The start-run lobby's own player cap. Private on this build; the reader searches NonPublic.</summary>
    internal const string LobbyMaxPlayers = "_maxPlayers";

    /// <summary>A lobby's roster of net ids. Declared by the in-run and load-run lobbies only.</summary>
    internal const string LobbyPlayerIds = "PlayerIds";

    /// <summary>Mirrors <see cref="GameApiPlayer.CanRemoveOrUsePotions"/> for the by-name read path.</summary>
    internal const string PlayerCanRemoveOrUsePotions = "CanUseOrRemovePotions";

    /// <summary>Mirrors <see cref="GameApiMainMenu.BlurBackstop"/>.</summary>
    internal const string MainMenuBlurBackstop = "BlurBackstop";
}

/// <summary>Game hook entry points whose parameter list differs by lane.</summary>
internal static class GameApiHooks
{
    /// <summary>
    /// The game's damage-modifier funnel with the bridge's stable argument list. The bridge only ever wants
    /// the returned number, so the funnel's <c>modifiers</c> out-parameter is discarded here.
    /// </summary>
    /// <remarks>
    /// This build's funnel also takes the <see cref="CardPlay"/> a card is being played through. The bridge
    /// computes an IN-HAND preview, where no play exists yet, and the game's own preview producers pass
    /// <see langword="null"/> in that position for the same reason — so null here is the game's own preview
    /// argument, not a stand-in.
    /// </remarks>
    internal static decimal ModifyDamage(
        IRunState runState,
        ICombatState? combatState,
        Creature? target,
        Creature? dealer,
        decimal damage,
        ValueProp props,
        CardModel? cardSource,
        ModifyDamageHookType modifyDamageHookType,
        CardPreviewMode previewMode)
        => Hook.ModifyDamage(
            runState,
            combatState,
            target,
            dealer,
            damage,
            props,
            cardSource,
            null,
            modifyDamageHookType,
            previewMode,
            out _);

    /// <summary>
    /// The combat-manager predicate the end-turn readiness postfix attaches to: the member the game's own
    /// turn loop consults when deciding that every seat has ended its turn. On this build that is the
    /// turn-state overload; the public parameterless one is a wrapper the turn loop no longer goes through,
    /// so patching it would leave the postfix off the decision path.
    /// </summary>
    /// <remarks>
    /// Resolved by parameter type NAME because the turn-state type is internal to the game assembly and
    /// cannot be named from here. <see cref="GameApiManifest"/> requires the same shape at startup.
    /// </remarks>
    internal static MethodInfo? AllPlayersReadyToEndTurnTarget()
        => typeof(CombatManager)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method =>
                string.Equals(method.Name, nameof(CombatManager.AllPlayersReadyToEndTurn), StringComparison.Ordinal)
                && method.GetParameters() is [{ ParameterType.Name: TurnStateTypeName }]);

    /// <summary>The combat turn-state type's simple name; internal to the game assembly.</summary>
    internal const string TurnStateTypeName = "CombatTurnState";
}

/// <summary>Spine binding members whose shape differs by lane.</summary>
internal static class GameApiSpine
{
    /// <summary>
    /// Play <paramref name="animationName"/> on <paramref name="trackId"/> and hand back the track entry the
    /// caller needs in order to seek it. This build's setter returns nothing and documents
    /// <c>GetCurrent(trackId)</c> as the way to reach the entry it just made current.
    /// </summary>
    internal static MegaTrackEntry? SetAnimation(
        MegaAnimationState state,
        string animationName,
        bool loop = true,
        int trackId = 0)
    {
        state.SetAnimation(animationName, loop, trackId);
        return state.GetCurrent(trackId);
    }
}

/// <summary>Main-menu members whose visibility differs by lane.</summary>
internal static class GameApiMainMenu
{
    /// <summary>
    /// The full-screen blur panel the menu puts behind a submenu. A <c>Control</c> when present. Private on
    /// this build, so it is read by name; <see cref="GameApiManifest"/> requires the name at startup.
    /// </summary>
    internal static object? BlurBackstop(NMainMenu mainMenu)
        => Sts2LiveIntrospection.GetMemberValue(mainMenu, GameApiNames.MainMenuBlurBackstop);
}

/// <summary>Net-service construction, whose arguments differ by lane.</summary>
internal static class GameApiNetHost
{
    /// <summary>
    /// A host net service configured the way the game's own host-start paths configure one: this build's
    /// service carries the local peer's version/mod record, and the game builds that record with
    /// <see cref="PeerVersionInfo.LocalDefault"/> at every one of its own host-start sites.
    /// </summary>
    internal static NetHostGameService Create() => new(PeerVersionInfo.LocalDefault());
}
