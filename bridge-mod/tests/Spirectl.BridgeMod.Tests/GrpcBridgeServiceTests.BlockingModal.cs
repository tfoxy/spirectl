using Spirectl.BridgeMod.Services;
using Spirectl.Proto.V0;
using Spirectl.Sts2;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2.Core.Transport;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed partial class GrpcBridgeServiceTests
{
    [Fact]
    public void StateIncludesBlockingModalOutsideRun()
    {
        var service = new GrpcBridgeService(RuntimeWithStateProvider(new StateSnapshot(
            StateSnapshot.CurrentSchemaVersion, "eng", "screens/main_menu", null, null,
            new StateBlockingModalSnapshot("modal:TestFtue", "ftue", "TestFtue", "Tutorial", "Instructions",
                [new StateModalButtonSnapshot("modal-button:0", "Got it!")]))));

        var result = service.HandleGetState(new StateRequest());

        Assert.Null(result.Success.Run);
        Assert.Equal("modal:TestFtue", result.Success.BlockingModal.Id);
        Assert.Equal("Tutorial", result.Success.BlockingModal.Title);
        Assert.Equal("Instructions", result.Success.BlockingModal.Body);
        Assert.Equal("Got it!", Assert.Single(result.Success.BlockingModal.Buttons).Label);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("modal:TestFtue", "modal-button:0")]
    public void ExecuteActionMapsDismissModalRequests(string modalId, string buttonId)
    {
        var handler = new FixedActionHandler(ActionExecutionResult.Success("dismiss", SemanticActionKind.DismissModal, "Dismissed."));
        var service = ModalActionService(handler);

        var result = service.HandleExecuteAction(new ActionRequest
        {
            RequestId = "dismiss",
            DismissModal = new DismissModalAction { ModalId = modalId, ButtonId = buttonId },
        });

        Assert.True(result.Success.Accepted);
        Assert.Equal(ActionKind.DismissModal, result.Success.Kind);
        var request = Assert.IsType<SemanticActionRequest>(handler.LastRequest);
        Assert.Equal(SemanticActionKind.DismissModal, request.Kind);
        Assert.Equal(modalId, request.Values?.GetValueOrDefault("modalId") ?? string.Empty);
        Assert.Equal(buttonId, request.Values?.GetValueOrDefault("buttonId") ?? string.Empty);
    }

    [Theory]
    [InlineData(true, 30)]
    [InlineData(false, 3000)]
    public void ExecuteDismissModalMapsClosureResult(bool closed, long waitedMs)
    {
        var handler = new FixedActionHandler(ActionExecutionResult.Success("dismiss", SemanticActionKind.DismissModal, "Pressed.",
            values: new Dictionary<string, string> { ["closed"] = closed.ToString(), ["waitedMs"] = waitedMs.ToString() }));
        var result = ModalActionService(handler).HandleExecuteAction(new ActionRequest { DismissModal = new DismissModalAction() });

        Assert.True(result.Success.Accepted);
        Assert.Equal(closed, result.Success.DismissModal.Closed);
        Assert.Equal(waitedMs, result.Success.DismissModal.WaitedMs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExecuteTutorialsMapsSettingAndBeforeAfter(bool enabled)
    {
        var handler = new FixedActionHandler(ActionExecutionResult.Success("tutorials", SemanticActionKind.Tutorials, "Updated.",
            values: new Dictionary<string, string> { ["before"] = (!enabled).ToString(), ["after"] = enabled.ToString() }));
        var service = ModalActionService(handler);

        var result = service.HandleExecuteAction(new ActionRequest { Tutorials = new TutorialsAction { Enabled = enabled } });

        Assert.Equal(ActionKind.Tutorials, result.Success.Kind);
        Assert.Equal(!enabled, result.Success.Tutorials.Before);
        Assert.Equal(enabled, result.Success.Tutorials.After);
        Assert.Equal(enabled ? "true" : "false", handler.LastRequest!.Values!["enabled"]);
    }

    [Fact]
    public void BlockedActionPreservesModalReasonAndBlocker()
    {
        var handler = new FixedActionHandler(ActionExecutionResult.Failure(SemanticActionKind.EndTurn,
            ActionFailureCode.BlockedByModal, "Run `sts2 act dismiss-modal`.",
            [new Spirectl.Sts2.Core.Actions.ActionFailureDetail("blocker", "modal:TestFtue", "Dismiss it.",
                Blocker: new ModalBlockerSnapshot("modal:TestFtue", "ftue", "Tutorial"))]));
        var service = ModalActionService(handler);

        var result = service.HandleExecuteAction(new ActionRequest { EndTurn = new EndTurnAction() });

        Assert.Equal(ErrorActionFailureReasonCode.BlockedByModal, result.Error.ActionFailure.ReasonCode);
        var blocker = Assert.Single(result.Error.Details).Blocker;
        Assert.Equal("modal:TestFtue", blocker.Id);
        Assert.Equal("ftue", blocker.Kind);
        Assert.Equal("Tutorial", blocker.Title);
    }

    private static GrpcBridgeService ModalActionService(IActionHandler handler)
        => new(BridgeRuntime.Create(new BridgeRuntimeOptions
        {
            StateExtractor = new ObservedGameStateExtractor(new ScaffoldRuntimeObservationProvider(), new RuntimeStateMapper()),
            ActionHandler = handler,
            LogStream = new InMemoryLogStream(),
            PerspectiveProvider = new DefaultPerspectiveProvider(),
            BridgeHost = new NullBridgeHost(),
        }));
}
