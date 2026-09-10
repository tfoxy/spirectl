using System.Buffers.Binary;
using Google.Protobuf;

namespace Spirectl.BridgeMod.Sts2Host;

internal enum StandaloneBridgeRpcMethod : ushort
{
    Handshake = 1,
    // Tag 2 retired: retired GetState. Reserved; do not reassign.
    ExecuteAction = 3,
    LoadFixture = 4,
    GetLogs = 5,
    GetDebugStatus = 6,
    StartDebugSession = 17,
    GetDebugSessionStatus = 18,
    EndDebugSession = 19,
    PauseDebug = 7,
    ResumeDebug = 8,
    StepDebug = 9,
    WaitDebug = 20,
    ListBreakpoints = 10,
    AddBreakpoint = 11,
    RemoveBreakpoint = 12,
    GetRuntimeSceneTree = 13,
    GetRuntimeSceneNode = 14,
    GetScreenshot = 15,
    ExtractAsset = 16,
    GetAssetCatalog = 38,
    HoverRuntimeSceneControl = 39,
    GetMods = 40,
    CloseGame = 21,
    CaptureScenario = 22,
    RestoreScenario = 23,
    CaptureCheckpoint = 24,
    RestoreCheckpoint = 25,
    ListCheckpoints = 26,
    DeleteCheckpoint = 27,
    RecordFixture = 28,
    GetHotReloadStatus = 29,
    RequestHotReload = 30,
    ExplainAsset = 31,
    ExecuteConsoleCommand = 32,
    GetDebugEvents = 33,
    SetRuntimeSceneNodeVisible = 34,
    GetModels = 44,
    // Tag 45 retired: retired WatchState. Reserved; do not reassign.
    GetState = 46,
    InspectPresentationResourceScenes = 47,
    InspectPresentationLocalization = 48,
    GetReference = 49,
    GetRuntimeTransitionStatus = 50,
    WatchState = 51,
    WatchCombatEvents = 52,
    GetCombatPreview = 53,
    GetMapDrawings = 54,
    UnhoverRuntimeSceneControl = 55,
}

internal enum StandaloneBridgeResponseStatus : ushort
{
    Success = 0,
    ProtocolError = 1,
}

internal readonly record struct StandaloneBridgeRequest(StandaloneBridgeRpcMethod Method, byte[] Payload);

internal readonly record struct StandaloneBridgeResponse(
    StandaloneBridgeResponseStatus Status,
    byte[] Payload);

internal static class StandaloneBridgeTransportProtocol
{
    private const uint Magic = 0x4c545053; // "SPTL" in little endian.
    private const ushort Version = 0;
    private const int HeaderLength = 12;
    private const int MaxPayloadBytes = 64 * 1024 * 1024;

    public static async Task<StandaloneBridgeRequest> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = await ReadExactlyAsync(stream, HeaderLength, cancellationToken).ConfigureAwait(false);
        ValidateHeader(header, out var methodValue, out var payloadLength);
        if (!Enum.IsDefined(typeof(StandaloneBridgeRpcMethod), methodValue))
        {
            throw new InvalidDataException($"Unsupported bridge RPC method '{methodValue}'.");
        }

