using Godot;

namespace Spirectl.Sts2.Live;

// Maps a browser KeyboardEvent.code (physical-key identifier, e.g. "KeyE", "Digit1", "Escape") to a Godot Key.
// Scoped to game-control keys; text entry is out of scope (the name form is plain Vue, external to the mirror).
// Reusable STS2 behavior (Godot enum mapping), so it lives in spirectl.
public static class Sts2BrowserKeyMap
{
    public static bool TryMap(string? code, out Key key)
    {
        key = Key.None;
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        // Letters: "KeyA".."KeyZ" → Key.A..Key.Z.
        if (code.Length == 4 && code.StartsWith("Key", System.StringComparison.Ordinal))
        {
            var c = char.ToUpperInvariant(code[3]);
            if (c is >= 'A' and <= 'Z')
            {
                key = Key.A + (c - 'A');
                return true;
            }
        }

        // Top-row digits: "Digit0".."Digit9" → Key.Key0..Key.Key9.
        if (code.Length == 6 && code.StartsWith("Digit", System.StringComparison.Ordinal))
        {
            var d = code[5];
            if (d is >= '0' and <= '9')
            {
                key = Key.Key0 + (d - '0');
                return true;
            }
        }

        // Numpad digits: "Numpad0".."Numpad9" → Key.Kp0..Key.Kp9.
        if (code.Length == 7 && code.StartsWith("Numpad", System.StringComparison.Ordinal))
        {
            var d = code[6];
            if (d is >= '0' and <= '9')
            {
                key = Key.Kp0 + (d - '0');
                return true;
            }
        }

        switch (code)
        {
            case "Enter":
            case "NumpadEnter":
                key = Key.Enter;
                return true;
            case "Escape":
                key = Key.Escape;
                return true;
            case "Space":
                key = Key.Space;
                return true;
            case "Tab":
                key = Key.Tab;
                return true;
            case "Backspace":
                key = Key.Backspace;
                return true;
            case "Delete":
                key = Key.Delete;
                return true;
            case "ArrowUp":
                key = Key.Up;
                return true;
            case "ArrowDown":
                key = Key.Down;
                return true;
            case "ArrowLeft":
                key = Key.Left;
                return true;
            case "ArrowRight":
                key = Key.Right;
                return true;
            case "Home":
                key = Key.Home;
                return true;
            case "End":
                key = Key.End;
                return true;
            case "PageUp":
                key = Key.Pageup;
                return true;
            case "PageDown":
                key = Key.Pagedown;
                return true;
            case "Minus":
                key = Key.Minus;
                return true;
            case "Equal":
                key = Key.Equal;
                return true;
            default:
                // Function keys F1..F12 → Key.F1..Key.F12.
                if (code.Length is 2 or 3 && code[0] == 'F' && int.TryParse(code.AsSpan(1), out var fn) && fn is >= 1 and <= 12)
                {
                    key = Key.F1 + (fn - 1);
                    return true;
                }

                return false;
        }
    }
}
