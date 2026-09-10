using HotMod.Shell.Diagnostics;

namespace HotMod.Shell.Runtime;

public sealed class ReloadMarkerWatcher : IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(250);

    private readonly string _markerPath;
    private readonly Action<string> _reload;
    private readonly Timer _debounceTimer;
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    public ReloadMarkerWatcher(string markerPath, Action<string> reload)
    {
        _markerPath = Path.GetFullPath(markerPath);
        _reload = reload;
        _debounceTimer = new Timer(OnDebounced, state: null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var directory = Path.GetDirectoryName(_markerPath) ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(directory);

        _watcher = new FileSystemWatcher(directory)
        {
            Filter = Path.GetFileName(_markerPath),
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
        };
        _watcher.Changed += OnMarkerChanged;
        _watcher.Created += OnMarkerChanged;
        _watcher.Renamed += OnMarkerRenamed;
        _watcher.EnableRaisingEvents = true;

        ReloadLog.WriteInfo("hot reload marker watcher started", new { markerPath = _markerPath });
    }

    public void Stop()
    {
        _debounceTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (_watcher is null)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnMarkerChanged;
        _watcher.Created -= OnMarkerChanged;
        _watcher.Renamed -= OnMarkerRenamed;
        _watcher.Dispose();
        _watcher = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _debounceTimer.Dispose();
        _disposed = true;
    }

    private void OnMarkerChanged(object sender, FileSystemEventArgs args)
    {
        if (PathsMatch(args.FullPath, _markerPath))
        {
            _debounceTimer.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnMarkerRenamed(object sender, RenamedEventArgs args)
    {
        if (PathsMatch(args.FullPath, _markerPath) || PathsMatch(args.OldFullPath, _markerPath))
        {
            _debounceTimer.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnDebounced(object? state)
    {
        try
        {
            _reload("marker-changed");
        }
        catch (Exception exception)
        {
            ReloadLog.WriteError("hot reload marker callback failed", new
            {
                exceptionType = exception.GetType().FullName,
                exceptionMessage = exception.Message
            });
        }
    }

    private static bool PathsMatch(string left, string right)
    {
        return string.Equals(Path.GetFullPath(left), right, StringComparison.Ordinal);
    }
}
