namespace Spirectl.DotnetTools.Inspection;

internal sealed record InspectionCommandRequest(
    string Command,
    string? Subject,
    string Query,
    string? SecondaryQuery,
    string? ContainerPath,
    string? AssembliesDir,
    string? ControlAssembliesDir,
    string? ResourcesDir,
    string? ModsDir,
    string? ExcludeDir,
    bool IncludeMods,
    bool IncludeDependencies,
    bool Full,
    int Limit,
    int Offset,
    string? SourceFilter,
    string? AssemblyFilter,
    IReadOnlyList<string> FormFilters,
    bool HasScript,
    string Sort,
    string? CacheDir,
    MetadataCatalogCacheMode CacheMode,
    bool Json);

internal enum MetadataCatalogCacheMode
{
    Auto,
    Disabled,
    Refresh,
    RequireHit,
}
