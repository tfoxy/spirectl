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
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace Spirectl.Sts2.Live.GameApi;

/// <summary>
/// The v111 lane's startup contract: every game member the bridge reaches by name or by an exact parameter
/// list, where a rename or an added parameter would otherwise degrade silently instead of failing.
///
/// <para>
/// Deliberately NOT every by-name read in the bridge. Scope is (a) the members that differ between supported
/// game builds, (b) the reads whose fallback is a plausible wrong answer rather than an obvious one, and
/// (c) the invocation targets whose parameter list the by-name invoker matches exactly.
/// </para>
/// </summary>
internal static class GameApiManifest
{
    internal static IReadOnlyList<GameApiRequirement> Requirements { get; } =
    [
        // ── The start-run lobby roster ───────────────────────────────────────────────────────────────
        new(typeof(StartRunLobby), GameApiNames.LobbyMaxPlayers, GameApiMemberKind.Value,
            Note: "the lobby's live player cap; without it every seat-count limit sizes off a constant"),
        new(typeof(StartRunLobby), nameof(StartRunLobby.LocalPlayer), GameApiMemberKind.Value,
            Note: "identifies the local seat in the start-run lobby"),
        new(typeof(StartRunLobby), nameof(StartRunLobby.Players), GameApiMemberKind.Value,
            Note: "the lobby roster the state snapshot and the synthetic-seat actions walk"),

        // ── In-run and saved-run lobby rosters ───────────────────────────────────────────────────────
        new(typeof(RunLobby), GameApiNames.LobbyPlayerIds, GameApiMemberKind.Value,
            Note: "the in-run roster the host-local seat sync watcher acknowledges combat sync for"),
        new(typeof(LoadRunLobby), GameApiNames.LobbyPlayerIds, GameApiMemberKind.Value,
            Note: "decides which saved-run seats report as connected"),

        // ── Per-player combat permissions ────────────────────────────────────────────────────────────
        new(typeof(Player), GameApiNames.PlayerCanRemoveOrUsePotions, GameApiMemberKind.Value,
            Note: "gates the potion actions the catalog advertises"),

        // EncounterModel.IsDebugEncounter has no successor on this build. The catalog reports false and says
        // so on its notice channel, so there is nothing here to require.

        // ── The damage-preview funnel (exact parameter list) ─────────────────────────────────────────
        new(typeof(Hook), nameof(Hook.ModifyDamage), GameApiMemberKind.Method,
            [
                typeof(IRunState),
                typeof(ICombatState),
                typeof(Creature),
                typeof(Creature),
                typeof(decimal),
                typeof(ValueProp),
                typeof(CardModel),
                typeof(CardPlay),
                typeof(ModifyDamageHookType),
                typeof(CardPreviewMode),
                typeof(IEnumerable<AbstractModel>).MakeByRefType(),
            ],
            "every previewed damage number in the state snapshot and the combat-preview endpoint"),

        // ── Lobby screen refresh, invoked BY NAME with an exact argument list ────────────────────────
        new(typeof(NCharacterSelectScreen), "PlayerConnected", GameApiMemberKind.Method,
            [typeof(GameLobbyPlayer)],
            "shows a newly created host-local seat in the native lobby list"),
        new(typeof(NCharacterSelectScreen), "PlayerChanged", GameApiMemberKind.Method,
            [typeof(GameLobbyPlayer), typeof(bool)],
            "refreshes an existing host-local seat's nameplate and character"),

        // ── The per-seat end-turn barrier the couch seats are arbitrated against ─────────────────────
        new(typeof(CombatManager), nameof(CombatManager.AllPlayersReadyToEndTurn), GameApiMemberKind.Method,
            Note: "the predicate the multi-seat end-turn readiness postfix attaches to",
            ParameterTypeNames: [GameApiHooks.TurnStateTypeName]),
        new(typeof(CombatManager), nameof(CombatManager.IsPlayerReadyToEndTurn), GameApiMemberKind.Method,
            [typeof(Player)],
            "per-seat readiness inside that postfix"),

        // ── The enemy-turn readiness set a synthetic seat's own client would complete ─────────────────
        // Two hops on this build, so both are required: the manager's turn state, then the set on it. The
        // second hop's owner is internal to the game assembly, so it is named by string.
        new(typeof(CombatManager), GameApiNames.CombatTurnState, GameApiMemberKind.Value,
            Note: "the combat's turn state, which this build moved the enemy-turn readiness set onto"),
        new GameApiRequirement(
            Owner: null,
            Member: GameApiNames.TurnStatePlayersReadyToBeginEnemyTurn,
            Kind: GameApiMemberKind.Value,
            OwnerTypeName: GameApiHooks.TurnStateTypeName,
            Note: "the set the host-local seat turn watcher completes for synthetic seats; without it the "
                + "watcher goes silently inert and a synthetic seat's combat stalls at the enemy-turn barrier"),

        // ── Spine animation control (also Harmony targets pinned by parameter types) ─────────────────
        new(typeof(MegaAnimationState), nameof(MegaAnimationState.SetAnimation), GameApiMemberKind.Method,
            [typeof(string), typeof(bool), typeof(int)],
            "pins a deterministic pose for clip/geometry extraction and reports played clips to the mirror"),
        new(typeof(MegaAnimationState), nameof(MegaAnimationState.GetCurrent), GameApiMemberKind.Method,
            [typeof(int)],
            "reaches the track entry this build's animation setter no longer returns"),

        // ── The queued-return transition a frozen spine node can only get from the mirror's replay ───
        // Both of these went missing in practice as a CALL, not as a member: the game kept declaring them and
        // stopped routing the character animator through them, and the mirror silently reported the last
        // one-shot forever. A member that disappears must refuse instead of degrading the same way again.
        new(typeof(AnimState), nameof(AnimState.GetNextState), GameApiMemberKind.Method,
            [],
            "resolves which clip this build queued behind a one-shot, so a played animation ends on the "
                + "idle the game chose instead of holding the one-shot's last frame forever"),
        new(typeof(MegaAnimationState), nameof(MegaAnimationState.AddAnimation), GameApiMemberKind.Method,
            [typeof(string), typeof(float), typeof(bool), typeof(int)],
            "reports a queued non-looping clip to the mirror"),
        new(typeof(MegaAnimationState), nameof(MegaAnimationState.AddAnimationTracked), GameApiMemberKind.Method,
            [typeof(string), typeof(float), typeof(bool), typeof(int)],
            "reports a queued LOOPING clip — the idle a one-shot hands back to — to the mirror"),

        // ── Fixture scaffolding: the main-menu overlay and the host net service ──────────────────────
        new(typeof(NMainMenu), GameApiNames.MainMenuBlurBackstop, GameApiMemberKind.Value,
            Note: "the menu blur panel a lobby fixture has to hide before it can drive the screen"),
        new(typeof(NetHostGameService), ".ctor", GameApiMemberKind.Constructor,
            [typeof(PeerVersionInfo)],
            "the host net service every live lobby fixture starts"),
    ];
}
