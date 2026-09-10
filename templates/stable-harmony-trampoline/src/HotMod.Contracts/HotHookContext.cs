namespace HotMod.Contracts;

public sealed record HotHookContext(
    string HookId,
    int Generation,
    IReadOnlyDictionary<string, string> Values);
