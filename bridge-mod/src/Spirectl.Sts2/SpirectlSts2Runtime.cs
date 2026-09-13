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
    /// <para>Starts at 1, with the first public release. It briefly carried a much larger number, inherited from
    /// a generation count the first adopting embedder had been keeping by hand before this constant existed; that
    /// count described changes nobody outside this repository ever saw, so it was reset rather than published.
    /// The number is only ever compared for equality against a value a cache stored, so where it starts does not
    /// matter — only that it moves when the bytes do.</para>
    ///
    /// <para>It is pinned by <c>AssetPayloadShapesArePinnedToAssetPayloadVersion</c>, which fails when a payload
    /// shape changes without this number moving.</para>
    /// </remarks>
    public const int AssetPayloadVersion = 1;
}
