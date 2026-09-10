using Spirectl.DotnetTools.Models;

namespace Spirectl.DotnetTools.Inspection;

internal sealed class SceneInspectionCatalog(SceneInspectionIndex index)
{
    public static SceneInspectionCatalog Load(string resourcesDir, string? modsDir, string? assembliesDir)
        => new(SceneCatalogLoader.Load(resourcesDir, modsDir, assembliesDir));

    public InspectionResponse Search(string query, int limit) => index.Search(query, limit);

    public InspectionResponse Tree(string sceneQuery) => index.Tree(sceneQuery);

    public InspectionResponse Node(string sceneQuery, string nodePathQuery) => index.Node(sceneQuery, nodePathQuery);
}
