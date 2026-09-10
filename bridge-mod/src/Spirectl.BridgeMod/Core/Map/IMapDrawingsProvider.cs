using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Core.Map;


// Read-only snapshot of the player map drawings (NMapDrawings) on the map screen. Drawings are
// deliberately NOT part of state — a run can accumulate unbounded strokes — so they get a
// separate on-demand surface, mirroring the screenshot / combat-preview providers. The live
// implementation reads NMapScreen.Instance.Drawings.GetSerializableMapDrawings() and transforms
// each net-normalized point into TheMap content space via Sts2MapDrawingTransform. Non-live
// runtimes report unavailable.
public interface IMapDrawingsProvider
{
    MapDrawingsOperationResult GetMapDrawings(MapDrawingsRequestSnapshot request)
        => MapDrawingsOperationResult.Failure(
            source: DataSourceKind.Stub,
            code: "map-drawings-unavailable",
            message: "Live map drawings require the live STS2 bridge host.");
}

public sealed record MapDrawingsRequestSnapshot(string? PlayerId = null);

public sealed record MapDrawingsOperationResult(
    DataSourceKind Source,
    bool MapActive,
    IReadOnlyList<MapDrawingStrokeSnapshot> Strokes,
    MapDrawingsFailureSnapshot? Error)
{
    public static MapDrawingsOperationResult Success(
        DataSourceKind source,
        bool mapActive,
        IReadOnlyList<MapDrawingStrokeSnapshot> strokes)
        => new(source, mapActive, strokes, null);

    public static MapDrawingsOperationResult Failure(
        DataSourceKind source,
        string code,
        string message)
        => new(
            source,
            MapActive: false,
            Array.Empty<MapDrawingStrokeSnapshot>(),
            new MapDrawingsFailureSnapshot(code, message));
}

// One drawn stroke (a SerializableMapDrawingLine); its points are already in TheMap content space,
// color is the owner character's recomputed MapDrawingColor ("#RRGGBB"), is_eraser marks erasers.
public sealed record MapDrawingStrokeSnapshot(
    string Id,
    string OwnerPlayerId,
    string ColorHex,
    bool IsEraser,
    IReadOnlyList<MapDrawingPointSnapshot> Points);

public sealed record MapDrawingPointSnapshot(double X, double Y);

public sealed record MapDrawingsFailureSnapshot(string Code, string Message);
