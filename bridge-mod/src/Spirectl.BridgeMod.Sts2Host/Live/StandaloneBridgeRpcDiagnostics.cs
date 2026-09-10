using System.Collections.Concurrent;
using System.Diagnostics;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Spirectl.Sts2;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Live;
using Spirectl.Proto.V0;

namespace Spirectl.BridgeMod.Sts2Host.Live;

internal sealed class StandaloneBridgeRpcDiagnostics
{
    private static readonly ConcurrentDictionary<long, ActiveRequestSnapshot> ActiveRequests = new();
    private static long _nextSequence;

    private readonly BridgeRuntime _runtime;
    private readonly long _sequence;
    private readonly Stopwatch _stopwatch;
    private readonly string _transport;
    private readonly string _method;
    private readonly string _requestId;
    private string _activePhase;

    private StandaloneBridgeRpcDiagnostics(
        BridgeRuntime runtime,
        long sequence,
        string transport,
        string method,
        string requestId,
        int payloadBytes)
    {
        _runtime = runtime;
        _sequence = sequence;
        _transport = transport;
        _method = method;
        _requestId = string.IsNullOrWhiteSpace(requestId) ? "<none>" : requestId;
        _activePhase = "received";
        _stopwatch = Stopwatch.StartNew();

        var dispatcher = Sts2MainThreadDispatcher.DescribeStatus();
        ActiveRequests[_sequence] = ActiveSnapshot(dispatcher);
        Write(
            BridgeLogLevel.Debug,
            $"phase=start transport={_transport} method={_method} requestId={_requestId} payloadBytes={payloadBytes} {FormatDispatcherStatus(dispatcher)}");
    }

    public static StandaloneBridgeRpcDiagnostics Start(
        BridgeRuntime runtime,
        string transport,
        StandaloneBridgeRequest request)
    {
        var sequence = Interlocked.Increment(ref _nextSequence);
        return new StandaloneBridgeRpcDiagnostics(
            runtime,
            sequence,
            transport,
            FormatMethodName(request.Method),
            ResolveRequestId(request),
            request.Payload.Length);
    }

    public void MarkHandlerEntered()
    {
        _activePhase = "handler";
        ActiveRequests[_sequence] = ActiveSnapshot(Sts2MainThreadDispatcher.DescribeStatus());
    }

    public void Complete(IMessage response)
    {
        ActiveRequests.TryRemove(_sequence, out _);
        Write(
            BridgeLogLevel.Debug,
            $"phase=complete transport={_transport} method={_method} requestId={_requestId} elapsedMs={ElapsedMilliseconds()} response={response.GetType().Name}");
    }

    public void Fail(Exception exception)
    {
        ActiveRequests.TryRemove(_sequence, out _);
        Write(
            BridgeLogLevel.Error,
            $"phase=failed transport={_transport} method={_method} requestId={_requestId} elapsedMs={ElapsedMilliseconds()} exception={exception.GetType().Name}: {exception.Message}");
    }

    public void WriteInFlightSnapshots()
    {
        foreach (var active in ActiveRequests.Values.OrderBy(active => active.Sequence))
        {
            if (active.Sequence == _sequence)
            {
                continue;
            }

            Write(
                BridgeLogLevel.Debug,
                $"phase=in-flight transport={active.Transport} method={active.Method} requestId={active.RequestId} elapsedMs={active.ElapsedMilliseconds()} activePhase={active.ActivePhase} {FormatDispatcherStatus(active.DispatcherStatus)}");
        }
    }

    private ActiveRequestSnapshot ActiveSnapshot(MainThreadDispatcherStatus dispatcher)
    {
        return new ActiveRequestSnapshot(
            _sequence,
            _transport,
            _method,
            _requestId,
            _activePhase,
            _stopwatch,
            dispatcher);
    }

    private long ElapsedMilliseconds()
        => Math.Max(0, _stopwatch.ElapsedMilliseconds);

