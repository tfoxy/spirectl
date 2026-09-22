using Spirectl.Sts2.Core.Actions;
using Spirectl.Sts2.Live;
using Xunit;

namespace Spirectl.BridgeMod.Tests;

// The Godot-free half of `controller-input`: the browser pad token vocabulary and the per-build candidate lists.
// The live resolve (which candidate the running game registers) needs a running engine and is exercised by the
// couch-coop live leg, not here.
public sealed class Sts2BrowserPadMapTests
{
    // Every token the browser wire contract may send, paired with the action name for the build that the token
    // must resolve to FIRST. Pinned literally rather than read back out of the map, so a typo in the table is a
    // failing test and not a self-consistent no-op: a wrong action name is invisible to the compiler and silently
    // stops the pad working against a live game.
    [Theory]
    [InlineData("faceSouth", "controller_face_button_south")]
    [InlineData("faceEast", "controller_face_button_east")]
    [InlineData("faceWest", "controller_face_button_west")]
    [InlineData("faceNorth", "controller_face_button_north")]
    [InlineData("dpadUp", "controller_d_pad_up")]
    [InlineData("dpadDown", "controller_d_pad_down")]
    [InlineData("dpadLeft", "controller_d_pad_left")]
    [InlineData("dpadRight", "controller_d_pad_right")]
    [InlineData("leftBumper", "controller_left_bumper")]
    [InlineData("rightBumper", "controller_right_bumper")]
    [InlineData("leftTrigger", "controller_left_trigger")]
    [InlineData("rightTrigger", "controller_right_trigger")]
    [InlineData("start", "controller_start_button")]
    [InlineData("select", "controller_select_button")]
    [InlineData("stickPress", "controller_l_stick_press")]
    [InlineData("stickUp", "controller_l_stick_up")]
    [InlineData("stickDown", "controller_l_stick_down")]
    [InlineData("stickLeft", "controller_l_stick_left")]
    [InlineData("stickRight", "controller_l_stick_right")]
    [InlineData("touchpad", "ui_controller_touch_pad")]
    public void MapsEveryWireTokenToItsNewestActionName(string token, string expected)
    {
        Assert.True(Sts2BrowserPadMap.TryMapCandidates(token, out var candidates));
        Assert.Equal(expected, candidates[0]);
    }

    // The nine inputs the game renamed between supported builds, with the retired spelling that must stay
    // available as a fallback so one bridge serves both builds.
    [Theory]
    [InlineData("dpadUp", "controller_d_pad_north")]
    [InlineData("dpadDown", "controller_d_pad_south")]
    [InlineData("dpadLeft", "controller_d_pad_west")]
    [InlineData("dpadRight", "controller_d_pad_east")]
    [InlineData("stickPress", "controller_joystick_press")]
    [InlineData("stickUp", "controller_joystick_up")]
    [InlineData("stickDown", "controller_joystick_down")]
    [InlineData("stickLeft", "controller_joystick_left")]
    [InlineData("stickRight", "controller_joystick_right")]
    public void KeepsTheOlderBuildsSpellingAsALaterCandidate(string token, string legacy)
    {
        Assert.True(Sts2BrowserPadMap.TryMapCandidates(token, out var candidates));
        Assert.Equal(2, candidates.Count);
        Assert.Equal(legacy, candidates[1]);
    }

    [Fact]
    public void OffersOnlyOneCandidateForTheInputsNoBuildRenamed()
    {
        string[] stable =
        [
            "faceSouth", "faceEast", "faceWest", "faceNorth",
            "leftBumper", "rightBumper", "leftTrigger", "rightTrigger",
            "start", "select", "touchpad",
        ];

        foreach (var token in stable)
        {
            Assert.True(Sts2BrowserPadMap.TryMapCandidates(token, out var candidates));
            Assert.Single(candidates);
        }
    }

    [Fact]
    public void CarriesExactlyTheTwentyTokensOfTheWireContract()
    {
        // A guard on the contract itself: the browser client and this map are edited in different repos, so an
        // added or dropped token should fail here rather than at a player's hands.
        Assert.Equal(20, Sts2BrowserPadMap.KnownTokens.Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("faceA")]
    [InlineData("controller_face_button_south")]
    // Case-sensitive, like Sts2BrowserKeyMap on KeyboardEvent.code: the tokens are a fixed contract, so a
    // misspelling is a client bug that must surface as a refusal instead of being quietly absorbed.
    [InlineData("dpadup")]
    [InlineData("DpadUp")]
    [InlineData("dPadUp")]
    // Right-stick inputs exist on one build only, so they are deliberately not part of the wire vocabulary.
    [InlineData("rightStickUp")]
    public void RefusesAnythingThatIsNotAContractToken(string? token)
    {
        Assert.False(Sts2BrowserPadMap.TryMapCandidates(token, out var candidates));
        Assert.Empty(candidates);
    }

    [Fact]
    public void TheActionKindStaysLastInTheEnum()
    {
        // The wire `kind` code IS the enum ordinal, so a kind inserted anywhere but the end renumbers every later
        // action. This guard travels with the NEWEST kind: whoever appends the next one moves the "is last" half
        // here into their own tests and leaves controller-input's ordinal pinned below.
        var values = Enum.GetValues<SemanticActionKind>();
        Assert.Equal(SemanticActionKind.ControllerInput, values[^1]);
        Assert.Equal(74, (int)SemanticActionKind.ControllerInput);
    }

    [Fact]
    public void NeverMapsTwoTokensOntoTheSameAction()
    {
        // Two tokens sharing an action name would make a client's press ambiguous to read back, and would most
        // likely mean a copy-paste slip in the table above.
        var all = Sts2BrowserPadMap.KnownTokens
            .SelectMany(token =>
            {
                Assert.True(Sts2BrowserPadMap.TryMapCandidates(token, out var candidates));
                return candidates;
            })
            .ToList();

        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
    }
}
