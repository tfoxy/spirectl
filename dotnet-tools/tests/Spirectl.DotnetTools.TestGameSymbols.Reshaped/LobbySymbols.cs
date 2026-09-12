namespace Spirectl.TestGame.Lobby;

// The same namespace and type names as the control build, minus SeatRecord and with four
// deliberate breaks. Each difference below is asserted by ReferenceVerification tests, so keep the
// comments and the surface in step when editing either side.

/// <summary>
/// Kept, but <c>IsOpen</c>, <c>Label</c> and <c>Close</c> are gone: three members whose owner type
/// survives.
/// </summary>
public sealed class SeatRegistry
{
    private readonly List<int> _seats = [];

    public int SeatCount => _seats.Count;

    public void Open()
    {
        _seats.Add(_seats.Count);
    }
}

/// <summary>Gained a third parameter.</summary>
public static class DamageHooks
{
    public static int ModifyDamage(int amount, int sourceId, int multiplier)
    {
        return (amount + sourceId) * multiplier;
    }
}

/// <summary>Dropped its return value.</summary>
public sealed class AnimationTrack
{
    public void Play(string animation, bool loop)
    {
        _ = loop ? animation.Length : 0;
    }
}

/// <summary>Unchanged.</summary>
public sealed class TrackHandle
{
    public int Order;
}
