#if ENABLE_STS2_LIVE_HOST
extern alias GodotLive;

using GodotLive::Godot;
#endif

using System.Reflection;
using Spirectl.Sts2;
using Xunit;

namespace Spirectl.BridgeMod.Tests
{
    public sealed class Sts2SupportedScreenIdsTests
    {
        [Fact]
        public void ScreenIdResolverRecognizesDerivedCardSelectionScreens()
        {
            var match = Resolve(new MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NModdedCardRewardSelectionScreen());

            Assert.Equal("Screens.CardSelection.NModdedCardRewardSelectionScreen", ReadString(match, "ScreenType"));
            Assert.Equal("Card Selection", ReadString(match, "ScreenTitle"));
        }

        [Fact]
        public void ScreenIdResolverRecognizesDerivedLoadRunLobbyScreens()
        {
            var match = Resolve(new MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect.NCustomLoadGameScreen());

            Assert.Equal("Screens.CharacterSelect.NCustomLoadGameScreen", ReadString(match, "ScreenType"));
            Assert.Equal("Load Run Lobby", ReadString(match, "ScreenTitle"));
        }

        [Fact]
        public void ScreenIdResolverRecognizesTreasureRelicCollectionScreens()
        {
            var match = Resolve(new MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic.NCustomTreasureRoomRelicCollection());

            Assert.Equal("Screens.TreasureRoomRelic.NCustomTreasureRoomRelicCollection", ReadString(match, "ScreenType"));
            Assert.Equal("Treasure Room", ReadString(match, "ScreenTitle"));
        }

        [Fact]
        public void ScreenIdResolverRecognizesCrystalSphereScreens()
        {
            var match = Resolve(new MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen());

            Assert.Equal("Events.Custom.CrystalSphere.NCrystalSphereScreen", ReadString(match, "ScreenType"));
            Assert.Equal("Crystal Sphere", ReadString(match, "ScreenTitle"));
        }

        [Fact]
        public void OverlayScreenResolverRecognizesDerivedCardSelectionScreens()
        {
            var match = ResolveOverlay(new MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NModdedCardRewardSelectionScreen());

            Assert.Equal("Screens.CardSelection.NModdedCardRewardSelectionScreen", ReadString(match, "ScreenType"));
            Assert.Equal("Card Selection", ReadString(match, "ScreenTitle"));
        }

        [Fact]
        public void OverlayScreenResolverKeepsUnknownCardOverlaysOutOfCombat()
        {
            var match = ResolveOverlay(new MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NExperimentalCardOverlay());

            Assert.Equal("card-overlay", ReadString(match, "ScreenType"));
            Assert.Equal("Card Overlay", ReadString(match, "ScreenTitle"));
            Assert.Equal("passive", ReadString(match, "OverlayPolicy"));
            Assert.False(ReadBool(match, "IsBlocking"));
            Assert.True(ReadBool(match, "IsPassive"));
        }

        [Fact]
        public void OverlayScreenResolverNamesUnsupportedOverlayFamilies()
        {
            var match = ResolveOverlay(new MegaCrit.Sts2.Core.Nodes.Screens.Overlays.NExperimentalInfoOverlay());

            Assert.Equal("unsupported-overlay", ReadString(match, "ScreenType"));
            Assert.Equal("Experimental Info Overlay", ReadString(match, "ScreenTitle"));
            Assert.Equal("unsupported", ReadString(match, "OverlayPolicy"));
            Assert.False(ReadBool(match, "IsSupported"));
            Assert.True(ReadBool(match, "IsBlocking"));
            Assert.False(ReadBool(match, "IsPassive"));
        }

        [Fact]
        public void OverlayScreenResolverNamesSimpleCardSelectionScreens()
        {
            var match = ResolveOverlay(new MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NSimpleCardSelectScreen());

            Assert.Equal("Screens.CardSelection.NSimpleCardSelectScreen", ReadString(match, "ScreenType"));
            Assert.Equal("Simple Card Selection", ReadString(match, "ScreenTitle"));
            Assert.Equal("blocking", ReadString(match, "OverlayPolicy"));
        }

        [Fact]
        public void OverlayScreenResolverNamesDeckCardSelectionScreens()
        {
            var match = ResolveOverlay(new MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckUpgradeSelectScreen());

            Assert.Equal("Screens.CardSelection.NDeckUpgradeSelectScreen", ReadString(match, "ScreenType"));
            Assert.Equal("Deck Card Selection", ReadString(match, "ScreenTitle"));
            Assert.Equal("blocking", ReadString(match, "OverlayPolicy"));
        }

        [Fact]
        public void OverlayScreenResolverNamesBundleSelectionScreens()
        {
            var match = ResolveOverlay(new MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NChooseABundleSelectionScreen());

            Assert.Equal("Screens.CardSelection.NChooseABundleSelectionScreen", ReadString(match, "ScreenType"));
            Assert.Equal("Choose a Bundle", ReadString(match, "ScreenTitle"));
            Assert.Equal("blocking", ReadString(match, "OverlayPolicy"));
        }

        [Fact]
        public void OverlayScreenResolverNamesRelicSelectionScreens()
        {
            var match = ResolveOverlay(new MegaCrit.Sts2.Core.Nodes.Screens.NChooseARelicSelection());

            Assert.Equal("Screens.NChooseARelicSelection", ReadString(match, "ScreenType"));
            Assert.Equal("Choose a Relic", ReadString(match, "ScreenTitle"));
            Assert.Equal("blocking", ReadString(match, "OverlayPolicy"));
        }

