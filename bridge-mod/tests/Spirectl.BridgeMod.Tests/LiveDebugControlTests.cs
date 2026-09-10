using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Debugging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.State;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

#if ENABLE_STS2_LIVE_HOST
using Spirectl.Sts2.Live.Debugging;

public sealed class LiveDebugControlTests
{
    [Fact]
    public void StatusUsesLiveHostSpecificUnavailableNotice()
    {
        var control = new LiveDebugControl();

        var status = control.GetStatus(new DebugStatusRequestSnapshot(null));

        Assert.False(status.Supported);
        Assert.Equal(DebugExecutionStateSnapshot.Unsupported, status.ExecutionState);
        Assert.Equal(DebugPauseReasonSnapshot.Unsupported, status.PauseReason);
        Assert.False(status.CanPause);
        Assert.False(status.CanResume);
        Assert.Empty(status.SupportedStepKinds);
        Assert.True(status.BreakpointManagementSupported);
        Assert.False(status.BreakpointEvaluationSupported);
        Assert.Single(status.Notices);
        Assert.Equal("live_debug_hooks_unavailable", status.Notices[0].Code);
    }

    [Fact]
    public void AddBreakpointKeepsStoredBreakpointProvisionalUntilHooksLand()
    {
        var control = new LiveDebugControl();

        var add = control.AddBreakpoint(new DebugBreakpointAddRequestSnapshot(
            SessionId: null,
            QueryPath: "screen.id",
            Kind: DebugBreakpointKindSnapshot.Match,
            Predicate: new DebugPredicateSnapshot(
                DebugPredicateOperatorSnapshot.Equals,
                "\"main-menu\""),
            Name: "menu-break",
            MinHitCount: 1,
            AutoRemoveOnHit: false));
        var list = control.ListBreakpoints(new DebugBreakpointListRequestSnapshot(null));

        Assert.True(add.Added);
        Assert.NotNull(add.Breakpoint);
        Assert.True(add.Breakpoint!.Enabled);
        Assert.True(add.Breakpoint.Provisional);
        Assert.Equal("screen.id", add.Breakpoint.QueryPath);
        Assert.Equal("menu-break", add.Breakpoint.Name);
        Assert.Contains(add.Notices, notice => notice.Code == "live_debug_breakpoint_storage_only");

        Assert.Single(list.Breakpoints);
        Assert.Equal(add.Breakpoint.Id, list.Breakpoints[0].Id);
        Assert.Equal(DebugExecutionStateSnapshot.Unsupported, list.Status.ExecutionState);
        Assert.Contains(list.Status.Notices, notice => notice.Code == "live_debug_hooks_unavailable");
    }

    [Fact]
    public void StatusPopulatesLastBreakpointHitFromObservableState()
    {
        var hooks = new FakeLiveDebugRuntimeHooks();
        var control = new LiveDebugControl(BuildMainMenuState, hooks);
        var session = control.StartSession(new DebugSessionStartRequestSnapshot("owner", PauseOnStart: true, LeaseTimeoutMs: 5_000));
        control.AddBreakpoint(new DebugBreakpointAddRequestSnapshot(
            SessionId: session.Session!.Id,
            QueryPath: "availableActions[arguments.choiceId=\"menu:start-run\"].kind",
            Kind: DebugBreakpointKindSnapshot.Match,
            Predicate: new DebugPredicateSnapshot(
                DebugPredicateOperatorSnapshot.Equals,
                "\"choose\""),
            Name: "start-run-action",
            MinHitCount: 1,
            AutoRemoveOnHit: false));

        var status = control.GetStatus(new DebugStatusRequestSnapshot(session.Session.Id));

        Assert.True(status.BreakpointEvaluationSupported);
        Assert.NotNull(status.LastBreakpointHit);
        Assert.Equal("start-run-action", status.LastBreakpointHit!.BreakpointName);
        Assert.Equal("\"choose\"", status.LastBreakpointHit.ActualJson);
        Assert.Equal("main-menu", status.LastBreakpointHit.ScreenType);
        Assert.Equal("screen:main-menu:test", status.LastBreakpointHit.ScreenInstanceId);
    }

