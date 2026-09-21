namespace Spirectl.Sts2.Embedding;

/// <summary>
/// The reason strings published on the <c>live-sts2-host</c> capability when the composed runtime is not backed
/// by a live STS2 host, as constants.
/// </summary>
/// <remarks>
/// <para>WHY THIS EXISTS. These three strings are the ONLY place the runtime says out loud why it cannot observe
/// the game, and an embedder reads them off the capability envelope to decide what to tell its operator. Until
/// now each was a literal at its construction site, so a consumer that wanted to act on one had to copy the
/// sentence — and a copied sentence drifts from the original silently, since nothing compares them. Naming them
/// here makes the wording a compile-time reference on both sides.</para>
/// <para>THE VALUES ARE PART OF THE CONTRACT. A consumer maps them to bounded log tokens, so changing the text
/// of an existing constant breaks that mapping just as surely as renaming a capability id would. Add a new
/// constant for a new reason; do not re-word an existing one.</para>
/// </remarks>
public static class LiveSts2HostUnsupportedReasons
{
    /// <summary>
    /// A build compiled WITH live-host support that found no STS2/Godot engine loaded in its own process, so it
    /// composed placeholder ports instead of the live ones.
    /// </summary>
    public const string OutsideGameProcess =
        "This live-host build is not running inside an initialized STS2/Godot process.";

    /// <summary>
    /// A build compiled WITHOUT live-host references at all: no amount of runtime context can make it observe a
    /// game, because the code that would do so was never compiled in.
    /// </summary>
    public const string NonLiveHostBuild =
        "This build was compiled without live STS2 host references.";

    /// <summary>
    /// The default for a runtime composed through the plain embedding entrypoints — a facade assembled from
    /// supplied ports, with no claim to a live host behind it.
    /// </summary>
    public const string NotALiveHostAdapter =
        "This runtime was not created from a live STS2 host adapter.";
}
