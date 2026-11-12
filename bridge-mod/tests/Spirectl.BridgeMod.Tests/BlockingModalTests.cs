using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class BlockingModalTests
{
    [Fact]
    public void ModalActionKindsAreAppendedWithoutRenumbering()
    {
        Assert.Equal(75, (int)SemanticActionKind.DismissModal);
        Assert.Equal(76, (int)SemanticActionKind.Tutorials);
        Assert.Equal(SemanticActionKind.Tutorials, Enum.GetValues<SemanticActionKind>()[^1]);
    }

    [Fact]
    public void FtueExposesTextAndSingleConfirm()
    {
        var modal = Sts2BlockingModalResolver.Resolve(Container(new FakeControl("CombatFtue",
            new FakeLabel("Header", "Combat tutorial"), new FakeRichTextLabel("Description", "Play a card."),
            new FakeButton("FtueConfirmButton", new FakeLabel("Label", "Got it!")))));
        Assert.NotNull(modal);
        Assert.Equal("modal:CombatFtue", modal.State.Id);
        Assert.Equal("ftue", modal.State.Kind);
        Assert.Equal("Combat tutorial", modal.State.Title);
        Assert.Equal("Play a card.", modal.State.Body);
        Assert.Equal("Got it!", Assert.Single(modal.State.Buttons).Label);
    }

    [Fact]
    public void TwoButtonPopupRequiresExplicitButton()
    {
        var modal = Sts2BlockingModalResolver.Resolve(Container(new FakeControl("AcceptTutorials",
            new FakeButton("Yes"), new FakeButton("No"))));
        Assert.Equal("popup", modal!.State.Kind);
        Assert.Equal(2, modal.State.Buttons.Count);
        var result = Sts2BlockingModalResolver.Dismiss(new SemanticActionRequest("test", SemanticActionKind.DismissModal, null, null, null, null, null, null, null, null, null, null, null), modal);
        Assert.Equal(ActionFailureCode.InvalidArgument, result.Error!.Code);
        Assert.Equal(["modal-button:0", "modal-button:1"], result.Error.Details.Select(d => d.Value));
    }

    [Fact]
    public void SingleButtonDismissalUsesPublicClickHook()
    {
        var button = new FakeButton("FtueConfirmButton");
        var modal = Sts2BlockingModalResolver.Resolve(Container(new FakeControl("CombatFtue", button)));
        var result = Sts2BlockingModalResolver.Dismiss(
            new SemanticActionRequest("test", SemanticActionKind.DismissModal, null, null, null, null, null, null, null, null, null, null, null), modal);

        Assert.True(result.Accepted);
        Assert.Equal(1, button.ClickCount);
        Assert.Equal("modal:CombatFtue", result.Values!["modalId"]);
        Assert.Equal("false", result.Values["closed"]);
        Assert.Equal("0", result.Values["waitedMs"]);
    }

    [Fact]
    public void ExplicitButtonDismissalClicksOnlySelectedButton()
    {
        var yes = new FakeButton("Yes");
        var no = new FakeButton("No");
        var modal = Sts2BlockingModalResolver.Resolve(Container(new FakeControl("AcceptTutorials", yes, no)));
        var result = Sts2BlockingModalResolver.Dismiss(
            new SemanticActionRequest("test", SemanticActionKind.DismissModal, null, null, null, null, null, null, null, null, null, null, null,
                Values: new Dictionary<string, string> { ["buttonId"] = "modal-button:1" }), modal);

        Assert.True(result.Accepted);
        Assert.Equal(0, yes.ClickCount);
        Assert.Equal(1, no.ClickCount);
    }

    [Fact]
    public void BackstopAloneIsNotModal() => Assert.Null(Sts2BlockingModalResolver.Resolve(Container()));

    [Fact]
    public void HiddenAndZeroSizeModalsAreIgnored()
    {
        Assert.Null(Sts2BlockingModalResolver.Resolve(Container(new FakeControl("HiddenFtue") { Visible = false })));
        Assert.Null(Sts2BlockingModalResolver.Resolve(Container(new FakeControl("HiddenFtue") { Size = new FakeSize(0, 0) })));
    }

    [Fact]
    public void PrecheckNamesBlockerAndAllowsDismissalRawInputAndHostSessionActions()
    {
        var modal = Sts2BlockingModalResolver.Resolve(Container(new FakeControl("CombatFtue", new FakeButton("Confirm"))));
        var failure = Sts2BlockingModalResolver.Precheck(SemanticActionKind.EndTurn, modal);
        Assert.Equal(ActionFailureCode.BlockedByModal, failure!.Error!.Code);
        Assert.Equal("modal:CombatFtue", Assert.Single(failure.Error.Details).Blocker!.Id);
        Assert.Equal(ActionFailureCode.BlockedByModal,
            Sts2BlockingModalResolver.Precheck(SemanticActionKind.SelectMapNode, modal)!.Error!.Code);
        foreach (var kind in new[] { SemanticActionKind.DismissModal, SemanticActionKind.MouseClick,
            SemanticActionKind.HoverElement, SemanticActionKind.KeyInput, SemanticActionKind.ControllerInput,
            SemanticActionKind.DisconnectClient, SemanticActionKind.SetClientName })
        {
            Assert.Null(Sts2BlockingModalResolver.Precheck(kind, modal));
        }
    }

    [Theory]
    [InlineData(3, true, 30)]
    [InlineData(int.MaxValue, false, 50)]
    public async Task DismissalWaitsForResolverOrDeadline(int visibleFrames, bool closed, int waitedMs)
    {
        var modal = Sts2BlockingModalResolver.Resolve(Container(new FakeControl("CombatFtue", new FakeButton("Confirm"))));
        var frames = 0;
        var pressed = ActionExecutionResult.Success("test", SemanticActionKind.DismissModal, "Pressed.");
        var result = await Sts2BlockingModalResolver.WaitForDismissalAsync(
            pressed, modal!.State.Id, () => frames < visibleFrames ? modal : null,
            () => { frames++; return Task.CompletedTask; }, timeoutMs: 50, elapsedMilliseconds: () => frames * 10);

        Assert.True(result.Accepted);
        Assert.Equal(closed ? "true" : "false", result.Values!["closed"]);
        Assert.Equal(waitedMs.ToString(), result.Values["waitedMs"]);
        if (!closed) Assert.Contains("still closing", result.Message);
    }

    [Fact]
    public async Task DismissalDeadlineDoesNotRequireAnotherProcessFrame()
    {
        var modal = Sts2BlockingModalResolver.Resolve(Container(new FakeControl("CombatFtue", new FakeButton("Confirm"))));
        var pressed = ActionExecutionResult.Success("test", SemanticActionKind.DismissModal, "Pressed.");
        var noFrame = new TaskCompletionSource();
        var result = await Sts2BlockingModalResolver.WaitForDismissalAsync(
            pressed, modal!.State.Id, () => modal, () => noFrame.Task, timeoutMs: 10);

        Assert.True(result.Accepted);
        Assert.Equal("false", result.Values!["closed"]);
        Assert.True(long.Parse(result.Values["waitedMs"]) >= 10);
    }

    private static FakeControl Container(params FakeControl[] children) => new("ModalContainer", [new FakeControl("Backstop"), .. children]);
    public record FakeSize(float X, float Y);
    public class FakeControl(string name, params FakeControl[] children)
    {
        public string Name { get; } = name;
        public bool Visible { get; init; } = true;
        public FakeSize Size { get; init; } = new(400, 200);
        public FakeControl[] Children { get; } = children;
    }
    public class FakeLabel(string name, string text) : FakeControl(name) { public string Text { get; } = text; }
    public class FakeRichTextLabel(string name, string text) : FakeLabel(name, text);
    public class FakeClickableControl(string name, params FakeControl[] children) : FakeControl(name, children)
    {
        public int ClickCount { get; private set; }
        public void ForceClick() => ClickCount++;
    }
    public class FakeButton(string name, params FakeControl[] children) : FakeClickableControl(name, children);
}
