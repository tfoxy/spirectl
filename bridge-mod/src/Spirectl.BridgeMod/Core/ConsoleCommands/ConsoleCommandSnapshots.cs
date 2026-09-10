using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Core.ConsoleCommands;


public sealed record ConsoleCommandRequestSnapshot(
    string RequestId,
    string Command,
    IReadOnlyList<string> Args,
    string Line);

public sealed record ConsoleCommandNoticeSnapshot(
    string Code,
    string Message,
    bool Provisional);

public sealed record ConsoleCommandExecutionResult(
    string RequestId,
    string Command,
    IReadOnlyList<string> Args,
    string Line,
    bool Accepted,
    bool Success,
    string Output,
    IReadOnlyList<string> OutputLines,
    DataSourceKind Source,
    bool Provisional,
    IReadOnlyList<ConsoleCommandNoticeSnapshot> Notices,
    ConsoleCommandFailure? Error)
{
    public static ConsoleCommandExecutionResult SuccessResult(
        string requestId,
        string command,
        IReadOnlyList<string> args,
        string line,
        bool success,
        string output,
        IReadOnlyList<string> outputLines,
        DataSourceKind source,
        bool provisional,
        IReadOnlyList<ConsoleCommandNoticeSnapshot>? notices = null)
    {
        return new ConsoleCommandExecutionResult(
            requestId,
            command,
            args,
            line,
            Accepted: true,
            Success: success,
            Output: output,
            OutputLines: outputLines,
            Source: source,
            Provisional: provisional,
            Notices: notices ?? Array.Empty<ConsoleCommandNoticeSnapshot>(),
            Error: null);
    }

    public static ConsoleCommandExecutionResult Failure(
        string requestId,
        string command,
        IReadOnlyList<string> args,
        string line,
        ConsoleCommandFailureCode code,
        string message,
        IReadOnlyList<ConsoleCommandDetail>? details = null)
    {
        return new ConsoleCommandExecutionResult(
            requestId,
            command,
            args,
            line,
            Accepted: false,
            Success: false,
            Output: string.Empty,
            OutputLines: Array.Empty<string>(),
            Source: DataSourceKind.Stub,
            Provisional: true,
            Notices: Array.Empty<ConsoleCommandNoticeSnapshot>(),
            Error: new ConsoleCommandFailure(code, message, details ?? Array.Empty<ConsoleCommandDetail>()));
    }
}

public enum ConsoleCommandFailureCode
{
    NotImplemented,
    InvalidRequest,
    BridgeNotAttached,
    RuntimeFailure,
}

public sealed record ConsoleCommandFailure(
    ConsoleCommandFailureCode Code,
    string Message,
    IReadOnlyList<ConsoleCommandDetail> Details);

public sealed record ConsoleCommandDetail(
    string Field,
    string Value,
    string Note);
