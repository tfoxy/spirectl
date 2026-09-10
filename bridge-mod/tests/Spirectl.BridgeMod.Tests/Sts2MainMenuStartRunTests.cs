using System.Reflection;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

#if ENABLE_STS2_LIVE_HOST
public sealed class Sts2MainMenuStartRunTests
{
    [Fact]
    public void MainMenuStartRunHookInspectorPrefersDirectScreenMethodPath()
    {
        var screen = new ScreenWithDirectMethodAndButton();

        var result = Inspect(screen);

        Assert.True(ReadBool(result, "HasCallableHook"));
        Assert.Equal(typeof(ScreenWithDirectMethodAndButton).FullName, ReadString(result, "ActiveScreenClassName"));
        Assert.Equal("screen-method:StartRun", ReadString(result, "ResolvedHookPath"));
        Assert.Equal(
            ["screen-method:StartRun", "button-member:StartRunButton", "button-method:StartRunButton.Pressed"],
            ReadStringList(result, "PresentCandidates"));
        Assert.Equal(
            ["screen-method:StartRun", "screen-method:StartNewRun", "screen-method:OnStartRunPressed", "screen-method:OnStartPressed", "button-member:StartRunButton", "button-method:StartRunButton.Pressed", "button-method:StartRunButton.OnPressed", "button-method:StartRunButton.Press", "button-method:StartRunButton.EmitPressed", "button-member:PlayButton", "button-member:StartButton"],
            ReadStringList(result, "CheckedProbePaths"));

        Assert.True(TryInvoke(screen, result));
        Assert.Equal(1, screen.StartRunInvocations);
        Assert.Equal(0, screen.StartRunButton.PressedInvocations);
    }

    [Fact]
    public void MainMenuStartRunHookInspectorResolvesButtonMemberMethodPath()
    {
        var screen = new ScreenWithStartRunButton();

        var result = Inspect(screen);

        Assert.True(ReadBool(result, "HasCallableHook"));
        Assert.Equal("button-method:StartRunButton.Pressed", ReadString(result, "ResolvedHookPath"));
        Assert.Equal(
            ["button-member:StartRunButton", "button-method:StartRunButton.Pressed"],
            ReadStringList(result, "PresentCandidates"));

        Assert.True(TryInvoke(screen, result));
        Assert.Equal(1, screen.StartRunButton.PressedInvocations);
    }

    [Fact]
    public void MainMenuStartRunHookInspectorReportsStableDiagnosticsWhenNoCallableHookExists()
    {
        var result = Inspect(new ScreenWithUnrecognizedStartRunHook());

        Assert.False(ReadBool(result, "HasCallableHook"));
        Assert.Equal(typeof(ScreenWithUnrecognizedStartRunHook).FullName, ReadString(result, "ActiveScreenClassName"));
        Assert.Null(ReadString(result, "ResolvedHookPath"));
        Assert.Equal(
            ["button-member:PlayButton"],
            ReadStringList(result, "PresentCandidates"));
        Assert.Equal(
            ["screen-method:StartRun", "screen-method:StartNewRun", "screen-method:OnStartRunPressed", "screen-method:OnStartPressed", "button-member:StartRunButton", "button-member:PlayButton", "button-method:PlayButton.Pressed", "button-method:PlayButton.OnPressed", "button-method:PlayButton.Press", "button-method:PlayButton.EmitPressed", "button-member:StartButton"],
            ReadStringList(result, "CheckedProbePaths"));
    }

    [Fact]
    public void CaptureMainMenuAndExecuteMainMenuChoiceAgreeWhenHookIsResolved()
    {
        var screenObject = new ScreenWithStartRunMethod();
        var observation = CaptureMainMenu(screenObject);

        var action = Assert.Single(observation.AvailableActions);
        Assert.Equal("menu:start-run", action.Arguments?.ChoiceId);
        Assert.Empty(observation.Notices);

        var result = ExecuteMainMenuChoice(screenObject);
        Assert.True(result.Accepted);
        Assert.Equal(1, screenObject.StartRunInvocations);
    }

