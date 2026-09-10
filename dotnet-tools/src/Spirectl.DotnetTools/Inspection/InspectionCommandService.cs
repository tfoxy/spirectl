using Spirectl.DotnetTools.Models;

namespace Spirectl.DotnetTools.Inspection;

internal sealed class InspectionCommandService
{
    private readonly MetadataCatalogLoader _metadataCatalogLoader;
    private readonly bool _usesDefaultMetadataCatalogLoader;

    public InspectionCommandService()
        : this(LoadMetadataCatalogFromRequest, usesDefaultMetadataCatalogLoader: true)
    {
    }

    internal InspectionCommandService(MetadataCatalogLoader metadataCatalogLoader)
        : this(metadataCatalogLoader, usesDefaultMetadataCatalogLoader: false)
    {
    }

    private InspectionCommandService(
        MetadataCatalogLoader metadataCatalogLoader,
        bool usesDefaultMetadataCatalogLoader)
    {
        _metadataCatalogLoader = metadataCatalogLoader;
        _usesDefaultMetadataCatalogLoader = usesDefaultMetadataCatalogLoader;
    }

    public InspectionResponse Execute(InspectionCommandRequest request)
    {
        return request.Command switch
        {
            "locate" => LoadMetadataCatalog(request, MetadataCatalogLoadMode.DeclarationsOnly).Locate(request.Subject!, request.Query, request.Limit),
            "describe" => LoadDescribeMetadataCatalog(request).Describe(request.Subject!, request.Query),
            "decompile" => LoadMetadataCatalog(request, MetadataCatalogLoadMode.WithReferences).Decompile(request.Subject!, request.Query, request.Full),
            "refs" => LoadMetadataCatalog(request, MetadataCatalogLoadMode.WithReferences).Refs(request.Subject!, request.Query, request.Limit),
            "derived" => LoadMetadataCatalog(request, MetadataCatalogLoadMode.DeclarationsOnly).Derived(request.Subject!, request.Query, request.Limit),
            "hooks" => LoadHookCatalog(request).List(
                request.Query,
                request.Limit,
                request.Offset,
                request.SourceFilter,
                request.AssemblyFilter,
                request.FormFilters,
                request.HasScript,
                request.Sort),
            "hook-info" => LoadHookCatalog(request).Info(request.Query),
            "scene-search" => LoadSceneCatalog(request).Search(request.Query, request.Limit),
            "scene-tree" => LoadSceneCatalog(request).Tree(request.Query),
            "scene-node" => LoadSceneCatalog(request).Node(
                request.Query,
                request.SecondaryQuery ?? throw new ToolCommandException(
                    2,
                    "usage_error",
                    "scene-node requires a scene query and node path")),
            _ => throw new ToolCommandException(
                2,
                "usage_error",
                $"unknown command '{request.Command}'"),
        };
    }

    private MetadataCatalog LoadMetadataCatalog(InspectionCommandRequest request, MetadataCatalogLoadMode mode)
    {
        return _metadataCatalogLoader(request, mode);
    }

    private MetadataCatalog LoadDescribeMetadataCatalog(InspectionCommandRequest request)
    {
        if (!_usesDefaultMetadataCatalogLoader)
        {
            return LoadMetadataCatalog(request, MetadataCatalogLoadMode.DeclarationsOnly);
        }

        return LoadTargetAwareDescribeMetadataCatalogFromRequest(request);
    }

    private static MetadataCatalog LoadMetadataCatalogFromRequest(InspectionCommandRequest request, MetadataCatalogLoadMode mode)
    {
        var assembliesDir = request.AssembliesDir ?? throw new ToolCommandException(
            2,
            "usage_error",
            "missing required --assemblies-dir option");

        if (!string.IsNullOrWhiteSpace(request.CacheDir) && request.CacheMode != MetadataCatalogCacheMode.Disabled)
        {
            return new MetadataCatalogCache().TryLoadOrBuild(
                request.CacheDir,
                assembliesDir,
                request.ModsDir,
                request.IncludeMods,
                request.IncludeDependencies,
                mode,
                request.CacheMode);
        }

        return MetadataCatalog.Load(
            assembliesDir,
            request.IncludeMods ? request.ModsDir : null,
            mode,
            request.IncludeDependencies);
    }

    private static MetadataCatalog LoadTargetAwareDescribeMetadataCatalogFromRequest(InspectionCommandRequest request)
    {
        var assembliesDir = request.AssembliesDir ?? throw new ToolCommandException(
            2,
            "usage_error",
            "missing required --assemblies-dir option");

        var selectedAssemblies = DescribeTargetResolver.ResolveSelectedAssemblies(
            request.Subject!,
            request.Query,
            assembliesDir,
            request.IncludeMods ? request.ModsDir : null,
            request.IncludeDependencies);

        if (selectedAssemblies is { Count: 1 })
        {
            if (!string.IsNullOrWhiteSpace(request.CacheDir) && request.CacheMode != MetadataCatalogCacheMode.Disabled)
            {
                return new MetadataCatalogCache().TryLoadOrBuild(
                    request.CacheDir,
                    assembliesDir,
                    request.ModsDir,
                    request.IncludeMods,
                    request.IncludeDependencies,
                    MetadataCatalogLoadMode.DeclarationsOnly,
                    request.CacheMode,
                    selectedAssemblies);
            }

            return MetadataCatalog.Load(
                assembliesDir,
                request.IncludeMods ? request.ModsDir : null,
                MetadataCatalogLoadMode.DeclarationsOnly,
                request.IncludeDependencies,
                selectedAssemblies);
        }

        return LoadMetadataCatalogFromRequest(request, MetadataCatalogLoadMode.DeclarationsOnly);
    }

    private static SceneInspectionCatalog LoadSceneCatalog(InspectionCommandRequest request)
    {
        return SceneInspectionCatalog.Load(
            request.ResourcesDir ?? throw new ToolCommandException(
                2,
                "usage_error",
                "missing required --resources-dir option"),
            request.IncludeMods ? request.ModsDir : null,
            request.AssembliesDir);
    }

    private HookIntelligenceCatalog LoadHookCatalog(InspectionCommandRequest request)
    {
        return HookIntelligenceCatalog.Load(
            LoadMetadataCatalog(request, MetadataCatalogLoadMode.WithReferences),
            request.ResourcesDir,
            request.IncludeMods ? request.ModsDir : null);
    }

    internal delegate MetadataCatalog MetadataCatalogLoader(
        InspectionCommandRequest request,
        MetadataCatalogLoadMode mode);
}
