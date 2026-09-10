using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// WS-S "one localization-error log + crash-reporter capture per card per poll" — the pure template predicate
// (Sts2LocTemplateText.HasUnsourceableSelector) that lets the model catalog return a card body's RAW text instead
// of asking the game to format a template it cannot source. Truth table derived from the shipped card-body corpus
// (all 14 languages: 8,237 bodies, 7,309 token-bearing, zero escapes, zero non-named-only bodies — counts and
// method in `.sts2/research/`), so the cases below are the real token GRAMMAR, not invented shapes.
public sealed class Sts2LocTemplateTextTests
{
    // The three card bodies behind the observed log lines: a plain damage body, a plain block body, and a
    // two-line body with two different tokens. These are the shape the CLI's `state actions` poll walked.
    private const string StrikeBody = "Deal {Damage:diff()} damage.";
    private const string DefendBody = "Gain {Block:diff()} [gold]Block[/gold].";
    private const string BashBody = "Deal {Damage:diff()} damage.\nApply {VulnerablePower:diff()} [gold]Vulnerable[/gold].";

    [Theory]
    // The three logged bodies.
    [InlineData(StrikeBody, true)]
    [InlineData(DefendBody, true)]
    [InlineData(BashBody, true)]
    // A NESTED selector: the outer `{Cards:` is named, so one hit is enough — no brace matching required.
    [InlineData("Draw {Cards:diff()} {Cards:plural:card|cards}.", true)]
    // The inner form of that nesting on its own (`{:diff()}` inside a plural branch) is NOT a named token; a body
    // that only had this would have nothing to source. It never occurs alone in the corpus.
    [InlineData("Draw {:diff()} cards.", false)]
    // A conditional branch that opens with a newline still has its named `{InCombat:` head.
    [InlineData("Deal {Damage:diff()} damage.{InCombat:\n(Hits {CalculatedHits:diff()} times)|}", true)]
    // A leading-underscore name is a legal token head.
    [InlineData("Gain {_internal} block.", true)]
    // Positional/digit placeholders are not named tokens: nothing to source from a variable bag.
    [InlineData("Deal {1} damage.", false)]
    [InlineData("{0}", false)]
    // Escaped opening brace: authored as a literal `{`.
    [InlineData("Deal \\{Damage} damage.", false)]
    [InlineData("\\{Damage:diff()}", false)]
    // A DOUBLED brace still counts: the escape in this content is the backslash form above, so the inner `{D` is
    // an ordinary token head. (Zero corpus bodies double a brace; pinned so the scan's behaviour is documented.)
    [InlineData("Deal {{Damage}} damage.", true)]
    // Token-free bodies: the 84 English card bodies with no template at all. These keep the formatting call.
    [InlineData("Procure a random potion.", false)]
    [InlineData("[gold]Block[/gold] is not removed at the start of your turn.", false)]
    // BBCode brackets are not braces.
    [InlineData("[gold]Upgrade[/gold] ALL your cards.", false)]
    // A trailing lone brace has no name after it.
    [InlineData("Deal damage {", false)]
    [InlineData("{", false)]
    // Empty / whitespace / null.
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void TruthTable(string? rawText, bool expected)
        => Assert.Equal(expected, Sts2LocTemplateText.HasUnsourceableSelector(rawText));

    [Fact]
    public void LocalizedBodies_AreDetectedTheSameAsEnglish()
    {
        // The tokens are language-invariant (the same names in every shipped table), so a translated body has to
        // read the same as its English source — otherwise a non-English poll would keep the error+capture spam.
        Assert.True(Sts2LocTemplateText.HasUnsourceableSelector(
            "Infliges {Damage:diff()} de daño.\nAplicas {VulnerablePower:diff()} de [gold]vulnerabilidad[/gold]."));
        Assert.True(Sts2LocTemplateText.HasUnsourceableSelector(BashBody));
    }

    [Fact]
    public void NoAllowlist_UnknownTokenNamesStillCount()
    {
        // Token names are open-ended (146 distinct names in today's corpus, one per card variable plus the
        // flag/icon helpers). A predicate keyed on known names would go quiet — and start spamming again — the
        // moment content added one, so any name must count.
        Assert.True(Sts2LocTemplateText.HasUnsourceableSelector("Gain {SomeTokenAddedNextPatch:diff()} block."));
    }
}
