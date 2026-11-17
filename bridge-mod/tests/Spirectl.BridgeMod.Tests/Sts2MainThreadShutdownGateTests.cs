#if ENABLE_STS2_LIVE_HOST
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class Sts2MainThreadShutdownGateTests
{
    [Fact]
    public void RunAfterCloseDoesNotInvokeAction()
    {
        var gate = new Sts2MainThreadShutdownGate();
        var invoked = false;
        gate.Close();

        gate.Run(() => invoked = true);

        Assert.False(invoked);
    }

    [Fact]
    public void CloseIsIdempotent()
    {
        var gate = new Sts2MainThreadShutdownGate();

        Assert.True(gate.Close());
        Assert.False(gate.Close());
        Assert.False(gate.Close());
    }

    [Fact]
    public async Task CloseWaitsForInFlightAction()
    {
        var gate = new Sts2MainThreadShutdownGate();
        using var releaseAction = new ManualResetEventSlim();
        var actionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = Task.Run(() => gate.Run(() =>
        {
            actionEntered.SetResult();
            Assert.True(releaseAction.Wait(TimeSpan.FromSeconds(10)));
        }));
        Task closing = Task.CompletedTask;

        try
        {
            await actionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            closing = Task.Run(() =>
            {
                closeStarted.SetResult();
                gate.Close();
            });
            await closeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var completed = await Task.WhenAny(closing, Task.Delay(100));
            Assert.NotSame(closing, completed);
        }
        finally
        {
            releaseAction.Set();
            await Task.WhenAll(inFlight, closing).WaitAsync(TimeSpan.FromSeconds(5));
        }

        var invoked = false;
        gate.Run(() => invoked = true);
        Assert.False(invoked);
    }
}
#endif
