using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2;

public static class Sts2UnsupportedScreenNotice
{
    public static StateNoticeSnapshot Create(
        string? screenType,
        string? screenTitle,
        string? source,
        string? screenClassName)
    {
        var normalizedScreenType = string.IsNullOrWhiteSpace(screenType) ? "unknown" : screenType.Trim();
        var normalizedScreenTitle = string.IsNullOrWhiteSpace(screenTitle) ? normalizedScreenType : screenTitle.Trim();
        var normalizedSource = string.IsNullOrWhiteSpace(source) ? "unknown" : source.Trim();
        var normalizedScreenClassName = string.IsNullOrWhiteSpace(screenClassName) ? "unknown" : screenClassName.Trim();

        return normalizedScreenType == "unknown"
            ? new StateNoticeSnapshot(
                "screen-unsupported",
                $"Live extraction is not implemented yet for an unknown screen (source '{normalizedSource}', class '{normalizedScreenClassName}').",
                true,
                Path: "screen",
                Severity: "unsupported",
                Source: nameof(Sts2UnsupportedScreenNotice))
            : new StateNoticeSnapshot(
                $"{normalizedScreenType}-unsupported",
                $"Live extraction is not implemented yet for screen id '{normalizedScreenType}' ('{normalizedScreenTitle}') from source '{normalizedSource}' class '{normalizedScreenClassName}'.",
                true,
                Path: "screen",
                Severity: "unsupported",
                Source: nameof(Sts2UnsupportedScreenNotice));
    }
}
