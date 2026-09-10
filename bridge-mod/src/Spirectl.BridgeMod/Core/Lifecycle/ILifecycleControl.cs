namespace Spirectl.Sts2.Core.Lifecycle;


public sealed record GameCloseOperationResult(
    bool Accepted,
    IReadOnlyList<string> Notices,
    LifecycleFailure? Error);

public enum LifecycleFailureCode
{
    NotImplemented,
    BridgeNotAttached,
    RuntimeFailure,
}

public sealed record LifecycleFailureDetail(
    string Field,
    string Value,
    string Note);

public sealed record LifecycleFailure(
    LifecycleFailureCode Code,
    string Message,
    IReadOnlyList<LifecycleFailureDetail> Details);

public interface ILifecycleControl
{
    GameCloseOperationResult CloseGame(string requestId);
}
