namespace Spirectl.Sts2.Core.Restore;


public sealed record RestoreFieldReportSnapshot(
    string Path,
    RestoreFieldFidelity Capture,
    RestoreFieldFidelity Restore,
    bool ValidationKey,
    string ReasonCode,
    string Message);

public sealed record RestoreMismatchSnapshot(
    string Path,
    string ExpectedJson,
    string ObservedJson,
    string Severity,
    string SupportClass = "",
    string ReasonCode = "",
    string SuggestedNextStep = "");

public sealed record RestoreVerificationSnapshot(
    RestoreVerificationStatus Status,
    string Quality,
    IReadOnlyList<string> CheckedFields,
    IReadOnlyList<RestoreMismatchSnapshot> Mismatches,
    string ExpectedSummaryJson,
    string ObservedSummaryJson);

public sealed record MultiplayerRestoreSnapshot(
    bool IsMultiplayer,
    MultiplayerRestoreModeSnapshot RestoreMode,
    string LocalPlayerId,
    string HostPlayerId,
    string LocalPlayerRole,
    IReadOnlyList<MultiplayerPlayerSnapshot> Players,
    MultiplayerLobbySnapshot? Lobby,
    bool RequiresRemoteClients,
    bool DegradedLocalOnlyAvailable,
    IReadOnlyList<MultiplayerRestoreLimitationSnapshot> Limitations);

public sealed record MultiplayerPlayerSnapshot(
    string Id,
    string NetId,
    int SlotId,
    string DisplayName,
    string SelectedCharacterId,
    bool IsReady,
    bool IsLocal,
    bool IsHost,
    bool IsRemote,
    string Character);

public sealed record MultiplayerLobbySnapshot(
    string LobbyId,
    string Phase,
    IReadOnlyList<LobbyCharacterSnapshot> AvailableCharacters);

public sealed record LobbyCharacterSnapshot(
    string Id,
    string Name,
    bool IsUnlocked);

public sealed record MultiplayerRestoreResultSnapshot(
    MultiplayerRestoreModeSnapshot Mode,
    string RemotePlayerMode,
    string LocalPlayerId,
    string HostPlayerId,
    IReadOnlyList<string> RestoredPlayerIds,
    IReadOnlyList<string> OmittedRemotePlayerIds,
    bool RequiresRemoteClients);

public sealed record MultiplayerRestoreLimitationSnapshot(
    string Code,
    string Message,
    string Field);

public enum RestoreFieldFidelity
{
    Unspecified,
    Exact,
    Partial,
    Inferred,
    Omitted,
    Unsupported,
    DegradedLocalMultiplayer,
}

public enum RestoreVerificationStatus
{
    Unspecified,
    Passed,
    Partial,
    Degraded,
    Failed,
}

public enum MultiplayerRestoreModeSnapshot
{
    Unspecified,
    LobbyOnly,
    HostLocalActiveRun,
    RemotePlayerPlaceholder,
    FullActiveMultiplayer,
    UnsupportedRemoteClientRequired,
    DegradedLocalOnly,
    ActiveMultiplayerUnsupported,
}
