namespace Spirectl.Sts2.Core.Debugging;


public sealed class PlaceholderDebugControl : IDebugControl
{
    private static readonly IReadOnlyList<DebugNoticeSnapshot> UnsupportedStatusNotices =
    [
        new DebugNoticeSnapshot(
            "debug_control_unavailable",
            "Live pause/resume/step hooks are not wired in this host yet."),
    ];

    private readonly object _sync = new();
    private readonly List<DebugBreakpointSnapshot> _breakpoints = [];
    private int _nextBreakpointId = 1;

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
            return new DebugSessionStartResultSnapshot(
                Started: false,
                Session: null,
                Status: BuildStatus(null),
                Notices:
                [
                    new DebugNoticeSnapshot(
                        "debug_session_unsupported",
                        "Debugger sessions are not supported until live debug hooks are implemented."),
                ]);
        }
    }

    public DebugSessionStatusResultSnapshot GetSessionStatus(DebugSessionStatusRequestSnapshot request)
    {
        lock (_sync)
        {
            return new DebugSessionStatusResultSnapshot(
                Found: false,
                Session: null,
                Status: BuildStatus(request.Id),
                Notices:
                [
                    new DebugNoticeSnapshot(
                        "debug_session_unsupported",
                        "Debugger sessions are not supported until live debug hooks are implemented."),
                ]);
        }
    }

    public DebugSessionEndResultSnapshot EndSession(DebugSessionEndRequestSnapshot request)
    {
        lock (_sync)
        {
            return new DebugSessionEndResultSnapshot(
                Ended: false,
                SessionId: request.Id,
                Status: BuildStatus(request.Id),
                Notices:
                [
                    new DebugNoticeSnapshot(
                        "debug_session_unsupported",
                        "Debugger sessions are not supported until live debug hooks are implemented."),
                ]);
        }
    }

    public DebugOperationResultSnapshot Pause(DebugSessionBoundRequestSnapshot request)
    {
        lock (_sync)
        {
            return UnsupportedOperation(
                request.SessionId,
                "debug_pause_unsupported",
                "Pause is not supported until live debug hooks are implemented.");
        }
    }

    public DebugOperationResultSnapshot Resume(DebugSessionBoundRequestSnapshot request)
    {
        lock (_sync)
        {
            return UnsupportedOperation(
                request.SessionId,
                "debug_resume_unsupported",
                "Resume is not supported until live debug hooks are implemented.");
        }
    }

    public DebugOperationResultSnapshot Step(DebugStepRequestSnapshot request)
    {
        lock (_sync)
        {
            return UnsupportedOperation(
                request.SessionId,
                "debug_step_unsupported",
                $"Step '{request.Kind}' is not supported until live debug hooks are implemented.");
        }
    }

    public DebugWaitResultSnapshot Wait(DebugWaitRequestSnapshot request)
    {
        lock (_sync)
        {
            return new DebugWaitResultSnapshot(
                Completed: false,
                TimedOut: false,
                Status: BuildStatus(request.SessionId),
                Notices:
                [
                    new DebugNoticeSnapshot(
                        "debug_wait_unsupported",
                        "Debug wait is not supported until live debug hooks are implemented."),
                ]);
        }
    }

    public DebugBreakpointListResultSnapshot ListBreakpoints(DebugBreakpointListRequestSnapshot request)
    {
        lock (_sync)
        {
            return new DebugBreakpointListResultSnapshot(
                Breakpoints: [.. _breakpoints],
                Status: BuildStatus(request.SessionId),
                Notices: []);
        }
    }

    public DebugBreakpointAddResultSnapshot AddBreakpoint(DebugBreakpointAddRequestSnapshot request)
    {
        lock (_sync)
        {
            var breakpoint = new DebugBreakpointSnapshot(
                Id: $"bp:{_nextBreakpointId++}",
                Name: string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim(),
                QueryPath: request.QueryPath,
                Kind: request.Kind,
                Predicate: request.Predicate,
                Enabled: true,
                Provisional: true,
                MinHitCount: Math.Max(1u, request.MinHitCount),
                HitCount: 0,
                AutoRemoveOnHit: request.AutoRemoveOnHit,
                LastObservedJson: null);
            _breakpoints.Add(breakpoint);

            return new DebugBreakpointAddResultSnapshot(
                Added: true,
                Breakpoint: breakpoint,
                Status: BuildStatus(request.SessionId),
                Notices:
                [
                    new DebugNoticeSnapshot(
                        "debug_breakpoint_storage_only",
                        "Breakpoint storage is available, but live breakpoint evaluation is not wired yet."),
                ]);
        }
    }

    public DebugBreakpointRemoveResultSnapshot RemoveBreakpoint(DebugBreakpointRemoveRequestSnapshot request)
    {
        lock (_sync)
        {
            var removed = _breakpoints.RemoveAll(item => item.Id == request.BreakpointId) > 0;
            var notices = removed
                ? Array.Empty<DebugNoticeSnapshot>()
                : [
                    new DebugNoticeSnapshot(
                        "debug_breakpoint_not_found",
                        $"No breakpoint with id '{request.BreakpointId}' is registered."),
                ];

            return new DebugBreakpointRemoveResultSnapshot(
                Removed: removed,
                BreakpointId: request.BreakpointId,
                Status: BuildStatus(request.SessionId),
                Notices: notices);
        }
    }

    public DebugEventStreamResultSnapshot GetEvents(DebugEventStreamRequestSnapshot request)
    {
        lock (_sync)
        {
            return new DebugEventStreamResultSnapshot(
                Events: [],
                FromSequence: request.FromSequence,
                NextSequence: request.FromSequence,
                Retention: new DebugEventRetentionSnapshot(0, 0, 0),
                Expired: false,
                Overflow: false,
                Notices:
                [
                    new DebugNoticeSnapshot(
                        "debug_event_stream_unsupported",
                        "Debugger event streams are not supported until live debug hooks are implemented."),
                ]);
        }
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

    private DebugStatusSnapshot BuildStatus(string? sessionId)
    {
        return new DebugStatusSnapshot(
            Supported: false,
            ExecutionState: DebugExecutionStateSnapshot.Unsupported,
            PauseReason: DebugPauseReasonSnapshot.Unsupported,
            PauseReasonDetail: "Live debug control has not been implemented for this host.",
            CanPause: false,
            CanResume: false,
            SupportedStepKinds: [],
            Breakpoints: [.. _breakpoints],
            LastBreakpointHit: null,
            SessionOwnership: DebugSessionOwnershipSnapshot.Unowned,
            ActiveSession: null,
            Notices: UnsupportedStatusNotices,
            BreakpointManagementSupported: true,
            BreakpointEvaluationSupported: false);
    }
}
