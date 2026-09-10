namespace Spirectl.Sts2.Core.Debugging;


public enum DebugExecutionStateSnapshot
{
    Running,
    Paused,
    Unsupported,
}

public enum DebugPauseReasonSnapshot
{
    None,
    Manual,
    Breakpoint,
    StepComplete,
    Unsupported,
    BridgeError,
}

public enum DebugStepKindSnapshot
{
    Frame,
    Action,
}

public enum DebugPredicateOperatorSnapshot
{
    Equals,
    Contains,
    Regex,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Exists,
    NotExists,
}

public enum DebugBreakpointKindSnapshot
{
    Match,
    Change,
}

public enum DebugSessionOwnershipSnapshot
{
    Unowned,
    OwnedByCaller,
    LeasedElsewhere,
}

public enum DebugSessionRoleSnapshot
{
    Controller,
    Observer,
}

public enum DebugEventKindSnapshot
{
    StatusChanged,
    BreakpointHit,
    Paused,
    Resumed,
    Stepped,
    LeaseChanged,
    ObserverAttached,
    ObserverDetached,
    Disconnected,
    ClientDropped,
    StateChanged,
}

public sealed record DebugNoticeSnapshot(
    string Code,
    string Message);

public sealed record DebugPredicateSnapshot(
    DebugPredicateOperatorSnapshot Operator,
    string? ExpectedJson);

public sealed record DebugSessionInfoSnapshot(
    string Id,
    string? Name,
    uint LeaseTimeoutMs,
    long LeaseExpiresAtUnixMs,
    DebugSessionRoleSnapshot Role = DebugSessionRoleSnapshot.Controller);

public sealed record DebugBreakpointSnapshot(
    string Id,
    string? Name,
    string QueryPath,
    DebugBreakpointKindSnapshot Kind,
    DebugPredicateSnapshot? Predicate,
    bool Enabled,
    bool Provisional,
    uint MinHitCount,
    uint HitCount,
    bool AutoRemoveOnHit,
    string? LastObservedJson);

public sealed record DebugBreakpointHitSnapshot(
    string BreakpointId,
    string? BreakpointName,
    string QueryPath,
    DebugBreakpointKindSnapshot Kind,
    DebugPredicateSnapshot? Predicate,
    string? ActualJson,
    string? ScreenType,
    string? ScreenInstanceId,
    uint HitCount);

public sealed record DebugStateQueryEvaluationSnapshot(
    string QueryPath,
    DebugPredicateSnapshot Predicate,
    bool Matched,
    string? ActualJson);

public sealed record DebugStatusRequestSnapshot(
    string? SessionId);

public sealed record DebugStatusSnapshot(
    bool Supported,
    DebugExecutionStateSnapshot ExecutionState,
    DebugPauseReasonSnapshot PauseReason,
    string? PauseReasonDetail,
    bool CanPause,
    bool CanResume,
    IReadOnlyList<DebugStepKindSnapshot> SupportedStepKinds,
    IReadOnlyList<DebugBreakpointSnapshot> Breakpoints,
    DebugBreakpointHitSnapshot? LastBreakpointHit,
    DebugSessionOwnershipSnapshot SessionOwnership,
    DebugSessionInfoSnapshot? ActiveSession,
    IReadOnlyList<DebugNoticeSnapshot> Notices,
    bool BreakpointManagementSupported,
    bool BreakpointEvaluationSupported,
    DebugSessionRoleSnapshot? CallerRole = null,
    IReadOnlyList<DebugSessionInfoSnapshot>? ObserverSessions = null);

public sealed record DebugSessionBoundRequestSnapshot(
    string? SessionId);

public sealed record DebugOperationResultSnapshot(
    bool Applied,
    DebugStatusSnapshot Status,
    IReadOnlyList<DebugNoticeSnapshot> Notices);

public sealed record DebugSessionStartRequestSnapshot(
    string? Name,
    bool PauseOnStart,
    uint LeaseTimeoutMs,
    DebugSessionRoleSnapshot Role = DebugSessionRoleSnapshot.Controller);

public sealed record DebugSessionStartResultSnapshot(
    bool Started,
    DebugSessionInfoSnapshot? Session,
    DebugStatusSnapshot Status,
    IReadOnlyList<DebugNoticeSnapshot> Notices);

public sealed record DebugSessionStatusRequestSnapshot(
    string Id);

public sealed record DebugSessionStatusResultSnapshot(
    bool Found,
    DebugSessionInfoSnapshot? Session,
    DebugStatusSnapshot Status,
    IReadOnlyList<DebugNoticeSnapshot> Notices);

public sealed record DebugSessionEndRequestSnapshot(
    string Id,
    bool ResumeRuntime);

public sealed record DebugSessionEndResultSnapshot(
    bool Ended,
    string SessionId,
    DebugStatusSnapshot Status,
    IReadOnlyList<DebugNoticeSnapshot> Notices);

