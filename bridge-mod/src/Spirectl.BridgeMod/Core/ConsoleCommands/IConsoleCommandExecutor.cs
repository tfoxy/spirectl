namespace Spirectl.Sts2.Core.ConsoleCommands;


public interface IConsoleCommandExecutor
{
    ConsoleCommandExecutionResult Execute(ConsoleCommandRequestSnapshot request);
}
