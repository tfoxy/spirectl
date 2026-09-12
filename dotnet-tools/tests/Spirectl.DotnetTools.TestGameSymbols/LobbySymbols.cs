namespace Spirectl.TestGame.Lobby;

/// <summary>A record type that the reshaped build removes outright.</summary>
public struct SeatRecord
{
    public int Id;

    public bool IsReady;

    public SeatRecord(int id, bool isReady)
    {
        Id = id;
        IsReady = isReady;
    }

    public string Render()
    {
        return IsReady ? $"seat {Id} ready" : $"seat {Id}";
    }
}

/// <summary>A type the reshaped build keeps while dropping three of its members.</summary>
public sealed class SeatRegistry
{
    public bool IsOpen;

    private readonly List<int> _seats = [];

    public string Label { get; set; } = "lobby";

    public int SeatCount => _seats.Count;

    public void Open()
    {
        IsOpen = true;
    }

    public void Close()
    {
        IsOpen = false;
    }
}

/// <summary>A hook whose parameter list grows in the reshaped build.</summary>
public static class DamageHooks
{
    public static int ModifyDamage(int amount, int sourceId)
    {
        return amount + sourceId;
    }
}

/// <summary>An animation seam whose return value disappears in the reshaped build.</summary>
public sealed class AnimationTrack
{
    public TrackHandle Play(string animation, bool loop)
    {
        return new TrackHandle { Order = loop ? animation.Length : 0 };
    }
}

/// <summary>Unchanged in the reshaped build, so it anchors the "nothing moved here" case.</summary>
public sealed class TrackHandle
{
    public int Order;
}
