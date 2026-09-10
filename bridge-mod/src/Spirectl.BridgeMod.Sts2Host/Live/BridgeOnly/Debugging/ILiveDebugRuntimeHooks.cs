using Spirectl.Sts2.Core.Debugging;

namespace Spirectl.Sts2.Live.Debugging;


public interface ILiveDebugRuntimeHooks
{
    LiveDebugRuntimeCapabilities DescribeCapabilities();

    LiveDebugRuntimeResult Pause();

    LiveDebugRuntimeResult Resume();

    LiveDebugRuntimeStepResult Step(
        DebugStepRequestSnapshot request,
        Func<DebugBreakpointHitSnapshot?> evaluateBreakpoint);
}

public sealed record LiveDebugRuntimeCapabilities(
    bool Supported,
    bool IsPaused,
    IReadOnlyList<DebugStepKindSnapshot> SupportedStepKinds,
    string? UnsupportedReason = null);

public sealed record LiveDebugRuntimeResult(
    bool Applied,
    string? NoticeCode = null,
    string? NoticeMessage = null,
    string? Detail = null);

public sealed record LiveDebugRuntimeStepResult(
    bool Applied,
    DebugBreakpointHitSnapshot? BreakpointHit = null,
    string? NoticeCode = null,
    string? NoticeMessage = null,
    string? Detail = null);
