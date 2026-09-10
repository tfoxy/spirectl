using Spirectl.Sts2.Core.Debugging;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Live.Debugging;


public sealed class LiveDebugControl : IDebugControl
{
    private static readonly IReadOnlyList<DebugNoticeSnapshot> UnavailableStatusNotices =
    [
        new DebugNoticeSnapshot(
            "live_debug_hooks_unavailable",
            "The live STS2 host owns debug control, but runtime hooks are not wired yet."),
    ];

    private readonly Func<GameStateSnapshot>? _snapshotProvider;
    private readonly ILiveDebugRuntimeHooks? _runtimeHooks;
    private readonly object _sync = new();
    private readonly List<DebugBreakpointSnapshot> _breakpoints = [];
    private int _nextBreakpointId = 1;
    private int _nextSessionId = 1;
    private DebugPauseReasonSnapshot _lastPauseReason = DebugPauseReasonSnapshot.Unsupported;
    private string? _lastPauseReasonDetail = "The live STS2 host owns debug control, but runtime hooks are still unavailable.";
    private DebugBreakpointHitSnapshot? _lastBreakpointHit;
    private SessionLease? _session;
    private readonly List<SessionLease> _observers = [];
    private readonly InMemoryDebugEventStore _events = new();

    public LiveDebugControl()
    {
    }

    public LiveDebugControl(Func<GameStateSnapshot> snapshotProvider)
    {
        _snapshotProvider = snapshotProvider;
    }

    public LiveDebugControl(
        Func<GameStateSnapshot> snapshotProvider,
        ILiveDebugRuntimeHooks runtimeHooks)
    {
        _snapshotProvider = snapshotProvider;
        _runtimeHooks = runtimeHooks;
    }

    public DebugStatusSnapshot GetStatus(DebugStatusRequestSnapshot request)
    {
        lock (_sync)
        {
            return BuildStatus(request.SessionId);
        }
    }

    public DebugSessionStartResultSnapshot StartSession(DebugSessionStartRequestSnapshot request)
    {
        lock (_sync)
        {
            var capabilities = DescribeCapabilities();
            if (!capabilities.Supported)
            {
                return new DebugSessionStartResultSnapshot(
                    Started: false,
                    Session: null,
                    Status: BuildStatus(null),
                    Notices:
                    [
                        new DebugNoticeSnapshot(
                            "debug_session_unsupported",
                            capabilities.UnsupportedReason ?? "Debugger sessions are not supported by the current live host."),
                    ]);
            }

            ExpireSessionIfNeeded();
            if (request.Role == DebugSessionRoleSnapshot.Observer)
            {
                var observer = CreateSession(request);
                _observers.Add(observer);
                AppendSessionEvent(DebugEventKindSnapshot.ObserverAttached, observer, "Observer attached.");
                return new DebugSessionStartResultSnapshot(
                    Started: true,
                    Session: observer.Info,
                    Status: BuildStatus(observer.Info.Id),
                    Notices: []);
            }

            if (_session is not null)
            {
                var notices = new[]
                {
                    new DebugNoticeSnapshot(
                        "debug_session_conflict",
                        $"Debugger session '{_session.Info.Id}' already owns the runtime."),
                };
                _events.Append(new DebugEventAppendSnapshot(
                    Kind: DebugEventKindSnapshot.LeaseChanged,
                    SessionId: null,
                    SessionRole: DebugSessionRoleSnapshot.Controller,
                    Notices: notices,
                    LeaseChanged: new DebugEventLeaseChangedSnapshot(
                        PreviousController: _session.Info,
                        Controller: _session.Info,
                        Conflict: new DebugLeaseConflictSnapshot(_session.Info, request.Name, request.LeaseTimeoutMs, notices))));
                return new DebugSessionStartResultSnapshot(
                    Started: false,
                    Session: null,
                    Status: BuildStatus(null),
                    Notices: notices);
            }

            _session = CreateSession(request);
            _events.Append(new DebugEventAppendSnapshot(
                Kind: DebugEventKindSnapshot.LeaseChanged,
                SessionId: _session.Info.Id,
                SessionRole: DebugSessionRoleSnapshot.Controller,
                LeaseChanged: new DebugEventLeaseChangedSnapshot(null, _session.Info, null)));
            if (request.PauseOnStart)
            {
                var pause = _runtimeHooks!.Pause();
                if (pause.Applied)
                {
                    _lastPauseReason = DebugPauseReasonSnapshot.Manual;
                    _lastPauseReasonDetail = pause.Detail ?? "Paused when starting a debugger session.";
                    AppendPauseEvent(_session.Info.Id, _lastPauseReasonDetail);
                }
            }

            return new DebugSessionStartResultSnapshot(
                Started: true,
                Session: _session.Info,
                Status: BuildStatus(_session.Info.Id),
                Notices: []);
        }
    }

