namespace Spirectl.Sts2.Live;

/// <summary>Pure policy for the scene watcher's authoritative clickable-focus channel.</summary>
internal static class Sts2ClickableFocus
{
    internal const string PropertyName = "IsFocused";

    internal static bool IsCapability(string name, bool isBoolProperty)
        => string.Equals(name, PropertyName, StringComparison.Ordinal) && isBoolProperty;

    // Nullable on purpose: a capable false is authoritative and distinct from an incapable/failed null read.
    internal static bool Changed(bool? previous, bool? current) => previous != current;
}
