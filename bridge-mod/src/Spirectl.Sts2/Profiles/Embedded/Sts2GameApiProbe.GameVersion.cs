namespace Spirectl.Sts2.Live.GameApi;

internal static partial class Sts2GameApiProbe
{
    // The embedded profile leaves the reference-data provider out, and with it the by-name read of the
    // game's release record. A refusal only needs the game version, which the install declares in
    // release_info.json and Sts2GameBuildIdentity reads without touching the game. Failures degrade to
    // "<unknown>" rather than masking the refusal.
    private static string DescribeGameVersion()
    {
        try
        {
            var (version, _) = Sts2GameBuildIdentity.ResolveContent();
            return string.IsNullOrWhiteSpace(version) ? "<unknown>" : version;
        }
        catch
        {
            return "<unknown>";
        }
    }
}
