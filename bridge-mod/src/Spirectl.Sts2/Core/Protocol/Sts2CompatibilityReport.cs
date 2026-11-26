using Spirectl.Sts2.Core.Logging;

namespace Spirectl.Sts2.Core.Protocol;

internal sealed record Sts2CompatibilityIssue(
    string Code, string Member, string Lane, string GameBuild, string Reason, bool Critical);

/// <summary>Process-wide compatibility failures, independent of the bounded runtime log.</summary>
internal sealed class Sts2CompatibilityReport(string lane = "", string gameBuild = "<unknown>")
{
    internal static Sts2CompatibilityReport Process { get; } = new(BridgeBuildInfo.Sts2ApiLane);

    private readonly System.Threading.Lock _sync = new();
    private readonly Dictionary<(string Code, string Member), Sts2CompatibilityIssue> _issues = [];
    private bool _summaryWritten;
    private ILogStream? _logStream;

    internal string Lane { get; private set; } = lane;
    internal string GameBuild { get; private set; } = gameBuild;

    internal void SetGameBuild(string apiLane, string version, ILogStream? logStream = null)
    {
        lock (_sync)
        {
            Lane = apiLane;
            GameBuild = version;
            _logStream = logStream;
        }
    }

    internal void Record(string code, string member, string reason, ILogStream? logStream = null, bool critical = false)
    {
        Sts2CompatibilityIssue issue;
        lock (_sync)
        {
            logStream ??= _logStream;
            issue = new(code, member, Lane, GameBuild, reason, critical);
            if (!_issues.TryAdd((code, member), issue))
            {
                return;
            }
        }

        logStream?.Write(BridgeLogLevel.Warn, "bridge.game-api",
            $"[{code}] {member}: {reason} (lane {issue.Lane}, game build {issue.GameBuild}).");
    }

    internal bool HasIssue(string code)
    {
        lock (_sync)
        {
            return _issues.Keys.Any(key => key.Code == code);
        }
    }

    internal bool HasMissing(string member)
    {
        lock (_sync)
        {
            return _issues.Values.Any(issue => issue.Code == "missing_game_member"
                && (issue.Member == member || issue.Member.StartsWith(member + "(", StringComparison.Ordinal)));
        }
    }

    internal IReadOnlyList<Sts2CompatibilityIssue> Snapshot()
    {
        lock (_sync)
        {
            return [.. _issues.Values];
        }
    }

    internal IReadOnlyDictionary<string, object?>? DescribeDiagnostic()
    {
        var issues = Snapshot();
        return issues.Count == 0 ? null : new Dictionary<string, object?>
        {
            ["degraded"] = issues.Select(issue => new Dictionary<string, object?>
            {
                ["code"] = issue.Code,
                ["member"] = issue.Member,
                ["lane"] = issue.Lane,
                ["gameBuild"] = issue.GameBuild,
                ["reason"] = issue.Reason,
                ["critical"] = issue.Critical,
            }).ToArray(),
        };
    }

    internal void WriteStartupSummary(ILogStream logStream)
    {
        lock (_sync)
        {
            if (_summaryWritten)
            {
                return;
            }

            _summaryWritten = true;
            logStream.Write(_issues.Count == 0 ? BridgeLogLevel.Info : BridgeLogLevel.Warn, "bridge.game-api",
                $"Game API lane {Lane}, game build {GameBuild}: "
                + (_issues.Count == 0 ? "no compatibility failures detected."
                    : $"degraded ({_issues.Count} issue(s)); see handshake diagnostic.degraded or game bridge-health."));
        }
    }
}

/// <summary>Isolates each hook family and retains failures that installers already log and swallow.</summary>
internal sealed class Sts2CompatibilityHookInstaller(Sts2CompatibilityReport report, ILogStream logStream)
{
    internal void Install(string family, Action<ILogStream> install, params string[] requiredMembers)
    {
        var missing = requiredMembers.FirstOrDefault(report.HasMissing);
        if (report.HasIssue("manifest_load_failed") || missing is not null)
        {
            report.Record("hook_install_skipped", family,
                missing is null ? "The game API manifest could not load; hook dependencies cannot be verified."
                    : $"Required hook member is unavailable: {missing}.", logStream);
            return;
        }

        var hookLog = new HookLogStream(report, logStream, family);
        try
        {
            install(hookLog);
        }
        catch (Exception exception)
        {
            report.Record("hook_install_failed", family,
                $"Hook family unavailable: {exception.GetType().Name}: {exception.Message}", logStream);
        }
        finally
        {
            hookLog.Complete();
        }
    }

    private sealed class HookLogStream(Sts2CompatibilityReport report, ILogStream logStream, string family) : ILogStream
    {
        private bool _installing = true;

        internal void Complete() => _installing = false;

        public void Write(BridgeLogLevel level, string target, string message)
        {
            if (_installing && level >= BridgeLogLevel.Warn)
            {
                report.Record("hook_install_warning", family, message);
            }

            logStream.Write(level, target, message);
        }

        public LogReadResult Read(LogQuery query) => logStream.Read(query);
    }
}
