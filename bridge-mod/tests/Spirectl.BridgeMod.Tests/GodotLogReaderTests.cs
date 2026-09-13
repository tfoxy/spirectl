using System.Text;
using Spirectl.Sts2.Core.Logging;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

public sealed class GodotLogReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "spirectl-godot-log-" + Guid.NewGuid());

    [Fact]
    public void GroupsMultilineErrorAndMarksCheckpointBoundary()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "godot.log");
        File.WriteAllText(path, "attempt=7\nERROR: join failed\n  at connect()\nready\n");

        var result = new GodotLogReader().Read(new GodotLogReadRequest(path, CheckpointMarker: "attempt="));

        Assert.False(result.Unavailable);
        Assert.False(result.Truncated);
        Assert.Equal(3, result.Entries.Count);
        Assert.True(result.Entries[0].IsCheckpoint);
        Assert.True(result.Entries[1].IsError);
        Assert.Equal("ERROR: join failed\n  at connect()", result.Entries[1].Text);
    }

    [Fact]
    public void LeavesPartialUtf8LineForTheNextReadAndReportsTruncation()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "godot.log");
        var prefix = Encoding.UTF8.GetBytes("ready\nerror ");
        File.WriteAllBytes(path, [.. prefix, 0xE2, 0x82]);

        var reader = new GodotLogReader();
        var first = reader.Read(new GodotLogReadRequest(path, MaxBytes: 64));
        Assert.Single(first.Entries);
        Assert.Equal("ready", first.Entries[0].Text);
        Assert.True(first.Truncated);

        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.WriteByte(0xAC);
            stream.WriteByte((byte)'\n');
        }

        var second = reader.Read(new GodotLogReadRequest(path, first.NextByteOffset, 64));
        Assert.Single(second.Entries);
        Assert.Equal("error €", second.Entries[0].Text);
        Assert.True(second.NextByteOffset > first.NextByteOffset);
        Assert.Equal(new FileInfo(path).Length, second.NextByteOffset);
    }

    [Fact]
    public void ReportsRotationAndUnavailableFilesWithoutThrowing()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "godot.log");
        File.WriteAllText(path, "old\n");
        var reader = new GodotLogReader();
        var initial = reader.Read(new GodotLogReadRequest(path));
        File.WriteAllText(path, "new\n");

        var rotated = reader.Read(new GodotLogReadRequest(path, initial.NextByteOffset + 100));
        var unavailable = reader.Read(new GodotLogReadRequest(Path.Combine(_directory, "missing.log")));

        Assert.True(rotated.Rotated);
        Assert.Equal("new", rotated.Entries[0].Text);
        Assert.True(unavailable.Unavailable);
    }

    [Fact]
    public void DetectsGodotErrorFormsAndCheckpointReplacement()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "godot.log");
        File.WriteAllText(path, "[12:00:00] [ERROR] failed\nSCRIPT ERROR: bad script\n");
        var reader = new GodotLogReader();
        var first = reader.Read(new GodotLogReadRequest(path));
        Assert.All(first.Entries, entry => Assert.True(entry.IsError));
        File.Delete(path);
        File.WriteAllText(path, "replacement\n");
        var second = reader.Read(new GodotLogReadRequest(path, Checkpoint: first.Checkpoint));
        Assert.True(second.Rotated);
        Assert.Equal("replacement", Assert.Single(second.Entries).Text);
    }

    [Fact]
    public void DetectsReplacementWithALargerNewFile()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "godot.log");
        File.WriteAllText(path, "old\n");
        var reader = new GodotLogReader();
        var first = reader.Read(new GodotLogReadRequest(path));

        File.Delete(path);
        File.WriteAllText(path, "replacement\n" + new string('x', GodotLogReader.HardMaximumBytes));

        var second = reader.Read(new GodotLogReadRequest(path, Checkpoint: first.Checkpoint));
        Assert.True(second.Rotated);
        Assert.Equal("replacement", second.Entries[0].Text);
    }

    [Fact]
    public void DoesNotTreatAppendAsRotation()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "godot.log");
        File.WriteAllText(path, "first\n");
        var reader = new GodotLogReader();
        var first = reader.Read(new GodotLogReadRequest(path));

        File.AppendAllText(path, "second\n");

        var second = reader.Read(new GodotLogReadRequest(path, Checkpoint: first.Checkpoint));
        Assert.False(second.Rotated);
        Assert.Equal("second", Assert.Single(second.Entries).Text);
    }

    [Fact]
    public void EnforcesHardCapAndDoesNotStallLongLines()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "godot.log");
        File.WriteAllText(path, new string('x', GodotLogReader.HardMaximumBytes * 2));
        var result = new GodotLogReader().Read(new GodotLogReadRequest(path, MaxBytes: int.MaxValue));
        Assert.True(result.Truncated);
        Assert.Equal(GodotLogReader.HardMaximumBytes, result.NextByteOffset);
    }

    [Fact]
    public void ContinuesPastAHugeErrorLineWithinTheByteBound()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "godot.log");
        File.WriteAllText(path, "ERROR " + new string('x', GodotLogReader.HardMaximumBytes * 2));
        var reader = new GodotLogReader();

        var first = reader.Read(new GodotLogReadRequest(path));
        var second = reader.Read(new GodotLogReadRequest(path, first.NextByteOffset));

        Assert.True(first.Truncated);
        Assert.True(Assert.Single(first.Entries).IsError);
        Assert.Equal(GodotLogReader.HardMaximumBytes, first.NextByteOffset);
        Assert.True(second.NextByteOffset > first.NextByteOffset);
    }

    [Fact]
    public void ReadsTailWithoutOvershootingAnInitialPartialUtf8Rune()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "godot.log");
        File.WriteAllText(path, "€" + new string('x', 51) + "\nERROR late\n");

        var result = new GodotLogReader().Read(new GodotLogReadRequest(path, MaxBytes: 64, ReadTail: true));

        Assert.True(result.Truncated);
        Assert.Contains(result.Entries, entry => entry.IsError && entry.Text == "ERROR late");
        Assert.Equal(new FileInfo(path).Length, result.NextByteOffset);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