        var payload = await ReadExactlyAsync(stream, payloadLength, cancellationToken).ConfigureAwait(false);
        return new StandaloneBridgeRequest((StandaloneBridgeRpcMethod)methodValue, payload);
    }

    public static async Task<StandaloneBridgeResponse> ReadResponseAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header = await ReadExactlyAsync(stream, HeaderLength, cancellationToken).ConfigureAwait(false);
        ValidateHeader(header, out var statusValue, out var payloadLength);
        if (!Enum.IsDefined(typeof(StandaloneBridgeResponseStatus), statusValue))
        {
            throw new InvalidDataException($"Unsupported bridge RPC response status '{statusValue}'.");
        }

        var payload = await ReadExactlyAsync(stream, payloadLength, cancellationToken).ConfigureAwait(false);
        return new StandaloneBridgeResponse((StandaloneBridgeResponseStatus)statusValue, payload);
    }

    public static Task WriteRequestAsync(
        Stream stream,
        StandaloneBridgeRpcMethod method,
        IMessage payload,
        CancellationToken cancellationToken)
    {
        return WriteFrameAsync(stream, (ushort)method, payload.ToByteArray(), cancellationToken);
    }

    public static Task WriteSuccessAsync(
        Stream stream,
        IMessage payload,
        CancellationToken cancellationToken)
    {
        return WriteFrameAsync(
            stream,
            (ushort)StandaloneBridgeResponseStatus.Success,
            payload.ToByteArray(),
            cancellationToken);
    }

    public static Task WriteProtocolErrorAsync(
        Stream stream,
        string message,
        CancellationToken cancellationToken)
    {
        return WriteFrameAsync(
            stream,
            (ushort)StandaloneBridgeResponseStatus.ProtocolError,
            System.Text.Encoding.UTF8.GetBytes(message),
            cancellationToken);
    }

    public static string DecodeProtocolError(byte[] payload)
    {
        return System.Text.Encoding.UTF8.GetString(payload);
    }

    public static async Task ServeSingleRequestAsync(
        Stream stream,
        Func<StandaloneBridgeRequest, IMessage> dispatch,
        Action<string> logWarning,
        Action<string> logError,
        string transportLabel,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
            var response = dispatch(request);
            await WriteSuccessAsync(stream, response, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            logWarning($"Rejected invalid {transportLabel} bridge request: {ex.Message}");
            try
            {
                await WriteProtocolErrorAsync(stream, ex.Message, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }
        catch (IOException)
        {
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logError($"Live {transportLabel} bridge request failed: {ex}");
            try
            {
                await WriteProtocolErrorAsync(
                    stream,
                    $"Live {transportLabel} bridge host failed while processing the request.",
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    public static async Task ServeRequestAsync(
        Stream stream,
        Func<StandaloneBridgeRequest, IMessage> dispatch,
        Func<StandaloneBridgeRequest, CancellationToken, IAsyncEnumerable<IMessage>?> dispatchStream,
        Action<string> logWarning,
        Action<string> logError,
        string transportLabel,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
            var streamResponses = dispatchStream(request, cancellationToken);
            if (streamResponses is not null)
            {
                await foreach (var response in streamResponses.WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    await WriteSuccessAsync(stream, response, cancellationToken).ConfigureAwait(false);
                }
                return;
            }

            var singleResponse = dispatch(request);
            await WriteSuccessAsync(stream, singleResponse, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            logWarning($"Rejected invalid {transportLabel} bridge request: {ex.Message}");
            try
            {
                await WriteProtocolErrorAsync(stream, ex.Message, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }
        catch (IOException)
        {
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logError($"Live {transportLabel} bridge request failed: {ex}");
            try
            {
                await WriteProtocolErrorAsync(
                    stream,
                    $"Live {transportLabel} bridge host failed while processing the request.",
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private static async Task<byte[]> ReadExactlyAsync(
        Stream stream,
        int length,
        CancellationToken cancellationToken)
    {
        if (length == 0)
        {
            return [];
        }

        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer;
    }

    private static void ValidateHeader(ReadOnlySpan<byte> header, out ushort tag, out int payloadLength)
    {
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (magic != Magic)
        {
            throw new InvalidDataException("Bridge IPC frame did not start with the expected magic header.");
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        if (version != Version)
        {
            throw new InvalidDataException($"Bridge IPC frame version '{version}' is not supported.");
        }

        tag = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        payloadLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header[8..]));
        if (payloadLength < 0 || payloadLength > MaxPayloadBytes)
        {
            throw new InvalidDataException(
                $"Bridge IPC payload length '{payloadLength}' exceeds the supported limit.");
        }
    }

    private static async Task WriteFrameAsync(
        Stream stream,
        ushort tag,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        if (payload.Length > MaxPayloadBytes)
        {
            throw new InvalidDataException(
                $"Bridge IPC payload length '{payload.Length}' exceeds the supported limit.");
        }

        var header = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), tag);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), checked((uint)payload.Length));

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
