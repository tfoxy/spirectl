namespace Spirectl.TestSymbols.Gameplay
{
    public interface IInspectable
    {
        string Inspect();
    }

    public interface ICombatHook
    {
        void OnCombatOpened(DeckController deck);
    }

    public interface IDeckHook : ICombatHook
    {
        void OnDeckChanged(DeckController deck);
    }

    public abstract class BaseController
    {
        public abstract string ControllerId { get; }
    }

    public abstract class CombatHookBase
    {
        public virtual void OnCombatOpened(DeckController deck)
        {
        }
    }

    public sealed class DeckController : BaseController, IInspectable
    {
        public sealed class DeckSnapshot
        {
            public DeckSnapshot(int count)
            {
                Count = count;
            }

            public int Count { get; }

            public string Label => $"cards:{Count}";
        }

        public const int MaxHandSize = 10;

        private readonly List<string> _drawnCards = [];
        private int _lastDrawAmount;

        public override string ControllerId => "deck";

        public int DrawCount { get; private set; }

        public string? LastCard { get; private set; }

        public event EventHandler? DeckChanged;

        public void DrawCard()
        {
            _lastDrawAmount = 1;
            DrawCount += 1;
            LastCard = "unknown";
            _drawnCards.Add(LastCard);
            DeckChanged?.Invoke(this, EventArgs.Empty);
        }

        public void DrawCard(int amount)
        {
            _lastDrawAmount = amount;
            DrawCount += amount;
            LastCard = $"draw:{amount}";
            _drawnCards.Add(LastCard);
            DeckChanged?.Invoke(this, EventArgs.Empty);
        }

        public static string DescribeCard(string name, int cost)
        {
            return $"{name}:{cost}";
        }

        public DeckSnapshot CreateSnapshot()
        {
            return new DeckSnapshot(DrawCount + _lastDrawAmount);
        }

        public string Inspect()
        {
            return $"{ControllerId}:{DrawCount}";
        }
    }

    public sealed class HandController : BaseController, IInspectable, ICombatHook
    {
        public override string ControllerId => "hand";

        public int SeenCards { get; private set; }

        public void OnCombatOpened(DeckController deck)
        {
            SeenCards += deck.DrawCount;
        }

        public string Inspect()
        {
            return $"{ControllerId}:{SeenCards}";
        }
    }

    public sealed class DeckAuditHook : IDeckHook
    {
        public string LastMessage { get; private set; } = "none";

        public void OnCombatOpened(DeckController deck)
        {
            LastMessage = deck.Inspect();
        }

        public void OnDeckChanged(DeckController deck)
        {
            LastMessage = DeckController.DescribeCard(deck.ControllerId, deck.DrawCount);
        }
    }

    public sealed class CombatDeckHook : CombatHookBase, IDeckHook
    {
        public override void OnCombatOpened(DeckController deck)
        {
            deck.DrawCard();
        }

        public void OnDeckChanged(DeckController deck)
        {
            deck.DrawCard(deck.DrawCount + 1);
        }
    }

    public sealed class ModdingNavigator
    {
        public string Run(DeckController deck, HandController hand)
        {
            deck.DrawCard();
            hand.OnCombatOpened(deck);
            var snapshot = deck.CreateSnapshot();
            return DeckController.DescribeCard(snapshot.Label, deck.DrawCount);
        }
    }
}

namespace Spirectl.TestSymbols.Ui
{
    public sealed class CombatScreenPresenter
    {
        public string Title => "Combat";

        public void Open()
        {
        }
    }
}
