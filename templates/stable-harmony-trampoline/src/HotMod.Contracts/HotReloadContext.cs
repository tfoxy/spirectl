namespace HotMod.Contracts;

public sealed record HotReloadContext(
    int Generation,
    DateTimeOffset LoadedAt,
    string SourceAssemblyPath,
    string ShadowAssemblyPath);
