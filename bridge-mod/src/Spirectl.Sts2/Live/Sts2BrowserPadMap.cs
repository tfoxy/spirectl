#if ENABLE_STS2_LIVE_HOST
using Godot;
#endif

namespace Spirectl.Sts2.Live;

// Maps a device-neutral browser gamepad token ("faceSouth", "dpadUp", "leftBumper") to the name of the
// ABSTRACT CONTROLLER INPUT the game's own UI consumes. The twin of Sts2BrowserKeyMap, for the pad half of
// browser-driven play; reusable STS2 behavior (the game's action vocabulary), so it lives in spirectl.
//
// Why a token and not a button index: the game's controller layer already owns the mapping from these abstract
// inputs onto the actions its screens react to, honouring the player's own rebinds. A client that sends
// "faceSouth" therefore re-implements nothing — it names an input, and the game decides what that means on the
// screen that is open. The token vocabulary is the browser Gamepad API's standard mapping, minus the device
// branding (no "A"/"cross"), because the client cannot know which glyph set the player expects.
//
// WHY THE TABLE IS CANDIDATE LISTS. The game RENAMED nine of these action strings between supported builds: the
// d-pad went compass-style to up/down/left/right, and the single joystick became the LEFT stick. So a token maps
// to a small ORDERED list and the live resolve picks the one the running build actually registers. The
// alternative — pinning one spelling per lane in GameApi/<lane>/ — would compile a build-time guess that fails
// silently against the other build; here the engine answers for itself, and a token whose every candidate is
// absent is REFUSED out loud (see Sts2ActionHandler.ExecuteControllerInput) instead of injecting a dud event.
//
// ORDER IS NEWEST-BUILD-FIRST, and that is load-bearing rather than cosmetic: a build that renamed an action may
// still carry the retired spelling in its input map for migration, while its UI only ever tests the CURRENT
// names. Probing newest-first therefore lands on the name the UI is watching; oldest-first could pick a
// registered-but-orphaned action that nothing reacts to. On a build that predates a rename the newer name simply
// is not registered and the probe falls through to the older one.
//
// The table is deliberately Godot-free and always compiled (like Sts2ScrollOffsetMath) so the token vocabulary
// is offline-unit-testable; only the one overload that probes the live input map needs Godot, hence the
// live-host guard, as in Sts2StreamSkipMeta.
public static class Sts2BrowserPadMap
{
    // Right-stick tokens are deliberately ABSENT: only the newer build declares right-stick inputs at all, so a
    // token for them could not be honoured on both builds, and the browser wire contract does not send them.
    private static readonly Dictionary<string, string[]> Tokens = new(StringComparer.Ordinal)
    {
        // Face buttons, named by POSITION so the client never has to guess the player's glyph set.
        ["faceSouth"] = ["controller_face_button_south"],
        ["faceEast"] = ["controller_face_button_east"],
        ["faceWest"] = ["controller_face_button_west"],
        ["faceNorth"] = ["controller_face_button_north"],

        // D-pad: renamed from compass points to directions between builds.
        ["dpadUp"] = ["controller_d_pad_up", "controller_d_pad_north"],
        ["dpadDown"] = ["controller_d_pad_down", "controller_d_pad_south"],
        ["dpadLeft"] = ["controller_d_pad_left", "controller_d_pad_west"],
        ["dpadRight"] = ["controller_d_pad_right", "controller_d_pad_east"],

        // Shoulders. Triggers are abstract inputs here, not axes: the game exposes them as pressed/released.
        ["leftBumper"] = ["controller_left_bumper"],
        ["rightBumper"] = ["controller_right_bumper"],
        ["leftTrigger"] = ["controller_left_trigger"],
        ["rightTrigger"] = ["controller_right_trigger"],

        ["start"] = ["controller_start_button"],
        ["select"] = ["controller_select_button"],

        // Left stick: the unqualified "joystick" became the explicit left stick when right-stick inputs arrived.
        ["stickPress"] = ["controller_l_stick_press", "controller_joystick_press"],
        ["stickUp"] = ["controller_l_stick_up", "controller_joystick_up"],
        ["stickDown"] = ["controller_l_stick_down", "controller_joystick_down"],
        ["stickLeft"] = ["controller_l_stick_left", "controller_joystick_left"],
        ["stickRight"] = ["controller_l_stick_right", "controller_joystick_right"],

        // The touchpad click, which the game names in its ui_ family rather than its controller_ one.
        ["touchpad"] = ["ui_controller_touch_pad"],
    };

    // The token vocabulary, for tests and diagnostics.
    public static IEnumerable<string> KnownTokens => Tokens.Keys;

    // Token → the action names that could carry it, newest build first. True for a known token; the candidates
    // are never empty. Matching is ORDINAL and case-sensitive, like Sts2BrowserKeyMap on KeyboardEvent.code: the
    // tokens are a fixed wire contract, so a misspelling is a client bug that should surface as a refusal rather
    // than be quietly absorbed.
    public static bool TryMapCandidates(string? token, out IReadOnlyList<string> candidates)
    {
        if (!string.IsNullOrWhiteSpace(token) && Tokens.TryGetValue(token, out var names))
        {
            candidates = names;
            return true;
        }

        candidates = [];
        return false;
    }

#if ENABLE_STS2_LIVE_HOST
    // The live resolve: the first candidate THIS game build registers as an input action. False for an unknown
    // token, and for a known token whose every candidate is absent — the caller distinguishes them through
    // TryMapCandidates, because the two mean very different things (a client bug vs. an unmapped game build).
    //
    // HasAction is the right question to ask even though the injected event bypasses the input map's BINDINGS:
    // Godot only updates action state for names present in the map, and the game's screens test these by name,
    // so a name the map does not carry is a guaranteed no-op. Crucially, HasAction stays true on a seat whose
    // joypad BINDINGS an embedder has erased for isolation — erasing a binding leaves the action itself.
    public static bool TryMap(string? token, out StringName action)
    {
        if (TryMapCandidates(token, out var candidates))
        {
            foreach (var name in candidates)
            {
                if (InputMap.HasAction(name))
                {
                    action = name;
                    return true;
                }
            }
        }

        action = default!;
        return false;
    }
#endif
}