    public DebugSessionStatusResultSnapshot GetSessionStatus(DebugSessionStatusRequestSnapshot request)
    {
        lock (_sync)
        {
            ExpireSessionIfNeeded();
            var session = _session?.Info;
            var observer = _observers.FirstOrDefault(item => string.Equals(item.Info.Id, request.Id, StringComparison.Ordinal))?.Info;
            var found = session is not null && string.Equals(session.Id, request.Id, StringComparison.Ordinal) || observer is not null;
            return new DebugSessionStatusResultSnapshot(
                Found: found,
                Session: found ? session is not null && string.Equals(session.Id, request.Id, StringComparison.Ordinal) ? session : observer : null,
                Status: BuildStatus(request.Id),
                Notices: found
                    ? []
                    : [new DebugNoticeSnapshot("debug_session_not_found", $"No active debugger session with id '{request.Id}' was found.")]);
        }
    }

    public DebugSessionEndResultSnapshot EndSession(DebugSessionEndRequestSnapshot request)
    {
        lock (_sync)
        {
            ExpireSessionIfNeeded();
            var observerIndex = _observers.FindIndex(item => string.Equals(item.Info.Id, request.Id, StringComparison.Ordinal));
            if (observerIndex >= 0)
            {
                var observer = _observers[observerIndex];
                _observers.RemoveAt(observerIndex);
                AppendSessionEvent(DebugEventKindSnapshot.ObserverDetached, observer, "Observer detached.");
                return new DebugSessionEndResultSnapshot(
                    Ended: true,
                    SessionId: request.Id,
                    Status: BuildStatus(null),
                    Notices: []);
            }

            if (_session is null || !string.Equals(_session.Info.Id, request.Id, StringComparison.Ordinal))
            {
                return new DebugSessionEndResultSnapshot(
                    Ended: false,
                    SessionId: request.Id,
                    Status: BuildStatus(request.Id),
                    Notices:
                    [
                        new DebugNoticeSnapshot(
                            "debug_session_not_found",
                            $"No active debugger session with id '{request.Id}' was found."),
                    ]);
            }

            var previous = _session.Info;
            if (request.ResumeRuntime)
            {
                var resume = _runtimeHooks?.Resume();
                if (resume?.Applied == true)
                {
                    _lastPauseReason = DebugPauseReasonSnapshot.None;
                    _lastPauseReasonDetail = null;
                    AppendResumeEvent(request.Id, resume.Detail);
                }
            }

            _session = null;
            _events.Append(new DebugEventAppendSnapshot(
                Kind: DebugEventKindSnapshot.LeaseChanged,
                SessionId: previous.Id,
                SessionRole: DebugSessionRoleSnapshot.Controller,
                LeaseChanged: new DebugEventLeaseChangedSnapshot(previous, null, null)));
            return new DebugSessionEndResultSnapshot(
                Ended: true,
                SessionId: request.Id,
                Status: BuildStatus(null),
                Notices: []);
        }
    }

    public DebugOperationResultSnapshot Pause(DebugSessionBoundRequestSnapshot request)
    {
        lock (_sync)
        {
            var leaseError = ValidateOwnedSession(request.SessionId);
            if (leaseError is not null)
            {
                return leaseError;
            }

            var capabilities = DescribeCapabilities();
            if (capabilities.IsPaused)
            {
                return new DebugOperationResultSnapshot(
                    Applied: false,
                    Status: BuildStatus(request.SessionId),
                    Notices:
                    [
                        new DebugNoticeSnapshot(
                            "live_debug_pause_already_paused",
                            "The live debug runtime is already paused."),
                    ]);
            }

            var result = _runtimeHooks!.Pause();
            if (result.Applied)
            {
                TouchSession(request.SessionId);
                _lastPauseReason = DebugPauseReasonSnapshot.Manual;
                _lastPauseReasonDetail = result.Detail ?? "Paused by dev debug pause.";
                AppendPauseEvent(request.SessionId, _lastPauseReasonDetail);
            }

            return BuildOperationResult(request.SessionId, result);
        }
    }

