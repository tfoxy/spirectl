using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Debugging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.State;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class DebugStateQueryEvaluatorTests
{
    [Fact]
    public void EvaluateMatchesNestedFilterPathAgainstProjectedState()
    {
        var evaluation = DebugStateQueryEvaluator.Evaluate(
            BuildMainMenuState(),
            "availableActions[arguments.choiceId=\"menu:start-run\"].kind",
            new DebugPredicateSnapshot(
                DebugPredicateOperatorSnapshot.Equals,
                "\"choose\""));

        Assert.True(evaluation.Matched);
        Assert.Equal("\"choose\"", evaluation.ActualJson);
    }

    [Fact]
    public void ValidatePathRejectsInvalidFilterSyntax()
    {
        var error = DebugStateQueryEvaluator.ValidatePath("choices[=]");

        Assert.Equal(
            "State query path 'choices[=]' uses an invalid filter expression '[=]'.",
            error);
    }

    private static GameStateSnapshot BuildMainMenuState()
    {
        return new GameStateSnapshot(
            SchemaVersion: "spirectl/v0",
            GameVersion: "sts2-test",
            BridgeVersion: "bridge-test",
            Source: DataSourceKind.Live,
            Provisional: false,
            ScreenType: "main-menu",
            ScreenTitle: "Main Menu",
            ScreenInstanceId: "screen:main-menu:test",
            ResolvedPerspective: new PlayerPerspective(PlayerScope.Local, null, UsesDefault: true),
            Menu: new MenuStateSnapshot("main-menu", "Main Menu"),
            Lobby: null,
            Run: null,
            Combat: null,
            Choices:
            [
                new ChoiceSnapshot("menu:start-run", "Start Run", "menu", Provisional: false),
            ],
            AvailableActions:
            [
                new AvailableActionSnapshot(
                    "action:menu:start-run",
                    SemanticActionKind.Choose,
                    "Start a new run from the main menu.",
                    "sts2 act choose --choice menu:start-run",
                    Provisional: false,
                    new ActionArgumentsSnapshot(
                        PlayerId: null,
                        CardId: null,
                        TargetId: null,
                        ChoiceId: "menu:start-run",
                        CharacterId: null,
                        MapNodeId: null)),
            ],
            Notices: [],
            Debug: null);
    }
}
