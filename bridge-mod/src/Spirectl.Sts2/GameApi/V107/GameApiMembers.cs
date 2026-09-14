using System.Reflection;
using MegaCrit.Sts2.Core.Animation;
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
    internal const string Name = "v107";
}

/// <summary>Per-player members that differ by lane.</summary>
internal static class GameApiPlayer
{
    /// <summary>Whether this player may take a potion out of a slot right now.</summary>
    internal static bool CanRemoveOrUsePotions(Player player) => player.CanRemovePotions;
}

/// <summary>Run-lobby roster members that differ by lane.</summary>
internal static class GameApiLobby
{
    /// <summary>The net ids the in-run lobby currently holds.</summary>
    internal static IEnumerable<ulong> ConnectedPlayerIds(RunLobby lobby) => lobby.ConnectedPlayerIds;
}

/// <summary>Encounter-model members that differ by lane.</summary>
internal static class GameApiEncounter
{
    /// <summary>False on a build where the game no longer carries the flag at all.</summary>
    internal const bool IsDebugEncounterSupported = true;

    internal static bool IsDebugEncounter(EncounterModel model) => model.IsDebugEncounter;
}

/// <summary>Combat-manager members that differ by lane.</summary>
internal static class GameApiCombat
{
    /// <summary>
    /// The players whose end-of-player-turn readiness action has already landed, as the game's own live set.
    /// Populated only between the host's own readiness and the side switch, so an empty set — and a
    /// <see langword="null"/> return, which callers must treat identically — means nothing is pending.
    /// v107 keeps the set on the combat manager itself.
    /// </summary>
    internal static IReadOnlyCollection<Player>? PlayersReadyToBeginEnemyTurn(CombatManager combatManager)
        => Sts2LiveIntrospection.GetMemberValue(
                combatManager,
                GameApiNames.CombatPlayersReadyToBeginEnemyTurn)
            as IReadOnlyCollection<Player>;
}

/// <summary>
/// Game member names the bridge reads BY NAME (through <see cref="Sts2LiveIntrospection"/>) rather than by
/// binding. A rename here is invisible to the compiler, so the names are pinned per lane and checked at
/// startup by <see cref="Sts2GameApiProbe"/> instead of falling back to a plausible default.
/// </summary>
internal static class GameApiNames
{
    /// <summary>
    /// Backs <see cref="GameApiCombat.PlayersReadyToBeginEnemyTurn"/>. A private field on this build, read
    /// straight off the combat manager.
    /// </summary>
    internal const string CombatPlayersReadyToBeginEnemyTurn = "_playersReadyToBeginEnemyTurn";

    /// <summary>The start-run lobby's own player cap.</summary>
    internal const string LobbyMaxPlayers = "MaxPlayers";

    /// <summary>A lobby's roster of net ids. Declared by the in-run and load-run lobbies only.</summary>
    internal const string LobbyPlayerIds = "ConnectedPlayerIds";

    /// <summary>Mirrors <see cref="GameApiPlayer.CanRemoveOrUsePotions"/> for the by-name read path.</summary>
    internal const string PlayerCanRemoveOrUsePotions = "CanRemovePotions";

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
            modifyDamageHookType,
            previewMode,
            out _);

    /// <summary>
    /// The combat-manager predicate the end-turn readiness postfix attaches to: the member the game's own
    /// turn loop consults when deciding that every seat has ended its turn.
    /// </summary>
    internal static MethodInfo? AllPlayersReadyToEndTurnTarget()
        => typeof(CombatManager).GetMethod(
            nameof(CombatManager.AllPlayersReadyToEndTurn),
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
}

/// <summary>Spine binding members whose shape differs by lane.</summary>
internal static class GameApiSpine
{
    /// <summary>
    /// Play <paramref name="animationName"/> on <paramref name="trackId"/> and hand back the track entry the
    /// caller needs in order to seek it. v107 returns the entry from the call itself.
    /// </summary>
    internal static MegaTrackEntry? SetAnimation(
        MegaAnimationState state,
        string animationName,
        bool loop = true,
        int trackId = 0)
        => state.SetAnimation(animationName, loop, trackId);

    /// <summary>
    /// The state this build's <c>CreatureAnimator</c> queues behind <paramref name="state"/>, or
    /// <see langword="null"/> when it queues nothing. One step of the walk the mirror's schedule hook flattens
    /// (<c>Sts2SpineDefaults.FlattenQueuedChain</c>); on this build that is the single <c>NextState</c> link.
    /// </summary>
    internal static AnimState? QueuedNextState(AnimState state) => state.NextState;

    /// <summary>
    /// Every <see cref="MegaAnimationState"/> method this build's animator queues a clip through, paired with
    /// the label the Harmony installer logs. The mirror's schedule hook patches each one so a queued return
    /// reaches the producer even for nodes the creature animator never drives. This build has exactly one.
    /// </summary>
    internal static IReadOnlyList<(string Description, MethodInfo? Target)> QueueAnimationTargets() =>
    [
        ("MegaAnimationState.AddAnimation", FindQueueMethod(nameof(MegaAnimationState.AddAnimation))),
    ];

    // Every queue overload the hook cares about takes (name, delay, loop, track); the return type differs and
    // is irrelevant to a postfix that reads only the arguments.
    private static MethodInfo? FindQueueMethod(string name)
        => typeof(MegaAnimationState).GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: [typeof(string), typeof(float), typeof(bool), typeof(int)],
            modifiers: null);
}

/// <summary>Main-menu members whose visibility differs by lane.</summary>
internal static class GameApiMainMenu
{
    /// <summary>The full-screen blur panel the menu puts behind a submenu. A <c>Control</c> when present.</summary>
    internal static object? BlurBackstop(NMainMenu mainMenu) => mainMenu.BlurBackstop;
}

/// <summary>Net-service construction, whose arguments differ by lane.</summary>
internal static class GameApiNetHost
{
    /// <summary>A host net service configured the way the game's own host-start paths configure one.</summary>
    internal static NetHostGameService Create() => new();
}