    [Fact]
    public void PauseResumeAndFrameStepUseRuntimeHooks()
    {
        var hooks = new FakeLiveDebugRuntimeHooks();
        var control = new LiveDebugControl(BuildMainMenuState, hooks);

        var session = control.StartSession(new DebugSessionStartRequestSnapshot("owner", PauseOnStart: false, LeaseTimeoutMs: 5_000));
        var pause = control.Pause(new DebugSessionBoundRequestSnapshot(session.Session!.Id));
        var step = control.Step(new DebugStepRequestSnapshot(session.Session.Id, DebugStepKindSnapshot.Frame, 1));
        var resume = control.Resume(new DebugSessionBoundRequestSnapshot(session.Session.Id));

        Assert.True(pause.Applied);
        Assert.Equal(DebugExecutionStateSnapshot.Paused, pause.Status.ExecutionState);
        Assert.Equal(DebugPauseReasonSnapshot.Manual, pause.Status.PauseReason);

        Assert.True(step.Applied);
        Assert.Equal(DebugExecutionStateSnapshot.Paused, step.Status.ExecutionState);
        Assert.Equal(DebugPauseReasonSnapshot.StepComplete, step.Status.PauseReason);
        Assert.Equal(1, hooks.StepCalls);

        Assert.True(resume.Applied);
        Assert.Equal(DebugExecutionStateSnapshot.Running, resume.Status.ExecutionState);
        Assert.Equal(DebugPauseReasonSnapshot.None, resume.Status.PauseReason);
    }

    [Fact]
    public void StepPrefersBreakpointHitsOverPlainStepCompletion()
    {
        var hooks = new FakeLiveDebugRuntimeHooks();
        var control = new LiveDebugControl(BuildMainMenuState, hooks);
        var session = control.StartSession(new DebugSessionStartRequestSnapshot("owner", PauseOnStart: false, LeaseTimeoutMs: 5_000));
        control.AddBreakpoint(new DebugBreakpointAddRequestSnapshot(
            SessionId: session.Session!.Id,
            QueryPath: "screen.id",
            Kind: DebugBreakpointKindSnapshot.Match,
            Predicate: new DebugPredicateSnapshot(
                DebugPredicateOperatorSnapshot.Equals,
                "\"main-menu\""),
            Name: "menu-break",
            MinHitCount: 1,
            AutoRemoveOnHit: false));
        control.Pause(new DebugSessionBoundRequestSnapshot(session.Session.Id));

        var step = control.Step(new DebugStepRequestSnapshot(session.Session.Id, DebugStepKindSnapshot.Frame, 1));

        Assert.True(step.Applied);
        Assert.Equal(DebugPauseReasonSnapshot.Breakpoint, step.Status.PauseReason);
        Assert.NotNull(step.Status.LastBreakpointHit);
        Assert.Equal("menu-break", step.Status.LastBreakpointHit!.BreakpointName);
    }

    [Fact]
    public void PauseAndStepStayIdempotentWhenRuntimeStateDoesNotAllowChange()
    {
        var hooks = new FakeLiveDebugRuntimeHooks();
        var control = new LiveDebugControl(BuildMainMenuState, hooks);

        var session = control.StartSession(new DebugSessionStartRequestSnapshot("owner", PauseOnStart: false, LeaseTimeoutMs: 5_000));
        control.Pause(new DebugSessionBoundRequestSnapshot(session.Session!.Id));
        var secondPause = control.Pause(new DebugSessionBoundRequestSnapshot(session.Session.Id));
        control.Resume(new DebugSessionBoundRequestSnapshot(session.Session.Id));
        var stepWhileRunning = control.Step(new DebugStepRequestSnapshot(session.Session.Id, DebugStepKindSnapshot.Frame, 1));

        Assert.False(secondPause.Applied);
        Assert.Contains(secondPause.Notices, notice => notice.Code == "live_debug_pause_already_paused");
        Assert.False(stepWhileRunning.Applied);
        Assert.Contains(stepWhileRunning.Notices, notice => notice.Code == "live_debug_step_requires_pause");
    }

