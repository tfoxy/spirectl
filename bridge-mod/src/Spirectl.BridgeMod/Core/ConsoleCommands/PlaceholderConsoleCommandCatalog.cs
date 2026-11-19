using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Core.ConsoleCommands;

public sealed class PlaceholderConsoleCommandCatalog : IConsoleCommandCatalog
{
    private static ConsoleCommandFailure Unavailable() => new(
        ConsoleCommandFailureCode.NotImplemented,
        "Console command discovery requires the live STS2 bridge host.", []);

    public ConsoleCommandCatalogSnapshot List(string requestId)
        => new(requestId, [], DataSourceKind.Stub, true, [], Unavailable());

    public ConsoleCompletionSnapshot Complete(ConsoleCompletionRequestSnapshot request)
        => new(request.RequestId, request.Command, false, request.Args.Count, [], "", "argument", "",
            DataSourceKind.Stub, true, [], Unavailable());
}