    public DebugOperationResultSnapshot Resume(DebugSessionBoundRequestSnapshot request)
    {
        lock (_sync)
        {
            var leaseError = ValidateOwnedSession(request.SessionId);
            if (leaseError is not null)
            {
                return leaseError;
            }

            var capabilities = DescribeCapabilities();
            if (!capabilities.IsPaused)
            {
                return new DebugOperationResultSnapshot(
                    Applied: false,
                    Status: BuildStatus(request.SessionId),
                    Notices:
                    [
                        new DebugNoticeSnapshot(
                            "live_debug_resume_already_running",
                            "The live debug runtime is already running."),
                    ]);
            }

            var result = _runtimeHooks!.Resume();
            if (result.Applied)
            {
                TouchSession(request.SessionId);
                _lastPauseReason = DebugPauseReasonSnapshot.None;
                _lastPauseReasonDetail = null;
                AppendResumeEvent(request.SessionId, result.Detail);
            }

            return BuildOperationResult(request.SessionId, result);
        }
    }

    public DebugOperationResultSnapshot Step(DebugStepRequestSnapshot request)
    {
        lock (_sync)
        {
            var leaseError = ValidateOwnedSession(request.SessionId);
            if (leaseError is not null)
            {
                return leaseError;
            }

            var capabilities = DescribeCapabilities();
            if (!capabilities.Supported)
            {
                return UnsupportedOperation(
                    request.SessionId,
                    "live_debug_step_unavailable",
                    capabilities.UnsupportedReason ?? $"Step '{request.Kind}' is not available until the live STS2 host wires runtime stepping hooks.");
            }

            if (!capabilities.IsPaused)
            {
                return new DebugOperationResultSnapshot(
                    Applied: false,
                    Status: BuildStatus(request.SessionId),
                    Notices:
                    [
                        new DebugNoticeSnapshot(
                            "live_debug_step_requires_pause",
                            "Pause the live debug runtime before requesting a step."),
                    ]);
            }

            if (!capabilities.SupportedStepKinds.Contains(request.Kind))
            {
                return new DebugOperationResultSnapshot(
                    Applied: false,
                    Status: BuildStatus(request.SessionId),
                    Notices:
                    [
                        new DebugNoticeSnapshot(
                            "live_debug_step_kind_unsupported",
                            $"Step kind '{request.Kind}' is not supported by the current live debug runtime."),
                    ]);
            }

            var result = _runtimeHooks!.Step(request, () => EvaluateBreakpoints(allowMutation: true).Hit);
            if (result.Applied)
            {
                TouchSession(request.SessionId);
                _lastBreakpointHit = result.BreakpointHit;
                if (result.BreakpointHit is not null)
                {
                    _lastPauseReason = DebugPauseReasonSnapshot.Breakpoint;
                    _lastPauseReasonDetail = result.Detail ?? "Paused on a matching debug breakpoint.";
                    AppendBreakpointHitEvent(request.SessionId, result.BreakpointHit);
                }
                else
                {
                    _lastPauseReason = DebugPauseReasonSnapshot.StepComplete;
                    _lastPauseReasonDetail = result.Detail ?? $"Completed {request.Kind} step ({request.Count}).";
                }

                _events.Append(new DebugEventAppendSnapshot(
                    Kind: DebugEventKindSnapshot.Stepped,
                    SessionId: request.SessionId,
                    SessionRole: DebugSessionRoleSnapshot.Controller,
                    Step: new DebugEventStepSnapshot(request.Kind, request.Count, result.Detail)));
            }

            return new DebugOperationResultSnapshot(
                Applied: result.Applied,
                Status: BuildStatus(request.SessionId),
                Notices: BuildNotices(result.NoticeCode, result.NoticeMessage));
        }
    }

