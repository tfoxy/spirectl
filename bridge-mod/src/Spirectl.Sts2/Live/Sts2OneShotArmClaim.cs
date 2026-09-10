namespace Spirectl.Sts2.Live;

// PROCESS-GLOBAL, CROSS-ASSEMBLY-IDENTITY single-arm claim for the env-gated one-shot subsystems (the spine
// geometry probe, the spine geoclip baker, and the tween JSONL recorder). Pure decision + thin impure wrapper,
// like Sts2StreamSkipMeta: the STRING key is the whole contract, and the truth table is unit-testable without a game.
//
// THE PROBLEM. The STS2 mod loadout can load the `spirectlbridge` mod and the CouchCoop mod at the same time, and
// CouchCoop embeds its OWN copy of this runtime under a DIFFERENT assembly identity — an MSBuild
// `AssemblyName=CouchCoop.Spirectl` override on a ProjectReference built with `EnableSts2LiveHost=true`. Reusable
// tween-recorder hooks can therefore still be installed by both identities. The env-armed geometry probe and
// geoclip bake are now standalone-host-owned, so the embedded Couch runtime does not install either one; their
// process-wide claims remain to protect repeated standalone initialization under distinct assembly identities.
//
// WHY THE EXISTING GUARDS DO NOT FIRE. Each subsystem already has a `private static bool _installed` behind a lock.
// A static is PER-ASSEMBLY-IDENTITY, so the two copies have two independent latches and each happily arms once.
// That per-copy behaviour is load-bearing elsewhere (each copy needs its own hint hub, its own dispatcher), so it
// must not be "fixed" generally — the ONE thing that has to be shared is the ANSWER TO "has somebody already armed
// this?", and that answer needs a slot neither copy owns.
//
// WHY AppDomain. `AppDomain.CurrentDomain` lives in System.Private.CoreLib, which is loaded exactly once per
// process and is shared by every AssemblyLoadContext, so its Get/SetData dictionary is the one storage both copies
// can address. They can only address the SAME entry if they agree on the key by VALUE, which is why the key is a
// `const string` and not a Type: `typeof(Sts2SpineGeoClipBaker)` is a different type object in each copy, and so is
// `typeof(Sts2OneShotArmClaim)` — only the string literal is identical across the identity split.
//
// REJECTED ALTERNATIVES (recorded so nobody re-litigates them):
//   * An ENV VAR claim is wrong. CouchCoop's HeadlessClientManager copies this process's environment into every
//     headless seat it spawns, so a claim written to the environment would travel into a child process and
//     silently disarm a seat that was legitimately armed on its own.
//   * A NAMED MUTEX is the wrong scope. It is machine-wide, so a second, unrelated game instance baking to a
//     different _OUT directory would stand down for no reason. The hazard is per-PROCESS: one RID space, one
//     output directory.
//   * Godot `SetMeta` on the SceneTree root is unavailable — a mod's Init runs before there is a SceneTree.
//
// KNOWN, DELIBERATELY UNTOUCHED. Sts2GodotMainThreadPump.Install has the same root cause (two copies, two
// SpirectlMainThreadDispatcher timers) and is NOT claimed here: an embedded runtime genuinely needs its own
// dispatcher, so a single-arm claim would break the loser rather than de-duplicate it.
//
// COST WHEN UNARMED: zero. Every call site keeps its own empty-spec early return ABOVE the claim, so a host that
// did not arm anything still pays exactly one environment read per subsystem and never reaches this file.
internal static class Sts2OneShotArmClaim
{
    // The claim slot's key prefix, joined with the subsystem name. This STRING is the cross-assembly contract:
    // both copies compile the same literal, so both address the same AppDomain entry. Never rename it without
    // renaming it in every copy at once — which, since CouchCoop ships this file UNMODIFIED under a different
    // assembly name, means never renaming it in a release the two copies can straddle.
    internal const string ClaimKeyPrefix = "spirectl.one-shot-arm/";

    // The subsystem names, named once. Same reason as the prefix: the STRING is the cross-assembly contract, so
    // a call site that spells it inline is a rename waiting to silently un-claim one copy — and a test that
    // spells it inline is a test that can pass while production uses a different slot.

    /// <summary>The env-armed geoclip baker (<c>SPIRECTL_SPINE_GEOCLIP_BAKE</c>).</summary>
    internal const string SpineGeoClipBakeSubsystem = "spine-geoclip-bake";

    /// <summary>The env-armed spine geometry probe (<c>SPIRECTL_SPINE_GEOMETRY_PROBE</c>).</summary>
    internal const string SpineGeometryProbeSubsystem = "spine-geometry-probe";

