using System;
using System.Threading;
using System.Threading.Tasks;

namespace Spirectl.Sts2.Live;

/// <summary>
/// Runtime-settable CPU budget for offline image ENCODES (Spine bake frames and single-image scene renders alike).
/// Godot-free (arithmetic, a volatile int and one <see cref="SemaphoreSlim"/>) so the policy is offline-unit-testable,
/// like <see cref="Sts2SpineDefaults"/>.
/// </summary>
/// <remarks>
/// <para>
/// WHY: an asset bake renders on the Godot MAIN thread and then encodes what it captured. The encode fan-out used to
/// be <c>max(2, ProcessorCount)</c> — i.e. it assumed the whole machine. That is wrong whenever several GAME
/// INSTANCES share the box (couch co-op runs one headless STS2 per browser seat, each with its own render/physics/
/// ENet threads): the encoders then oversubscribe every core, the game loops miss their frame budget, and an ENet
/// peer can drop. The mod publishes how many instances are alive via <see cref="GameInstances"/> and the encoders
/// leave that many cores alone.
/// </para>
/// <para>
/// The gate is PROCESS-WIDE rather than per-bake. Each bake minting its own semaphore meant the budget only ever
/// capped one bake's internal fan-out: two concurrent bakes — or a bake overlapping a background render, now that
/// the single-image lane encodes off-thread too — could each run <see cref="EncodeThreads"/> encoders and together
/// oversubscribe exactly the cores the budget exists to protect. One shared gate makes the number mean what it says
/// across the whole process.
/// </para>
/// <para>
/// This is a THROUGHPUT knob only: lowering it makes an encode wait longer, never produce different bytes. The floor
/// of 1 keeps the pipeline alive on any core count. Re-sizing is best-effort — an encode already holding a lease on
/// the previous gate keeps it, so the process can briefly exceed a freshly lowered budget rather than deadlock.
/// </para>
/// </remarks>
public static class Sts2RenderEncodeBudget
{
    /// <summary>
    /// Opt-OUT switch (<c>=0</c>) for encoding a SINGLE-IMAGE render off the main thread. Default ON.
    /// </summary>
    /// <remarks>
    /// Two reasons it exists. It is a behaviour change on the shipped <c>/bg/</c> path (same bytes, different
    /// thread), so there has to be a way to put the render back the way it was without a rebuild. And it is what
    /// makes the win MEASURABLE: with it, one build can produce both halves of the before/after, so the comparison
    /// is not two builds with two different sets of everything else. The frame encoders of a clip bake are NOT
    /// covered — they have always run off-thread.
    /// </remarks>
    public const string OffloadEnvVar = "SPIRECTL_RENDER_ENCODE_OFFLOAD";

    private static readonly bool OffloadSingleImageEncodes =
        ResolveOffloadEnabled(Environment.GetEnvironmentVariable(OffloadEnvVar));

    /// <summary>Whether a single-image render hands its encode to a worker thread. Read once at startup.</summary>
    public static bool OffloadEnabled => OffloadSingleImageEncodes;

    /// <summary>
    /// The opt-out rule as a PURE function of the env value, so "absent means ON" is a test rather than a comment.
    /// <see cref="OffloadEnabled"/> reads its variable once at startup (the flag has to mean the same thing for
    /// every render in a process), which leaves the default itself unassertable from a test — this is the seam.
    /// </summary>
    /// <remarks>
    /// Only the exact string <c>0</c> disables it. Deliberately narrower than the geoclip levers' word lists: this
    /// switch exists to put a shipped render path back the way it was, so a value nobody recognises must leave the
    /// shipped behaviour in place rather than silently revert it.
    /// </remarks>
    internal static bool ResolveOffloadEnabled(string? rawEnvValue) => rawEnvValue != "0";

    private static int _gameInstances = 1;

    private static readonly object GateLock = new();
    private static SemaphoreSlim _gate = new(ComputeEncodeThreads(Environment.ProcessorCount, 1));
    private static int _gateThreads = ComputeEncodeThreads(Environment.ProcessorCount, 1);

    /// <summary>
    /// How many STS2 game instances are currently alive on this machine, INCLUDING this one (so the floor is 1).
    /// Set by the host mod as co-op seats come and go; never read by the game itself.
    /// </summary>
    public static int GameInstances
    {
        get => Volatile.Read(ref _gameInstances);
        set => Volatile.Write(ref _gameInstances, Math.Max(1, value));
    }

    /// <summary>The current encode fan-out: <c>max(1, ProcessorCount - GameInstances)</c>.</summary>
    public static int EncodeThreads => ComputeEncodeThreads(Environment.ProcessorCount, GameInstances);

    /// <summary>Pure form of <see cref="EncodeThreads"/> (the unit-tested seam).</summary>
    public static int ComputeEncodeThreads(int processorCount, int gameInstances)
        => Math.Max(1, Math.Max(1, processorCount) - Math.Max(1, gameInstances));

    /// <summary>How many encode slots the CURRENT shared gate was built with (diagnostics and tests).</summary>
    public static int CurrentGateThreads
    {
        get
        {
            lock (GateLock)
            {
                return _gateThreads;
            }
        }
    }

    /// <summary>
    /// Take one encode slot from the process-wide gate; dispose the lease to give it back.
    /// </summary>
    /// <remarks>
    /// The uncontended path completes SYNCHRONOUSLY (<see cref="SemaphoreSlim.WaitAsync()"/> hands back an
    /// already-completed task, which an <c>await</c> resumes inline), so a caller on the Godot main thread pays no
    /// scheduling hop for a slot that was free — which matters because the clip lane acquires once per frame. Only
    /// a genuinely contended acquire costs a hop back onto the caller's context, the correct price for waiting.
    /// </remarks>
    public static async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        var gate = CurrentGate();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(gate);
    }

    /// <summary>Blocking form of <see cref="AcquireAsync"/>, for an encode running on a worker thread already.</summary>
    public static IDisposable Acquire(CancellationToken cancellationToken = default)
    {
        var gate = CurrentGate();
        gate.Wait(cancellationToken);
        return new Lease(gate);
    }

    /// <summary>
    /// The gate sized for the CURRENT budget, rebuilding it when <see cref="EncodeThreads"/> has moved. Leases
    /// already taken against the old gate release into the old gate (see the best-effort note above), so a resize
    /// never strands a waiter or over-releases.
    /// </summary>
    private static SemaphoreSlim CurrentGate()
    {
        var threads = EncodeThreads;
        lock (GateLock)
        {
            if (threads != _gateThreads)
            {
                // The previous gate is simply dropped rather than disposed: a SemaphoreSlim that was never asked
                // for its WaitHandle holds no unmanaged resource, and disposing one under an in-flight lease would
                // turn that lease's Release into an ObjectDisposedException.
                _gate = new SemaphoreSlim(threads);
                _gateThreads = threads;
            }

            return _gate;
        }
    }

    /// <summary>Rebuild the shared gate from the current budget (test isolation; the host never needs this).</summary>
    internal static void ResetGate()
    {
        lock (GateLock)
        {
            _gateThreads = EncodeThreads;
            _gate = new SemaphoreSlim(_gateThreads);
        }
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