    public DebugWaitResultSnapshot Wait(DebugWaitRequestSnapshot request)
    {
        lock (_sync)
        {
            var leaseError = ValidateOwnedSession(request.SessionId);
            if (leaseError is not null)
            {
                return new DebugWaitResultSnapshot(
                    Completed: false,
                    TimedOut: false,
                    Status: leaseError.Status,
                    Notices: leaseError.Notices);
            }

            var resume = _runtimeHooks!.Resume();
            if (!resume.Applied)
            {
                return new DebugWaitResultSnapshot(
                    Completed: false,
                    TimedOut: false,
                    Status: BuildStatus(request.SessionId),
                    Notices: BuildNotices(resume.NoticeCode, resume.NoticeMessage));
            }
        }

        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(Math.Max(1u, request.TimeoutMs == 0 ? 5_000u : request.TimeoutMs));
        while (DateTimeOffset.UtcNow < deadline)
        {
            lock (_sync)
            {
                ExpireSessionIfNeeded();
                if (_session is null || !string.Equals(_session.Info.Id, request.SessionId, StringComparison.Ordinal))
                {
                    return new DebugWaitResultSnapshot(
                        Completed: false,
                        TimedOut: false,
                        Status: BuildStatus(request.SessionId),
                        Notices:
                        [
                            new DebugNoticeSnapshot(
                                "debug_session_expired",
                                $"Debugger session '{request.SessionId}' is no longer active."),
                        ]);
                }

                var hit = EvaluateBreakpoints(allowMutation: true).Hit;
                if (hit is not null)
                {
                    _runtimeHooks!.Pause();
                    TouchSession(request.SessionId);
                    _lastBreakpointHit = hit;
                    _lastPauseReason = DebugPauseReasonSnapshot.Breakpoint;
                    _lastPauseReasonDetail = "Paused on a matching debug breakpoint during debug wait.";
                    AppendBreakpointHitEvent(request.SessionId, hit);
                    AppendPauseEvent(request.SessionId, _lastPauseReasonDetail);
                    return new DebugWaitResultSnapshot(
                        Completed: true,
                        TimedOut: false,
                        Status: BuildStatus(request.SessionId),
                        Notices: []);
                }
            }

            Thread.Sleep(25);
        }

        lock (_sync)
        {
            _runtimeHooks!.Pause();
            TouchSession(request.SessionId);
            _lastPauseReason = DebugPauseReasonSnapshot.Manual;
            _lastPauseReasonDetail = "Paused after debug wait reached its timeout.";
            AppendPauseEvent(request.SessionId, _lastPauseReasonDetail);
            return new DebugWaitResultSnapshot(
                Completed: false,
                TimedOut: true,
                Status: BuildStatus(request.SessionId),
                Notices:
                [
                    new DebugNoticeSnapshot(
                        "debug_wait_timeout",
                        $"Debug wait timed out after {Math.Max(1u, request.TimeoutMs == 0 ? 5_000u : request.TimeoutMs)} ms."),
                ]);
        }
    }

    public DebugEventStreamResultSnapshot GetEvents(DebugEventStreamRequestSnapshot request)
    {
        lock (_sync)
        {
            ExpireSessionIfNeeded();
            return _events.Replay(request);
        }
    }

    public DebugBreakpointListResultSnapshot ListBreakpoints(DebugBreakpointListRequestSnapshot request)
    {
        lock (_sync)
        {
            return new DebugBreakpointListResultSnapshot(
                Breakpoints: [.. MaybeEvaluateBreakpoints(request.SessionId)],
                Status: BuildStatus(request.SessionId),
                Notices: []);
        }
    }

    public DebugBreakpointAddResultSnapshot AddBreakpoint(DebugBreakpointAddRequestSnapshot request)
    {
        lock (_sync)
        {
            var leaseError = ValidateSessionLeaseForBreakpointMutation(request.SessionId);
            if (leaseError is not null)
            {
                return new DebugBreakpointAddResultSnapshot(
                    Added: false,
                    Breakpoint: null,
                    Status: leaseError.Status,
                    Notices: leaseError.Notices);
            }

            var breakpoint = new DebugBreakpointSnapshot(
                Id: $"bp:{_nextBreakpointId++}",
                Name: string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim(),
                QueryPath: request.QueryPath,
                Kind: request.Kind,
                Predicate: request.Predicate,
                Enabled: true,
                Provisional: _runtimeHooks is null,
                MinHitCount: Math.Max(1u, request.MinHitCount),
                HitCount: 0,
                AutoRemoveOnHit: request.AutoRemoveOnHit,
                LastObservedJson: null);
            _breakpoints.Add(breakpoint);
            TouchSession(request.SessionId);

            var notices = _runtimeHooks is null
                ? [new DebugNoticeSnapshot("live_debug_breakpoint_storage_only", "The live STS2 host can store debug breakpoints, but runtime stepping hooks are not wired yet.")]
                : Array.Empty<DebugNoticeSnapshot>();

            return new DebugBreakpointAddResultSnapshot(
                Added: true,
                Breakpoint: breakpoint,
                Status: BuildStatus(request.SessionId),
                Notices: notices);
        }
    }

