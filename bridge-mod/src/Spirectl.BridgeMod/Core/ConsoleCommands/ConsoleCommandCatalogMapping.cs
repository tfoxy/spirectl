using System.Text.RegularExpressions;

namespace Spirectl.Sts2.Core.ConsoleCommands;

public static class ConsoleCommandCatalogMapping
{
    private static readonly Regex LabeledCandidate = new(@"^(\S+) \((.*)\)$", RegexOptions.CultureInvariant);

    public static (string Value, string Label) SplitCandidate(string raw)
    {
        var match = LabeledCandidate.Match(raw);
        return match.Success ? (match.Groups[1].Value, match.Groups[2].Value) : (raw, string.Empty);
    }

    public static ConsoleCommandDescriptorSnapshot Descriptor(
        string name, string usage, string description, bool networked, bool debugOnly)
        => new(name, usage, description, networked, debugOnly);

    public static string[] Arguments(IReadOnlyList<string> preceding, string prefix)
        => [.. preceding, prefix];

    public static string CommandPrefix(string command, IReadOnlyList<string> preceding)
        => command + " " + (preceding.Count == 0 ? string.Empty : string.Join(" ", preceding) + " ");

    public static string CompletionType(string type) => type switch
    {
        "Command" => "command",
        "Subcommand" => "subcommand",
        _ => "argument",
    };
}
