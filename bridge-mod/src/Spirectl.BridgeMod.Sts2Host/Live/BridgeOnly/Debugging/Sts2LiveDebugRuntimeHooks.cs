using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using Spirectl.Sts2.Core.Debugging;

namespace Spirectl.Sts2.Live.Debugging;


internal sealed class Sts2LiveDebugRuntimeHooks : ILiveDebugRuntimeHooks
{
    public LiveDebugRuntimeCapabilities DescribeCapabilities()
    {
        return Sts2MainThreadDispatcher.Invoke(DescribeCapabilitiesOnMainThread);
    }

    public LiveDebugRuntimeResult Pause()
    {
        return Sts2MainThreadDispatcher.Invoke(PauseOnMainThread);
    }

    public LiveDebugRuntimeResult Resume()
    {
        return Sts2MainThreadDispatcher.Invoke(ResumeOnMainThread);
    }

    public LiveDebugRuntimeStepResult Step(
        DebugStepRequestSnapshot request,
        Func<DebugBreakpointHitSnapshot?> evaluateBreakpoint)
    {
        return request.Kind switch
        {
            DebugStepKindSnapshot.Frame => Sts2MainThreadDispatcher.InvokeAsync(() =>
                StepFramesOnMainThread(request.Count, evaluateBreakpoint)).GetAwaiter().GetResult(),
            DebugStepKindSnapshot.Action => Sts2MainThreadDispatcher.InvokeAsync(() =>
                StepActionsOnMainThread(request.Count, evaluateBreakpoint)).GetAwaiter().GetResult(),
            _ => new LiveDebugRuntimeStepResult(
                Applied: false,
                NoticeCode: "live_debug_step_kind_unsupported",
                NoticeMessage: $"Step kind '{request.Kind}' is not supported by the current live debug runtime."),
        };
    }

    private static LiveDebugRuntimeCapabilities DescribeCapabilitiesOnMainThread()
    {
        var runManager = RunManager.Instance;
        if (NGame.Instance == null || runManager?.ActionExecutor == null)
        {
            return new LiveDebugRuntimeCapabilities(
                Supported: false,
                IsPaused: false,
                SupportedStepKinds: [],
                UnsupportedReason: "Live debug control currently requires an active run with a wired action executor.");
        }

        return new LiveDebugRuntimeCapabilities(
            Supported: true,
            IsPaused: runManager.ActionExecutor.IsPaused || CombatManager.Instance?.IsPaused == true,
            SupportedStepKinds:
            [
                DebugStepKindSnapshot.Frame,
                DebugStepKindSnapshot.Action,
            ]);
    }

    private static LiveDebugRuntimeResult PauseOnMainThread()
    {
        var runManager = RunManager.Instance;
        if (runManager?.ActionExecutor == null)
        {
            return new LiveDebugRuntimeResult(
                Applied: false,
                NoticeCode: "live_debug_pause_unavailable",
                NoticeMessage: "Pause requires an active run with a wired action executor.");
        }

        runManager.ActionExecutor.Pause();
        CombatManager.Instance?.Pause();
        return new LiveDebugRuntimeResult(
            Applied: true,
            Detail: "Paused the live action executor.");
    }

    private static LiveDebugRuntimeResult ResumeOnMainThread()
    {
        var runManager = RunManager.Instance;
        if (runManager?.ActionExecutor == null)
        {
            return new LiveDebugRuntimeResult(
                Applied: false,
                NoticeCode: "live_debug_resume_unavailable",
                NoticeMessage: "Resume requires an active run with a wired action executor.");
        }

        runManager.ActionExecutor.Unpause();
        CombatManager.Instance?.Unpause();
        return new LiveDebugRuntimeResult(Applied: true);
    }

