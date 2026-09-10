using Spirectl.Sts2;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Core.State;

public sealed class PlaceholderStateExtractor : IGameStateExtractor
{
    public GameStateSnapshot Extract(GameStateQuery query, PlayerPerspective perspective)
    {
        var playerId = perspective.PlayerId ?? "p1";
        var multiplayerScaffold = PresentationScaffoldState.CreateMultiplayer();
        var scaffold = multiplayerScaffold.PlayersById.TryGetValue(playerId, out var requestedPlayer)
            ? requestedPlayer
            : PresentationScaffoldState.Create(playerId);
        return new GameStateSnapshot(
            SchemaVersion: "spirectl/v0",
            GameVersion: "unknown",
            BridgeVersion: BridgeBuildInfo.BridgeVersion,
            Source: DataSourceKind.Stub,
            Provisional: true,
            ScreenType: "combat",
            ScreenTitle: "Combat",
            ScreenInstanceId: "screen:combat:1",
            ResolvedPerspective: perspective with
            {
                PlayerId = perspective.PlayerId ?? "p1",
            },
            Menu: null,
            Lobby: null,
            Run: new RunStateSnapshot(
                Seed: "MIL2-CONTRACT-SEED",
                Floor: 3,
                Act: 1,
                Players:
                [
                    scaffold.RunPlayer,
                ],
                PlayersById: new Dictionary<string, PlayerStateSnapshot>
                {
                    [playerId] = scaffold.RunPlayer,
                },
                ActLabel: "Act 1",
                FloorLabel: "Floor 3",
                EncounterId: "encounter:jaw-worm",
                EncounterLabel: "Gnash Grub",
                RoomId: "room:monster",
                RoomLabel: "Monster Room",
                BossId: "boss:slime-boss",
                BossLabel: "Slime Boss"),
            Combat: new CombatStateSnapshot(
                Turn: 2,
                ActivePlayerId: perspective.PlayerId ?? "p1",
                IsPlayerTurn: true,
                Hand:
                [
                    .. scaffold.Hand,
                ],
                Players:
                [
                    scaffold.CombatPlayer,
                ],
                Enemies:
                [
                    multiplayerScaffold.Enemy,
                ],
                PlayersById: new Dictionary<string, CombatPlayerStateSnapshot>
                {
                    [playerId] = scaffold.CombatPlayer,
                },
                Potions: scaffold.Potions,
                DrawPileCardIds: ["draw:defend"],
                DiscardPileCardIds: ["discard:bash"],
                ExhaustPileCardIds: [],
                EncounterId: "encounter:jaw-worm",
                EncounterLabel: "Gnash Grub",
                DrawPile: scaffold.DrawPile,
                DiscardPile: scaffold.DiscardPile,
                ExhaustPile: scaffold.ExhaustPile,
                EncounterVisuals: PresentationScaffoldState.CreateEncounterVisuals()),
            Map: null,
            EventRoom: null,
            TreasureRoom: null,
            RelicSelection: null,
            RestSite: null,
            Shop: null,
            Rewards: null,
            CardSelection: null,
            SimpleCardSelection: null,
            DeckCardSelection: null,
            BundleSelection: null,
            MultiplayerLobby: null,
            Choices:
            [
                new ChoiceSnapshot("target:e_1", "Gnash Grub", "target", Provisional: true, OwnerPlayerId: perspective.PlayerId ?? "p1"),
            ],
            AvailableActions:
            [
                new AvailableActionSnapshot(
                    Sts2ActionIds.PlayCard(perspective.PlayerId ?? "p1", "c_1", "e_1"),
                    SemanticActionKind.PlayCard,
                    "Play a card against an optional target.",
                    "sts2 act play-card --card c_1 --target e_1",
                    Provisional: true,
                    Arguments: new ActionArgumentsSnapshot(perspective.PlayerId ?? "p1", "c_1", "e_1", null, null, null)),
                new AvailableActionSnapshot(
                    Sts2ActionIds.EndTurn(perspective.PlayerId ?? "p1"),
                    SemanticActionKind.EndTurn,
                    "End the current combat turn.",
                    "sts2 act end-turn",
                    Provisional: true,
                    Arguments: new ActionArgumentsSnapshot(perspective.PlayerId ?? "p1", null, null, null, null, null)),
            ],
            Notices: [],
            Debug: query.IncludeDebug
                ? new DebugStateSnapshot(
                    [
                        "stub bridge runtime provider is active",
                        "game extraction hooks are not wired yet",
                    ])
                : null,
            Language: "mock");
    }
}
