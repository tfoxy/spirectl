namespace Spirectl.Sts2.Live;

// WS-S LOCALIZED-BODY TEMPLATE PREDICATE — PURE and Godot-free (like Sts2CancelledTweenResync / Sts2ReparentEmit)
// so the scan is unit-testable without the live host.
//
// WHY A CALLER WOULD ASK. A localized body can be a TEMPLATE: `{`-delimited NAMED tokens that the game's text
// formatter substitutes from a per-context variable bag (a card's live damage/block numbers, its pile/targeting
// flags, icon prefixes). That bag only exists on the game's own per-pile description path; a bare table+key
// lookup has none. Asking such a body to format anyway is not a cheap no-op — the OBSERVED behaviour is that the
// game emits a "Localization formatting error" log line AND fires a crash-reporter capture, then returns the raw
// text unchanged. So for a token-bearing body the formatted result IS the raw text, and the call is pure cost:
// one log line plus one capture per body per request, on a path the CLI polls. A caller that only needs "the body
// as authored" tests this predicate first and takes the raw text, which is byte-identical and silent.
//
// THE RULE. True iff the text contains a `{` that is (a) immediately followed by a letter or `_` — i.e. the start
// of a NAMED token, not a positional `{0}`, not a nested `{:...}`, not an escaped `{{` — and (b) not preceded by a
// backslash. Deliberately NO allowlist of token names: the names are open-ended (one per card variable, plus
// keyword/flag/icon helpers), so an allowlist would silently rot as content is added. Deliberately NO brace
// matching either: this answers "would the formatter have to source a name?", which the opening delimiter alone
// decides.
//
// CORPUS CHECK (card bodies across all 14 shipped languages; counts + method in `.sts2/research/`, see AGENTS.md):
// 8,237 bodies, 7,309 carrying a named token, 146 distinct token names, ZERO backslash escapes, and ZERO bodies
// whose only `{` is non-named. On the shipped corpus this predicate is therefore exactly "contains a template
// token"; the escape and positional clauses are defensive, and are what the unit tests pin.
internal static class Sts2LocTemplateText
{
    // True when `rawText` carries at least one named template token the caller cannot source on its own.
    // Null/empty/token-free text is false — those bodies format to themselves and cost nothing to format.
    internal static bool HasUnsourceableSelector(string? rawText)
    {
        if (string.IsNullOrEmpty(rawText))
        {
            return false;
        }

        // Stop one short of the end: a trailing `{` has no name after it.
        for (var index = 0; index < rawText.Length - 1; index++)
        {
            if (rawText[index] != '{')
            {
                continue;
            }

            // Backslash-escaped opening brace: authored as a literal `{`, never sourced. (A doubled `\\{` would be
            // a literal backslash before a REAL token and is read as escaped here; the corpus has no escapes at
            // all, so the simple test stays.)
            if (index > 0 && rawText[index - 1] == '\\')
            {
                continue;
            }

            var next = rawText[index + 1];
            if (char.IsLetter(next) || next == '_')
            {
                return true;
            }
        }

        return false;
    }
}
