using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Spirectl.Sts2.Live;

// WHICH BUILD OF THE GAME IS THIS, AND WHICH STEAM BRANCH DID IT COME FROM.
//
// Two questions an embedder that keeps anything derived from the game's content has to answer, and neither is
// answerable from `release_info.json` alone. That file names the BUILD (`version`, `main_assembly_hash`) but its
// `branch` field is the release tag — "v0.107.1", not "public" — so it cannot tell a stable install from a beta
// one. The distinction matters because the two branches render different pixels from identical `res://` paths: a
// cache keyed only on the resource path serves one branch the other's bytes, with no visible cause.
//
// Godot-free on purpose, so the whole ladder is offline-unit-testable and — more importantly — so it can be
// called BEFORE any runtime exists. An embedder that must purge a stale cache has to do it before the first read
// or write, which is earlier than a bridge handshake.
//
// THE LADDER, best answer first:
//
//   1. STEAMWORKS, in-process — `Steamworks.SteamApps`, the only source that is authoritative rather than
//      circumstantial. It needs Steam initialized, so it is unavailable to a caller that runs before the game's
//      own `SteamAPI_Init()` and to a non-Steam launch; `GetAppBuildId()` is how that is detected. It answers
//      about the install STEAM HAS MOUNTED, which is not necessarily the one this assembly was loaded from — see
//      the note on rung 2 for why that distinction decides whether the answer is usable at all.
//   2. STEAM'S INSTALL MANIFEST on disk — `<install>/../../appmanifest_<appid>.acf`, whose `MountedConfig.BetaKey`
//      is what is actually mounted (as against `UserConfig.BetaKey`, which is what the user last selected and may
//      be a branch still downloading). Same layout on Windows and Linux and in every Steam library, because the
//      manifest always sits two levels above the install directory.
//   3. NOTHING. A copied install outside `steamapps` launched without Steam satisfies neither, and gets an empty
//      branch. Callers decide what to do with that; this type never guesses.
//
// RUNG 1 IS GATED ON RUNG 2 FINDING US, and that is the one non-obvious thing here. Steamworks is a question
// about an APP, not about a directory: a second copy of the game launched from outside the Steam library, with
// Steam running, gets a confident `GetCurrentBetaName()` describing the OTHER install — the one Steam mounted.
// MEASURED: a public-beta build run that way reports branch "public". So the Steamworks answer is used only when
// the manifest walk positively identifies the install we loaded from as the mounted one (it refuses any manifest
// whose `installdir` names a different folder). When it does not, this falls through to an empty branch, and a
// caller that needs a separator gets a better one from its own knowledge than from a confidently wrong branch.
//
// The cost is one false negative: a genuine Steam install whose manifest is unreadable (permissions, a partial
// library move) loses the Steamworks answer too and reports no branch. That is a coarser answer, never a wrong
// one, which is the direction this whole ladder is built to fail in.
//
// The build identity (`Version`, `MainAssemblyHash`) always comes from `release_info.json`, which ships with the
// game and is present whatever the launch route.
public sealed record Sts2GameBuildIdentity(
    string Branch,
    string BranchSource,
    int BuildId,
    string Version,
    int MainAssemblyHash)
{
    /// <summary>An identity that answered nothing — every field at its zero value.</summary>
    public static readonly Sts2GameBuildIdentity Unknown = new(string.Empty, UnknownSource, 0, string.Empty, 0);

    public const string SteamworksSource = "steamworks";
    public const string AppManifestSource = "appmanifest";
    public const string UnknownSource = "unknown";

    /// <summary>
    /// Slay the Spire 2's Steam application id, hardcoded.
    /// </summary>
    /// <remarks>
    /// Deliberately a constant rather than a read of <c>steam_appid.txt</c>: that file is not shipped by the game,
    /// it is written by <c>sts2 game deploy</c> / <c>game launch</c> so a directly-launched binary can initialize
    /// Steam at all. Reading it would therefore work on every development machine and silently degrade on a real
    /// player's install — the worst shape a fallback can have. The id is public and does not change.
    /// </remarks>
    public const int SteamAppId = 2868840;

    /// <summary>Whether a branch was identified at all. Says nothing about which one.</summary>
    public bool HasBranch => Branch.Length > 0;

    /// <summary>
    /// Resolve the identity of the install this assembly was loaded from, or of
    /// <paramref name="installRoot"/> when one is given.
    /// </summary>
    /// <remarks>
    /// Never throws and never blocks: every rung of the ladder degrades to the next, and an install root that
    /// cannot be found at all yields <see cref="Unknown"/>. Cheap enough to call at startup — one reflection
    /// probe and at most two small file reads — but callers that need it repeatedly should cache the result,
    /// since it cannot change while the process lives.
    /// <para>The Steamworks rung answers only for an install Steam itself mounted; see the ladder note at the
    /// top of this file for why that is checked before its answer is believed.</para>
    /// </remarks>
    public static Sts2GameBuildIdentity Resolve(string? installRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(installRoot) ? TryResolveInstallRoot() : installRoot;

        // Directly, not through ResolveContent: `root` is already resolved here, and routing through the public
        // overload would walk for it a second time whenever it came back null.
        var (version, mainAssemblyHash) = ReadReleaseInfo(root);

        // Read FIRST, because it decides both rungs: a non-null answer is proof that the install we resolved is
        // the one Steam has mounted for this app, which is the precondition for Steamworks describing US.
        var manifest = TryReadAppManifest(root);

        // Rung 1. The reflection probe is isolated so a missing/renamed Steamworks assembly is a fall-through
        // rather than a crash — see TryReadSteamworks for why the catch has to live out here.
        if (manifest is not null)
        {
            try
            {
                var steam = TryReadSteamworks();
                if (steam is { } live && live.Branch.Length > 0)
                {
                    return new Sts2GameBuildIdentity(
                        live.Branch, SteamworksSource, live.BuildId, version, mainAssemblyHash);
                }
            }
            catch
            {
                // Steam not initialized, or no Steamworks assembly in this process. Fall through.
            }
        }

        // Rung 2.
        if (manifest is { } mounted && mounted.Branch.Length > 0)
        {
            return new Sts2GameBuildIdentity(
                mounted.Branch, AppManifestSource, mounted.BuildId, version, mainAssemblyHash);
        }

        // Rung 3: no branch, but the build identity may still have been readable.
        return new Sts2GameBuildIdentity(string.Empty, UnknownSource, 0, version, mainAssemblyHash);
    }

    /// <summary>
    /// What the install DECLARES ABOUT ITSELF — its version and content hash, read from
    /// <c>release_info.json</c> — with no Steam, no manifest and no reflection anywhere in the call.
    /// </summary>
    /// <param name="installRoot">The install to read, or <see langword="null"/> to resolve this assembly's own.</param>
    /// <remarks>
    /// <para>
    /// Split out of <see cref="Resolve"/> because these two facts answer a different question from the branch,
    /// and an embedder keying a cache on content wants only these. They ship with the install, so they are the
    /// same on every launch of it: no Steam client, no library layout, nothing that can be up or down. The
    /// branch, by contrast, is circumstantial — which makes it the wrong thing to make a cache's LAYOUT depend
    /// on, and the reason this overload exists.
    /// </para>
    /// <para>
    /// A version of <see cref="string.Empty"/> and a hash of 0 mean "this install would not say", never
    /// "matches anything" — the same convention the rest of this type uses.
    /// </para>
    /// </remarks>
    public static (string Version, int MainAssemblyHash) ResolveContent(string? installRoot = null)
        => ReadReleaseInfo(string.IsNullOrWhiteSpace(installRoot) ? TryResolveInstallRoot() : installRoot);

    /// <summary>
    /// The game install directory this assembly was loaded from, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// TWO STARTING POINTS, tried in order, each walked upwards looking for <c>release_info.json</c>.
    /// </para>
    /// <para>
    /// 1. THE ASSEMBLY'S OWN DIRECTORY. A bridge or mod assembly deployed into the install lives at
    /// <c>&lt;install&gt;/mods/&lt;mod&gt;/</c>, so the file is two levels up. Preferred over the executable
    /// because Godot resolves its executable through <c>/proc/self/exe</c>, which follows symlinks — a symlinked
    /// game binary reports the install it points AT rather than the one it was launched from, and an assembly
    /// sitting inside the real install is the more specific answer.
    /// </para>
    /// <para>
    /// 2. THE PROCESS EXECUTABLE'S DIRECTORY, when the first finds nothing. A mod installed from the STEAM
    /// WORKSHOP does not live under the install at all — it sits at
    /// <c>steamapps/workshop/content/&lt;appid&gt;/&lt;item&gt;/</c>, a sibling branch of the tree that is never
    /// an ancestor of the game — so walking up from it can only fail. MEASURED: it walks
    /// item → appid → content → workshop → steamapps and gives up. That is the shape every real player has, and
    /// with no root there is no version and no content hash, which leaves an embedder unable to tell two game
    /// builds apart. The symlink caveat above is why this is the FALLBACK and not the primary; as an answer of
    /// last resort it is strictly better than none.
    /// </para>
    /// </remarks>
    public static string? TryResolveInstallRoot()
        => TryWalkToInstallRoot(DirectoryOf(SafeAssemblyLocation()))
           ?? TryWalkToInstallRoot(DirectoryOf(SafeProcessPath()));

    /// <summary>Walk up from <paramref name="startDirectory"/> looking for <c>release_info.json</c>.</summary>
    /// <remarks>
    /// Bounded rather than fixed-depth so a differently nested deployment still resolves. Internal so the two
    /// starting points above can be exercised without a process to place them in.
    /// </remarks>
    internal static string? TryWalkToInstallRoot(string? startDirectory)
    {
        var directory = startDirectory;
        for (var depth = 0; depth < 5 && !string.IsNullOrWhiteSpace(directory); depth++)
        {
            try
            {
                if (File.Exists(Path.Combine(directory, ReleaseInfoFileName)))
                {
                    return directory;
                }
                directory = Path.GetDirectoryName(directory);
            }
            catch (Exception exception) when (IsIoFailure(exception))
            {
                return null;
            }
        }

        return null;
    }

    private static string? SafeAssemblyLocation()
    {
        try { return typeof(Sts2GameBuildIdentity).Assembly.Location; }
        catch (Exception exception) when (IsIoFailure(exception)) { return null; }
    }

    private static string? SafeProcessPath()
    {
        try { return Environment.ProcessPath; }
        catch (Exception exception) when (IsIoFailure(exception)) { return null; }
    }

    private static string? DirectoryOf(string? filePath)
    {
        try
        {
            return string.IsNullOrWhiteSpace(filePath) ? null : Path.GetDirectoryName(filePath);
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            return null;
        }
    }

    private const string ReleaseInfoFileName = "release_info.json";

    /// <summary>The branch name and build id Steam itself reports, or null when Steam cannot answer.</summary>
    /// <remarks>
    /// <para>THE BUILD ID IS THE LIVENESS TEST, and the order matters. <c>GetCurrentBetaName</c> reports the
    /// name of a BETA branch, and returns FALSE on the default one — so "no beta name" is an answer ("public"),
    /// not a failure, and reading it as a failure is what makes this rung never fire on an ordinary install.
    /// MEASURED: it does exactly that here. <c>GetAppBuildId</c> is the call that separates the two readings —
    /// a non-zero build id means Steam is initialized and answering, so an empty beta name beside it means the
    /// default branch. A zero means Steam is not up and the whole rung falls through.</para>
    /// <para><c>[MethodImpl(NoInlining)]</c> and the caller-side catch are load-bearing together: the
    /// assembly-load failure for a missing Steamworks.NET happens when the CALLING method is JIT-compiled, so a
    /// <c>try</c> inside this method would never run. Same contract the Godot probes in this repo use.</para>
    /// <para>Bound by method name and arity, never by parameter name — the Steamworks SDK has renamed
    /// <c>pchName</c> to <c>pchBetaName</c> across versions, and a rename must not turn into a silent
    /// fall-through to a weaker rung.</para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (string Branch, int BuildId)? TryReadSteamworks()
    {
        var steamApps = Type.GetType("Steamworks.SteamApps, Steamworks.NET", throwOnError: false);
        if (steamApps is null)
        {
            return null;
        }

        var statics = steamApps.GetMethods(BindingFlags.Public | BindingFlags.Static);

        var getBuildId = statics.FirstOrDefault(m => m.Name == "GetAppBuildId" && m.GetParameters().Length == 0);
        if (getBuildId?.Invoke(null, null) is not int buildId || buildId <= 0)
        {
            return null; // Steam is not initialized in this process — say nothing rather than guess.
        }

        var getBetaName = statics.FirstOrDefault(
            m => m.Name == "GetCurrentBetaName" && m.GetParameters().Length == 2);
        if (getBetaName is null)
        {
            return (DefaultBranch, buildId);
        }

        // 256 matches the buffer every Steamworks sample uses for a branch name; Steam truncates rather than
        // failing, and a truncated branch name would still be wrong, so a generous buffer is the cheap choice.
        var arguments = new object?[] { null, 256 };
        var named = getBetaName.Invoke(null, arguments) is true
            && arguments[0] is string beta
            && !string.IsNullOrWhiteSpace(beta)
            ? beta.Trim()
            : DefaultBranch;

        return (named, buildId);
    }

    /// <summary>
    /// The mounted branch and build id from Steam's install manifest for <paramref name="installRoot"/>.
    /// </summary>
    /// <remarks>
    /// Reads exactly <c>&lt;install&gt;/../../appmanifest_&lt;SteamAppId&gt;.acf</c> and refuses it unless its
    /// <c>installdir</c> names this install's own folder — a manifest left behind by a moved or renamed install
    /// would otherwise be believed, and believing the wrong manifest is worse than having none.
    /// <para><c>MountedConfig</c> is preferred over <c>UserConfig</c> because they disagree exactly when it
    /// matters: during a branch switch the user's selection has moved and the files on disk have not.</para>
    /// </remarks>
    internal static (string Branch, int BuildId)? TryReadAppManifest(string? installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(installRoot);
            var installDirName = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var steamApps = Path.GetDirectoryName(Path.GetDirectoryName(full));
            if (string.IsNullOrWhiteSpace(steamApps) || string.IsNullOrWhiteSpace(installDirName))
            {
                return null;
            }

            var manifestPath = Path.Combine(
                steamApps,
                string.Create(CultureInfo.InvariantCulture, $"appmanifest_{SteamAppId}.acf"));
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            var state = Sts2ValveKeyValues.Parse(File.ReadAllText(manifestPath))?.Child("AppState");
            if (state is null)
            {
                return null;
            }

            var declaredInstallDir = state.Value("installdir");
            if (!string.Equals(declaredInstallDir, installDirName, PathNameComparison))
            {
                return null;
            }

            var branch = state.Child("MountedConfig")?.Value("BetaKey")
                ?? state.Child("UserConfig")?.Value("BetaKey")
                ?? string.Empty;
            var buildId = int.TryParse(
                state.Value("buildid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;

            // An app on the default branch may carry no BetaKey at all; that absence IS "public", and reporting
            // it as unknown would send an ordinary player down the weakest rung of the ladder.
            return (string.IsNullOrWhiteSpace(branch) ? DefaultBranch : branch.Trim(), buildId);
        }
        catch (Exception exception) when (IsIoFailure(exception))
        {
            return null;
        }
    }

    /// <summary>Steam's name for a default (non-beta) branch.</summary>
    public const string DefaultBranch = "public";

    private static (string Version, int MainAssemblyHash) ReadReleaseInfo(string? installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
        {
            return (string.Empty, 0);
        }

        try
        {
            var path = Path.Combine(installRoot, ReleaseInfoFileName);
            if (!File.Exists(path))
            {
                return (string.Empty, 0);
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var version = root.TryGetProperty("version", out var versionElement)
                && versionElement.ValueKind == JsonValueKind.String
                ? versionElement.GetString() ?? string.Empty
                : string.Empty;
            var hash = root.TryGetProperty("main_assembly_hash", out var hashElement)
                && hashElement.ValueKind == JsonValueKind.Number
                && hashElement.TryGetInt32(out var parsed)
                ? parsed
                : 0;
            return (version, hash);
        }
        catch (Exception exception) when (IsIoFailure(exception) || exception is JsonException)
        {
            return (string.Empty, 0);
        }
    }

    private static StringComparison PathNameComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool IsIoFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}

/// <summary>
/// The smallest Valve KeyValues reader that answers this file's questions: quoted <c>"key" "value"</c> pairs and
/// <c>"key" { … }</c> blocks, which is the whole of an <c>appmanifest_*.acf</c>.
/// </summary>
/// <remarks>
/// Deliberately not a general KeyValues implementation — no conditionals, no includes, no unquoted tokens, no
/// duplicate-key merging. Anything it does not understand it skips, because a manifest it half-understands must
/// degrade to "no answer" rather than to a confident wrong one.
/// </remarks>
internal sealed class Sts2ValveKeyValues
{
    private readonly Dictionary<string, Sts2ValveKeyValues> _children =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public Sts2ValveKeyValues? Child(string key) =>
        _children.TryGetValue(key, out var child) ? child : null;

    public string? Value(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public static Sts2ValveKeyValues? Parse(string text)
    {
        var index = 0;
        var root = new Sts2ValveKeyValues();
        return TryParseInto(text, ref index, root, depth: 0) ? root : null;
    }

    // Bounded so a malformed file with unbalanced braces cannot recurse without end.
    private const int MaxDepth = 16;

    private static bool TryParseInto(string text, ref int index, Sts2ValveKeyValues node, int depth)
    {
        if (depth > MaxDepth)
        {
            return false;
        }

        while (true)
        {
            SkipTrivia(text, ref index);
            if (index >= text.Length || text[index] == '}')
            {
                if (index < text.Length)
                {
                    index++; // consume the '}' for the caller
                }
                return true;
            }

            if (TryReadQuoted(text, ref index) is not { } key)
            {
                return false;
            }

            SkipTrivia(text, ref index);
            if (index >= text.Length)
            {
                return false;
            }

            if (text[index] == '{')
            {
                index++;
                var child = new Sts2ValveKeyValues();
                if (!TryParseInto(text, ref index, child, depth + 1))
                {
                    return false;
                }
                node._children[key] = child;
                continue;
            }

            if (TryReadQuoted(text, ref index) is not { } value)
            {
                return false;
            }
            node._values[key] = value;
        }
    }

    private static void SkipTrivia(string text, ref int index)
    {
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                index++;
                continue;
            }

            // `//` line comments are the only comment form KeyValues has.
            if (text[index] == '/' && index + 1 < text.Length && text[index + 1] == '/')
            {
                while (index < text.Length && text[index] is not ('\n' or '\r'))
                {
                    index++;
                }
                continue;
            }

            return;
        }
    }

    private static string? TryReadQuoted(string text, ref int index)
    {
        if (index >= text.Length || text[index] != '"')
        {
            return null;
        }

        index++;
        var builder = new System.Text.StringBuilder();
        while (index < text.Length)
        {
            var c = text[index++];
            if (c == '"')
            {
                return builder.ToString();
            }
            if (c == '\\' && index < text.Length)
            {
                var escaped = text[index++];
                builder.Append(escaped switch
                {
                    'n' => '\n',
                    't' => '\t',
                    _ => escaped,
                });
                continue;
            }
            builder.Append(c);
        }

        return null; // unterminated
    }
}
