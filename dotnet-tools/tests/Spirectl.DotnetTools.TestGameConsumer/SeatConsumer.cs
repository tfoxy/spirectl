using Spirectl.TestGame.Lobby;

namespace Spirectl.TestGameConsumer;

/// <summary>
/// Binds one member of the control game surface per verification bucket, so the emitted
/// <c>TypeRef</c>/<c>MemberRef</c> tables are the fixture the reference-verification tests read.
/// </summary>
public static class SeatConsumer
{
    public static string Summarize()
    {
        var seat = new SeatRecord(7, isReady: true);
        var registry = new SeatRegistry();
        registry.Open();
        registry.Close();
        var isOpen = registry.IsOpen;
        var label = registry.Label;
        var seatCount = registry.SeatCount;
        var damage = DamageHooks.ModifyDamage(3, 4);
        var handle = new AnimationTrack().Play("idle", loop: true);

        return string.Join(
            "/",
            seat.Id,
            seat.IsReady,
            seat.Render(),
            isOpen,
            label,
            seatCount,
            damage,
            handle.Order);
    }
}
