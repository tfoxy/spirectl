using System.Reflection;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Protocol;

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
/// <param name="Owner">
/// The declaring game type. Strongly typed, so a type rename is a build error. <see langword="null"/> only
/// when the owner is internal to the game assembly and must ride <paramref name="OwnerTypeName"/> instead.
/// </param>
/// <param name="Member">The member name as the current lane expects it.</param>
/// <param name="Kind">Property/field, or method.</param>
/// <param name="ParameterTypes">For <see cref="GameApiMemberKind.Method"/>, the exact parameter list.</param>
/// <param name="Note">What breaks when this member is missing. Shown in the compatibility report.</param>
/// <param name="ParameterTypeNames">
/// An alternative to <paramref name="ParameterTypes"/> for a method whose parameter types are internal to the
/// game assembly and therefore cannot be named here: the parameter type <em>simple names</em>, in order.
/// </param>
/// <param name="OwnerTypeName">
/// An alternative to <paramref name="Owner"/> for the same reason: the declaring type's simple or full name,
/// resolved out of the loaded game assembly. Pass exactly one of the two — a requirement with neither
/// resolves nothing and reports itself missing.
/// </param>
/// <param name="Critical">Whether a miss must refuse startup to avoid unsafe behavior. Defaults to false.</param>
internal sealed record GameApiRequirement(
    Type? Owner,
    string Member,
    GameApiMemberKind Kind,
    Type[]? ParameterTypes = null,
    string? Note = null,
    string[]? ParameterTypeNames = null,
    string? OwnerTypeName = null,
    bool Critical = false)
{
    internal string Describe(bool includeNote = true)
    {
        string[] parameters = ParameterTypeNames
            ?? [.. (ParameterTypes ?? []).Select(type => type.Name)];
        var owner = Owner?.FullName ?? OwnerTypeName ?? "<no declaring type>";
        var signature = Kind is GameApiMemberKind.Method or GameApiMemberKind.Constructor
            ? $"{owner}.{Member}({string.Join(", ", parameters)})"
            : $"{owner}.{Member}";
        return !includeNote || Note is null ? signature : $"{signature} — {Note}";
    }
}

/// <summary>Game-independent requirement resolution, shared by startup and offline regression tests.</summary>
internal static class Sts2GameApiRequirements
{
    private const BindingFlags MemberFlags =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    internal static IReadOnlyList<GameApiRequirement> FindMissing(
        IEnumerable<GameApiRequirement> requirements,
        Func<string, Type?>? findGameType = null)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        return [.. requirements.Where(requirement => !Resolves(requirement, findGameType))];
    }

    internal static void Verify(
        IEnumerable<GameApiRequirement> requirements,
        Sts2CompatibilityReport report,
        ILogStream logStream,
        Func<string, Type?>? findGameType = null)
    {
        var critical = new List<string>();
        foreach (var requirement in FindMissing(requirements, findGameType))
        {
            report.Record("missing_game_member", requirement.Describe(includeNote: false),
                "Member did not resolve; " + (requirement.Note ?? "dependent behavior is unavailable."),
                logStream, requirement.Critical);
            if (requirement.Critical)
            {
                critical.Add(requirement.Describe());
            }
        }

        if (critical.Count > 0)
        {
            throw new InvalidOperationException(
                $"The spirectl live bridge refuses to start: {critical.Count} critical game member(s) "
                + $"are missing in API lane {report.Lane} (game build {report.GameBuild}): "
                + string.Join("; ", critical));
        }
    }

    private static bool Resolves(GameApiRequirement requirement, Func<string, Type?>? findGameType)
    {
        try
        {
            return Resolve(requirement, findGameType);
        }
        catch (Exception)
        {
            // An unloadable or ambiguous member is unavailable to this lane as well.
            return false;
        }
    }

    private static bool Resolve(GameApiRequirement requirement, Func<string, Type?>? findGameType)
    {
        var owner = requirement.Owner
            ?? (requirement.OwnerTypeName is { } name ? findGameType?.Invoke(name) : null);
        if (owner is null)
        {
            return false;
        }

        if (requirement.Kind == GameApiMemberKind.Constructor)
        {
            return owner.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: requirement.ParameterTypes ?? [],
                modifiers: null) is not null;
        }

        for (var type = owner; type is not null; type = type.BaseType)
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

}
