using Spirectl.Sts2.Core.Perspective;

namespace Spirectl.Sts2.Embedding;

internal sealed record RefreshOperationState(
    string? ActiveRefreshOperationId,
    DateTimeOffset? ActiveRefreshStartedAtUtc,
    TimeSpan? ActiveRefreshElapsed,
    string? ActiveRefreshProjection,
    TimeSpan? ActiveRefreshTimeout,
    bool ActiveRefreshAbandoned,
    DateTimeOffset? CooldownUntilUtc,
    DateTimeOffset? LastSuccessfulRefreshAtUtc,
    ulong? LastSuccessfulRevision,
    string? LastFailureReason,
    DateTimeOffset? LastFailureAtUtc,
    bool RetryAllowed,
    int ConsecutiveFailures,
    int UnhealthyFailureThreshold);

internal sealed record ProjectionCacheKey(
    string Scope,
    string? PlayerId)
{
    public static ProjectionCacheKey From(PerspectiveSelection? perspective)
        => new(
            perspective?.Scope == PlayerScope.Omniscient ? "omniscient" : "local",
            Normalize(perspective?.PlayerId));

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
