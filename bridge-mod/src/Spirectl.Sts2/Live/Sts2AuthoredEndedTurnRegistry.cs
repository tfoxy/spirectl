namespace Spirectl.Sts2.Live;

/// <summary>
/// Fixture-authored "this seat has ended its turn" intent, keyed by player net id.
///
/// FALLBACK path for authored ended turns: the fixture loader normally drives the REAL
/// ready state via <c>PlayerCmd.EndTurn(canBackOut: true)</c> (safe in multi-seat
/// single-machine combats thanks to <c>Sts2EndTurnReadinessHooks</c>). When that hook is
/// unavailable, or readying the authored seats would leave no seat still playing (the
/// vanilla singleplayer branch then resolves the whole turn the moment one seat readies),
/// the loader records the intent here instead, without touching the live turn;
/// <c>Sts2StateProvider.ResolvePlayerHasEndedTurn</c> reports the seat as ended and the
/// presentation marks its cards unplayable. Reset on every combat load.
/// </summary>
internal static class Sts2AuthoredEndedTurnRegistry
{
    private static readonly HashSet<ulong> EndedNetIds = [];

    public static void Reset() => EndedNetIds.Clear();

    public static void Mark(ulong netId) => EndedNetIds.Add(netId);

    public static bool Contains(ulong netId) => EndedNetIds.Contains(netId);
}
