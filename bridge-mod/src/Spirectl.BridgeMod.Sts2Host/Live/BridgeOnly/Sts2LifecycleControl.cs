using Godot;
using Spirectl.Sts2.Core.Lifecycle;
using Spirectl.Sts2.Core.Logging;

namespace Spirectl.Sts2.Live;


public sealed class Sts2LifecycleControl : ILifecycleControl
{
    private readonly ILogStream _logStream;

    public Sts2LifecycleControl(ILogStream logStream)
    {
        _logStream = logStream;
    }

    public GameCloseOperationResult CloseGame(string requestId)
    {
        try
        {
            return Sts2MainThreadDispatcher.Invoke(CloseOnMainThread);
        }
        catch (Exception ex)
        {
            _logStream.Write(BridgeLogLevel.Error, "bridge.lifecycle", $"Game close request failed: {ex}");
            return new GameCloseOperationResult(
                Accepted: false,
                Notices: [],
                Error: new LifecycleFailure(
                    LifecycleFailureCode.RuntimeFailure,
                    "failed to request graceful game close from the live bridge host.",
                    [
                        new LifecycleFailureDetail(
                            Field: "exception",
                            Value: ex.GetType().Name,
                            Note: ex.Message),
                    ]));
        }
    }

    private GameCloseOperationResult CloseOnMainThread()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
        {
            return new GameCloseOperationResult(
                Accepted: false,
                Notices: ["No SceneTree was available for graceful shutdown."],
                Error: new LifecycleFailure(
                    LifecycleFailureCode.RuntimeFailure,
                    "the live bridge host could not resolve the Godot SceneTree for graceful game close.",
                    [
                        new LifecycleFailureDetail(
                            Field: "main_loop",
                            Value: "null",
                            Note: "Engine.GetMainLoop() did not return a SceneTree."),
                    ]));
        }

        tree.Quit();
        _logStream.Write(BridgeLogLevel.Info, "bridge.lifecycle", "Requested SceneTree.Quit().");
        return new GameCloseOperationResult(
            Accepted: true,
            Notices: ["Requested SceneTree.Quit()."],
            Error: null);
    }
}
