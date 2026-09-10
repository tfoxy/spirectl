using Spirectl.Sts2.Core.Protocol;

namespace Spirectl.Sts2.Core.Mods;


public interface IModInspector
{
    ModListOperationResult ListMods(ModListRequestSnapshot request);
}

public sealed class PlaceholderModInspector : IModInspector
{
    public ModListOperationResult ListMods(ModListRequestSnapshot request)
        => ModListOperationResult.Failure(
            "mods_unavailable",
            "Live mod inspection requires an attached STS2 host bridge.");
}

public sealed record ModListRequestSnapshot(string RequestId);

public sealed record ModListOperationResult(
    IReadOnlyList<LiveModInfoSnapshot> Mods,
    IReadOnlyList<string> Notices,
    ModListFailureSnapshot? Error)
{
    public static ModListOperationResult Success(
        IReadOnlyList<LiveModInfoSnapshot> mods,
        IReadOnlyList<string>? notices = null)
        => new(mods, notices ?? [], Error: null);

    public static ModListOperationResult Failure(string code, string message)
        => new(Mods: [], Notices: [], new ModListFailureSnapshot(code, message));
}

public sealed record LiveModInfoSnapshot(
    string Id,
    string Name,
    string Version,
    string Source,
    string Path,
    LiveModLoadStateSnapshot LoadState,
    bool Enabled,
    bool Active,
    string? AssemblyPath,
    IReadOnlyList<string> Errors);

public enum LiveModLoadStateSnapshot
{
    Unspecified,
    None,
    Loaded,
    Disabled,
    Failed,
    AddedAtRuntime,
    Unknown,
}

public sealed record ModListFailureSnapshot(string Code, string Message);