    public DebugBreakpointRemoveResultSnapshot RemoveBreakpoint(DebugBreakpointRemoveRequestSnapshot request)
    {
        lock (_sync)
        {
            var leaseError = ValidateSessionLeaseForBreakpointMutation(request.SessionId);
            if (leaseError is not null)
            {
                return new DebugBreakpointRemoveResultSnapshot(
                    Removed: false,
                    BreakpointId: request.BreakpointId,
                    Status: leaseError.Status,
                    Notices: leaseError.Notices);
            }

            var removed = _breakpoints.RemoveAll(item => item.Id == request.BreakpointId) > 0;
            if (removed && string.Equals(_lastBreakpointHit?.BreakpointId, request.BreakpointId, StringComparison.Ordinal))
            {
                _lastBreakpointHit = null;
            }

            TouchSession(request.SessionId);
            return new DebugBreakpointRemoveResultSnapshot(
                Removed: removed,
                BreakpointId: request.BreakpointId,
                Status: BuildStatus(request.SessionId),
                Notices: removed
                    ? []
                    : [new DebugNoticeSnapshot("debug_breakpoint_not_found", $"No breakpoint with id '{request.BreakpointId}' is registered.")]);
        }
    }

    private DebugStatusSnapshot BuildStatus(string? sessionId)
    {
        ExpireSessionIfNeeded();

        var capabilities = DescribeCapabilities();
        var breakpointState = capabilities.Supported && capabilities.IsPaused
            ? EvaluateBreakpoints(allowMutation: true)
            : new BreakpointEvaluationStatus(_snapshotProvider is not null && capabilities.Supported, _lastBreakpointHit, []);
        if (breakpointState.Hit is not null)
        {
            _lastBreakpointHit = breakpointState.Hit;
        }

        var notices = new List<DebugNoticeSnapshot>(breakpointState.Notices);
        if (!capabilities.Supported)
        {
            notices.AddRange(UnavailableStatusNotices);
        }

        return new DebugStatusSnapshot(
            Supported: capabilities.Supported,
            ExecutionState: capabilities.Supported
                ? capabilities.IsPaused ? DebugExecutionStateSnapshot.Paused : DebugExecutionStateSnapshot.Running
                : DebugExecutionStateSnapshot.Unsupported,
            PauseReason: capabilities.Supported
                ? capabilities.IsPaused ? _lastPauseReason : DebugPauseReasonSnapshot.None
                : DebugPauseReasonSnapshot.Unsupported,
            PauseReasonDetail: capabilities.Supported
                ? capabilities.IsPaused ? _lastPauseReasonDetail : null
                : capabilities.UnsupportedReason ?? _lastPauseReasonDetail,
            CanPause: capabilities.Supported && !capabilities.IsPaused,
            CanResume: capabilities.Supported && capabilities.IsPaused,
            SupportedStepKinds: capabilities.Supported ? capabilities.SupportedStepKinds : [],
            Breakpoints: [.. _breakpoints],
            LastBreakpointHit: _lastBreakpointHit,
            SessionOwnership: DescribeSessionOwnership(sessionId),
            ActiveSession: _session?.Info,
            Notices: notices,
            BreakpointManagementSupported: true,
            BreakpointEvaluationSupported: breakpointState.BreakpointEvaluationSupported,
            CallerRole: DescribeCallerRole(sessionId),
            ObserverSessions: _observers.Select(item => item.Info).ToArray());
    }

    private IReadOnlyList<DebugBreakpointSnapshot> MaybeEvaluateBreakpoints(string? sessionId)
    {
        _ = sessionId;
        var capabilities = DescribeCapabilities();
        if (capabilities.Supported && capabilities.IsPaused)
        {
            _ = EvaluateBreakpoints(allowMutation: true);
        }

        return _breakpoints;
    }

