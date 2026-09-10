namespace Spirectl.Sts2.Core.Models;

// Shared facts for the synthetic "Random Character" lobby entry (id RANDOM_CHARACTER): a
// character-select meta-button that is NOT a real game model — it is excluded from
// ModelDb.AllCharacters (MegaCrit.Sts2.Core.Models.ModelDb.AllCharacters skips
// MegaCrit.Sts2.Core.Models.Characters.RandomCharacter), so it never appears through the
// normal "characters" model-catalog family. Both the live lobby character-button list
// (Sts2RuntimeObservationProvider.RandomLobbyCharacter) and the "randomCharacter" reference
// topic (Sts2ReferenceDataProvider) source the SAME values here so they never drift.
//
// Values are the game's runtime values for the random-character screen, ported from the
// RandomCharacter class and its CharacterModel base:
// RandomCharacter overrides CharacterSelectIconPath/CharacterSelectLockedIconPath with its own
// "char_select_random[..._locked].png" assets — DISTINCT icons, unlike the base
// CharacterModel convention of deriving the locked icon by appending "_locked" to the SAME
// unlocked asset name. The select-background scene path follows the base CharacterModel
// convention (SceneHelper.GetScenePath("screens/char_select/char_select_bg_" + id)).
public static class RandomCharacterFacts
{
    public const string Id = "RANDOM_CHARACTER";
    public const string LocTable = "characters";
    public const string NameKey = "RANDOM_CHARACTER.name";
    public const string DescriptionKey = "RANDOM_CHARACTER.description";

    public const string PortraitAssetKey = "res://images/packed/character_select/char_select_random.png";

    // Distinct from PortraitAssetKey — RandomCharacter.CharacterSelectLockedIconPath overrides the
    // CharacterModel base (which would otherwise derive "char_select_random_locked.png" the same
    // way, but the base's default is UNUSED here since RandomCharacter supplies its own override).
    public const string LockedIconAssetKey = "res://images/packed/character_select/char_select_random_locked.png";

    public const string SelectBackgroundAssetKey = "res://scenes/screens/char_select/char_select_bg_random_character.tscn";
}
