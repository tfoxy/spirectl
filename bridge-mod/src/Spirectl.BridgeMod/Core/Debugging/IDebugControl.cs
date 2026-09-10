namespace Spirectl.Sts2.Core.Debugging;


public interface IDebugControl
{
    DebugStatusSnapshot GetStatus(DebugStatusRequestSnapshot request);

    DebugSessionStartResultSnapshot StartSession(DebugSessionStartRequestSnapshot request);

    DebugSessionStatusResultSnapshot GetSessionStatus(DebugSessionStatusRequestSnapshot request);

    DebugSessionEndResultSnapshot EndSession(DebugSessionEndRequestSnapshot request);

    DebugOperationResultSnapshot Pause(DebugSessionBoundRequestSnapshot request);

    DebugOperationResultSnapshot Resume(DebugSessionBoundRequestSnapshot request);

    DebugOperationResultSnapshot Step(DebugStepRequestSnapshot request);

    DebugWaitResultSnapshot Wait(DebugWaitRequestSnapshot request);

    DebugBreakpointListResultSnapshot ListBreakpoints(DebugBreakpointListRequestSnapshot request);

    DebugBreakpointAddResultSnapshot AddBreakpoint(DebugBreakpointAddRequestSnapshot request);

    DebugBreakpointRemoveResultSnapshot RemoveBreakpoint(DebugBreakpointRemoveRequestSnapshot request);

    DebugEventStreamResultSnapshot GetEvents(DebugEventStreamRequestSnapshot request);
}
