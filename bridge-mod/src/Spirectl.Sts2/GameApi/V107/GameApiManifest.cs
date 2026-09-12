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
/// The v107 lane's startup contract: every game member the bridge reaches by name or by an exact parameter
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

        // ── Encounter catalog ────────────────────────────────────────────────────────────────────────
        new(typeof(EncounterModel), nameof(EncounterModel.IsDebugEncounter), GameApiMemberKind.Value,
            Note: "marks encounters the catalog reports as debug-only"),

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
            [],
            "the predicate the multi-seat end-turn readiness postfix attaches to"),
        new(typeof(CombatManager), nameof(CombatManager.IsPlayerReadyToEndTurn), GameApiMemberKind.Method,
            [typeof(Player)],
            "per-seat readiness inside that postfix"),

        // ── Spine animation control (also a Harmony target pinned by parameter types) ────────────────
        new(typeof(MegaAnimationState), nameof(MegaAnimationState.SetAnimation), GameApiMemberKind.Method,
            [typeof(string), typeof(bool), typeof(int)],
            "pins a deterministic pose for clip/geometry extraction and reports played clips to the mirror"),

        // ── Fixture scaffolding: the main-menu overlay and the host net service ──────────────────────
        new(typeof(NMainMenu), GameApiNames.MainMenuBlurBackstop, GameApiMemberKind.Value,
            Note: "the menu blur panel a lobby fixture has to hide before it can drive the screen"),
        new(typeof(NetHostGameService), ".ctor", GameApiMemberKind.Constructor,
            [],
            "the host net service every live lobby fixture starts"),
    ];
}
