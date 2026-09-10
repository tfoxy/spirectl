using HotMod.Contracts;
using HotMod.Shell.Diagnostics;

namespace HotMod.Shell.Runtime;

public sealed class HotRuntime : IHotHost, IDisposable
{
    private static readonly TimeSpan UnloadProbeDelay = TimeSpan.FromMilliseconds(25);

    private readonly object _gate = new();
    private ReloadGeneration? _currentGeneration;
    private ReloadManager? _reloadManager;
    private HotRuntimeOptions? _options;
    private ReloadMarkerWatcher? _watcher;
    private bool _reloadInProgress;
    private ReloadReport? _lastReloadReport;
    private ReloadError? _restartRequiredError;

    public static HotRuntime Current { get; } = new();

    public DateTimeOffset Now => DateTimeOffset.UtcNow;

    public async Task InitializeAsync(HotRuntimeOptions options)
    {
        _options = options;
        _reloadManager = new ReloadManager(this);
        ReloadLog.WriteInfo("hot mod shell initialized", new
        {
            hotReloadEnabled = options.HotReloadEnabled,
            logicAssemblyPath = options.LogicAssemblyPath,
            reloadMarkerPath = options.ReloadMarkerPath
        });

        if (File.Exists(options.LogicAssemblyPath))
        {
            await ReloadAsync("initial");
        }

        if (options.HotReloadEnabled)
        {
            _watcher = new ReloadMarkerWatcher(options.ReloadMarkerPath, reason => _ = ReloadAsync(reason));
            _watcher.Start();
            ReloadLog.WriteInfo("hot reload enabled", new
            {
                markerPath = options.ReloadMarkerPath,
                logicAssemblyPath = options.LogicAssemblyPath
            });
        }
        else
        {
            ReloadLog.WriteInfo("hot reload disabled", new
            {
                logicAssemblyPath = options.LogicAssemblyPath
            });
        }
    }

    public void Initialize(HotRuntimeOptions options)
    {
        InitializeAsync(options).GetAwaiter().GetResult();
    }

    public async Task<ReloadReport> ReloadAsync(string reason)
    {
        if (_reloadManager is null || _options is null)
        {
            var failureReport = ReloadReport.Failed(
                Now,
                sourceAssemblyPath: null,
                shadowAssemblyPath: null,
                previousGeneration: _currentGeneration?.Generation,
                durationMs: 0,
                new ReloadError(
                    ReloadErrorCode.ReloadRestartRequired,
                    ReloadPhase.Load,
                    "Hot runtime has not been initialized.",
                    ExceptionType: null,
                    ExceptionMessage: null,
                    RestartRequired: true));
            ReloadLog.WriteError("hot reload failed", failureReport);
            StoreReloadReport(failureReport);
            return failureReport;
        }

        lock (_gate)
        {
            if (_reloadInProgress)
            {
                var busy = ReloadReport.Busy(Now, _options.LogicAssemblyPath, _currentGeneration?.Generation, durationMs: 0);
                _lastReloadReport = busy;
                return busy;
            }

            _reloadInProgress = true;
        }

        try
        {
            ReloadGeneration? current;
            lock (_gate)
            {
                current = _currentGeneration;
            }

            var outcome = await _reloadManager.ReloadAsync(
                new ReloadRequest(_options.LogicAssemblyPath, _options.ShadowRoot, _options.EntryTypeName, reason, VerifyUnload: true),
                current);

            var report = outcome.Report;
            if (outcome.NewGeneration is not null)
            {
                lock (_gate)
                {
                    _currentGeneration = outcome.NewGeneration;
                }

                report = FinalizePreviousGeneration(report, outcome.OldGenerationToDispose);
            }

            ReloadLog.WriteInfo(report.Status == ReloadStatus.Loaded ? "hot reload loaded" : "hot reload failed", report);
            StoreReloadReport(report);
            return report;
        }
        finally
        {
            lock (_gate)
            {
                _reloadInProgress = false;
            }
        }
    }

    public ReloadReport Reload(string reason)
    {
        return ReloadAsync(reason).GetAwaiter().GetResult();
    }

