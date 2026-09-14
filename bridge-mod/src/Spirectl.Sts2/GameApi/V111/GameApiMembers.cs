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

/// <summary>Combat-manager members that differ by lane.</summary>
internal static class GameApiCombat
{
    /// <summary>
    /// The players whose end-of-player-turn readiness action has already landed, as the game's own live set.
    /// Populated only between the host's own readiness and the side switch, so an empty set — and a
    /// <see langword="null"/> return, which callers must treat identically — means nothing is pending.
    /// </summary>
    /// <remarks>
    /// Two hops on this build: the set moved off the combat manager onto the turn state that owns one combat.
    /// Both hops are by name, because the turn-state type is internal to the game assembly and cannot be named
    /// from here. The manager carries no turn state outside a live combat, which reads as nothing pending.
    /// </remarks>
    internal static IReadOnlyCollection<Player>? PlayersReadyToBeginEnemyTurn(CombatManager combatManager)
    {
        var turnState = Sts2LiveIntrospection.GetMemberValue(combatManager, GameApiNames.CombatTurnState);
        return turnState is null
            ? null
            : Sts2LiveIntrospection.GetMemberValue(
                    turnState,
                    GameApiNames.TurnStatePlayersReadyToBeginEnemyTurn)
                as IReadOnlyCollection<Player>;
    }
}

/// <summary>
/// Game member names the bridge reads BY NAME (through <see cref="Sts2LiveIntrospection"/>) rather than by
/// binding. A rename here is invisible to the compiler, so the names are pinned per lane and checked at
/// startup by <see cref="Sts2GameApiProbe"/> instead of falling back to a plausible default.
/// </summary>
internal static class GameApiNames
{
    /// <summary>
    /// The turn state the combat manager owns for the combat it is running; null between combats. First hop of
    /// <see cref="GameApiCombat.PlayersReadyToBeginEnemyTurn"/>.
    /// </summary>
    internal const string CombatTurnState = "_turnState";

    /// <summary>
    /// The readiness set on that turn state. Second hop of
    /// <see cref="GameApiCombat.PlayersReadyToBeginEnemyTurn"/>.
    /// </summary>
    internal const string TurnStatePlayersReadyToBeginEnemyTurn = "PlayersReadyToBeginEnemyTurn";

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

    /// <summary>
    /// The state this build's <c>CreatureAnimator</c> queues behind <paramref name="state"/>, or
    /// <see langword="null"/> when it queues nothing. One step of the walk the mirror's schedule hook flattens
    /// (<c>Sts2SpineDefaults.FlattenQueuedChain</c>).
    /// </summary>
    /// <remarks>
    /// This build picks the successor through <see cref="AnimState.GetNextState"/> rather than the plain
    /// <c>NextState</c> link, and the answer can differ per call: a queued return may be chosen from several
    /// candidates. Reading the same accessor is what makes the mirror's recorded return MATCH the clip the game
    /// queued, and the mirror's schedule replay is the only thing that ever ends a one-shot on a seat, since the
    /// couch CPU saver freezes spine nodes so the native track queue never advances on its own. The bridge's
    /// caller is a postfix on the animator's own setter, so this runs one instruction after the game made the
    /// same call, on the same object, in the same frame.
    ///
    /// <para><c>NextState</c> still exists here and is still the answer for anything that sets it, but a build
    /// that populates only the newer chain would degrade to "nothing queued" if it were read directly — which
    /// is why <see cref="GameApiManifest"/> requires the accessor at startup.</para>
    /// </remarks>
    internal static AnimState? QueuedNextState(AnimState state) => state.GetNextState();

    /// <summary>
    /// Every <see cref="MegaAnimationState"/> method this build's animator queues a clip through, paired with
    /// the label the Harmony installer logs. The mirror's schedule hook patches each one so a queued return
    /// reaches the producer even for nodes the creature animator never drives.
    /// </summary>
    /// <remarks>
    /// This build queues through two entry points, split by whether the caller wants the resulting track entry
    /// back, and its animator uses the tracked one for a LOOPING return — an idle handed back to after a
    /// one-shot. Patching only the untracked overload here would leave exactly that transition unobserved.
    /// </remarks>
    internal static IReadOnlyList<(string Description, MethodInfo? Target)> QueueAnimationTargets() =>
    [
        ("MegaAnimationState.AddAnimation", FindQueueMethod(nameof(MegaAnimationState.AddAnimation))),
        ("MegaAnimationState.AddAnimationTracked", FindQueueMethod(nameof(MegaAnimationState.AddAnimationTracked))),
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