    private static async Task<LiveDebugRuntimeStepResult> StepFramesOnMainThread(
        uint count,
        Func<DebugBreakpointHitSnapshot?> evaluateBreakpoint)
    {
        var runManager = RunManager.Instance;
        var game = NGame.Instance;
        var tree = game?.GetTree();
        if (runManager?.ActionExecutor == null || game == null || tree == null)
        {
            return new LiveDebugRuntimeStepResult(
                Applied: false,
                NoticeCode: "live_debug_frame_step_unavailable",
                NoticeMessage: "Frame stepping requires an active run and scene tree.");
        }

        runManager.ActionExecutor.Unpause();
        CombatManager.Instance?.Unpause();

        try
        {
            for (var index = 0u; index < Math.Max(1u, count); index += 1)
            {
                await game.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                var breakpointHit = evaluateBreakpoint();
                if (breakpointHit is not null)
                {
                    return new LiveDebugRuntimeStepResult(
                        Applied: true,
                        BreakpointHit: breakpointHit,
                        Detail: $"Paused on breakpoint during frame step {index + 1}.");
                }
            }

            return new LiveDebugRuntimeStepResult(
                Applied: true,
                Detail: $"Completed frame step of {Math.Max(1u, count)} frame(s).");
        }
        finally
        {
            runManager.ActionExecutor.Pause();
            CombatManager.Instance?.Pause();
        }
    }

    private static async Task<LiveDebugRuntimeStepResult> StepActionsOnMainThread(
        uint count,
        Func<DebugBreakpointHitSnapshot?> evaluateBreakpoint)
    {
        var runManager = RunManager.Instance;
        var actionExecutor = runManager?.ActionExecutor;
        var actionQueueSet = runManager?.ActionQueueSet;
        var game = NGame.Instance;
        var tree = game?.GetTree();
        if (actionExecutor == null || actionQueueSet == null || game == null || tree == null)
        {
            return new LiveDebugRuntimeStepResult(
                Applied: false,
                NoticeCode: "live_debug_action_step_unavailable",
                NoticeMessage: "Action stepping requires an active run with a wired action queue.");
        }

        if (actionExecutor.CurrentlyRunningAction == null && actionQueueSet.GetReadyAction() == null)
        {
            return new LiveDebugRuntimeStepResult(
                Applied: false,
                NoticeCode: "live_debug_action_step_idle",
                NoticeMessage: "Action stepping requires a running or ready game action in the current run.");
        }

        var requestedCount = Math.Max(1u, count);
        var remaining = requestedCount;
        var completion = new TaskCompletionSource<LiveDebugRuntimeStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Finish(LiveDebugRuntimeStepResult result)
        {
            completion.TrySetResult(result);
        }

        void HandleAfterAction(GameAction _)
        {
            var breakpointHit = evaluateBreakpoint();
            if (breakpointHit is not null)
            {
                Finish(new LiveDebugRuntimeStepResult(
                    Applied: true,
                    BreakpointHit: breakpointHit,
                    Detail: $"Paused on breakpoint before completing action step {requestedCount}."));
                return;
            }

            if (--remaining == 0)
            {
                Finish(new LiveDebugRuntimeStepResult(
                    Applied: true,
                    Detail: $"Completed action step of {requestedCount} action(s)."));
            }
        }

        actionExecutor.AfterActionExecuted += HandleAfterAction;
        actionExecutor.Unpause();
        CombatManager.Instance?.Unpause();

        try
        {
            while (!completion.Task.IsCompleted)
            {
                await game.ToSignal(tree, SceneTree.SignalName.ProcessFrame);

                var breakpointHit = evaluateBreakpoint();
                if (breakpointHit is not null)
                {
                    Finish(new LiveDebugRuntimeStepResult(
                        Applied: true,
                        BreakpointHit: breakpointHit,
                        Detail: $"Paused on breakpoint during action step {requestedCount}."));
                    continue;
                }

                if (!actionExecutor.IsRunning
                    && actionExecutor.CurrentlyRunningAction == null
                    && actionQueueSet.GetReadyAction() == null)
                {
                    Finish(new LiveDebugRuntimeStepResult(
                        Applied: false,
                        NoticeCode: "live_debug_action_step_idle",
                        NoticeMessage: "The action queue became idle before the requested action step completed.",
                        Detail: $"Completed {requestedCount - remaining} of {requestedCount} requested action steps before the queue went idle."));
                }
            }

            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            actionExecutor.AfterActionExecuted -= HandleAfterAction;
            actionExecutor.Pause();
            CombatManager.Instance?.Pause();
        }
    }
}
