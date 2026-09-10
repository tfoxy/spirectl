namespace Spirectl.Sts2.Live;

public sealed record Sts2SelectedCardViewEntry(string CardId, string PlayerId);

public sealed class Sts2SelectedCardViewState
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Sts2SelectedCardViewEntry> _selectedByPlayerId = new(StringComparer.Ordinal);

    public Sts2SelectedCardViewEntry? Get(string? playerId)
    {
        if (string.IsNullOrWhiteSpace(playerId))
        {
            return null;
        }

        lock (_gate)
        {
            return _selectedByPlayerId.GetValueOrDefault(playerId);
        }
    }

    // Idempotent assignment for authored setup (fixture loads): unlike Toggle,
    // reloading the same fixture must not flip the selection off.
    public void Set(string playerId, string cardId)
    {
        lock (_gate)
        {
            _selectedByPlayerId[playerId] = new Sts2SelectedCardViewEntry(cardId, playerId);
        }
    }

    public Sts2SelectedCardViewEntry? Toggle(string playerId, string cardId)
    {
        lock (_gate)
        {
            if (_selectedByPlayerId.TryGetValue(playerId, out var selected)
                && string.Equals(selected.CardId, cardId, StringComparison.Ordinal))
            {
                _selectedByPlayerId.Remove(playerId);
                return null;
            }

            var next = new Sts2SelectedCardViewEntry(cardId, playerId);
            _selectedByPlayerId[playerId] = next;
            return next;
        }
    }

    public void ClearIf(string? playerId, string cardId)
    {
        if (string.IsNullOrWhiteSpace(playerId))
        {
            return;
        }

        lock (_gate)
        {
            if (_selectedByPlayerId.TryGetValue(playerId, out var selected)
                && string.Equals(selected.CardId, cardId, StringComparison.Ordinal))
            {
                _selectedByPlayerId.Remove(playerId);
            }
        }
    }
}