    private void Write(BridgeLogLevel level, string message)
    {
        try
        {
            _runtime.LogStream.Write(level, "bridge.rpc", message);
        }
        catch
        {
            // Diagnostics must never make an otherwise valid bridge RPC fail.
        }
    }

    private static string ResolveRequestId(StandaloneBridgeRequest request)
    {
        try
        {
            var message = ParseRequest(request);
            var field = message?.Descriptor.FindFieldByName("request_id");
            if (field is null)
            {
                return string.Empty;
            }

            return field.Accessor.GetValue(message) as string ?? string.Empty;
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or InvalidDataException)
        {
            return "<unparseable>";
        }
    }

    private static IMessage? ParseRequest(StandaloneBridgeRequest request)
    {
        return request.Method switch
        {
            StandaloneBridgeRpcMethod.Handshake => HandshakeRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.GetState => StateRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.ExecuteAction => ActionRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.LoadFixture => FixtureLoadRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.GetLogs => LogsRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.GetDebugStatus => DebugStatusRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.StartDebugSession => DebugSessionStartRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.GetDebugSessionStatus => DebugSessionStatusRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.EndDebugSession => DebugSessionEndRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.PauseDebug => DebugPauseRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.ResumeDebug => DebugResumeRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.StepDebug => DebugStepRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.WaitDebug => DebugWaitRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.ListBreakpoints => DebugBreakpointListRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.AddBreakpoint => DebugBreakpointAddRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.RemoveBreakpoint => DebugBreakpointRemoveRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.GetDebugEvents => DebugEventStreamRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.GetRuntimeSceneTree => RuntimeSceneTreeRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.GetRuntimeSceneNode => RuntimeSceneNodeRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.HoverRuntimeSceneControl => RuntimeSceneControlHoverRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.GetRuntimeTransitionStatus => RuntimeTransitionStatusRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.InspectPresentationResourceScenes => PresentationResourceSceneRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.InspectPresentationLocalization => PresentationLocalizationRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.GetScreenshot => ScreenshotRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.GetAssetCatalog => AssetCatalogRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.ExtractAsset => AssetExtractRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.ExplainAsset => AssetExplainRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.GetMods => ModListRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.GetModels => ModelCatalogRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.GetCombatPreview => CombatPreviewRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.GetMapDrawings => MapDrawingsRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.GetReference => ReferenceRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.CloseGame => GameCloseRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.CaptureScenario => ScenarioCaptureRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.RestoreScenario => ScenarioRestoreRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.CaptureCheckpoint => CheckpointCaptureRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.RestoreCheckpoint => CheckpointRestoreRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.ListCheckpoints => CheckpointListRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.DeleteCheckpoint => CheckpointDeleteRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.RecordFixture => RecordedFixtureRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.GetHotReloadStatus => HotReloadStatusRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.RequestHotReload => HotReloadRequest.Parser.ParseFrom(request.Payload),
            StandaloneBridgeRpcMethod.ExecuteConsoleCommand => ConsoleCommandRequest.Parser.ParseFrom(request.Payload),
            _ => null,
        };
    }

    private static string FormatDispatcherStatus(MainThreadDispatcherStatus status)
    {
        return "mainThreadCaptured="
            + status.HasCapturedContext.ToString().ToLowerInvariant()
            + " capturedThreadId="
            + (status.CapturedThreadId?.ToString() ?? "<none>")
            + " currentThreadId="
            + status.CurrentThreadId
            + " onCapturedThread="
            + status.IsOnCapturedThread.ToString().ToLowerInvariant();
    }

    private static string FormatMethodName(StandaloneBridgeRpcMethod method)
    {
        var text = method.ToString();
        var builder = new System.Text.StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (char.IsUpper(character) && i > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private sealed record ActiveRequestSnapshot(
        long Sequence,
        string Transport,
        string Method,
        string RequestId,
        string ActivePhase,
        Stopwatch Stopwatch,
        MainThreadDispatcherStatus DispatcherStatus)
    {
        public long ElapsedMilliseconds()
            => Math.Max(0, Stopwatch.ElapsedMilliseconds);
    }
}