public sealed record DebugWaitRequestSnapshot(
    string SessionId,
    uint TimeoutMs);

public sealed record DebugWaitResultSnapshot(
    bool Completed,
    bool TimedOut,
    DebugStatusSnapshot Status,
    IReadOnlyList<DebugNoticeSnapshot> Notices);

public sealed record DebugBreakpointListRequestSnapshot(
    string? SessionId);

public sealed record DebugBreakpointListResultSnapshot(
    IReadOnlyList<DebugBreakpointSnapshot> Breakpoints,
    DebugStatusSnapshot Status,
    IReadOnlyList<DebugNoticeSnapshot> Notices);

public sealed record DebugBreakpointAddRequestSnapshot(
    string? SessionId,
    string QueryPath,
    DebugBreakpointKindSnapshot Kind,
    DebugPredicateSnapshot? Predicate,
    string? Name,
    uint MinHitCount,
    bool AutoRemoveOnHit);

public sealed record DebugBreakpointAddResultSnapshot(
    bool Added,
    DebugBreakpointSnapshot? Breakpoint,
    DebugStatusSnapshot Status,
    IReadOnlyList<DebugNoticeSnapshot> Notices);

public sealed record DebugBreakpointRemoveRequestSnapshot(
    string? SessionId,
    string BreakpointId);

public sealed record DebugBreakpointRemoveResultSnapshot(
    bool Removed,
    string BreakpointId,
    DebugStatusSnapshot Status,
    IReadOnlyList<DebugNoticeSnapshot> Notices);

public sealed record DebugStepRequestSnapshot(
    string? SessionId,
    DebugStepKindSnapshot Kind,
    uint Count);

public sealed record DebugEventStatusChangedSnapshot(
    DebugExecutionStateSnapshot PreviousExecutionState,
    DebugExecutionStateSnapshot ExecutionState,
    DebugPauseReasonSnapshot PauseReason,
    string? PauseReasonDetail);

public sealed record DebugEventStepSnapshot(
    DebugStepKindSnapshot Kind,
    uint Count,
    string? Detail);

public sealed record DebugLeaseConflictSnapshot(
    DebugSessionInfoSnapshot ActiveController,
    string? RequestedName,
    uint RequestedLeaseTimeoutMs,
    IReadOnlyList<DebugNoticeSnapshot> Notices);

public sealed record DebugEventLeaseChangedSnapshot(
    DebugSessionInfoSnapshot? PreviousController,
    DebugSessionInfoSnapshot? Controller,
    DebugLeaseConflictSnapshot? Conflict);

public sealed record DebugEventSessionSnapshot(
    DebugSessionInfoSnapshot Session,
    string? Reason = null,
    ulong DroppedEventCount = 0);

public sealed record DebugEventStateChangedSnapshot(
    string? ScreenType,
    string? ScreenInstanceId,
    IReadOnlyList<string> ChangedPaths,
    string? SelectedStateJson);

public sealed record DebugEventSnapshot(
    ulong Sequence,
    long UnixTimeMs,
    DebugEventKindSnapshot Kind,
    string? SessionId,
    DebugSessionRoleSnapshot? SessionRole,
    IReadOnlyList<DebugNoticeSnapshot> Notices,
    DebugEventStatusChangedSnapshot? StatusChanged = null,
    DebugBreakpointHitSnapshot? BreakpointHit = null,
    string? PauseDetail = null,
    string? ResumeDetail = null,
    DebugEventStepSnapshot? Step = null,
    DebugEventLeaseChangedSnapshot? LeaseChanged = null,
    DebugEventSessionSnapshot? SessionChanged = null,
    DebugEventStateChangedSnapshot? StateChanged = null);

public sealed record DebugEventAppendSnapshot(
    DebugEventKindSnapshot Kind,
    string? SessionId,
    DebugSessionRoleSnapshot? SessionRole,
    IReadOnlyList<DebugNoticeSnapshot>? Notices = null,
    DebugEventStatusChangedSnapshot? StatusChanged = null,
    DebugBreakpointHitSnapshot? BreakpointHit = null,
    string? PauseDetail = null,
    string? ResumeDetail = null,
    DebugEventStepSnapshot? Step = null,
    DebugEventLeaseChangedSnapshot? LeaseChanged = null,
    DebugEventSessionSnapshot? SessionChanged = null,
    DebugEventStateChangedSnapshot? StateChanged = null);

public sealed record DebugEventStreamRequestSnapshot(
    string? SessionId,
    ulong FromSequence,
    uint Limit);

public sealed record DebugEventRetentionSnapshot(
    ulong OldestRetainedSequence,
    ulong NewestSequence,
    uint RetentionLimit);

public sealed record DebugEventStreamResultSnapshot(
    IReadOnlyList<DebugEventSnapshot> Events,
    ulong FromSequence,
    ulong NextSequence,
    DebugEventRetentionSnapshot Retention,
    bool Expired,
    bool Overflow,
    IReadOnlyList<DebugNoticeSnapshot> Notices);
