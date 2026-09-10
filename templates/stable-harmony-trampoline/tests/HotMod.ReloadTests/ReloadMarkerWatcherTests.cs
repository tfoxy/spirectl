using HotMod.Shell.Runtime;
using Xunit;

namespace HotMod.ReloadTests;

public sealed class ReloadMarkerWatcherTests
{
    [Fact]
    public async Task MarkerWatcherDebouncesMultipleTouchesIntoOneReload()
    {
        var directory = Directory.CreateTempSubdirectory("hotmod-marker-");
        var markerPath = Path.Combine(directory.FullName, "reload.marker");
        var reloads = new List<string>();
        using var observed = new SemaphoreSlim(0, 1);
        using var watcher = new ReloadMarkerWatcher(markerPath, reason =>
        {
            lock (reloads)
            {
                reloads.Add(reason);
            }

            observed.Release();
        });

        watcher.Start();
        await File.WriteAllTextAsync(markerPath, "1");
        await Task.Delay(50);
        await File.WriteAllTextAsync(markerPath, "2");

        Assert.True(await observed.WaitAsync(TimeSpan.FromSeconds(3)));
        await Task.Delay(400);

        lock (reloads)
        {
            Assert.Single(reloads);
            Assert.Equal("marker-changed", reloads[0]);
        }
    }
}
