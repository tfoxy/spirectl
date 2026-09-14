using Xunit;

namespace Spirectl.BridgeMod.Tests;

/// <summary>
/// Guards the ONE invariant the immediate-reward claim cannot be allowed to lose: the reward row is retired only
/// after the game has said the seat actually received the reward.
///
/// <para>Reward.SelectUnsynchronized returns "was the reward received", and it answers false for real — a potion
/// reward taken with a full belt, which on screen leaves the row claimable rather than consuming it. A claim path
/// that starts the select and retires the row without waiting looks correct for every reward that cannot be
/// refused, and silently destroys the one reward the player did not get.</para>
///
/// <para>This is a SOURCE-TEXT test because the behavior is not reachable from this suite. Sts2ActionHandler lives
/// under Live/, which only compiles when EnableSts2LiveHost is true; `validate.sh bridge-tests` does not set it, so
/// nothing here can even NAME Reward, RewardCommit or Sts2RewardCaptureRegistry, let alone run a refusing obtain.
/// Nor is there a decision worth lifting into the Godot-free core: the decision IS the bool, and a helper that
/// returned it back would be a tautology that passes with and without the fix. What is left to protect is the
/// SHAPE of the call — awaited, with the retire behind the refusal guard — which is exactly what the previous
/// version of this code got wrong by binding a Task&lt;bool&gt; to a plain Task parameter and discarding the answer.
/// The same fallback, for the same reason, as Sts2SpineGeoClipProbeSuppressionTests.</para>
/// </summary>
public sealed class Sts2RewardClaimRefusalTests
{
    private const string RewardCommitSource = "bridge-mod/src/Spirectl.Sts2/Live/Sts2ActionHandler.RewardCommit.cs";

    [Fact]
    public void EveryRewardSelectIsAwaitedSoTheGamesReceivedAnswerIsObserved()
    {
        var lines = ReadRewardCommitSource();

        var offenders = new List<string>();
        var selects = 0;
        for (var index = 0; index < lines.Length; index += 1)
        {
            // Calls only. The file's header comment names Reward.SelectUnsynchronized() while explaining which
            // primitive each reward kind commits through; prose has nothing to await.
            if (!lines[index].Contains("SelectUnsynchronized(", StringComparison.Ordinal)
                || lines[index].TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            selects += 1;

            // Fire-and-forget is the bug: the returned Task<bool> binds happily to any `Task` parameter, so the
            // answer is discarded without a compiler complaint.
            if (!lines[index].Contains("await ", StringComparison.Ordinal))
            {
                offenders.Add($"{RewardCommitSource}:{index + 1}: {lines[index].Trim()}");
            }
        }

        // A rename that emptied the file of calls would satisfy Assert.Empty vacuously.
        Assert.True(selects >= 1, "expected the immediate-reward claim to still call Reward.SelectUnsynchronized; "
            + $"found {selects} call(s).");
        Assert.Empty(offenders);
    }

    [Fact]
    public void TheRowRetireSitsBehindTheRefusalGuard()
    {
        var body = ReadClaimHelperBody();

        var select = IndexOfLineContaining(body, "await commit.Reward.SelectUnsynchronized()");
        var guard = IndexOfLineContaining(body, "if (!received)");
        var earlyReturn = IndexOfLineContaining(body, "return;");
        var hostRetire = IndexOfLineContaining(body, "\"RewardCollectedFrom\"");
        var capturedRetire = IndexOfLineContaining(body, "Sts2RewardCaptureRegistry.Consume(");

        // The select is observed, the refusal leaves early, and BOTH retire branches — the native screen row and
        // the captured browser overlay — are downstream of that early return.
        Assert.True(select >= 0, "expected ClaimImmediateRewardThenRetire to await Reward.SelectUnsynchronized.");
        Assert.True(guard > select, "expected the refusal guard to read the awaited result.");
        Assert.True(earlyReturn > guard, "expected the refusal guard to return without retiring the row.");
        Assert.True(hostRetire > earlyReturn, "expected the host-seat RewardCollectedFrom retire to be gated on the "
            + "game having accepted the reward.");
        Assert.True(capturedRetire > earlyReturn, "expected the captured-seat Consume retire to be gated on the "
            + "game having accepted the reward.");
    }

    private static int IndexOfLineContaining(IReadOnlyList<string> lines, string needle)
    {
        for (var index = 0; index < lines.Count; index += 1)
        {
            if (lines[index].Contains(needle, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    // The helper's own lines, from its signature to the `    }` that closes it at member indentation. Scoping to
    // the method matters: FinalizeRewardCommit later in the same file contains the same two retire calls, so a
    // whole-file ordering check would pass on the strength of the wrong method.
    private static IReadOnlyList<string> ReadClaimHelperBody()
    {
        var lines = ReadRewardCommitSource();
        var start = IndexOfLineContaining(lines, "private async Task ClaimImmediateRewardThenRetire(");
        Assert.True(start >= 0, $"expected ClaimImmediateRewardThenRetire in {RewardCommitSource}.");

        for (var index = start + 1; index < lines.Length; index += 1)
        {
            if (lines[index] == "    }")
            {
                return lines[start..(index + 1)];
            }
        }

        throw new InvalidOperationException($"Could not find the end of ClaimImmediateRewardThenRetire in {RewardCommitSource}.");
    }

    private static string[] ReadRewardCommitSource()
        => File.ReadAllLines(Path.Combine(FindRepositoryRoot(), RewardCommitSource));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "spirectl.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find repository root.");
    }
}