    public string DescribeSpirectlHotReloadStatusJson()
    {
        return ReloadJson.Serialize(CreateSpirectlStatus());
    }

    public SpirectlHotReloadStatus CreateSpirectlStatusSnapshot()
    {
        return CreateSpirectlStatus();
    }

    public async Task<string> RequestSpirectlHotReloadJsonAsync(string requestJson)
    {
        var request = ReloadJson.Deserialize<SpirectlHotReloadRequest>(requestJson)
            ?? throw new InvalidOperationException("Hot-reload request JSON was empty.");
        var validation = ValidateSpirectlRequest(request);
        if (validation is not null)
        {
            StoreReloadReport(validation);
            return ReloadJson.Serialize(new SpirectlHotReloadResponse(
                Accepted: false,
                CreateSpirectlStatus(),
                validation,
                []));
        }

        if (!request.WaitForCompletion)
        {
            _ = Task.Run(async () => await ReloadAsync($"spirectl:{request.RequestId}").ConfigureAwait(false));
            return ReloadJson.Serialize(new SpirectlHotReloadResponse(
                Accepted: true,
                CreateSpirectlStatus(),
                Report: null,
                []));
        }

        var reloadTask = ReloadAsync($"spirectl:{request.RequestId}");
        var timeout = TimeSpan.FromMilliseconds(Math.Max(1, request.TimeoutMs));
        var completed = await Task.WhenAny(reloadTask, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != reloadTask)
        {
            var timeoutReport = ReloadReport.Failed(
                Now,
                _options?.LogicAssemblyPath,
                shadowAssemblyPath: null,
                previousGeneration: _currentGeneration?.Generation,
                durationMs: request.TimeoutMs,
                new ReloadError(
                    ReloadErrorCode.ReloadBusy,
                    ReloadPhase.Load,
                    "Timed out waiting for hot reload completion.",
                    ExceptionType: null,
                    ExceptionMessage: null,
                    RestartRequired: false));
            StoreReloadReport(timeoutReport);
            return ReloadJson.Serialize(new SpirectlHotReloadResponse(
                Accepted: true,
                CreateSpirectlStatus(),
                timeoutReport,
                []));
        }

        var report = await reloadTask.ConfigureAwait(false);
        return ReloadJson.Serialize(new SpirectlHotReloadResponse(
            Accepted: true,
            CreateSpirectlStatus(),
            report,
            []));
    }

    public HotLogicResult? OnCombatTurn(IReadOnlyDictionary<string, string> values)
    {
        return DispatchHook("combat.turn", values);
    }

    public HotLogicResult? DispatchHook(string hookId, IReadOnlyDictionary<string, string> values)
    {
        ReloadGeneration? generation;
        lock (_gate)
        {
            generation = _currentGeneration;
        }

        if (generation?.Logic is null)
        {
            return null;
        }

        try
        {
            var context = new HotHookContext(hookId, generation.Generation, values);
            return generation.Logic.OnHook(context);
        }
        catch (Exception exception)
        {
            ReloadLog.WriteError("hot logic hook failed", new
            {
                hookId,
                generation = generation.Generation,
                exceptionType = exception.GetType().FullName,
                exceptionMessage = exception.Message
            });
            return null;
        }
    }

