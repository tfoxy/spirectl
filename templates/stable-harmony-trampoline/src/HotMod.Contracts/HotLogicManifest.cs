namespace HotMod.Contracts;

public sealed record HotLogicManifest(
    string ModId,
    string DisplayName,
    string LogicVersion,
    int ContractVersion,
    IReadOnlyDictionary<string, string> Capabilities);