    private DebugOperationResultSnapshot? ValidateOwnedSession(string? sessionId)
    {
        var capabilities = DescribeCapabilities();
        if (!capabilities.Supported)
        {
            return UnsupportedOperation(
                sessionId,
                "live_debug_unavailable",
                capabilities.UnsupportedReason ?? "Live debug control is not available in the current host.");
        }

        ExpireSessionIfNeeded();
        if (IsObserverSession(sessionId))
        {
            return new DebugOperationResultSnapshot(
                Applied: false,
                Status: BuildStatus(sessionId),
                Notices:
                [
                    new DebugNoticeSnapshot(
                        "debug_observer_mutation_forbidden",
                        "Observer debugger sessions cannot pause, resume, step, wait, or mutate breakpoints."),
                ]);
        }

        if (_session is null)
        {
            return new DebugOperationResultSnapshot(
                Applied: false,
                Status: BuildStatus(sessionId),
                Notices:
                [
                    new DebugNoticeSnapshot(
                        "debug_session_required",
                        "Start a debugger session before running mutating debug operations."),
                ]);
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return new DebugOperationResultSnapshot(
                Applied: false,
                Status: BuildStatus(null),
                Notices:
                [
                    new DebugNoticeSnapshot(
                        "debug_session_required",
                        $"Debugger session '{_session.Info.Id}' currently owns the runtime."),
                ]);
        }

        if (!string.Equals(_session.Info.Id, sessionId, StringComparison.Ordinal))
        {
            return new DebugOperationResultSnapshot(
                Applied: false,
                Status: BuildStatus(sessionId),
                Notices:
                [
                    new DebugNoticeSnapshot(
                        "debug_session_conflict",
                        $"Debugger session '{_session.Info.Id}' currently owns the runtime."),
                ]);
        }

        TouchSession(sessionId);
        return null;
    }

    private DebugOperationResultSnapshot? ValidateSessionLeaseForBreakpointMutation(string? sessionId)
    {
        ExpireSessionIfNeeded();
        if (IsObserverSession(sessionId))
        {
            return new DebugOperationResultSnapshot(
                Applied: false,
                Status: BuildStatus(sessionId),
                Notices:
                [
                    new DebugNoticeSnapshot(
                        "debug_observer_mutation_forbidden",
                        "Observer debugger sessions cannot pause, resume, step, wait, or mutate breakpoints."),
                ]);
        }

        if (_session is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return new DebugOperationResultSnapshot(
                Applied: false,
                Status: BuildStatus(null),
                Notices:
                [
                    new DebugNoticeSnapshot(
                        "debug_session_required",
                        $"Debugger session '{_session.Info.Id}' currently owns the runtime."),
                ]);
        }

        if (!string.Equals(_session.Info.Id, sessionId, StringComparison.Ordinal))
        {
            return new DebugOperationResultSnapshot(
                Applied: false,
                Status: BuildStatus(sessionId),
                Notices:
                [
                    new DebugNoticeSnapshot(
                        "debug_session_conflict",
                        $"Debugger session '{_session.Info.Id}' currently owns the runtime."),
                ]);
        }

        TouchSession(sessionId);
        return null;
    }

    private DebugOperationResultSnapshot UnsupportedOperation(string? sessionId, string code, string message)
    {
        return new DebugOperationResultSnapshot(
            Applied: false,
            Status: BuildStatus(sessionId),
            Notices:
            [
                new DebugNoticeSnapshot(code, message),
            ]);
    }

    private DebugOperationResultSnapshot BuildOperationResult(string? sessionId, LiveDebugRuntimeResult result)
    {
        return new DebugOperationResultSnapshot(
            Applied: result.Applied,
            Status: BuildStatus(sessionId),
            Notices: BuildNotices(result.NoticeCode, result.NoticeMessage));
    }

    private LiveDebugRuntimeCapabilities DescribeCapabilities()
    {
        return _runtimeHooks?.DescribeCapabilities()
            ?? new LiveDebugRuntimeCapabilities(
                Supported: false,
                IsPaused: false,
                SupportedStepKinds: [],
                UnsupportedReason: "The live STS2 host owns debug control, but runtime hooks are not wired yet.");
    }