    [Fact]
    public void CaptureMainMenuAndExecuteMainMenuChoiceAgreeWhenHookIsUnavailable()
    {
        var screenObject = new ScreenWithUnrecognizedStartRunHook();
        var observation = CaptureMainMenu(screenObject);

        Assert.Empty(observation.AvailableActions);
        var notice = Assert.Single(observation.Notices);
        Assert.Equal("main-menu-start-run-hook-unavailable", notice.Code);
        Assert.True(notice.Provisional);

        var result = ExecuteMainMenuChoice(screenObject);
        Assert.False(result.Accepted);
        Assert.NotNull(result.Error);
        Assert.Equal(ActionFailureCode.RuntimeFailure, result.Error!.Code);
        Assert.Equal(
            ["choice_id", "screen_class", "resolved_hook_path", "checked_probe_paths", "present_candidates"],
            result.Error.Details.Select(detail => detail.Field).ToArray());
        Assert.Equal("menu:start-run", result.Error.Details[0].Value);
        Assert.Equal(typeof(ScreenWithUnrecognizedStartRunHook).FullName, result.Error.Details[1].Value);
        Assert.Equal(string.Empty, result.Error.Details[2].Value);
        Assert.Contains("button-member:PlayButton", result.Error.Details[3].Value);
        Assert.Equal("button-member:PlayButton", result.Error.Details[4].Value);
    }

    private static object Inspect(object screenObject)
    {
        var helperType = typeof(Sts2ActionCatalog).Assembly.GetType("Spirectl.Sts2.Live.Sts2MainMenuStartRunHooks");
        Assert.NotNull(helperType);

        var inspect = helperType!.GetMethod("Inspect", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, [typeof(object)]);
        Assert.NotNull(inspect);

        var result = inspect!.Invoke(null, [screenObject]);
        Assert.NotNull(result);
        return result!;
    }

    private static bool TryInvoke(object screenObject, object inspection)
    {
        var helperType = typeof(Sts2ActionCatalog).Assembly.GetType("Spirectl.Sts2.Live.Sts2MainMenuStartRunHooks");
        Assert.NotNull(helperType);

        var invoke = helperType!.GetMethod("TryInvoke", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(invoke);

        return Assert.IsType<bool>(invoke!.Invoke(null, [screenObject, inspection]));
    }

    private static BridgeRuntimeObservation CaptureMainMenu(object screenObject)
    {
        var method = typeof(Sts2RuntimeObservationProvider).GetMethod(
            "CaptureMainMenu",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(ScreenLocatorResult), typeof(GameStateQuery), typeof(object)]);
        Assert.NotNull(method);

        var screen = new ScreenLocatorResult(
            "main-menu",
            "Main Menu",
            "screen:main-menu:test",
            "test",
            screenObject.GetType().Name,
            screenObject.GetType().FullName ?? screenObject.GetType().Name);

        return Assert.IsType<BridgeRuntimeObservation>(method!.Invoke(null, [screen, new GameStateQuery(null, false), screenObject]));
    }

    private static ActionExecutionResult ExecuteMainMenuChoice(object screenObject)
    {
        var method = typeof(Sts2ActionHandler).GetMethod(
            "ExecuteMainMenuChoice",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
            [typeof(SemanticActionRequest), typeof(ScreenLocatorResult), typeof(object), typeof(ILogStream)]);
        Assert.NotNull(method);

        var request = new SemanticActionRequest(
            "req-main-menu",
            SemanticActionKind.Choose,
            null,
            null,
            null,
            "menu:start-run",
            null,
            null,
            null,
            null,
            null,
            null);
        var screen = new ScreenLocatorResult(
            "main-menu",
            "Main Menu",
            "screen:main-menu:test",
            "test",
            screenObject.GetType().Name,
            screenObject.GetType().FullName ?? screenObject.GetType().Name);

        return Assert.IsType<ActionExecutionResult>(method!.Invoke(null, [request, screen, screenObject, new InMemoryLogStream()]));
    }

    private static bool ReadBool(object target, string propertyName)
        => Assert.IsType<bool>(target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target));

    private static string? ReadString(object target, string propertyName)
        => target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target) as string;

    private static string[] ReadStringList(object target, string propertyName)
        => Assert.IsAssignableFrom<IEnumerable<string>>(
            target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target))
            .ToArray();

    private sealed class ScreenWithStartRunMethod
    {
        public int StartRunInvocations { get; private set; }

        public void StartRun()
        {
            StartRunInvocations++;
        }
    }

    private sealed class ScreenWithDirectMethodAndButton
    {
        public PressableButton StartRunButton { get; } = new();
        public int StartRunInvocations { get; private set; }

        public void StartRun()
        {
            StartRunInvocations++;
        }
    }

    private sealed class ScreenWithStartRunButton
    {
        public PressableButton StartRunButton { get; } = new();
    }

    private sealed class ScreenWithUnrecognizedStartRunHook
    {
        public UnrecognizedButton PlayButton { get; } = new();
    }

    private sealed class PressableButton
    {
        public int PressedInvocations { get; private set; }

        public void Pressed()
        {
            PressedInvocations++;
        }
    }

    private sealed class UnrecognizedButton
    {
        public void Fire()
        {
        }
    }
}
#endif
