using Spirectl.Sts2.Core.ConsoleCommands;
using Spirectl.Proto.V0;

namespace Spirectl.Sts2.Core.Protocol;

public sealed partial class BridgeRuntimeProtocolAdapter
{
    public ConsoleCommandCatalogResult HandleListConsoleCommands(ConsoleCommandCatalogRequest request)
    {
        var result = _runtime.ListConsoleCommands(request.RequestId);
        if (result.Error is not null)
            return new ConsoleCommandCatalogResult { Error = ToProtoBridgeError(result.Error) };
        var response = new ConsoleCommandCatalogResponse
        {
            RequestId = result.RequestId, Source = ToProtoDataSource(result.Source), Provisional = result.Provisional,
        };
        response.Commands.Add(result.Commands.Select(cmd => new ConsoleCommandDescriptor
        {
            Name = cmd.Name, Usage = cmd.Usage, Description = cmd.Description,
            Networked = cmd.Networked, DebugOnly = cmd.DebugOnly,
        }));
        response.Notices.Add(result.Notices.Select(ToProtoConsoleNotice));
        return new ConsoleCommandCatalogResult { Success = response };
    }

    public ConsoleCompletionResult HandleCompleteConsoleArguments(ConsoleCompletionRequest request)
    {
        var result = _runtime.CompleteConsoleArguments(new ConsoleCompletionRequestSnapshot(
            request.RequestId, request.Command, request.Args.ToArray(), request.Prefix));
        if (result.Error is not null)
            return new ConsoleCompletionResult { Error = ToProtoBridgeError(result.Error) };
        var response = new ConsoleCompletionResponse
        {
            RequestId = result.RequestId, Command = result.Command, KnownCommand = result.KnownCommand,
            ArgumentIndex = (uint)result.ArgumentIndex, CommonPrefix = result.CommonPrefix,
            CompletionType = result.CompletionType, CommandPrefix = result.CommandPrefix,
            Source = ToProtoDataSource(result.Source), Provisional = result.Provisional,
        };
        foreach (var raw in result.Candidates)
        {
            var (value, label) = ConsoleCommandCatalogMapping.SplitCandidate(raw);
            response.Candidates.Add(value);
            response.CandidateDetails.Add(new ConsoleCompletionCandidate { Value = value, Label = label, Raw = raw });
        }
        response.Notices.Add(result.Notices.Select(ToProtoConsoleNotice));
        return new ConsoleCompletionResult { Success = response };
    }

    private static ConsoleCommandNotice ToProtoConsoleNotice(ConsoleCommandNoticeSnapshot notice)
        => new() { Code = notice.Code, Message = notice.Message, Provisional = notice.Provisional };

    private static BridgeError ToProtoBridgeError(ConsoleCommandFailure error)
    {
        var protoError = new BridgeError
        {
            Code = error.Code switch
            {
                ConsoleCommandFailureCode.NotImplemented => BridgeErrorCode.NotImplemented,
                ConsoleCommandFailureCode.InvalidRequest => BridgeErrorCode.InvalidAction,
                ConsoleCommandFailureCode.BridgeNotAttached => BridgeErrorCode.BridgeNotAttached,
                ConsoleCommandFailureCode.RuntimeFailure => BridgeErrorCode.RuntimeFailure,
                _ => BridgeErrorCode.RuntimeFailure,
            },
            Message = error.Message,
        };

        protoError.Details.Add(error.Details.Select(detail => new ErrorDetail
        {
            Field = detail.Field,
            Value = detail.Value,
            Note = detail.Note,
        }));

        return protoError;
    }

}
