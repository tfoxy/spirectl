namespace Spirectl.Sts2;

public static class SpirectlSts2Runtime
{
    public const int ApiVersion = 3;

    /// <summary>
    /// The shape version of the asset payloads this runtime returns — the number an embedder that CACHES asset
    /// bytes should put in its cache key.
    /// </summary>
    /// <remarks>
    /// <para>BUMP THIS whenever a change alters the bytes or the shape a consumer would keep: a new or removed
    /// field on <c>EmbeddableAssetPayload</c> / <c>EmbeddableAssetFrame</c>, a different default encoder, format
    /// or content type, a change to frame placement or clip-rect semantics, or any change to what a given key
    /// renders. Do NOT bump it for something a cache cannot observe (an internal refactor, a new request option
    /// whose default is byte-identical, a comment).</para>
    ///
    /// <para>Independent of <see cref="ApiVersion"/> on purpose: the transport contract and the picture bytes
    /// move for different reasons, and an embedder invalidating its cache on API version would throw away a
    /// warm cache for an unrelated RPC change.</para>
    ///
    /// <para>Seeded at 13 to match the cache generation the first adopting embedder had already reached by
    /// hand-counting spirectl's payload changes, so adopting the constant invalidates nothing. It is pinned by
    /// <c>AssetPayloadShapesArePinnedToAssetPayloadVersion</c>, which fails when a payload shape changes without
    /// this number moving.</para>
    /// </remarks>
    public const int AssetPayloadVersion = 13;
}
