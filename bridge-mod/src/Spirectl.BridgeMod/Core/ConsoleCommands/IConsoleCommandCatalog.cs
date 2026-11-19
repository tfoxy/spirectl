using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Core.ConsoleCommands;

public interface IConsoleCommandCatalog
{
    ConsoleCommandCatalogSnapshot List(string requestId);
    ConsoleCompletionSnapshot Complete(ConsoleCompletionRequestSnapshot request);
}

public sealed record ConsoleCommandDescriptorSnapshot(
    string Name, string Usage, string Description, bool Networked, bool DebugOnly);

public sealed record ConsoleCommandCatalogSnapshot(
    string RequestId,
    IReadOnlyList<ConsoleCommandDescriptorSnapshot> Commands,
    DataSourceKind Source,
    bool Provisional,
    IReadOnlyList<ConsoleCommandNoticeSnapshot> Notices,
    ConsoleCommandFailure? Error = null);

public sealed record ConsoleCompletionRequestSnapshot(
    string RequestId, string Command, IReadOnlyList<string> Args, string Prefix);

public sealed record ConsoleCompletionSnapshot(
    string RequestId,
    string Command,
    bool KnownCommand,
    int ArgumentIndex,
    IReadOnlyList<string> Candidates,
    string CommonPrefix,
    string CompletionType,
    string CommandPrefix,
    DataSourceKind Source,
    bool Provisional,
    IReadOnlyList<ConsoleCommandNoticeSnapshot> Notices,
    ConsoleCommandFailure? Error = null);
