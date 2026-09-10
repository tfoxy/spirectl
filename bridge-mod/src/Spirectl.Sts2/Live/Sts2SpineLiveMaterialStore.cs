namespace Spirectl.Sts2.Live;

// Godot-free core of the LIVE SPINE-MATERIAL CARRY (round-9 boss-map-node fade). Always compiled (no Godot
// dependency) so the record/resolve/eviction contract is offline-unit-testable, like Sts2SpineMaterialKey (which
// computes the signature this store is keyed by).
//
// WHY THIS EXISTS. A SpineSprite can paint through a `normal_material` ShaderMaterial the game re-parameterises at
// RUNTIME: `NBossMapPoint.RefreshColorInstantly` pushes the current act's MapBgColor / MapTraveled|UntraveledColor
// into `res://shaders/boss_map_point.gdshader` (an R/G/B channel-remap mask). Round-8 taught the bake to apply that
// material, but it read the material off an OFFLINE `PackedScene.Instantiate()` of the addressed scene — and
// `boss_map_point.tscn` marks its ShaderMaterial sub-resource `resource_local_to_scene = true`, so every
// instantiation gets its OWN copy carrying the AUTHORED uniforms, never the live tint. Meanwhile the `&mat=` cache
// key hashes the LIVE node's uniform values (Sts2SpineInspector.ReadMaterialKey). Key tracked live, render used
// authored: the game showed one flat act-tinted silhouette while both mirrors painted the raw three-tone mask
// (tan head + black body + white outline).
//
// THE CARRY. The inspector already reads the live (shader path, uniform values) pair to compute the signature, so it
// records that exact snapshot here under that exact signature. The bake — which receives the signature verbatim in
// the clip URL's `&mat=` selector — resolves it and renders through a material built from the LIVE values. Because
// the key IS the hash of the recorded content, a hit is correct BY CONSTRUCTION: it is impossible to render one
// tint under another tint's cache key.
//
// BOUNDED. Signatures are minted per distinct uniform value-set, capped per node by
// Sts2SpineMaterialKey.MaxSignatureChanges; shipped shader materials change on state transitions (act / travel
// state), not per frame. The store keeps the most recent `capacity` signatures and drops the oldest, so a
// hypothetical churny material costs a fixed amount of memory and merely degrades the oldest identity back to
// today's offline-material behaviour.
internal sealed class Sts2SpineLiveMaterialStore<TValue>
{
    // Comfortably more than the handful of distinct shaded-spine identities a session produces (one boss map point
    // per act × travel state), small enough that even sampler-valued uniforms cannot pin meaningful memory.
    internal const int DefaultCapacity = 64;

    /// <summary>A shader address plus the uniform name/value pairs the LIVE node carried for it.</summary>
    internal sealed record Snapshot(string ShaderResPath, IReadOnlyList<(string Name, TValue Value)> Uniforms);

    private readonly object _gate = new();
    private readonly Dictionary<string, Snapshot> _bySignature = new(StringComparer.Ordinal);
    private readonly Queue<string> _insertionOrder = new();
    private readonly int _capacity;

    public Sts2SpineLiveMaterialStore(int capacity = DefaultCapacity) => _capacity = Math.Max(1, capacity);

    /// <summary>How many distinct signatures are currently resolvable (diagnostics + the eviction test).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _bySignature.Count;
            }
        }
    }

    /// <summary>
    /// Remember the uniform snapshot a signature was computed from. Ignored (returns false) for a blank signature /
    /// shader path or an empty uniform list — those cannot address anything, and a signature with no uniforms
    /// carries no information the shader path does not already carry. Re-recording a known signature is a no-op:
    /// equal signatures mean equal content, so the stored snapshot is already the right one (and its eviction
    /// position must not be disturbed by a per-tick re-read).
    /// </summary>
    public bool Record(string? signature, string? shaderResPath, IReadOnlyList<(string Name, TValue Value)>? uniforms)
    {
        if (string.IsNullOrWhiteSpace(signature)
            || string.IsNullOrWhiteSpace(shaderResPath)
            || uniforms is null
            || uniforms.Count == 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (_bySignature.ContainsKey(signature))
            {
                return true;
            }

            _bySignature[signature] = new Snapshot(shaderResPath, uniforms);
            _insertionOrder.Enqueue(signature);
            while (_insertionOrder.Count > _capacity && _insertionOrder.TryDequeue(out var oldest))
            {
                _bySignature.Remove(oldest);
            }

            return true;
        }
    }

    /// <summary>
    /// The snapshot a signature was computed from, or null when it was never recorded / has been evicted (the
    /// caller then falls back to the offline material — today's behaviour).
    /// </summary>
    public Snapshot? Resolve(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            return null;
        }

        lock (_gate)
        {
            return _bySignature.GetValueOrDefault(signature);
        }
    }
}