    private BreakpointEvaluationStatus EvaluateBreakpoints(bool allowMutation)
    {
        if (_snapshotProvider is null)
        {
            return new BreakpointEvaluationStatus(false, _lastBreakpointHit, []);
        }

        try
        {
            var snapshot = _snapshotProvider();
            for (var index = 0; index < _breakpoints.Count; index += 1)
            {
                var breakpoint = _breakpoints[index];
                if (!breakpoint.Enabled)
                {
                    continue;
                }

                var observedJson = DebugStateQueryEvaluator.ObserveValueJson(snapshot, breakpoint.QueryPath);
                if (breakpoint.Kind == DebugBreakpointKindSnapshot.Change)
                {
                    var changed = breakpoint.LastObservedJson is not null
                        && !string.Equals(breakpoint.LastObservedJson, observedJson, StringComparison.Ordinal);
                    var hitCount = breakpoint.HitCount + (changed ? 1u : 0u);
                    var previousJson = breakpoint.LastObservedJson;
                    breakpoint = breakpoint with
                    {
                        LastObservedJson = observedJson,
                        HitCount = hitCount,
                    };
                    if (allowMutation)
                    {
                        _breakpoints[index] = breakpoint;
                        AppendStateChangeIfNeeded(snapshot, breakpoint.QueryPath, previousJson, observedJson);
                    }

                    if (!changed || hitCount < breakpoint.MinHitCount)
                    {
                        continue;
                    }

                    var hit = new DebugBreakpointHitSnapshot(
                        BreakpointId: breakpoint.Id,
                        BreakpointName: breakpoint.Name,
                        QueryPath: breakpoint.QueryPath,
                        Kind: breakpoint.Kind,
                        Predicate: breakpoint.Predicate,
                        ActualJson: observedJson,
                        ScreenType: snapshot.ScreenType,
                        ScreenInstanceId: snapshot.ScreenInstanceId,
                        HitCount: hitCount);
                    if (allowMutation && breakpoint.AutoRemoveOnHit)
                    {
                        _breakpoints.RemoveAt(index);
                    }

                    return new BreakpointEvaluationStatus(true, hit, []);
                }

                var evaluation = DebugStateQueryEvaluator.Evaluate(
                    snapshot,
                    breakpoint.QueryPath,
                    breakpoint.Predicate ?? new DebugPredicateSnapshot(DebugPredicateOperatorSnapshot.Exists, null));
                var previousObservedJson = breakpoint.LastObservedJson;
                breakpoint = breakpoint with
                {
                    LastObservedJson = observedJson ?? evaluation.ActualJson,
                    HitCount = breakpoint.HitCount + (evaluation.Matched ? 1u : 0u),
                };
                if (allowMutation)
                {
                    _breakpoints[index] = breakpoint;
                    AppendStateChangeIfNeeded(snapshot, breakpoint.QueryPath, previousObservedJson, breakpoint.LastObservedJson);
                }

                if (!evaluation.Matched || breakpoint.HitCount < breakpoint.MinHitCount)
                {
                    continue;
                }

                var matchHit = new DebugBreakpointHitSnapshot(
                    BreakpointId: breakpoint.Id,
                    BreakpointName: breakpoint.Name,
                    QueryPath: breakpoint.QueryPath,
                    Kind: breakpoint.Kind,
                    Predicate: breakpoint.Predicate,
                    ActualJson: evaluation.ActualJson,
                    ScreenType: snapshot.ScreenType,
                    ScreenInstanceId: snapshot.ScreenInstanceId,
                    HitCount: breakpoint.HitCount);
                if (allowMutation && breakpoint.AutoRemoveOnHit)
                {
                    _breakpoints.RemoveAt(index);
                }

                return new BreakpointEvaluationStatus(true, matchHit, []);
            }

            return new BreakpointEvaluationStatus(true, _lastBreakpointHit, []);
        }
        catch (Exception ex)
        {
            return new BreakpointEvaluationStatus(
                false,
                _lastBreakpointHit,
                [
                    new DebugNoticeSnapshot(
                        "live_debug_state_projection_failed",
                        $"The live STS2 host could not project observable state for breakpoint evaluation: {ex.Message}"),
                ]);
        }
    }

