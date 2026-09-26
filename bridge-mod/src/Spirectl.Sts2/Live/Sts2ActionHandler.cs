using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using Godot;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Protocol;
using Spirectl.Sts2.Core.State;
using Spirectl.Sts2;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace Spirectl.Sts2.Live;

public sealed partial class Sts2ActionHandler : IActionHandler
{
    private readonly Sts2ScreenLocator _screenLocator;
    private readonly ILogStream _logStream;
    private readonly Sts2SelectedCardViewState _selectedCardViewState;

    public Sts2ActionHandler(
        Sts2ScreenLocator screenLocator,
        ILogStream logStream,
        Sts2SelectedCardViewState? selectedCardViewState = null)
    {
        _screenLocator = screenLocator;
        _logStream = logStream;
        _selectedCardViewState = selectedCardViewState ?? new Sts2SelectedCardViewState();
    }

    public ActionExecutionResult Execute(SemanticActionRequest request)
    {
        return Sts2MainThreadDispatcher.Invoke(() => ExecuteOnMainThread(request));
    }
}