    [Fact]
    public void SessionLeaseOwnsMutatingDebugOperations()
    {
        var hooks = new FakeLiveDebugRuntimeHooks();
        var control = new LiveDebugControl(BuildMainMenuState, hooks);

        var session = control.StartSession(new DebugSessionStartRequestSnapshot(
            Name: "owner",
            PauseOnStart: false,
            LeaseTimeoutMs: 5_000));
        var unownedPause = control.Pause(new DebugSessionBoundRequestSnapshot(null));
        var ownedPause = control.Pause(new DebugSessionBoundRequestSnapshot(session.Session!.Id));
        var status = control.GetStatus(new DebugStatusRequestSnapshot(session.Session.Id));

        Assert.True(session.Started);
        Assert.NotNull(session.Session);
        Assert.False(unownedPause.Applied);
        Assert.Contains(unownedPause.Notices, notice => notice.Code == "debug_session_required");
        Assert.True(ownedPause.Applied);
        Assert.Equal(DebugSessionOwnershipSnapshot.OwnedByCaller, status.SessionOwnership);
        Assert.NotNull(status.ActiveSession);
        Assert.Equal(session.Session.Id, status.ActiveSession!.Id);
    }

    [Fact]
    public void ObserverSessionsDoNotReplaceControllerLeaseAndCannotMutate()
    {
        var hooks = new FakeLiveDebugRuntimeHooks();
        var control = new LiveDebugControl(BuildMainMenuState, hooks);

        var controller = control.StartSession(new DebugSessionStartRequestSnapshot("owner", false, 5_000));
        var observer = control.StartSession(new DebugSessionStartRequestSnapshot(
            "watcher",
            PauseOnStart: false,
            LeaseTimeoutMs: 5_000,
            Role: DebugSessionRoleSnapshot.Observer));
        var observerPause = control.Pause(new DebugSessionBoundRequestSnapshot(observer.Session!.Id));
        var observerBreakpoint = control.AddBreakpoint(new DebugBreakpointAddRequestSnapshot(
            SessionId: observer.Session.Id,
            QueryPath: "screen.id",
            Kind: DebugBreakpointKindSnapshot.Match,
            Predicate: null,
            Name: "observer-break",
            MinHitCount: 1,
            AutoRemoveOnHit: false));
        var controllerStatus = control.GetStatus(new DebugStatusRequestSnapshot(controller.Session!.Id));
        var observerStatus = control.GetStatus(new DebugStatusRequestSnapshot(observer.Session.Id));

        Assert.True(controller.Started);
        Assert.True(observer.Started);
        Assert.Equal(DebugSessionRoleSnapshot.Observer, observer.Session.Role);
        Assert.Equal(controller.Session.Id, controllerStatus.ActiveSession!.Id);
        Assert.Single(controllerStatus.ObserverSessions!);
        Assert.Equal(DebugSessionRoleSnapshot.Controller, controllerStatus.CallerRole);
        Assert.Equal(DebugSessionRoleSnapshot.Observer, observerStatus.CallerRole);
        Assert.False(observerPause.Applied);
        Assert.Contains(observerPause.Notices, notice => notice.Code == "debug_observer_mutation_forbidden");
        Assert.False(observerBreakpoint.Added);
        Assert.Contains(observerBreakpoint.Notices, notice => notice.Code == "debug_observer_mutation_forbidden");
    }

    [Fact]
    public void SecondControllerIsRejectedAndRecordedAsLeaseConflictEvent()
    {
        var hooks = new FakeLiveDebugRuntimeHooks();
        var control = new LiveDebugControl(BuildMainMenuState, hooks);

        var controller = control.StartSession(new DebugSessionStartRequestSnapshot("owner", false, 5_000));
        var conflict = control.StartSession(new DebugSessionStartRequestSnapshot("second", false, 5_000));
        var events = control.GetEvents(new DebugEventStreamRequestSnapshot(null, 1, 10));

        Assert.True(controller.Started);
        Assert.False(conflict.Started);
        Assert.Contains(conflict.Notices, notice => notice.Code == "debug_session_conflict");
        Assert.Contains(events.Events, evt => evt.Kind == DebugEventKindSnapshot.LeaseChanged && evt.LeaseChanged?.Conflict is not null);
    }