    private SessionLease CreateSession(DebugSessionStartRequestSnapshot request)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var timeoutMs = Math.Max(1u, request.LeaseTimeoutMs == 0 ? 60_000u : request.LeaseTimeoutMs);
        return new SessionLease(new DebugSessionInfoSnapshot(
            Id: $"dbg:{_nextSessionId++}",
            Name: string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim(),
            LeaseTimeoutMs: timeoutMs,
            LeaseExpiresAtUnixMs: now + timeoutMs,
            Role: request.Role));
    }

    private void TouchSession(string? sessionId)
    {
        if (_session is null || string.IsNullOrWhiteSpace(sessionId) || !string.Equals(_session.Info.Id, sessionId, StringComparison.Ordinal))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _session = new SessionLease(_session.Info with
        {
            LeaseExpiresAtUnixMs = now + _session.Info.LeaseTimeoutMs,
        });
    }

    private void ExpireSessionIfNeeded()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var index = _observers.Count - 1; index >= 0; index -= 1)
        {
            if (_observers[index].Info.LeaseExpiresAtUnixMs <= now)
            {
                var expired = _observers[index];
                _observers.RemoveAt(index);
                AppendSessionEvent(DebugEventKindSnapshot.ObserverDetached, expired, "Observer lease expired.");
            }
        }

        if (_session is null)
        {
            return;
        }

        if (_session.Info.LeaseExpiresAtUnixMs > now)
        {
            return;
        }

        var previous = _session.Info;
        _session = null;
        _events.Append(new DebugEventAppendSnapshot(
            Kind: DebugEventKindSnapshot.Disconnected,
            SessionId: previous.Id,
            SessionRole: DebugSessionRoleSnapshot.Controller,
            SessionChanged: new DebugEventSessionSnapshot(previous, "Controller lease expired.")));
    }

    private DebugSessionOwnershipSnapshot DescribeSessionOwnership(string? sessionId)
    {
        if (_session is null)
        {
            return DebugSessionOwnershipSnapshot.Unowned;
        }

        return string.Equals(_session.Info.Id, sessionId, StringComparison.Ordinal)
            ? DebugSessionOwnershipSnapshot.OwnedByCaller
            : DebugSessionOwnershipSnapshot.LeasedElsewhere;
    }

    private DebugSessionRoleSnapshot? DescribeCallerRole(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        if (_session is not null && string.Equals(_session.Info.Id, sessionId, StringComparison.Ordinal))
        {
            return DebugSessionRoleSnapshot.Controller;
        }

        return IsObserverSession(sessionId) ? DebugSessionRoleSnapshot.Observer : null;
    }

    private bool IsObserverSession(string? sessionId)
    {
        return !string.IsNullOrWhiteSpace(sessionId)
            && _observers.Any(item => string.Equals(item.Info.Id, sessionId, StringComparison.Ordinal));
    }

    private void AppendPauseEvent(string? sessionId, string? detail)
    {
        _events.Append(new DebugEventAppendSnapshot(
            Kind: DebugEventKindSnapshot.Paused,
            SessionId: sessionId,
            SessionRole: DebugSessionRoleSnapshot.Controller,
            PauseDetail: detail));
    }

    private void AppendResumeEvent(string? sessionId, string? detail)
    {
        _events.Append(new DebugEventAppendSnapshot(
            Kind: DebugEventKindSnapshot.Resumed,
            SessionId: sessionId,
            SessionRole: DebugSessionRoleSnapshot.Controller,
            ResumeDetail: detail));
    }

    private void AppendBreakpointHitEvent(string? sessionId, DebugBreakpointHitSnapshot hit)
    {
        _events.Append(new DebugEventAppendSnapshot(
            Kind: DebugEventKindSnapshot.BreakpointHit,
            SessionId: sessionId,
            SessionRole: DebugSessionRoleSnapshot.Controller,
            BreakpointHit: hit));
    }

    private void AppendSessionEvent(DebugEventKindSnapshot kind, SessionLease session, string reason)
    {
        _events.Append(new DebugEventAppendSnapshot(
            Kind: kind,
            SessionId: session.Info.Id,
            SessionRole: session.Info.Role,
            SessionChanged: new DebugEventSessionSnapshot(session.Info, reason)));
    }

    private void AppendStateChangeIfNeeded(
        GameStateSnapshot snapshot,
        string queryPath,
        string? previousJson,
        string? observedJson)
    {
        if (string.Equals(previousJson, observedJson, StringComparison.Ordinal))
        {
            return;
        }

        _events.Append(new DebugEventAppendSnapshot(
            Kind: DebugEventKindSnapshot.StateChanged,
            SessionId: _session?.Info.Id,
            SessionRole: DebugSessionRoleSnapshot.Controller,
            StateChanged: new DebugEventStateChangedSnapshot(
                snapshot.ScreenType,
                snapshot.ScreenInstanceId,
                [queryPath],
                observedJson)));
    }

    private static IReadOnlyList<DebugNoticeSnapshot> BuildNotices(string? code, string? message)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(message))
        {
            return [];
        }

        return [new DebugNoticeSnapshot(code, message)];
    }

    private sealed record SessionLease(DebugSessionInfoSnapshot Info);

    private sealed record BreakpointEvaluationStatus(
        bool BreakpointEvaluationSupported,
        DebugBreakpointHitSnapshot? Hit,
        IReadOnlyList<DebugNoticeSnapshot> Notices);
}
