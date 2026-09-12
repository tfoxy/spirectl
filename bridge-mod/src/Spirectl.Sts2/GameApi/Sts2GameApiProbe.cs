using System.Reflection;
using Spirectl.Sts2.Core.Logging;

namespace Spirectl.Sts2.Live.GameApi;

/// <summary>What kind of member a <see cref="GameApiRequirement"/> asks for.</summary>
internal enum GameApiMemberKind
{
    /// <summary>A property or a field, public or not, declared on the type or one of its bases.</summary>
    Value,

    /// <summary>A method with an exact parameter list, public or not.</summary>
    Method,

    /// <summary>An instance constructor with an exact parameter list.</summary>
    Constructor,
}

/// <summary>
/// One game member the bridge depends on and the compiler cannot check for us — because the bridge reaches
/// it by name, or because a changed parameter list would silently turn an invocation into a no-op instead of
/// a build error.
/// </summary>
/// <param name="Owner">The declaring game type. Strongly typed, so a type rename is a build error.</param>
/// <param name="Member">The member name as the current lane expects it.</param>
/// <param name="Kind">Property/field, or method.</param>
/// <param name="ParameterTypes">For <see cref="GameApiMemberKind.Method"/>, the exact parameter list.</param>
/// <param name="Note">What breaks when this member is missing. Shown in the refusal.</param>
/// <param name="ParameterTypeNames">
/// An alternative to <paramref name="ParameterTypes"/> for a method whose parameter types are internal to the
/// game assembly and therefore cannot be named here: the parameter type <em>simple names</em>, in order.
/// </param>
internal sealed record GameApiRequirement(
    Type Owner,
    string Member,
    GameApiMemberKind Kind,
    Type[]? ParameterTypes = null,
    string? Note = null,
    string[]? ParameterTypeNames = null)
{
    internal string Describe()
    {
        var parameters = ParameterTypeNames
            ?? (ParameterTypes ?? []).Select(type => type.Name).ToArray();
        var signature = Kind is GameApiMemberKind.Method or GameApiMemberKind.Constructor
            ? $"{Owner.FullName}.{Member}({string.Join(", ", parameters)})"
            : $"{Owner.FullName}.{Member}";
        return Note is null ? signature : $"{signature} — {Note}";
    }
}

/// <summary>
/// The startup gate on the game API the live bridge binds to by name.
///
/// <para>
/// Nothing else in the bridge fails loudly when a game member moves. The by-name reader returns null and the
/// state projection turns that into <c>0</c> / <c>false</c> / <c>null</c>; the by-name invoker matches on an
/// exact parameter count, so one added game parameter turns an invocation into a silently ignored
/// <see langword="false"/>. Both produce a bridge that starts, runs, and reports plausible wrong numbers.
/// </para>
///
/// <para>
/// So every lane-sensitive member is declared once, in its lane's <c>GameApiManifest</c>, and resolved
/// against the loaded game assembly before the live composition is built. A single miss refuses the whole
/// bridge, naming the member, the lane, and the game build.
/// </para>
/// </summary>
internal static class Sts2GameApiProbe
{
    private const BindingFlags MemberFlags =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private static readonly object Sync = new();
    private static bool _ran;
    private static Exception? _failure;

    /// <summary>
    /// Resolve the current lane's manifest once per process. Throws (and keeps throwing the same refusal on
    /// every later call) when any member is missing.
    /// </summary>
    internal static void EnsureCompatible(ILogStream? logStream = null)
    {
        lock (Sync)
        {
            if (_ran)
            {
                if (_failure is not null)
                {
                    throw _failure;
                }

                return;
            }

            _ran = true;
            var requirements = GameApiManifest.Requirements;
            var missing = FindMissing(requirements);
            var gameVersion = DescribeGameVersion();
            if (missing.Count > 0)
            {
                _failure = new InvalidOperationException(BuildRefusal(missing, gameVersion));
                throw _failure;
            }

            logStream?.Write(
                BridgeLogLevel.Info,
                "bridge.game-api",
                $"Game API lane {GameApiLane.Name} verified against game build {gameVersion}: "
                    + $"{requirements.Count} members resolved.");
        }
    }

    /// <summary>Every requirement that does not resolve, rendered for a human. Empty means compatible.</summary>
    internal static IReadOnlyList<string> FindMissing(IEnumerable<GameApiRequirement> requirements)
    {
        ArgumentNullException.ThrowIfNull(requirements);

        var missing = new List<string>();
        foreach (var requirement in requirements)
        {
            if (!Resolves(requirement))
            {
                missing.Add(requirement.Describe());
            }
        }

        return missing;
    }

    private static bool Resolves(GameApiRequirement requirement)
    {
        if (requirement.Kind == GameApiMemberKind.Constructor)
        {
            return requirement.Owner.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: requirement.ParameterTypes ?? [],
                modifiers: null) is not null;
        }

        for (var type = requirement.Owner; type is not null; type = type.BaseType)
        {
            if (requirement.Kind == GameApiMemberKind.Method)
            {
                if (requirement.ParameterTypeNames is { } names)
                {
                    if (type.GetMethods(MemberFlags).Any(method =>
                            string.Equals(method.Name, requirement.Member, StringComparison.Ordinal)
                            && method.GetParameters().Select(parameter => parameter.ParameterType.Name)
                                .SequenceEqual(names, StringComparer.Ordinal)))
                    {
                        return true;
                    }

                    continue;
                }

                if (type.GetMethod(
                        requirement.Member,
                        MemberFlags,
                        binder: null,
                        types: requirement.ParameterTypes ?? [],
                        modifiers: null) is not null)
                {
                    return true;
                }

                continue;
            }

            if (type.GetProperty(requirement.Member, MemberFlags) is not null
                || type.GetField(requirement.Member, MemberFlags) is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The refusal text for a set of missing members. Exposed so its shape is testable.</summary>
    internal static string BuildRefusal(IReadOnlyList<string> missing, string gameVersion)
    {
        var lines = string.Join(Environment.NewLine, missing.Select(entry => $"  - {entry}"));
        return $"The spirectl live bridge refuses to start: {missing.Count} game member(s) the '{GameApiLane.Name}' "
            + $"API lane depends on are missing from the loaded game assembly (build {gameVersion})."
            + Environment.NewLine
            + lines
            + Environment.NewLine
            + "Either this game build moved under the lane it is mapped to, or the wrong lane was compiled. "
            + "The version-to-lane table is bridge-mod/Sts2GameApi.props; the members are declared in "
            + $"bridge-mod/src/Spirectl.Sts2/GameApi/{GameApiLane.Name.ToUpperInvariant()}/GameApiManifest.cs.";
    }

    // The game's own release record, reached the same way the reference-data provider reaches it. Only
    // meaningful inside a running game, so failures degrade to "<unknown>" rather than masking the refusal.
    private static string DescribeGameVersion()
    {
        try
        {
            var version = Sts2ReferenceDataProvider.ResolveReleaseInfo() is { } release
                ? Sts2LiveIntrospection.GetMemberValue(release, "Version") as string
                : null;
            return string.IsNullOrWhiteSpace(version) ? "<unknown>" : version;
        }
        catch
        {
            return "<unknown>";
        }
    }
}