    [Fact]
    public void EventReplayReportsBoundedSequenceAndExpiredMetadata()
    {
        var store = new InMemoryDebugEventStore(retentionLimit: 2);
        store.Append(new DebugEventAppendSnapshot(DebugEventKindSnapshot.Paused, "dbg:1", DebugSessionRoleSnapshot.Controller));
        store.Append(new DebugEventAppendSnapshot(DebugEventKindSnapshot.Resumed, "dbg:1", DebugSessionRoleSnapshot.Controller));
        store.Append(new DebugEventAppendSnapshot(DebugEventKindSnapshot.Stepped, "dbg:1", DebugSessionRoleSnapshot.Controller));

        var replay = store.Replay(new DebugEventStreamRequestSnapshot(null, 1, 10));

        Assert.True(replay.Expired);
        Assert.Equal(2u, replay.Retention.RetentionLimit);
        Assert.Equal(2ul, replay.Retention.OldestRetainedSequence);
        Assert.Equal(3ul, replay.Retention.NewestSequence);
        Assert.Equal([2ul, 3ul], replay.Events.Select(evt => evt.Sequence).ToArray());
        Assert.Contains(replay.Notices, notice => notice.Code == "debug_event_replay_expired");
    }

    [Fact]
    public void PauseResumeStepAndBreakpointHitsAreReplayableEvents()
    {
        var hooks = new FakeLiveDebugRuntimeHooks();
        var control = new LiveDebugControl(BuildMainMenuState, hooks);
        var session = control.StartSession(new DebugSessionStartRequestSnapshot("owner", false, 5_000));
        control.Pause(new DebugSessionBoundRequestSnapshot(session.Session!.Id));
        control.AddBreakpoint(new DebugBreakpointAddRequestSnapshot(
            SessionId: session.Session.Id,
            QueryPath: "screen.id",
            Kind: DebugBreakpointKindSnapshot.Match,
            Predicate: new DebugPredicateSnapshot(DebugPredicateOperatorSnapshot.Equals, "\"main-menu\""),
            Name: "menu-break",
            MinHitCount: 1,
            AutoRemoveOnHit: false));
        control.Step(new DebugStepRequestSnapshot(session.Session.Id, DebugStepKindSnapshot.Frame, 1));
        control.Resume(new DebugSessionBoundRequestSnapshot(session.Session.Id));

        var events = control.GetEvents(new DebugEventStreamRequestSnapshot(null, 1, 20));

        Assert.Contains(events.Events, evt => evt.Kind == DebugEventKindSnapshot.Paused);
        Assert.Contains(events.Events, evt => evt.Kind == DebugEventKindSnapshot.Stepped);
        Assert.Contains(events.Events, evt => evt.Kind == DebugEventKindSnapshot.BreakpointHit);
        Assert.Contains(events.Events, evt => evt.Kind == DebugEventKindSnapshot.Resumed);
        Assert.All(events.Events, evt => Assert.True(evt.Sequence > 0));
    }

    [Fact]
    public void ChangeBreakpointsTrackObservedJsonHitCountsAndAutoRemove()
    {
        var hooks = new FakeLiveDebugRuntimeHooks();
        var currentScreen = "main-menu";
        var control = new LiveDebugControl(() => BuildScreenState(currentScreen), hooks);
        var session = control.StartSession(new DebugSessionStartRequestSnapshot(
            Name: "owner",
            PauseOnStart: true,
            LeaseTimeoutMs: 5_000));
        var add = control.AddBreakpoint(new DebugBreakpointAddRequestSnapshot(
            SessionId: session.Session!.Id,
            QueryPath: "screen.id",
            Kind: DebugBreakpointKindSnapshot.Change,
            Predicate: null,
            Name: "screen-change",
            MinHitCount: 2,
            AutoRemoveOnHit: true));

        var firstStatus = control.GetStatus(new DebugStatusRequestSnapshot(session.Session.Id));
        currentScreen = "combat";
        var secondStatus = control.GetStatus(new DebugStatusRequestSnapshot(session.Session.Id));
        currentScreen = "map";
        var thirdStatus = control.GetStatus(new DebugStatusRequestSnapshot(session.Session.Id));

        Assert.True(add.Added);
        Assert.NotNull(add.Breakpoint);
        Assert.Equal(DebugBreakpointKindSnapshot.Change, add.Breakpoint!.Kind);
        Assert.Equal("\"main-menu\"", firstStatus.Breakpoints[0].LastObservedJson);
        Assert.Equal(0u, firstStatus.Breakpoints[0].HitCount);
        Assert.Equal("\"combat\"", secondStatus.Breakpoints[0].LastObservedJson);
        Assert.Equal(1u, secondStatus.Breakpoints[0].HitCount);
        Assert.NotNull(thirdStatus.LastBreakpointHit);
        Assert.Equal("screen-change", thirdStatus.LastBreakpointHit!.BreakpointName);
        Assert.Equal("\"map\"", thirdStatus.LastBreakpointHit.ActualJson);
        Assert.Equal(2u, thirdStatus.LastBreakpointHit.HitCount);
        Assert.Empty(thirdStatus.Breakpoints);
    }