        [Fact]
        public void ScreenIdResolverKeepsUnknownScreensUnsupported()
        {
            var match = Resolve(new MegaCrit.Sts2.Core.Nodes.Screens.Experimental.NExperimentalScreen());

            Assert.Null(match);
        }

#if ENABLE_STS2_LIVE_HOST
        [Fact]
        public void CanConfirmSelectionRecognizesEnchantSinglePreviewConfirmButton()
        {
            // SELF_HELP_BOOK (and every enchant event) opens NDeckEnchantSelectScreen, which arms
            // _singlePreviewConfirmButton — NOT _previewConfirmButton. CanConfirmSelection must use the same
            // confirm-button fallback chain TryConfirmSelection does, else confirm-selection is wrongly
            // rejected ("requires a currently staged selection on the active overlay") for a staged enchant.
            var screen = new MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NDeckEnchantSelectScreen
            {
                _selectedCards = new object[] { new object() },     // one card staged
                _enchantSinglePreviewContainer = new object(),       // preview open (no Visible member => visible)
                _singlePreviewConfirmButton = new FakeExecutable(),  // armed (Disabled == false)
                // _previewConfirmButton intentionally left null => exercises the fallback chain.
            };

            Assert.True(InvokeCanConfirmSelection(screen));
        }

        private static bool InvokeCanConfirmSelection(object screenObject)
        {
            var inspector = typeof(Sts2ActionCatalog).Assembly.GetType("Spirectl.Sts2.Live.Sts2CardSelectionScreenInspector");
            Assert.NotNull(inspector);

            var method = inspector!.GetMethod(
                "CanConfirmSelection",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            return Assert.IsType<bool>(method!.Invoke(null, new object?[] { screenObject }));
        }

        private sealed class FakeExecutable
        {
            public bool Disabled;
        }
#endif

        private static object? Resolve(object screenObject)
        {
            var helperType = typeof(Sts2ActionCatalog).Assembly.GetType("Spirectl.Sts2.Sts2SupportedScreenIds");
            Assert.NotNull(helperType);

            var method = helperType!.GetMethod(
                "TryResolve",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(method);

            var parameters = new object?[] { screenObject, null };
            var resolved = Assert.IsType<bool>(method!.Invoke(null, parameters));
            return resolved ? parameters[1] : null;
        }

        private static object? ResolveOverlay(object overlayObject)
        {
            var helperType = typeof(Sts2ActionCatalog).Assembly.GetType("Spirectl.Sts2.Sts2OverlayScreenFamilies");
            Assert.NotNull(helperType);

            var method = helperType!.GetMethod(
                "TryResolve",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var parameters = new object?[] { overlayObject, null };
            var resolved = Assert.IsType<bool>(method!.Invoke(null, parameters));
            return resolved ? parameters[1] : null;
        }

        private static string? ReadString(object? target, string propertyName)
            => target?.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target) as string;

        private static bool ReadBool(object? target, string propertyName)
            => Assert.IsType<bool>(target?.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(target));
    }
}

namespace MegaCrit.Sts2.Core.Nodes.Screens.CardSelection
{
    public class NCardRewardSelectionScreen;
#if ENABLE_STS2_LIVE_HOST
    public class NCardGridSelectionScreen
    {
        public object? _cardRow;
        public object? _confirmButton;
        public object? _selectedCards;
    }

    public class NChooseABundleSelectionScreen
    {
        public object? _bundleRow;
    }
#else
    public class NCardGridSelectionScreen;
    public class NChooseABundleSelectionScreen;
#endif

    public sealed class NModdedCardRewardSelectionScreen : NCardRewardSelectionScreen;
    public sealed class NSimpleCardSelectScreen : NCardGridSelectionScreen;
    public sealed class NDeckCardSelectScreen : NCardGridSelectionScreen;
    public sealed class NDeckUpgradeSelectScreen : NCardGridSelectionScreen;
    public sealed class NDeckTransformSelectScreen : NCardGridSelectionScreen;
    public sealed class NDeckEnchantSelectScreen : NCardGridSelectionScreen
#if ENABLE_STS2_LIVE_HOST
    {
        // The enchant screen names its preview confirm button / container differently from the other
        // deck variants (which use _previewConfirmButton + _previewContainer).
        public object? _singlePreviewConfirmButton;
        public object? _multiPreviewConfirmButton;
        public object? _enchantSinglePreviewContainer;
        public object? _enchantMultiPreviewContainer;
    }
#else
    ;
#endif
}

namespace MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect
{
    public class NMultiplayerLoadGameScreen;

    public sealed class NCustomLoadGameScreen : NMultiplayerLoadGameScreen;
}

namespace MegaCrit.Sts2.Core.Nodes.Screens.Experimental
{
    public sealed class NExperimentalScreen;
}

namespace MegaCrit.Sts2.Core.Nodes.Screens.Overlays
{
    public sealed class NExperimentalCardOverlay;
    public sealed class NExperimentalInfoOverlay;
}

namespace MegaCrit.Sts2.Core.Nodes.Screens
{
    public sealed class NChooseARelicSelection;
}

namespace MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic
{
    public class NTreasureRoomRelicCollection;

    public sealed class NCustomTreasureRoomRelicCollection : NTreasureRoomRelicCollection;
}

namespace MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere
{
    public sealed class NCrystalSphereScreen;
}
