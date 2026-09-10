using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Core.State;

namespace Spirectl.Sts2.Live;

public sealed class Sts2StateProvider : IStateProvider
{
    private readonly RunShellStateBuilder _runBuilder;

    public Sts2StateProvider(Sts2SelectedCardViewState? selectedCardViewState = null)
    {
        _runBuilder = new RunShellStateBuilder(selectedCardViewState ?? new Sts2SelectedCardViewState());
    }

    public StateSnapshot Observe(PlayerPerspective perspective)
        => Sts2MainThreadDispatcher.Invoke(() => ObserveOnMainThread(perspective));

    private StateSnapshot ObserveOnMainThread(PlayerPerspective perspective)
    {
        var run = _runBuilder.ResolveRun(perspective);
        var rootScene = ResolveRootScene(run);
        var screenObject = Sts2LiveIntrospection.ResolveCurrentScreenObject();
        return Envelope(rootScene, CharacterSelectStateBuilder.ResolveCharacterSelect(screenObject, perspective), run);
    }

    // Both lobby kinds project into the shared StateCharacterSelect surface: the start-run
    // NCharacterSelectScreen (begin a new co-op run) and the load-run NMultiplayerLoadGameScreen
    // (resume a saved co-op run). The catalog binds them through the same characterSelect.*
    // shape, so a saved-lobby fixture renders without a save file on disk.
    private static string ResolveRootScene(StateRunSnapshot? run)
    {
        if (run is not null)
        {
            return "run";
        }

        var screenObject = Sts2LiveIntrospection.ResolveCurrentScreenObject();
        if (Sts2SupportedScreenIds.IsStartRunLobbyScreen(screenObject))
        {
            return "screens/character_select_screen";
        }

        if (Sts2SupportedScreenIds.IsLoadRunLobbyScreen(screenObject))
        {
            return "screens/multiplayer_load_game_screen";
        }

        return Sts2SupportedScreenIds.TryResolve(screenObject, out var match)
            && Sts2SupportedScreenIds.IsLobbyScreenType(match.ScreenType)
            ? "screens/character_select_screen"
            : "screens/main_menu";
    }

    private static StateSnapshot Envelope(
        string rootScene,
        StateCharacterSelectSnapshot? characterSelect,
        StateRunSnapshot? run)
        => new(
            StateSnapshot.CurrentSchemaVersion,
            StateProjectionValues.CurrentLanguage(),
            rootScene,
            characterSelect,
            run);

    // Internal callers retain these entrypoints while their projection helpers have a focused owner.
    internal static StateHandSelectionViewSnapshot? ResolveHandSelectionView(ICollection<StateNoticeSnapshot> notices, string? playerId)
        => RunShellStateBuilder.ResolveHandSelectionView(notices, playerId);
    internal static string Slug(string? value) => StateProjectionValues.Slug(value);
    internal static string? ResolveModelId(object? model) => StateProjectionValues.ResolveModelId(model);
    internal static int ToInt32(object? value) => StateProjectionValues.ToInt32(value);
}
