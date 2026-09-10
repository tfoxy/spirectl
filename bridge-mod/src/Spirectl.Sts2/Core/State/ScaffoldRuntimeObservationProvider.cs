using Spirectl.Sts2;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Core.State;

public sealed class ScaffoldRuntimeObservationProvider : IRuntimeObservationProvider
{
    public BridgeRuntimeObservation Observe(GameStateQuery query)
    {
        var scaffold = PresentationScaffoldState.CreateMultiplayer();
        var p1 = scaffold.PlayersById["p1"];
        var p2 = scaffold.PlayersById["p2"];
        return new BridgeRuntimeObservation(
            SchemaVersion: "spirectl/v0",
            GameVersion: "unknown",
            BridgeVersion: BridgeBuildInfo.BridgeVersion,
            Source: DataSourceKind.Stub,
            Provisional: true,
            ScreenType: "combat",
            ScreenTitle: "Combat",
            ScreenInstanceId: "screen:combat:1",
            DefaultPlayerId: "p1",
            Menu: null,
            Lobby: null,
            Run: new RunStateSnapshot(
                Seed: "MIL2-CONTRACT-SEED",
                Floor: 3,
                Act: 1,
                Players:
                [
                    p1.RunPlayer,
                    p2.RunPlayer,
                ],
                PlayersById: new Dictionary<string, PlayerStateSnapshot>
                {
                    ["p1"] = p1.RunPlayer,
                    ["p2"] = p2.RunPlayer,
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
                ActivePlayerId: "p1",
                IsPlayerTurn: true,
                Hand:
                [
                    .. p1.Hand,
                ],
                Players:
                [
                    p1.CombatPlayer,
                    p2.CombatPlayer,
                ],
                Enemies:
                [
                    scaffold.Enemy,
                ],
                PlayersById: new Dictionary<string, CombatPlayerStateSnapshot>
                {
                    ["p1"] = p1.CombatPlayer,
                    ["p2"] = p2.CombatPlayer,
                },
                Potions: p1.Potions,
                DrawPileCardIds: p1.CombatPlayer.DrawPileCardIds ?? [],
                DiscardPileCardIds: p1.CombatPlayer.DiscardPileCardIds ?? [],
                ExhaustPileCardIds: [],
                EncounterId: "encounter:jaw-worm",
                EncounterLabel: "Gnash Grub",
                DrawPile: p1.DrawPile,
                DiscardPile: p1.DiscardPile,
                ExhaustPile: p1.ExhaustPile,
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
                new ChoiceSnapshot("target:e_1", "Gnash Grub", "target", Provisional: true, OwnerPlayerId: "p1"),
                new ChoiceSnapshot("target:e_1", "Gnash Grub", "target", Provisional: true, OwnerPlayerId: "p2"),
            ],
            AvailableActions:
            [
                new AvailableActionSnapshot(
                    Sts2ActionIds.PlayCard("p1", "c_1", "e_1"),
                    SemanticActionKind.PlayCard,
                    "Play a card against an optional target.",
                    "sts2 act play-card --card c_1 --target e_1",
                    Provisional: false,
                    Arguments: new ActionArgumentsSnapshot("p1", "c_1", "e_1", null, null, null),
                    OwnerPlayerId: "p1",
                    Perspective: OwnershipMetadata.ResolvePerspective("p1"),
                    RemoteOrchestration: OwnershipMetadata.LocalOnlyDegraded(provisional: true),
                    LegalityStatus: ActionLegalityKind.Legal),
                new AvailableActionSnapshot(
                    Sts2ActionIds.EndTurn("p1"),
                    SemanticActionKind.EndTurn,
                    "End the current combat turn.",
                    "sts2 act end-turn",
                    Provisional: false,
                    Arguments: new ActionArgumentsSnapshot("p1", null, null, null, null, null),
                    OwnerPlayerId: "p1",
                    Perspective: OwnershipMetadata.ResolvePerspective("p1"),
                    RemoteOrchestration: OwnershipMetadata.LocalOnlyDegraded(provisional: true),
                    LegalityStatus: ActionLegalityKind.Legal),
                new AvailableActionSnapshot(
                    Sts2ActionIds.PlayCard("p2", "c_p2_1", "e_1"),
                    SemanticActionKind.PlayCard,
                    "Play a p2 card against an optional target.",
                    "sts2 act play-card --player p2 --card c_p2_1 --target e_1",
                    Provisional: false,
                    Arguments: new ActionArgumentsSnapshot("p2", "c_p2_1", "e_1", null, null, null),
                    OwnerPlayerId: "p2",
                    Perspective: OwnershipMetadata.ResolvePerspective("p2"),
                    RemoteOrchestration: OwnershipMetadata.LocalOnlyDegraded(provisional: true),
                    LegalityStatus: ActionLegalityKind.Legal),
                new AvailableActionSnapshot(
                    Sts2ActionIds.EndTurn("p2"),
                    SemanticActionKind.EndTurn,
                    "End p2 combat turn.",
                    "sts2 act end-turn --player p2",
                    Provisional: false,
                    Arguments: new ActionArgumentsSnapshot("p2", null, null, null, null, null),
                    OwnerPlayerId: "p2",
                    Perspective: OwnershipMetadata.ResolvePerspective("p2"),
                    RemoteOrchestration: OwnershipMetadata.LocalOnlyDegraded(provisional: true),
                    LegalityStatus: ActionLegalityKind.Legal),
            ],
            Notices: [],
            Debug: query.IncludeDebug
                ? new DebugStateSnapshot(
                    [
                        "scaffold observation provider is active",
                        "live game extraction hooks are not wired in the core bootstrap",
                    ])
                : null,
            Language: "mock");
    }
}
