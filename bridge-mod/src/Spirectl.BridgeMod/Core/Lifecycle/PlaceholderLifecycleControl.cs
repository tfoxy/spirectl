namespace Spirectl.Sts2.Core.Lifecycle;


public sealed class PlaceholderLifecycleControl : ILifecycleControl
{
    public GameCloseOperationResult CloseGame(string requestId)
    {
        return new GameCloseOperationResult(
            Accepted: false,
            Notices: [],
            Error: new LifecycleFailure(
                LifecycleFailureCode.NotImplemented,
                "graceful game close requires the live STS2 bridge host.",
                [
                    new LifecycleFailureDetail(
                        Field: "command",
                        Value: "game close",
                        Note: "Run game close against a live bridge host or configure game.stopCommand for CLI fallback."),
                ]));
    }
}
