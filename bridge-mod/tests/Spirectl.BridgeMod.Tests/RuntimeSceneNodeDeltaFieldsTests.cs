using System.Reflection;
using System.Text.RegularExpressions;
using Spirectl.Sts2.Core.SceneInspection;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

/// <summary>
/// Holds <see cref="RuntimeSceneNodeDeltaFields"/> to the watcher it describes.
///
/// <para>The published split is only worth anything if it cannot drift: an embedder that keeps the volatile
/// fields between keyframes and re-applies the static ones is trusting this list to be complete AND correctly
/// sided. Drift is silent on the wire — a newly-added static field the consumer treats as volatile just
/// un-styles nodes on every non-keyframe tick — so the guard has to be a test rather than a convention.</para>
///
/// <para>Two independent assertions, because either alone has a hole. Reflection proves the list COVERS the
/// record exactly (a new field added to the delta fails here even if the watcher never passes it). The
/// source-text scan proves each field is on the side the watcher actually puts it on (reflection cannot see an
/// <c>includeStatic ? … : null</c>). The source scan is deliberately scoped to the one argument list rather
/// than the file: the identifier appears throughout the watcher, including in comments.</para>
/// </summary>
public sealed class RuntimeSceneNodeDeltaFieldsTests
{
    private const string WatcherSource = "bridge-mod/src/Spirectl.Sts2/Live/Sts2RuntimeSceneWatcher.cs";

    [Fact]
    public void StaticAndVolatileFieldNamesPartitionTheDeltaRecord()
    {
        var recordProperties = typeof(RuntimeSceneNodeDelta)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.Name != "EqualityContract")
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        var statics = RuntimeSceneNodeDeltaFields.StaticFieldNames.ToHashSet(StringComparer.Ordinal);
        var volatiles = RuntimeSceneNodeDeltaFields.VolatileFieldNames.ToHashSet(StringComparer.Ordinal);

        Assert.Equal(RuntimeSceneNodeDeltaFields.StaticFieldNames.Count, statics.Count);
        Assert.Equal(RuntimeSceneNodeDeltaFields.VolatileFieldNames.Count, volatiles.Count);
        Assert.Empty(statics.Intersect(volatiles, StringComparer.Ordinal));

        var union = statics.Union(volatiles, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(union.Except(recordProperties, StringComparer.Ordinal));
        Assert.Empty(recordProperties.Except(union, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryIncludeStaticGatedArgumentIsListedAsStaticAndTheRestAsVolatile()
    {
        var arguments = ReadBuildNodeDeltaArguments();

        // A rename or a restructure that emptied the parse would satisfy the loop below vacuously.
        Assert.True(
            arguments.Count >= 60,
            $"expected the BuildNodeDelta argument list to survive; parsed {arguments.Count} argument(s).");

        var statics = RuntimeSceneNodeDeltaFields.StaticFieldNames.ToHashSet(StringComparer.Ordinal);
        var volatiles = RuntimeSceneNodeDeltaFields.VolatileFieldNames.ToHashSet(StringComparer.Ordinal);

        var misplaced = new List<string>();
        foreach (var (name, value) in arguments)
        {
            var gated = value.Contains("includeStatic", StringComparison.Ordinal);
            if (gated && !statics.Contains(name))
            {
                misplaced.Add($"{name} is gated on includeStatic but is not in StaticFieldNames.");
            }

            if (!gated && !volatiles.Contains(name))
            {
                misplaced.Add($"{name} is passed ungated but is not in VolatileFieldNames.");
            }
        }

        Assert.Empty(misplaced);
    }

    /// <summary>
    /// The `Name: value` pairs of the one `new RuntimeSceneNodeDelta(...)` construction inside BuildNodeDelta,
    /// with comment lines dropped (several of them mention includeStatic in prose).
    /// </summary>
    private static List<(string Name, string Value)> ReadBuildNodeDeltaArguments()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), WatcherSource));

        var methodStart = source.IndexOf(
            "private static RuntimeSceneNodeDelta BuildNodeDelta(",
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0, $"BuildNodeDelta not found in {WatcherSource}.");

        var listStart = source.IndexOf("return new RuntimeSceneNodeDelta(", methodStart, StringComparison.Ordinal);
        Assert.True(listStart >= 0, $"BuildNodeDelta's RuntimeSceneNodeDelta construction not found in {WatcherSource}.");

        // Walk to the matching close paren rather than to a literal terminator: an argument value is itself a
        // call expression, so any fixed string would be a guess about the last one.
        var bodyStart = listStart + "return new RuntimeSceneNodeDelta(".Length;
        var depth = 1;
        var listEnd = -1;
        for (var index = bodyStart; index < source.Length; index += 1)
        {
            if (source[index] == '(')
            {
                depth += 1;
            }
            else if (source[index] == ')')
            {
                depth -= 1;
                if (depth == 0)
                {
                    listEnd = index;
                    break;
                }
            }
        }

        Assert.True(listEnd >= 0, "BuildNodeDelta's argument list is not terminated.");

        var body = source[bodyStart..listEnd];
        var arguments = new List<(string, string)>();
        var pending = string.Empty;
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            pending = pending.Length == 0 ? line : $"{pending} {line}";
            if (!pending.EndsWith(",", StringComparison.Ordinal) && !pending.EndsWith(")", StringComparison.Ordinal))
            {
                continue;
            }

            var match = Regex.Match(pending, @"^([A-Za-z_][A-Za-z0-9_]*):\s*(.*)$");
            if (match.Success)
            {
                arguments.Add((match.Groups[1].Value, match.Groups[2].Value));
            }

            pending = string.Empty;
        }

        // The last argument carries no trailing comma.
        var tail = Regex.Match(pending.Trim(), @"^([A-Za-z_][A-Za-z0-9_]*):\s*(.*)$");
        if (tail.Success)
        {
            arguments.Add((tail.Groups[1].Value, tail.Groups[2].Value));
        }

        return arguments;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "spirectl.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find repository root.");
    }
}