    [Fact]
    public void WaitResumesUntilABreakpointMatches()
    {
        var hooks = new FakeLiveDebugRuntimeHooks();
        var currentScreen = "main-menu";
        var control = new LiveDebugControl(() => BuildScreenState(currentScreen), hooks);
        var session = control.StartSession(new DebugSessionStartRequestSnapshot(
            Name: "owner",
            PauseOnStart: true,
            LeaseTimeoutMs: 5_000));
        control.AddBreakpoint(new DebugBreakpointAddRequestSnapshot(
            SessionId: session.Session!.Id,
            QueryPath: "screen.id",
            Kind: DebugBreakpointKindSnapshot.Match,
            Predicate: new DebugPredicateSnapshot(DebugPredicateOperatorSnapshot.Equals, "\"combat\""),
            Name: "combat-break",
            MinHitCount: 1,
            AutoRemoveOnHit: false));

        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            currentScreen = "combat";
        });

        var wait = control.Wait(new DebugWaitRequestSnapshot(session.Session.Id, 500));

        Assert.True(wait.Completed);
        Assert.False(wait.TimedOut);
        Assert.NotNull(wait.Status.LastBreakpointHit);
        Assert.Equal("combat-break", wait.Status.LastBreakpointHit!.BreakpointName);
        Assert.Equal("\"combat\"", wait.Status.LastBreakpointHit.ActualJson);
        Assert.True(hooks.IsPaused);
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

    private static GameStateSnapshot BuildScreenState(string screenType)
    {
        return BuildMainMenuState() with
        {
            ScreenType = screenType,
            ScreenTitle = screenType,
            ScreenInstanceId = $"screen:{screenType}:test",
        };
    }

    private sealed class FakeLiveDebugRuntimeHooks : ILiveDebugRuntimeHooks
    {
        public bool IsPaused { get; private set; }

        public int StepCalls { get; private set; }

        public LiveDebugRuntimeCapabilities DescribeCapabilities()
        {
            return new LiveDebugRuntimeCapabilities(
                Supported: true,
                IsPaused: IsPaused,
                SupportedStepKinds:
                [
                    DebugStepKindSnapshot.Frame,
                    DebugStepKindSnapshot.Action,
                ]);
        }

        public LiveDebugRuntimeResult Pause()
        {
            IsPaused = true;
            return new LiveDebugRuntimeResult(Applied: true, Detail: "Paused by test hook.");
        }

        public LiveDebugRuntimeResult Resume()
        {
            IsPaused = false;
            return new LiveDebugRuntimeResult(Applied: true);
        }

        public LiveDebugRuntimeStepResult Step(
            DebugStepRequestSnapshot request,
            Func<DebugBreakpointHitSnapshot?> evaluateBreakpoint)
        {
            StepCalls += 1;
            IsPaused = false;
            var breakpointHit = evaluateBreakpoint();
            IsPaused = true;
            return new LiveDebugRuntimeStepResult(
                Applied: true,
                BreakpointHit: breakpointHit,
                Detail: $"Completed fake {request.Kind} step.");
        }
    }
}
#endif
