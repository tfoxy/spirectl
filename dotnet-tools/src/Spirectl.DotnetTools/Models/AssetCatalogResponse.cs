namespace Spirectl.DotnetTools.Models;

public sealed record AssetCatalogResponse(
    string Command,
    string Status,
    string Query,
    IReadOnlyList<InspectionSearchRoot> SearchRoots,
    IReadOnlyList<string> Notes,
    int MatchCount,
    IReadOnlyList<AssetCatalogMatch> Matches);

public sealed record AssetCatalogMatch(
    string LogicalPath,
    string SourcePath,
    string SourceRoot,
    string AssetKind,
    string StorageKind,
    string LoadPath,
    bool ReadableOffline,
    string? ContainerPath = null,
    string? ResourceType = null);

public sealed record AssetReadResponse(
    string Command,
    string Status,
    string LogicalPath,
    string StorageKind,
    string? ContainerPath,
    string ContentsBase64,
    IReadOnlyList<string> Notes);
