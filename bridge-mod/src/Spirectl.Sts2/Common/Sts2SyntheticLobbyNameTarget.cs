using System.Reflection;

namespace Spirectl.Sts2;

// Which PlatformUtil lookup the synthetic lobby-name Harmony hook (Sts2SyntheticLobbyNameHooks) patches.
// PURE and Godot-free (like Sts2LobbyHostResolver) so the target selection is offline-unit-testable without the
// live STS2 assembly; the Harmony install that consumes it stays live-host-only.
//
// WHY THE *RAW* SEAM. PlatformUtil exposes two same-shaped lookups: `GetPlayerName`, which BBCode-escapes what it
// returns, and `GetPlayerNameRaw`, which it delegates to and which returns the platform's name unescaped.
//
// The host's lobby NAMEPLATE — the thing that showed a name from a PREVIOUS session when a browser/native client
// joined with a name — is a plain-text label, so it reads the RAW lookup (that is the documented rule for
// plain-text controls), exactly once, when the nameplate node becomes ready. So a hook on `GetPlayerName` never
// reached that call site and the SetClientName override never landed.
//
// Patching the RAW method is also strictly BETTER than patching both:
//   * GetPlayerName DELEGATES to GetPlayerNameRaw, so every BBCode call site inherits the override for free — and
//     inherits it with the .EscapeBbcodeTags() still applied on top of the overridden name.
//   * Patching BOTH would DOUBLE-APPLY the postfix: the outer GetPlayerName postfix would overwrite the
//     already-escaped string with the raw override, stripping the escaping. A display name containing '[' would
//     then reach the BBCode parser unescaped — precisely the failure the escaping exists to prevent, since player
//     names are untrusted external input and may contain square brackets.
// Hence: patch GetPlayerNameRaw INSTEAD OF GetPlayerName, never in addition to it.
internal static class Sts2SyntheticLobbyNameTarget
{
    internal const string PlatformUtilTypeName = "MegaCrit.Sts2.Core.Platform.PlatformUtil";

    // The one and only patched method (see the file header for why it is the raw lookup).
    internal const string TargetMethodName = "GetPlayerNameRaw";

    // The delegating BBCode-escaping wrapper. Deliberately NOT patched: it calls TargetMethodName internally, so it
    // already inherits the override with escaping intact, and patching it too would strip that escaping.
    internal const string DelegatingMethodName = "GetPlayerName";

    // Shape gate: (platformType, ulong playerId) -> string. The postfix reads __args[1] as the netId, so an
    // overload with a different arity/second parameter must never be patched.
    internal static bool IsPatchTarget(MethodInfo method)
    {
        if (!string.Equals(method.Name, TargetMethodName, StringComparison.Ordinal))
        {
            return false;
        }

        var parameters = method.GetParameters();
        return parameters.Length == 2
               && parameters[1].ParameterType == typeof(ulong)
               && method.ReturnType == typeof(string);
    }

    internal static MethodInfo[] SelectPatchTargets(Type platformUtilType)
        => [.. platformUtilType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(IsPatchTarget)];
}