    // Handed to ShouldArm by TryClaim to say "my caller already checked its own spec". The spec check is the
    // caller's, not ours: each subsystem reads a different env var, and doing it here would cost an unarmed host
    // a second environment read for no gain.
    private const string SpecAlreadyChecked = "<checked-by-caller>";

    private static readonly Lock Sync = new();

    // This copy's identity, as the other copy will see it: "Spirectl.Sts2" in the bridge mod, "CouchCoop.Spirectl"
    // in the embedded copy. Read off the assembly rather than configured, because the assembly name IS the identity
    // that split the statics — there is deliberately no embedder opt-in, mod-id hand-off or COUCHCOOP_* env var to
    // get out of sync with reality. Same idiom as BridgeBuildInfo's assembly read.
    internal static string SelfOwner => typeof(Sts2OneShotArmClaim).Assembly.GetName().Name ?? "<unknown>";

    /// <summary>
    /// The whole rule, pure and therefore unit-testable without a game: given the subsystem's own arming
    /// <paramref name="spec"/>, whoever is parked in the process-global slot (<paramref name="existingOwner"/>,
    /// null when nobody is), and this copy's identity (<paramref name="selfOwner"/>), should this copy arm?
    /// </summary>
    internal static OneShotArmVerdict ShouldArm(string subsystem, string? spec, string? existingOwner, string selfOwner)
    {
        _ = subsystem;

        // Nobody asked for this subsystem at all — the unarmed host's answer, and the reason it never pays for a
        // claim slot.
        if (string.IsNullOrWhiteSpace(spec))
        {
            return OneShotArmVerdict.NotRequested;
        }

        if (string.IsNullOrEmpty(existingOwner))
        {
            return OneShotArmVerdict.Arm;
        }

        // Re-entry by the copy that already owns the claim is NOT a stand-down: it is exactly what the existing
        // per-assembly `_installed` latch means, and preserving that keeps the claim a purely ADDITIONAL outer
        // guard rather than a behaviour change for a single-copy host.
        return string.Equals(existingOwner, selfOwner, StringComparison.Ordinal)
            ? OneShotArmVerdict.AlreadyOwned(selfOwner)
            : OneShotArmVerdict.StandDown(existingOwner);
    }

    /// <summary>
    /// Impure half: read the process-global claim slot for <paramref name="subsystem"/> and park
    /// <paramref name="selfOwner"/> in it if it is free. True means "you may arm"; false means somebody already
    /// did, and <paramref name="existingOwner"/> names them (for the caller's stand-down log line).
    /// </summary>
    internal static bool TryClaim(string subsystem, string selfOwner, out string? existingOwner)
    {
        var key = ClaimKeyPrefix + subsystem;
        lock (Sync)
        {
            // Get/SetData has no compare-and-swap, so this read-then-write is not atomic on its own. It does not
            // need to be: both Install calls run sequentially on the game's main thread, from two [ModInitializer]
            // entry points, so the lock (which only guards a same-copy race) plus an ordered read/write is enough.
            var parked = AppDomain.CurrentDomain.GetData(key) as string;
            var verdict = ShouldArm(subsystem, SpecAlreadyChecked, parked, selfOwner);
            existingOwner = verdict.Owner;
            if (verdict.Decision != OneShotArmDecision.Arm)
            {
                return false;
            }

            AppDomain.CurrentDomain.SetData(key, selfOwner);
            return true;
        }
    }
}

/// <summary>What <see cref="Sts2OneShotArmClaim.ShouldArm"/> decided.</summary>
internal enum OneShotArmDecision
{
    /// <summary>The subsystem was not armed at all (empty spec), so there is nothing to claim.</summary>
    NotRequested,

    /// <summary>The slot was free: this copy takes it and does the work.</summary>
    Arm,

    /// <summary>Another copy owns the slot: this copy does nothing and says who won.</summary>
    StandDown,
}

/// <summary>
/// A decision plus the owner it was made against. <see cref="Owner"/> is null only when nobody is parked in the
/// slot; on a stand-down it names the copy that won, which is the whole content of the log line the caller emits.
/// </summary>
internal readonly record struct OneShotArmVerdict(OneShotArmDecision Decision, string? Owner)
{
    internal static readonly OneShotArmVerdict NotRequested = new(OneShotArmDecision.NotRequested, null);

    internal static readonly OneShotArmVerdict Arm = new(OneShotArmDecision.Arm, null);

    // Idempotent re-entry by the current owner. Reported as NotRequested — there is no work left to request — but
    // it still carries the owner so a caller that logs the verdict names itself rather than "<none>".
    internal static OneShotArmVerdict AlreadyOwned(string selfOwner)
        => new(OneShotArmDecision.NotRequested, selfOwner);

    internal static OneShotArmVerdict StandDown(string existingOwner)
        => new(OneShotArmDecision.StandDown, existingOwner);
}
