namespace Spirectl.Sts2.Live;

// Explicit combat-background layer selection for composed://combat-background/<id>/image renders —
// the grammar + validation behind `AssetExtractRequestSnapshot.CompositionSelector`. PURE and
// Godot-free (like Sts2StreamSkipMeta / Sts2ViewportContentPrune) so the contract is unit-testable
// without the live host; the Godot-typed composed render lane that consumes it
// (Sts2AssetExtractProvider.RenderAssets.ExtractComposedCombatBackgroundSceneAsync) stays in the
// live-host glob.
//
// Background. The composed combat-background render discovers layer variants from
// res://scenes/backgrounds/<id>/layers and injects the FIRST sorted candidate per root placeholder
// ("deterministic-first-sorted"). Live rooms randomize which variant is mounted, so a host that
// wants a pre-rendered background matching the room it is actually showing must be able to name
// the mounted layer set explicitly (it reads the layer sub-scene res:// paths off the live tree).
//
// Contract (embedder-facing, pinned by Sts2CombatBackgroundLayerSelectionTests):
//   * Selector grammar: a comma-separated, ordered list of res:// layer SCENE paths
//     (`res://.../layers/<id>_bg_00_c.tscn,res://.../layers/<id>_fg_a.tscn`). Entries are trimmed,
//     backslashes normalized to '/', empty entries dropped, exact duplicates collapsed.
//   * The filename convention maps each path to its placeholder exactly as deterministic discovery
//     does (`_bg_NN_*` => Layer_NN, `_fg_*` => Foreground) — the selection replaces the DISCOVERY
//     LIST, not the placeholder-matching rules.
//   * ABSENT (null/whitespace selector) => deterministic discovery, byte-identical to today. That
//     is also the caller's fallback when it has not probed the live layer set yet.
//   * A malformed selector (no parseable entries, or an entry that is not a res:// scene path) and
//     a selection the render cannot honor (unrecognized layer filename, no matching placeholder,
//     two variants for one placeholder, unloadable scene) are CLEAN failure results — the caller
//     falls back; never a crash, and never a silent partial composition that would not match the
//     mounted room.
internal static class Sts2CombatBackgroundLayerSelection
{
    // Failure `field` value for selector-related failure details (the embedder-visible signal that
    // the SELECTION, not the scene, was the problem).
    internal const string RequestField = "composition_selector";

    private static readonly string[] SceneExtensions = [".tscn", ".escn", ".scn"];

    // IsExplicit=false + Error=null  => absent selector (deterministic discovery).
    // IsExplicit=true               => LayerPaths is the normalized ordered selection.
    // Error != null                 => malformed selector (clean failure, caller falls back).
    internal sealed record SelectorParseResult(bool IsExplicit, IReadOnlyList<string> LayerPaths, string? Error)
    {
        internal static readonly SelectorParseResult Absent = new(false, [], null);
    }

    internal static SelectorParseResult Parse(string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return SelectorParseResult.Absent;
        }

        var entries = selector
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Replace('\\', '/'))
            .ToList();
        if (entries.Count == 0)
        {
            return new SelectorParseResult(false, [], "The combat background layer selector contains no layer scene paths.");
        }

        var normalized = new List<string>(entries.Count);
        foreach (var entry in entries)
        {
            if (!entry.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
                || !SceneExtensions.Any(extension => entry.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            {
                return new SelectorParseResult(
                    false,
                    [],
                    $"Combat background layer selector entry '{entry}' is not a res:// layer scene path (.tscn/.escn/.scn).");
            }

            if (!normalized.Contains(entry, StringComparer.Ordinal))
            {
                normalized.Add(entry);
            }
        }

        return new SelectorParseResult(true, normalized, null);
    }

    // The requested layer paths the built composition plan did NOT select — non-empty means the
    // render could not honor the explicit selection (unparseable layer filename, placeholder
    // missing from the root scene, or two variants competing for one placeholder) and must fail
    // instead of silently rendering a different layer set than the live room mounted.
    internal static IReadOnlyList<string> FindUnhonoredLayerPaths(
        IReadOnlyList<string> requestedLayerPaths,
        IEnumerable<string?> selectedLayerPaths)
    {
        var selected = selectedLayerPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!.Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
        return requestedLayerPaths
            .Where(path => !selected.Contains(path))
            .ToList();
    }
}
