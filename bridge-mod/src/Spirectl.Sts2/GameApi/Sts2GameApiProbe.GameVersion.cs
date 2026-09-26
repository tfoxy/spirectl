namespace Spirectl.Sts2.Live.GameApi;

internal static partial class Sts2GameApiProbe
{
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
