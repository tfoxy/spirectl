using Godot;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Runs;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Map;
using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Live;


/// <summary>
/// Live player map-drawings provider. On the open map screen it reads
/// <c>NMapScreen.Instance.Drawings.GetSerializableMapDrawings()</c> (the same serializable lines
/// the game network-syncs), resolves each owner's <c>MapDrawingColor</c> (recomputed per
/// character, never serialized in the save) from the run state, and transforms every
/// net-normalized point into TheMap content space via <see cref="Sts2MapDrawingTransform"/> using
/// the LIVE <c>NMapDrawings.Size</c>. Off the map screen it reports <c>MapActive=false</c> with no
/// strokes (not an error). Runs on the game main thread (BridgeRuntime wraps the call in
/// MainThreadInvoker).
/// </summary>
internal sealed class Sts2MapDrawingsProvider(ILogStream logStream) : IMapDrawingsProvider
{
    private readonly ILogStream _logStream = logStream;

    public MapDrawingsOperationResult GetMapDrawings(MapDrawingsRequestSnapshot request)
    {
        var mapScreen = NMapScreen.Instance;
        if (mapScreen is null || !mapScreen.IsOpen)
        {
            // Not an error: drawings are simply unavailable when the map screen is closed.
            return MapDrawingsOperationResult.Success(
                DataSourceKind.Live, mapActive: false, Array.Empty<MapDrawingStrokeSnapshot>());
        }

        var drawings = mapScreen.Drawings;
        if (drawings is null)
        {
            return MapDrawingsOperationResult.Success(
                DataSourceKind.Live, mapActive: true, Array.Empty<MapDrawingStrokeSnapshot>());
        }

        // The live NMapDrawings control size drives the net<->content transform each call.
        var size = drawings.Size;
        var runState = RunManager.Instance?.DebugOnlyGetState();
        var colorByPlayerId = BuildColorMap(runState);

        var serialized = drawings.GetSerializableMapDrawings();
        var strokes = new List<MapDrawingStrokeSnapshot>();
        var strokeIndex = 0;
        foreach (var playerDrawings in serialized.drawings)
        {
            var ownerPlayerId = $"p:{playerDrawings.playerId}";
            var colorHex = colorByPlayerId.TryGetValue(playerDrawings.playerId, out var hex)
                ? hex
                : "#000000";
            foreach (var line in playerDrawings.lines)
            {
                var points = new List<MapDrawingPointSnapshot>(line.mapPoints.Count);
                foreach (var net in line.mapPoints)
                {
                    var (contentX, contentY) =
                        Sts2MapDrawingTransform.NetToContent(net.X, net.Y, size.X, size.Y);
                    points.Add(new MapDrawingPointSnapshot(contentX, contentY));
                }

                strokes.Add(new MapDrawingStrokeSnapshot(
                    Id: $"draw:{ownerPlayerId}:{strokeIndex++}",
                    OwnerPlayerId: ownerPlayerId,
                    ColorHex: colorHex,
                    IsEraser: line.isEraser,
                    Points: points));
            }
        }

        _logStream.Write(
            BridgeLogLevel.Debug,
            "bridge.map.drawings",
            $"map-drawings: players={serialized.drawings.Count} strokes={strokes.Count} size={size.X}x{size.Y}");

        return MapDrawingsOperationResult.Success(DataSourceKind.Live, mapActive: true, strokes);
    }

    // Owner color is the character's MapDrawingColor (recomputed, never serialized). Keyed by the
    // player's ulong NetId — the same value SerializablePlayerMapDrawings.playerId carries.
    private static Dictionary<ulong, string> BuildColorMap(RunState? runState)
    {
        var map = new Dictionary<ulong, string>();
        if (runState is null)
        {
            return map;
        }

        foreach (var player in runState.Players)
        {
            var color = player.Character?.MapDrawingColor ?? Colors.Black;
            map[player.NetId] = "#" + color.ToHtml(false);
        }

        return map;
    }
}
