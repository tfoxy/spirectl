namespace HotMod.Contracts;

public sealed record HotLogicResult(
    bool Handled,
    IReadOnlyList<string> Messages);
