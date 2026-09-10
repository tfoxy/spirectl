using System.Reflection;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Runs;
using Spirectl.Sts2.Core.ConsoleCommands;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Live;


public sealed class Sts2ConsoleCommandExecutor : IConsoleCommandExecutor
{
    private const string LogTarget = "bridge.dev-console";

    private static readonly MethodInfo InternalProcessCommandMethod =
        typeof(DevConsole).GetMethod(
            "ProcessCommand",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(Player), typeof(string), typeof(string[])],
            modifiers: null)
        ?? throw new MissingMethodException(typeof(DevConsole).FullName, "ProcessCommand(Player?, string, string[])");

    private static readonly FieldInfo CommandsField =
        typeof(DevConsole).GetField("_commands", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(DevConsole).FullName, "_commands");

    private readonly ILogStream _logStream;
    private DevConsole? _console;

    public Sts2ConsoleCommandExecutor(ILogStream logStream)
    {
        _logStream = logStream;
    }

    public ConsoleCommandExecutionResult Execute(ConsoleCommandRequestSnapshot request)
    {
        return Sts2MainThreadDispatcher.Invoke(() => ExecuteOnMainThread(request));
    }

    private ConsoleCommandExecutionResult ExecuteOnMainThread(ConsoleCommandRequestSnapshot request)
    {
        var command = request.Command.Trim();
        if (string.IsNullOrWhiteSpace(command))
        {
            return ConsoleCommandExecutionResult.Failure(
                request.RequestId,
                request.Command,
                request.Args,
                request.Line,
                ConsoleCommandFailureCode.InvalidRequest,
                "dev console requires a command name.",
                [
                    new ConsoleCommandDetail(
                        "command",
                        string.Empty,
                        "Provide the first positional token after 'dev console'.")
                ]);
        }

        var args = request.Args.ToArray();
        var line = string.IsNullOrWhiteSpace(request.Line)
            ? BuildConsoleLine(command, args)
            : request.Line.Trim();

        try
        {
            var console = GetConsole();
            var runManager = RunManager.Instance;
            var runState = runManager?.DebugOnlyGetState();
            var player = runState is null ? null : LocalContext.GetMe(runState);
            var dispatchPath = ConsoleCommandDispatchPolicy.Resolve(
                isRealMultiplayer: runManager?.IsSingleplayerOrFakeMultiplayer == false,
                isNetworkedCommand: IsNetworkedCommand(console, command),
                hasRunState: runState is not null,
                hasLocalPlayer: player is not null);
            var notices = Array.Empty<ConsoleCommandNoticeSnapshot>() as IReadOnlyList<ConsoleCommandNoticeSnapshot>;
            CmdResult result;
            if (dispatchPath == ConsoleCommandDispatchPath.Networked)
            {
                result = console.ProcessCommand(line);
                notices =
                [
                    new ConsoleCommandNoticeSnapshot(
                        "networked-console-path",
                        "The command was routed through STS2 DevConsole.ProcessCommand so networked console action enqueue semantics are preserved.",
                        false)
                ];
            }
            else
            {
                result = (CmdResult)InternalProcessCommandMethod.Invoke(
                    console,
                    [player, command, args])!;
                if (result.task is not null)
                {
                    TaskHelper.RunSafely(result.task);
                }
            }

            var output = result.msg ?? string.Empty;
            return ConsoleCommandExecutionResult.SuccessResult(
                request.RequestId,
                command,
                args,
                line,
                result.success,
                output,
                OutputLines(output),
                DataSourceKind.Live,
                provisional: false,
                notices);
        }
        catch (Exception ex)
        {
            var unwrapped = ex is TargetInvocationException { InnerException: not null }
                ? ex.InnerException
                : ex;
            _logStream.Write(
                BridgeLogLevel.Error,
                LogTarget,
                $"Console command '{line}' failed with an unhandled runtime exception: {unwrapped}");
            return ConsoleCommandExecutionResult.Failure(
                request.RequestId,
                command,
                args,
                line,
                ConsoleCommandFailureCode.RuntimeFailure,
                $"Console command '{line}' failed with an unhandled runtime exception.",
                [
                    new ConsoleCommandDetail(
                        "exception",
                        unwrapped.GetType().FullName ?? unwrapped.GetType().Name,
                        unwrapped.Message)
                ]);
        }
    }

    private DevConsole GetConsole()
        => _console ??= new DevConsole(shouldAllowDebugCommands: true);

    private static bool IsNetworkedCommand(DevConsole console, string command)
    {
        if (CommandsField.GetValue(console) is not Dictionary<string, AbstractConsoleCmd> commands)
        {
            return false;
        }

        return commands.TryGetValue(command.ToLowerInvariant(), out var consoleCommand)
            && consoleCommand.IsNetworked;
    }

    private static string BuildConsoleLine(string command, IReadOnlyList<string> args)
    {
        return args.Count == 0 ? command : command + " " + string.Join(" ", args);
    }

    private static IReadOnlyList<string> OutputLines(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return Array.Empty<string>();
        }

        return output
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .ToArray();
    }
}