    public void Log(HotLogLevel level, string message, IReadOnlyDictionary<string, string>? fields = null)
    {
        var data = fields is null ? null : new { fields };
        switch (level)
        {
            case HotLogLevel.Debug:
            case HotLogLevel.Information:
                ReloadLog.WriteInfo(message, data);
                break;
            case HotLogLevel.Warning:
                ReloadLog.WriteWarning(message, data);
                break;
            case HotLogLevel.Error:
                ReloadLog.WriteError(message, data);
                break;
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
        ReloadGeneration? generation;
        lock (_gate)
        {
            generation = _currentGeneration;
            _currentGeneration = null;
        }

        generation?.DisposeLogic();
        generation?.RequestUnload();
    }

    public void RecordRestartRequiredError(ReloadError error)
    {
        lock (_gate)
        {
            _restartRequiredError = error;
        }
    }

    private SpirectlHotReloadStatus CreateSpirectlStatus()
    {
        ReloadGeneration? generation;
        ReloadReport? lastReloadReport;
        bool reloadInProgress;
        ReloadError? restartRequiredError;
        lock (_gate)
        {
            generation = _currentGeneration;
            lastReloadReport = _lastReloadReport;
            reloadInProgress = _reloadInProgress;
            restartRequiredError = _restartRequiredError;
        }

        return new SpirectlHotReloadStatus(
            new SpirectlHotReloadProtocolInfo("spirectl.m57.hot-reload-shell", 0),
            _options?.ShellModId ?? "hotmod.template",
            generation?.Generation ?? 0,
            _options?.LogicAssemblyPath ?? string.Empty,
            HotContract.ContractVersion,
            reloadInProgress,
            lastReloadReport,
            restartRequiredError?.RestartRequired == true || lastReloadReport?.Error?.RestartRequired == true);
    }

    private ReloadReport? ValidateSpirectlRequest(SpirectlHotReloadRequest request)
    {
        if (_options is null)
        {
            return RestartRequiredFailure("Hot runtime has not been initialized.");
        }

        if (!string.Equals(request.ShellModId, _options.ShellModId, StringComparison.Ordinal))
        {
            return RestartRequiredFailure($"Requested shellModId '{request.ShellModId}' does not match shell '{_options.ShellModId}'.", restartRequired: false);
        }

        if (!Path.GetFullPath(request.LogicArtifactPath).Equals(Path.GetFullPath(_options.LogicAssemblyPath), StringComparison.OrdinalIgnoreCase))
        {
            return RestartRequiredFailure("Requested logic artifact path does not match this shell.", restartRequired: false);
        }

        if (request.ExpectedContractVersion != HotContract.ContractVersion)
        {
            return RestartRequiredFailure($"Requested contract version {request.ExpectedContractVersion} does not match shell contract {HotContract.ContractVersion}.", restartRequired: true);
        }

        return null;
    }

    private ReloadReport RestartRequiredFailure(string message, bool restartRequired = true)
    {
        return ReloadReport.Failed(
            Now,
            _options?.LogicAssemblyPath,
            shadowAssemblyPath: null,
            previousGeneration: _currentGeneration?.Generation,
            durationMs: 0,
            new ReloadError(
                restartRequired ? ReloadErrorCode.ReloadRestartRequired : ReloadErrorCode.ReloadActivationFailed,
                restartRequired ? ReloadPhase.Contract : ReloadPhase.Source,
                message,
                ExceptionType: null,
                ExceptionMessage: null,
                RestartRequired: restartRequired));
    }

    private void StoreReloadReport(ReloadReport report)
    {
        lock (_gate)
        {
            _lastReloadReport = report;
        }
    }

    private static ReloadReport FinalizePreviousGeneration(ReloadReport report, ReloadGeneration? previousGeneration)
    {
        if (previousGeneration is null)
        {
            return report;
        }

        var warnings = report.Warnings.ToList();
        var dispose = previousGeneration.DisposeLogic();
        if (dispose.Exception is not null)
        {
            warnings.Add(new ReloadWarning(
                ReloadErrorCode.ReloadPreviousDisposeFailed,
                ReloadPhase.Dispose,
                $"Previous generation {previousGeneration.Generation} dispose failed: {dispose.Exception.Message}"));
        }

        previousGeneration.RequestUnload();
        var collected = VerifyCollected(previousGeneration.LoadContextWeakReference);
        if (!collected)
        {
            warnings.Add(new ReloadWarning(
                ReloadErrorCode.ReloadUnloadNotCollected,
                ReloadPhase.Unload,
                $"Previous generation {previousGeneration.Generation} load context was not collected."));
        }

        return report with
        {
            PreviousDisposed = dispose.Exception is null,
            PreviousUnloadRequested = true,
            PreviousCollected = collected,
            Warnings = warnings
        };
    }

    private static bool VerifyCollected(WeakReference weakReference)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!weakReference.IsAlive)
            {
                return true;
            }

            Thread.Sleep(UnloadProbeDelay);
        }

        return !weakReference.IsAlive;
    }
}
