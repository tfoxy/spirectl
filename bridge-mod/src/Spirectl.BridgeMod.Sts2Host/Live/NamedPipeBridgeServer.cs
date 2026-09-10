using Google.Protobuf;
using Spirectl.BridgeMod.Services;
using Spirectl.Sts2;
using Spirectl.Sts2.Core.Logging;
using System.IO.Pipes;

namespace Spirectl.BridgeMod.Sts2Host.Live;

public sealed class NamedPipeBridgeServer(BridgeRuntime runtime, HostedBridgeHost hostStatus, string pipeName) : IHostedBridgeServer
{
    private readonly BridgeRuntime _runtime = runtime;
    private readonly HostedBridgeHost _hostStatus = hostStatus;
    private readonly GrpcBridgeService _service = new(runtime);
    private readonly string _pipeName = pipeName;
    private CancellationTokenSource? _lifetime;
    private Task? _acceptLoop;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_lifetime.Token), CancellationToken.None);

        var message = $"Live named-pipe bridge host listening at {_pipeName}.";
        _hostStatus.MarkRunning(message);
        HostedBridgeServerFactory.LogTransportMessage(_runtime, BridgeLogLevel.Info, message);
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime?.Cancel();

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _acceptLoop = null;
        }

        _lifetime?.Dispose();
        _lifetime = null;

        _hostStatus.MarkStopped("Live named-pipe bridge host stopped.");
        HostedBridgeServerFactory.LogTransportMessage(
            _runtime,
            BridgeLogLevel.Info,
            "Live named-pipe bridge host stopped.");
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(() => HandleConnectionAsync(server, cancellationToken), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _hostStatus.MarkStopped($"Live named-pipe bridge host failed: {ex.Message}");
            HostedBridgeServerFactory.LogTransportMessage(
                _runtime,
                BridgeLogLevel.Error,
                $"Live named-pipe bridge host failed: {ex}");
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        await using var stream = server;
        await StandaloneBridgeTransportProtocol.ServeRequestAsync(
            stream,
            DispatchRequest,
            DispatchStreamRequest,
            message => HostedBridgeServerFactory.LogTransportMessage(_runtime, BridgeLogLevel.Warn, message),
            message => HostedBridgeServerFactory.LogTransportMessage(_runtime, BridgeLogLevel.Error, message),
            "named-pipe",
            cancellationToken).ConfigureAwait(false);
    }

    private IMessage DispatchRequest(StandaloneBridgeRequest request)
    {
        var diagnostics = StandaloneBridgeRpcDiagnostics.Start(_runtime, "named-pipe", request);
        try
        {
            if (request.Method == StandaloneBridgeRpcMethod.GetLogs)
            {
                diagnostics.WriteInFlightSnapshots();
            }

            diagnostics.MarkHandlerEntered();
            var response = DispatchRequestCore(request);
            diagnostics.Complete(response);
            return response;
        }
        catch (Exception ex)
        {
            diagnostics.Fail(ex);
            throw;
        }
    }

    private IAsyncEnumerable<IMessage>? DispatchStreamRequest(
        StandaloneBridgeRequest request,
        CancellationToken cancellationToken)
    {
        return request.Method switch
        {
            StandaloneBridgeRpcMethod.WatchState => DispatchWatchStateAsync(request, cancellationToken),
            StandaloneBridgeRpcMethod.WatchCombatEvents => DispatchWatchCombatEventsAsync(request, cancellationToken),
            _ => null,
        };
    }

    private async IAsyncEnumerable<IMessage> DispatchWatchStateAsync(
        StandaloneBridgeRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var diagnostics = StandaloneBridgeRpcDiagnostics.Start(_runtime, "named-pipe", request);
        diagnostics.MarkHandlerEntered();
        await foreach (var evt in _service.HandleWatchState(
            Spirectl.Proto.V0.StateWatchRequest.Parser.ParseFrom(request.Payload),
            cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            diagnostics.Complete(evt);
            yield return evt;
        }
    }

    private async IAsyncEnumerable<IMessage> DispatchWatchCombatEventsAsync(
        StandaloneBridgeRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var diagnostics = StandaloneBridgeRpcDiagnostics.Start(_runtime, "named-pipe", request);
        diagnostics.MarkHandlerEntered();
        await foreach (var evt in _service.HandleWatchCombatEvents(
            Spirectl.Proto.V0.WatchCombatEventsRequest.Parser.ParseFrom(request.Payload),
            cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            diagnostics.Complete(evt);
            yield return evt;
        }
    }

    private IMessage DispatchRequestCore(StandaloneBridgeRequest request)
    {
        return request.Method switch
        {
            StandaloneBridgeRpcMethod.Handshake => _service.HandleHandshake(
                Spirectl.Proto.V0.HandshakeRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.GetState => _service.HandleGetState(
                Spirectl.Proto.V0.StateRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.ExecuteAction => _service.HandleExecuteAction(
                Spirectl.Proto.V0.ActionRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.LoadFixture => _service.HandleLoadFixture(
                Spirectl.Proto.V0.FixtureLoadRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.GetLogs => _service.HandleGetLogs(
                Spirectl.Proto.V0.LogsRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.ExecuteConsoleCommand => _service.HandleExecuteConsoleCommand(
                Spirectl.Proto.V0.ConsoleCommandRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.GetDebugStatus => _service.HandleGetDebugStatus(
                Spirectl.Proto.V0.DebugStatusRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.StartDebugSession => _service.HandleStartDebugSession(
                Spirectl.Proto.V0.DebugSessionStartRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.GetDebugSessionStatus => _service.HandleGetDebugSessionStatus(
                Spirectl.Proto.V0.DebugSessionStatusRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.EndDebugSession => _service.HandleEndDebugSession(
                Spirectl.Proto.V0.DebugSessionEndRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.PauseDebug => _service.HandlePauseDebug(
                Spirectl.Proto.V0.DebugPauseRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.ResumeDebug => _service.HandleResumeDebug(
                Spirectl.Proto.V0.DebugResumeRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.StepDebug => _service.HandleStepDebug(
                Spirectl.Proto.V0.DebugStepRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.WaitDebug => _service.HandleWaitDebug(
                Spirectl.Proto.V0.DebugWaitRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.ListBreakpoints => _service.HandleListBreakpoints(
                Spirectl.Proto.V0.DebugBreakpointListRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.AddBreakpoint => _service.HandleAddBreakpoint(
                Spirectl.Proto.V0.DebugBreakpointAddRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.RemoveBreakpoint => _service.HandleRemoveBreakpoint(
                Spirectl.Proto.V0.DebugBreakpointRemoveRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.GetDebugEvents => _service.HandleGetDebugEvents(
                Spirectl.Proto.V0.DebugEventStreamRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.GetRuntimeSceneTree => _service.HandleGetRuntimeSceneTree(
                Spirectl.Proto.V0.RuntimeSceneTreeRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.GetRuntimeSceneNode => _service.HandleGetRuntimeSceneNode(
                Spirectl.Proto.V0.RuntimeSceneNodeRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.SetRuntimeSceneNodeVisible => _service.HandleSetRuntimeSceneNodeVisible(
                Spirectl.Proto.V0.RuntimeSceneSetVisibleRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.HoverRuntimeSceneControl => _service.HandleHoverRuntimeSceneControl(
                Spirectl.Proto.V0.RuntimeSceneControlHoverRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.UnhoverRuntimeSceneControl => _service.HandleUnhoverRuntimeSceneControl(
                Spirectl.Proto.V0.RuntimeSceneControlUnhoverRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.GetRuntimeTransitionStatus => _service.HandleGetRuntimeTransitionStatus(
                Spirectl.Proto.V0.RuntimeTransitionStatusRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.InspectPresentationResourceScenes => _service.HandleInspectPresentationResourceScenes(
                Spirectl.Proto.V0.PresentationResourceSceneRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.InspectPresentationLocalization => _service.HandleInspectPresentationLocalization(
                Spirectl.Proto.V0.PresentationLocalizationRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.GetScreenshot => _service.HandleGetScreenshot(
                Spirectl.Proto.V0.ScreenshotRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.GetAssetCatalog => _service.HandleGetAssetCatalog(
                Spirectl.Proto.V0.AssetCatalogRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.ExtractAsset => _service.HandleExtractAsset(
                Spirectl.Proto.V0.AssetExtractRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.ExplainAsset => _service.HandleExplainAsset(
                Spirectl.Proto.V0.AssetExplainRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.GetMods => _service.HandleGetMods(
                Spirectl.Proto.V0.ModListRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.GetModels => _service.HandleGetModels(
                Spirectl.Proto.V0.ModelCatalogRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.GetCombatPreview => _service.HandleGetCombatPreview(
                Spirectl.Proto.V0.CombatPreviewRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.GetMapDrawings => _service.HandleGetMapDrawings(
                Spirectl.Proto.V0.MapDrawingsRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.GetReference => _service.HandleGetReference(
                Spirectl.Proto.V0.ReferenceRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.CloseGame => _service.HandleCloseGame(
                Spirectl.Proto.V0.GameCloseRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.CaptureScenario => _service.HandleCaptureScenario(
                Spirectl.Proto.V0.ScenarioCaptureRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.RestoreScenario => _service.HandleRestoreScenario(
                Spirectl.Proto.V0.ScenarioRestoreRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.CaptureCheckpoint => _service.HandleCaptureCheckpoint(
                Spirectl.Proto.V0.CheckpointCaptureRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.RestoreCheckpoint => _service.HandleRestoreCheckpoint(
                Spirectl.Proto.V0.CheckpointRestoreRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.ListCheckpoints => _service.HandleListCheckpoints(
                Spirectl.Proto.V0.CheckpointListRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.DeleteCheckpoint => _service.HandleDeleteCheckpoint(
                Spirectl.Proto.V0.CheckpointDeleteRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.RecordFixture => _service.HandleRecordFixture(
                Spirectl.Proto.V0.RecordedFixtureRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.GetHotReloadStatus => _service.HandleGetHotReloadStatus(
                Spirectl.Proto.V0.HotReloadStatusRequest.Parser.ParseFrom(request.Payload)),
            StandaloneBridgeRpcMethod.RequestHotReload => _service.HandleRequestHotReloadAsync(
                Spirectl.Proto.V0.HotReloadRequest.Parser.ParseFrom(request.Payload)).GetAwaiter().GetResult(),
            _ => throw new InvalidDataException($"Unsupported bridge RPC method '{request.Method}'."),
        };
    }
}
