#if ENABLE_STS2_LIVE_HOST
using System.Linq;
using Spirectl.Sts2.Core.Logging;
using Spirectl.Sts2.Core.Models;
using Spirectl.Sts2.Core.Reference;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// Live-host gate: these exercise the real Sts2ReferenceDataProvider reflecting
// over the game's StsColors palette. Run via
// `scripts/validate.sh bridge-live-host-tests`. StsColors reflection and
// Godot.Color.ToHtml are pure managed, so they work without a running engine;
// the version topic (which reads ReleaseInfoManager via Godot file IO) is left
// to end-to-end CLI verification.
public sealed class Sts2ReferenceDataProviderTests
{
    [Fact]
    public void ColorsTopicReflectsStsColorsPalette()
    {
        var provider = new Sts2ReferenceDataProvider(new InMemoryLogStream());

        var result = provider.GetReference(new ReferenceRequestSnapshot("colors", []));

        Assert.Null(result.Error);
        Assert.Equal(ReferenceStatus.Ok, result.Status);
        var colors = Assert.IsType<GameColorsSnapshot>(result.Payload);
        Assert.True(
            colors.Colors.Count >= 30,
            $"expected a rich StsColors palette, got {colors.Colors.Count}");

        var aqua = colors.Colors.Single(color => color.Name == "aqua");
        Assert.Equal("2aebbe", aqua.Hex);
        Assert.Equal(1f, aqua.A, 3);

        // screenBackdrop is new Color(0, 0, 0, 0.8f): alpha-bearing, so 8 hex
        // digits and A < 1.
        var backdrop = colors.Colors.Single(color => color.Name == "screenBackdrop");
        Assert.Equal(8, backdrop.Hex.Length);
        Assert.True(backdrop.A < 1f);
    }

    [Fact]
    public void ColorsTopicFiltersByKeyAndReportsMissing()
    {
        var provider = new Sts2ReferenceDataProvider(new InMemoryLogStream());

        var result = provider.GetReference(
            new ReferenceRequestSnapshot("colors", ["gold", "not-a-color"]));

        Assert.Equal(ReferenceStatus.Partial, result.Status);
        Assert.Equal(new[] { "not-a-color" }, result.MissingKeys);
        var colors = Assert.IsType<GameColorsSnapshot>(result.Payload);
        Assert.Equal("gold", Assert.Single(colors.Colors).Name);
    }

    [Fact]
    public void UnknownTopicReportsUnsupported()
    {
        var provider = new Sts2ReferenceDataProvider(new InMemoryLogStream());

        var result = provider.GetReference(new ReferenceRequestSnapshot("bogus", []));

        Assert.Null(result.Error);
        Assert.Equal(ReferenceStatus.UnsupportedTopic, result.Status);
        Assert.Null(result.Payload);
        Assert.Contains(result.Notices, notice => notice.Code == "reference-unsupported-topic");
    }

    [Fact]
    public void RandomCharacterTopicMatchesRandomCharacterFacts()
    {
        var provider = new Sts2ReferenceDataProvider(new InMemoryLogStream());

        // Topic matching is case-insensitive (NormalizeTopic lowercases before dispatch); the
        // canonical spelling used by the endpoint/CLI is camelCase "randomCharacter".
        var result = provider.GetReference(new ReferenceRequestSnapshot("randomCharacter", []));

        Assert.Null(result.Error);
        Assert.Equal(ReferenceStatus.Ok, result.Status);
        Assert.Equal("randomCharacter", result.Topic);
        var snapshot = Assert.IsType<RandomCharacterSnapshot>(result.Payload);
        var character = snapshot.Character;

        Assert.Equal(RandomCharacterFacts.Id, character.Id);
        Assert.Equal(RandomCharacterFacts.PortraitAssetKey, character.CharacterSelectIconAssetKey);
        // The distinct LOCKED icon asset — the catalog stub this topic replaces wrongly reused the
        // unlocked icon here; this is the fix.
        Assert.Equal(RandomCharacterFacts.LockedIconAssetKey, character.CharacterSelectLockedIconAssetKey);
        Assert.NotEqual(character.CharacterSelectIconAssetKey, character.CharacterSelectLockedIconAssetKey);
        Assert.Equal(RandomCharacterFacts.SelectBackgroundAssetKey, character.CharacterSelectBgPath);

        // Reference-mode convention: resolved text stays null, and the game-sourced {table, key}
        // pairs ride LocalizationRefs so a host merges them onto the fields (mirroring how real
        // characters are fetched through the "characters" model-catalog family).
        Assert.Null(character.Title);
        Assert.Null(character.CharacterSelectTitle);
        Assert.Null(character.CharacterSelectDesc);
        Assert.Equal(RandomCharacterFacts.LocTable, character.LocalizationRefs["title"].Table);
        Assert.Equal(RandomCharacterFacts.NameKey, character.LocalizationRefs["title"].Key);
        Assert.Equal(RandomCharacterFacts.LocTable, character.LocalizationRefs["characterSelectTitle"].Table);
        Assert.Equal(RandomCharacterFacts.NameKey, character.LocalizationRefs["characterSelectTitle"].Key);
        Assert.Equal(RandomCharacterFacts.LocTable, character.LocalizationRefs["characterSelectDesc"].Table);
        Assert.Equal(RandomCharacterFacts.DescriptionKey, character.LocalizationRefs["characterSelectDesc"].Key);
    }
}
#endif
