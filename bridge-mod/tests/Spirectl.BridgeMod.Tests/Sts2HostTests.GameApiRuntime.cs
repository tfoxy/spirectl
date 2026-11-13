#if ENABLE_STS2_LIVE_HOST
extern alias Sts2Live;

using Spirectl.Sts2;
using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Perspective;
using Spirectl.Sts2.Live;
using Spirectl.Sts2.Live.GameApi;
using Xunit;
#if STS2_API_V107
using LobbyPlayer = Sts2Live::MegaCrit.Sts2.Core.Entities.Multiplayer.LobbyPlayer;
#else
using LobbyPlayer = Sts2Live::MegaCrit.Sts2.Core.Entities.Multiplayer.StartRunLobbyPlayer;
#endif
#if STS2_API_V113
using CardSelectionScreen = Sts2Live::MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.ICardSelectionScreen;
#else
using CardSelectionScreen = Sts2Live::MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.ICardSelector;
#endif

namespace Spirectl.BridgeMod.Tests;

public sealed partial class Sts2HostTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void GameApiCancelEndTurnReportsRefusalInsteadOfSuccess(bool canUndo, bool clearsReadiness)
    {
        Assert.True(Sts2ActionCatalog.CanCancelEndTurn("combat", "p:7", true, "p:7", true));
        var handler = new Sts2ActionHandler(new Sts2ScreenLocator(), new InMemoryLogStream());
        var request = new SemanticActionRequest(
            "cancel-test", SemanticActionKind.CancelEndTurn, null, null, null, null, null, null, null, null, null, null);
        var undoCalls = 0;

        var result = handler.ExecuteCancelEndTurn(request, "p:7", () => canUndo, () =>
        {
            undoCalls++;
            return clearsReadiness;
        });

        Assert.False(result.Accepted);
        Assert.Equal(ActionFailureCode.InvalidAction, result.Error!.Code);
        var detail = Assert.Single(result.Error.Details);
        Assert.Equal("player_id", detail.Field);
        Assert.Equal("p:7", detail.Value);
        Assert.Contains("does not allow", detail.Note);
        Assert.Equal(canUndo ? 1 : 0, undoCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void GameApiCancelEndTurnDistinguishesSuccessFromMissingHook(bool? cancelled)
    {
        var handler = new Sts2ActionHandler(new Sts2ScreenLocator(), new InMemoryLogStream());
        var request = new SemanticActionRequest(
            "cancel-test", SemanticActionKind.CancelEndTurn, null, null, null, null, null, null, null, null, null, null);

        var result = handler.ExecuteCancelEndTurn(request, "p:7", () => true, () => cancelled);

        Assert.Equal(cancelled is true, result.Accepted);
        if (cancelled is null)
        {
            Assert.Equal(ActionFailureCode.MissingHook, result.Error!.Code);
        }
    }

#if STS2_API_V113
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GameApiCancelEndTurnVerifiesReadinessAfterUndo(bool remainsReady)
    {
        var undoCalls = 0;

        var cancelled = GameApiCombatActions.UndoReadyToEndTurn(() => undoCalls++, () => remainsReady);

        Assert.Equal(!remainsReady, cancelled);
        Assert.Equal(1, undoCalls);
    }
#else
    [Fact]
    public void GameApiCancelEndTurnPreservesLegacyLanePermission()
    {
        Assert.True(GameApiCombatActions.CanUndoReadyToEndTurn(null!, null!));
    }
#endif

    [Fact]
    public void GameApiLobbyUnlockCopyPreservesSeatIdentityAndReadiness()
    {
        var source = new LobbyPlayer { id = 7, slotId = 1, isReady = false };
#if STS2_API_V113
        source.maxStandardAscension = 8;
        source.maxBlitzAscension = 4;
        source.isRotatingDread = true;
        source.isModded = true;
#else
        source.maxMultiplayerAscensionUnlocked = 8;
#endif
        var target = new LobbyPlayer { id = 99, slotId = 2, isReady = true };

        GameApiLobbyPlayer.CopyUnlockState(ref target, source);

        Assert.Equal(99UL, target.id);
        Assert.Equal(2, target.slotId);
        Assert.True(target.isReady);
        Assert.Equal(8, GameApiLobbyPlayer.MaxMultiplayerAscensionUnlocked(target));
#if STS2_API_V113
        Assert.Equal(4, target.maxBlitzAscension);
        Assert.True(target.isRotatingDread);
        Assert.True(target.isModded);
#endif
    }

    [Theory]
    [InlineData("Standard", 4)]
    [InlineData("Blitz", 0)]
    public void GameApiLobbyProjectsModeAndRosterMinimumBlitzCap(string gameMode, int peerCap)
    {
        var local = new LobbyPlayer { id = 1 };
        var peer = new LobbyPlayer { id = 2 };
#if STS2_API_V113
        local.maxStandardAscension = 8;
        peer.maxStandardAscension = 6;
        local.maxBlitzAscension = 9;
        peer.maxBlitzAscension = peerCap;
#else
        local.maxMultiplayerAscensionUnlocked = 8;
        peer.maxMultiplayerAscensionUnlocked = 6;
#endif
        var screen = new MegaCrit.Sts2.Core.Nodes.Screens.Blitz.NBlitzScreen
        {
            Lobby = new BlitzLobbyStub
            {
                GameMode = gameMode,
                Players = [local, peer],
            },
        };
        var state = CharacterSelectStateBuilder.ResolveCharacterSelect(
            screen, new PlayerPerspective(PlayerScope.Local, null, true));

        Assert.NotNull(state);
        Assert.Equal(3, state.Lobby.Ascension);
        Assert.Equal(5, state.Lobby.MaxAscension);
        Assert.Equal(8, state.Lobby.Players[0].MaxMultiplayerAscensionUnlocked);
        Assert.Equal(6, state.Lobby.Players[1].MaxMultiplayerAscensionUnlocked);
#if STS2_API_V113
        Assert.Equal(gameMode, state.Lobby.GameMode);
        Assert.Equal(peerCap, state.Lobby.MaxBlitzAscension);
        Assert.Equal(9, state.Lobby.Players[0].MaxBlitzAscensionUnlocked);
        Assert.Equal(peerCap, state.Lobby.Players[1].MaxBlitzAscensionUnlocked);
#else
        Assert.Null(state.Lobby.GameMode);
        Assert.Null(state.Lobby.MaxBlitzAscension);
        Assert.All(state.Lobby.Players, player => Assert.Null(player.MaxBlitzAscensionUnlocked));
#endif
    }

    [Fact]
    public void GameApiLobbyEmptyRosterHasNoBlitzCap()
    {
        var screen = new MegaCrit.Sts2.Core.Nodes.Screens.Blitz.NBlitzScreen
        {
            Lobby = new BlitzLobbyStub(),
        };
        var state = CharacterSelectStateBuilder.ResolveCharacterSelect(
            screen, new PlayerPerspective(PlayerScope.Local, null, true));

        Assert.NotNull(state);
        Assert.Null(state.Lobby.MaxBlitzAscension);
        Assert.Empty(state.Lobby.Players);
#if STS2_API_V113
        Assert.Equal("Blitz", state.Lobby.GameMode);
#else
        Assert.Null(state.Lobby.GameMode);
#endif
    }

    [Fact]
    public void GameApiCardSelectionRecognizesTheLaneScreenInterface()
    {
        Assert.True(GameApiCardSelection.IsCardSelection(new CardSelectionStub()));
        Assert.False(GameApiCardSelection.IsCardSelection(new object()));
        Assert.False(GameApiCardSelection.IsCardSelection(null));
    }

    [Fact]
    public void GameApiLobbyRefreshUsesTheLaneParameterList()
    {
        var screen = new LobbyScreenStub();

        Assert.True(GameApiLobbyPlayer.RefreshChanged(screen, new LobbyPlayer { id = 42 }));
        Assert.Equal(42UL, screen.RefreshedPlayerId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GameApiMerchantPurchaseUsesTheLaneSignatureAndAllowsCancellation(bool cardRemoval)
    {
        var inventory = new object();
        var entry = new MerchantEntryStub();

        Assert.Equal(true, GameApiMerchantPurchase.Purchase(entry, inventory, cardRemoval));
        Assert.Same(inventory, entry.Inventory);
        Assert.False(entry.IgnoreCost);
        Assert.True(entry.Cancelable);
        Assert.Null(GameApiMerchantPurchase.Purchase(null, inventory, cardRemoval));
    }

#if STS2_API_V113
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GameApiMerchantPurchaseSynchronizesOnlyTheLocalPlayer(bool isLocal)
    {
        var localPlayer = new object();
        var entryPlayer = isLocal ? localPlayer : new object();
        var synchronized = Task.FromResult(true);
        var unsynchronized = Task.FromResult(false);
        var synchronizedCalls = 0;
        var unsynchronizedCalls = 0;

        var result = GameApiMerchantPurchase.PurchaseForPlayer(
            entryPlayer,
            localPlayer,
            () => { synchronizedCalls++; return synchronized; },
            () => { unsynchronizedCalls++; return unsynchronized; });

        Assert.Same(isLocal ? synchronized : unsynchronized, result);
        Assert.Equal(isLocal ? 1 : 0, synchronizedCalls);
        Assert.Equal(isLocal ? 0 : 1, unsynchronizedCalls);
    }

    [Fact]
    public void GameApiMerchantPurchaseDoesNotSynchronizeMissingPlayers()
    {
        var fallback = Task.FromResult(false);

        Assert.Same(fallback, GameApiMerchantPurchase.PurchaseForPlayer(
            null, null, () => throw new InvalidOperationException(), () => fallback));
    }

    [Fact]
    public void GameApiMerchantRoomInventoryUsesTheSynchronizerInsteadOfTheRoom()
    {
        var inventory = new object();
        var calls = 0;

        Assert.Same(inventory, GameApiMerchant.RoomInventory(new MerchantRoomStub(), () =>
        {
            calls++;
            return inventory;
        }));
        Assert.Equal(1, calls);
    }

    private sealed class MerchantRoomStub
    {
        public object GetLocalInventory() => throw new InvalidOperationException("Do not read the room inventory.");
    }
#endif

    [Fact]
    public void GameApiTreasureUsesTheLaneHoldersAndRefusesAnAmbiguousSoleRelic()
    {
        var holder = new object();
        var collection = new TreasureCollectionStub { Active = [holder] };

        Assert.Equal([holder], GameApiTreasure.Holders(collection));
#if STS2_API_V113
        Assert.Same(holder, GameApiTreasure.SingleplayerHolder(collection));
        collection.Active = [holder, new object()];
        Assert.Null(GameApiTreasure.SingleplayerHolder(collection));
        collection.Active = [];
        Assert.Empty(GameApiTreasure.Holders(collection));
        Assert.Null(GameApiTreasure.SingleplayerHolder(collection));
#else
        collection.SingleplayerRelicHolder = holder;
        Assert.Same(holder, GameApiTreasure.SingleplayerHolder(collection));
        collection.Active = [];
        Assert.Equal([holder], GameApiTreasure.Holders(collection));
        collection.SingleplayerRelicHolder = null;
        collection.Multiplayer = [holder];
        Assert.Equal([holder], GameApiTreasure.Holders(collection));
#endif
    }

    [Fact]
    public void GameApiRestSitePreservesUnavailableSmithProgress()
    {
#if STS2_API_V113
        Assert.Null(GameApiRestSite.SmithCount(new SmithOptionStub()));
#else
        Assert.Equal(2, GameApiRestSite.SmithCount(new SmithOptionStub()));
#endif
        Assert.Null(GameApiRestSite.SmithCount(new object()));
    }

    [Fact]
    public void GameApiManifestPinsEveryReflectedInteractionMember()
    {
        var required = new List<string>
        {
            GameApiMerchantPurchase.PurchaseMember,
            GameApiTreasure.ActiveHoldersMember,
        };
#if !STS2_API_V113
        required.Add(GameApiRestSite.SmithCountMember);
        required.Add(GameApiTreasure.SingleplayerHolderMember);
        required.Add(GameApiTreasure.MultiplayerHoldersMember);
#endif
        Assert.All(required, member => Assert.Contains(GameApiManifest.Requirements, requirement => requirement.Member == member));
    }

    private sealed class MerchantEntryStub
    {
        public object? Inventory { get; private set; }
        public bool IgnoreCost { get; private set; }
        public bool Cancelable { get; private set; } = true;

#if STS2_API_V113
        public bool OnTryPurchaseUnsynchronized(object inventory, bool ignoreCost)
#else
        public bool OnTryPurchaseWrapper(object inventory, bool ignoreCost)
            => OnTryPurchaseWrapper(inventory, ignoreCost, true);

        public bool OnTryPurchaseWrapper(object inventory, bool ignoreCost, bool cancelable)
#endif
        {
            Inventory = inventory;
            IgnoreCost = ignoreCost;
#if !STS2_API_V113
            Cancelable = cancelable;
#endif
            return true;
        }
    }

    private sealed class TreasureCollectionStub
    {
        public object[] Active { get; set; } = [];
#if STS2_API_V113
        public object[] HoldersInUse => Active;
#else
        private object[] _holdersInUse => Active;
        public object? SingleplayerRelicHolder { get; set; }
        public object[] Multiplayer { get; set; } = [];
        private object[] _multiplayerHolders => Multiplayer;
#endif
    }

    private sealed class SmithOptionStub
    {
        public int SmithCount => 2;
    }

    private sealed class BlitzLobbyStub
    {
        public string GameMode { get; init; } = "Blitz";
        public LobbyPlayer[] Players { get; init; } = [];
        public int Ascension => 3;
        public int MaxAscension => 5;
        public int MaxPlayers => 4;
    }

    private sealed class LobbyScreenStub
    {
        public ulong RefreshedPlayerId { get; private set; }

#if STS2_API_V113
        public void PlayerChanged(LobbyPlayer player) => RefreshedPlayerId = player.id;
#else
        public void PlayerChanged(LobbyPlayer player, bool animate)
        {
            Assert.False(animate);
            RefreshedPlayerId = player.id;
        }
#endif
    }

    private sealed class CardSelectionStub : CardSelectionScreen
    {
        public Task<IEnumerable<Sts2Live::MegaCrit.Sts2.Core.Models.CardModel>> CardsSelected()
            => Task.FromResult(Enumerable.Empty<Sts2Live::MegaCrit.Sts2.Core.Models.CardModel>());
    }
}
#endif
