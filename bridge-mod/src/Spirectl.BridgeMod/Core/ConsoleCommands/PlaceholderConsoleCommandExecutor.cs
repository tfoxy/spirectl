namespace Spirectl.Sts2.Core.ConsoleCommands;


public sealed class PlaceholderConsoleCommandExecutor : IConsoleCommandExecutor
{
    public ConsoleCommandExecutionResult Execute(ConsoleCommandRequestSnapshot request)
    {
        return ConsoleCommandExecutionResult.Failure(
            request.RequestId,
            request.Command,
            request.Args,
            request.Line,
            ConsoleCommandFailureCode.NotImplemented,
            "Console command execution requires the live STS2 bridge host.",
            [
                new ConsoleCommandDetail(
                    "command",
                    "dev console",
                    "Use a live bridge transport attached to Slay the Spire 2.")
            ]);
    }
}
