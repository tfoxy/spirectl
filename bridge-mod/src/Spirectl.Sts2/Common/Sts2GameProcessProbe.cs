namespace Spirectl.Sts2;

/// <summary>
/// Answers one question — "is a real STS2/Godot engine behind this assembly?" — from managed state alone.
/// </summary>
/// <remarks>
/// <para>WHY NOT A PROCESS NAME. The gate this replaces tested the current process's NAME for the substrings
/// "SlayTheSpire" and "Godot". The game's macOS executable is named with spaces, so the test was false on every
/// Mac: the runtime composed placeholder ports and then reported a plausible-looking game state forever, which
/// is far worse than reporting nothing. A name is a false POSITIVE waiting to happen too — any unrelated process
/// named after the game would have composed a LIVE runtime and died on its first native call — so it is not kept
/// as an extra signal either. A process name answers nothing about what is loaded inside the process.</para>
/// <para>WHY NOT ASK THE ENGINE. Outside a Godot process a GodotSharp call does not throw: it terminates the
/// process with a segmentation fault, with no managed exception to catch. So the gate cannot be a trial call, and
/// has to be answerable from managed metadata — which it is, because a real game process is exactly the process
/// that has both the engine's binding assembly and the game's own assembly loaded.</para>
/// </remarks>
internal static class Sts2GameProcessProbe
{
    /// <summary>Simple name of the game's own managed assembly.</summary>
    internal const string GameAssemblySimpleName = "sts2";

    /// <summary>Simple name of the Godot .NET binding assembly an engine host loads.</summary>
    internal const string GodotAssemblySimpleName = "GodotSharp";

    /// <summary>
    /// True only when BOTH <see cref="GameAssemblySimpleName"/> and <see cref="GodotAssemblySimpleName"/> appear
    /// in the given set of ALREADY-LOADED assembly simple names (ordinal, case-insensitive).
    /// </summary>
    /// <remarks>
    /// Requiring both matters: a tool that references the game's types for compilation can have <c>sts2</c>
    /// loaded with no engine anywhere, and an unrelated Godot app has the binding with no game.
    /// </remarks>
    internal static bool IsInsideGameProcess(IEnumerable<string?> loadedAssemblySimpleNames)
    {
        if (loadedAssemblySimpleNames is null)
        {
            return false;
        }

        var sawGame = false;
        var sawGodot = false;
        foreach (var name in loadedAssemblySimpleNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            sawGame = sawGame || string.Equals(name, GameAssemblySimpleName, StringComparison.OrdinalIgnoreCase);
            sawGodot = sawGodot || string.Equals(name, GodotAssemblySimpleName, StringComparison.OrdinalIgnoreCase);
            if (sawGame && sawGodot)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The ambient overload: the same test over the assemblies the current <see cref="AppDomain"/> has already
    /// loaded. Fails closed (returns false) if the domain cannot be enumerated.
    /// </summary>
    /// <remarks>
    /// ALREADY-LOADED ASSEMBLIES ONLY — this must never reach for <c>Type.GetType</c> or <c>Assembly.Load</c>.
    /// Those resolve on demand, so they would return true in ANY process that merely has the DLLs on its probing
    /// path: the bridge's own test host compiles against the reference lanes and has both sitting next to it, and
    /// a load-based gate would quietly declare that host a live game and hand it a live runtime. Loaded-set
    /// membership is the whole point of the check.
    /// </remarks>
    internal static bool IsInsideGameProcess()
    {
        try
        {
            return IsInsideGameProcess(AppDomain.CurrentDomain
                .GetAssemblies()
                .Select(assembly => assembly.GetName().Name));
        }
        catch
        {
            return false;
        }
    }
}
