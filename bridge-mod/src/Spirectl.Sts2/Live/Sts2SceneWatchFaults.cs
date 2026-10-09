using System.Globalization;
using System.Text;
using Spirectl.Sts2.Core.SceneInspection;

namespace Spirectl.Sts2.Live;

// Fault handling for the live scene watcher (Sts2RuntimeSceneWatcher), kept PURE and Godot-free so the decisions are
// unit-testable without the Godot-coupled watcher: which numeric reads are unusable, how long to wait before retrying
// a capture that threw, how often a fault may be logged, and the exact log line formats.
//
// Why the watcher needs it. A capture walks every tracked node and commits each node's Last* values as it goes. If
// it throws midway, the nodes already processed have committed values the client never received, and any tween
// suppression window that closed during that walk has already spent its one-shot settle re-emit. A non-finite read
// (NaN or Infinity in a transform, rect or colour) is worse in two ways: it fails every change comparison, so the
// node re-emits on every tick, and a JSON writer downstream refuses the value, so the whole delta it rides in is lost.
// A degenerate (zero-scale) transform can also make an inverse non-finite, which is how a finite scene can still
// produce such a value in a re-based local transform.
internal static class Sts2SceneFiniteGuard
{
    // A null snapshot carries nothing to guard: it already means "no value on this channel".
    internal static bool IsFinite(double? value) => value is not { } v || double.IsFinite(v);

    internal static bool IsFinite(RuntimeSceneVector2Snapshot? v)
        => v is null || (double.IsFinite(v.X) && double.IsFinite(v.Y));

    internal static bool IsFinite(RuntimeSceneTransform2DSnapshot? t)
        => t is null
            || (double.IsFinite(t.XAxis.X) && double.IsFinite(t.XAxis.Y)
                && double.IsFinite(t.YAxis.X) && double.IsFinite(t.YAxis.Y)
                && double.IsFinite(t.Origin.X) && double.IsFinite(t.Origin.Y));

    internal static bool IsFinite(RuntimeSceneRect2Snapshot? r)
        => r is null
            || (double.IsFinite(r.Position.X) && double.IsFinite(r.Position.Y)
                && double.IsFinite(r.Size.X) && double.IsFinite(r.Size.Y));

    internal static bool IsFinite(RuntimeSceneColorSnapshot? c)
        => c is null || (double.IsFinite(c.R) && double.IsFinite(c.G) && double.IsFinite(c.B) && double.IsFinite(c.A));

    // A 6-tuple [a, b, c, d, tx, ty] (the tween-endpoint wire form) or any other flat numeric array.
    internal static bool IsFinite(IReadOnlyList<double>? values)
    {
        if (values is null)
        {
            return true;
        }

        for (var i = 0; i < values.Count; i++)
        {
            if (!double.IsFinite(values[i]))
            {
                return false;
            }
        }

        return true;
    }

    // The numeric fields the LEAN (per-tick) text read can carry: sizes and the two colours. Layout, metrics and
    // shadow are not read on the lean path, so they are not checked here.
    internal static bool IsFiniteLeanText(RuntimeSceneTextPropertiesSnapshot? text)
        => text is null
            || (IsFinite(text.FontSize) && IsFinite(text.LineHeight) && IsFinite(text.OutlineSize)
                && IsFinite(text.LetterSpacing) && IsFinite(text.AppliedFontSize) && IsFinite(text.ThemeFontSize)
                && IsFinite(text.ConfiguredMinFontSize) && IsFinite(text.ConfiguredMaxFontSize)
                && IsFinite(text.TextColor) && IsFinite(text.OutlineColor));

    // Drop each non-finite lean text field to null ("unknown"), leaving the string and every finite field intact,
    // and name the fields dropped. The watcher keeps no last-emitted text snapshot, so "keep the previous value" is
    // not available for these; null is the wire's existing "not known" value, which every consumer already handles.
    // Returns the same instance when nothing was dropped.
    internal static RuntimeSceneTextPropertiesSnapshot? DropNonFiniteLeanText(
        RuntimeSceneTextPropertiesSnapshot? text, List<(string Field, string Value)> dropped)
    {
        if (text is null || IsFiniteLeanText(text))
        {
            return text;
        }

        return text with
        {
            FontSize = Drop(text.FontSize, "fontSize", dropped),
            LineHeight = Drop(text.LineHeight, "lineHeight", dropped),
            OutlineSize = Drop(text.OutlineSize, "outlineSize", dropped),
            LetterSpacing = Drop(text.LetterSpacing, "letterSpacing", dropped),
            AppliedFontSize = Drop(text.AppliedFontSize, "appliedFontSize", dropped),
            ThemeFontSize = Drop(text.ThemeFontSize, "themeFontSize", dropped),
            ConfiguredMinFontSize = Drop(text.ConfiguredMinFontSize, "configuredMinFontSize", dropped),
            ConfiguredMaxFontSize = Drop(text.ConfiguredMaxFontSize, "configuredMaxFontSize", dropped),
            TextColor = Drop(text.TextColor, "textColor", dropped),
            OutlineColor = Drop(text.OutlineColor, "outlineColor", dropped),
        };
    }

    private static double? Drop(double? value, string field, List<(string Field, string Value)> dropped)
    {
        if (IsFinite(value))
        {
            return value;
        }

        dropped.Add((field, Sts2SceneWatchFaultFormat.Number(value!.Value)));
        return null;
    }

    private static RuntimeSceneColorSnapshot? Drop(
        RuntimeSceneColorSnapshot? value, string field, List<(string Field, string Value)> dropped)
    {
        if (IsFinite(value))
        {
            return value;
        }

        dropped.Add((field, Sts2SceneWatchFaultFormat.Value(value)));
        return null;
    }
}

// When to retry a capture that threw. The watcher requests a FULL keyframe after every failed capture (see
// Sts2RuntimeSceneWatcher.OnTick), because only a keyframe re-ships what the aborted walk committed but never
// dispatched. A fault that persists (a node whose read throws on every tick until it is freed) would then mean one
// full, unpruned walk per tick, so consecutive failures back off geometrically from the active cadence up to a cap.
// The first retry is never delayed beyond the active cadence, and the cap bounds how long after the faulting node
// goes away the recovering keyframe can be late.
internal static class Sts2SceneCaptureRetry
{
    internal static long DelayMs(int consecutiveFailures, long minMs, long maxMs)
    {
        if (consecutiveFailures <= 1 || minMs <= 0)
        {
            return Math.Max(0, Math.Min(minMs, maxMs));
        }

        var shift = Math.Min(consecutiveFailures - 1, 20);
        var delay = minMs << shift;
        return Math.Min(delay, maxMs);
    }
}

// Bounds how often a fault reaches the log. The first occurrence of a key always logs; repeats of that key inside
// KeyIntervalMs are counted, and the next occurrence after the interval logs with that count. On top of the per-key
// limit, at most GlobalBudget lines are written per GlobalWindowMs across ALL keys (a VFX with hundreds of faulting
// nodes is one event, not hundreds of lines); lines refused by the budget are counted and reported on the next line
// that is written. Counts are reported only when a later occurrence is logged: nothing here schedules work, so a
// fault that stops simply stops logging. Thread-safe; only the fault path ever calls it.
internal sealed class Sts2RateLimitedLog(long keyIntervalMs, int globalBudget, long globalWindowMs, int maxKeys = 512)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, KeyState> _keys = new(StringComparer.Ordinal);
    private long? _windowStartMs;
    private int _windowLines;
    private long _budgetDropped;

    private sealed class KeyState
    {
        // Null until this key's first line is actually written (a first occurrence refused by the global budget
        // must still log as soon as the budget allows).
        public long? LastLoggedMs;
        public long Suppressed;
    }

    internal readonly record struct Admission(bool Log, long Suppressed, long BudgetDropped);

    internal Admission Admit(string key, long nowMs)
    {
        lock (_gate)
        {
            if (!_keys.TryGetValue(key, out var state))
            {
                if (_keys.Count >= maxKeys)
                {
                    // Bounded memory. Forgetting the keys can only re-log a first occurrence, which the global budget
                    // still bounds.
                    _keys.Clear();
                }

                state = new KeyState();
                _keys[key] = state;
            }
            else if (state.LastLoggedMs is { } lastLogged && nowMs - lastLogged < keyIntervalMs)
            {
                state.Suppressed++;
                return new Admission(false, 0, 0);
            }

            if (_windowStartMs is not { } windowStart || nowMs - windowStart >= globalWindowMs)
            {
                _windowStartMs = nowMs;
                _windowLines = 0;
            }

            if (_windowLines >= globalBudget)
            {
                state.Suppressed++;
                _budgetDropped++;
                return new Admission(false, 0, 0);
            }

            _windowLines++;
            var suppressed = state.Suppressed;
            var budgetDropped = _budgetDropped;
            state.Suppressed = 0;
            state.LastLoggedMs = nowMs;
            _budgetDropped = 0;
            return new Admission(true, suppressed, budgetDropped);
        }
    }
}

// The exact log line formats. The live QA leg greps godot.log for the `SPIRECTL_SCENE_WATCH` tag and the event word
// after it, so these are a contract: change them only together with whatever reads them.
internal static class Sts2SceneWatchFaultFormat
{
    internal const string Tag = "SPIRECTL_SCENE_WATCH";
    private const int MaxMessageChars = 300;

    internal readonly record struct NodeContext(string? Id, string? Path, string? NodeType);

    // SPIRECTL_SCENE_WATCH capture-failed exception=<type> message="<msg>" at=<frame> site=<frame> phase=<phase>
    //   node=<id> path=<path> nodeType=<type> consecutive=<n> suppressed=<n> budgetDropped=<n> retryMs=<ms> next=full
    internal static string CaptureFailed(
        Exception exception, string phase, NodeContext node, int consecutive, long suppressed, long budgetDropped,
        long retryMs)
    {
        var sb = new StringBuilder(256);
        sb.Append(Tag).Append(" capture-failed exception=").Append(exception.GetType().FullName ?? exception.GetType().Name)
            .Append(" message=\"").Append(OneLine(exception.Message)).Append('"')
            .Append(" at=").Append(TopFrame(exception) ?? "-")
            .Append(" site=").Append(FirstFrameIn(exception, "Spirectl.") ?? "-")
            .Append(" phase=").Append(phase);
        AppendNode(sb, node);
        sb.Append(" consecutive=").Append(consecutive.ToString(CultureInfo.InvariantCulture))
            .Append(" suppressed=").Append(suppressed.ToString(CultureInfo.InvariantCulture))
            .Append(" budgetDropped=").Append(budgetDropped.ToString(CultureInfo.InvariantCulture))
            .Append(" retryMs=").Append(retryMs.ToString(CultureInfo.InvariantCulture))
            .Append(" next=full");
        return sb.ToString();
    }

    // SPIRECTL_SCENE_WATCH capture-recovered failures=<n> outageMs=<ms> resync=<full|incremental>
    internal static string CaptureRecovered(int failures, long outageMs, bool full)
        => $"{Tag} capture-recovered failures={failures.ToString(CultureInfo.InvariantCulture)} "
            + $"outageMs={outageMs.ToString(CultureInfo.InvariantCulture)} resync={(full ? "full" : "incremental")}";

    // SPIRECTL_SCENE_WATCH non-finite channel=<channel> node=<id> path=<path> nodeType=<type> value=<value>
    //   action=<kept-last|dropped> suppressed=<n> budgetDropped=<n>
    internal static string NonFinite(
        string channel, NodeContext node, string value, string action, long suppressed, long budgetDropped)
    {
        var sb = new StringBuilder(192);
        sb.Append(Tag).Append(" non-finite channel=").Append(channel);
        AppendNode(sb, node);
        sb.Append(" value=").Append(value)
            .Append(" action=").Append(action)
            .Append(" suppressed=").Append(suppressed.ToString(CultureInfo.InvariantCulture))
            .Append(" budgetDropped=").Append(budgetDropped.ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static void AppendNode(StringBuilder sb, NodeContext node)
        => sb.Append(" node=").Append(Token(node.Id))
            .Append(" path=").Append(Token(node.Path))
            .Append(" nodeType=").Append(Token(node.NodeType));

    // Space-free so a `key=value` grep never splits a value: node names may contain spaces.
    private static string Token(string? value)
        => string.IsNullOrEmpty(value) ? "-" : value.Replace(' ', '_');

    internal static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    internal static string Value(RuntimeSceneTransform2DSnapshot? t)
        => t is null
            ? "null"
            : $"[{Number(t.XAxis.X)},{Number(t.XAxis.Y)},{Number(t.YAxis.X)},{Number(t.YAxis.Y)},{Number(t.Origin.X)},{Number(t.Origin.Y)}]";

    internal static string Value(RuntimeSceneRect2Snapshot? r)
        => r is null ? "null" : $"[{Number(r.Position.X)},{Number(r.Position.Y)},{Number(r.Size.X)},{Number(r.Size.Y)}]";

    internal static string Value(RuntimeSceneColorSnapshot? c)
        => c is null ? "null" : $"[{Number(c.R)},{Number(c.G)},{Number(c.B)},{Number(c.A)}]";

    internal static string Value(IReadOnlyList<double>? values)
    {
        if (values is null)
        {
            return "null";
        }

        var sb = new StringBuilder("[");
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(Number(values[i]));
        }

        return sb.Append(']').ToString();
    }

    internal static string OneLine(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var flat = message.Replace("\r", " ").Replace("\n", " ").Replace("\"", "'");
        return flat.Length <= MaxMessageChars ? flat : flat[..MaxMessageChars] + "...";
    }

    // The frame the exception was thrown from, as Type.Method.
    internal static string? TopFrame(Exception exception)
    {
        var frames = new System.Diagnostics.StackTrace(exception, false).GetFrames();
        return frames.Length == 0 ? null : Describe(frames[0]);
    }

    // The innermost frame whose declaring type's namespace starts with `namespacePrefix`: when the throw comes from
    // engine or runtime code, the frame that called into it.
    internal static string? FirstFrameIn(Exception exception, string namespacePrefix)
    {
        foreach (var frame in new System.Diagnostics.StackTrace(exception, false).GetFrames())
        {
            var type = frame.GetMethod()?.DeclaringType;
            if (type?.FullName is { } name && name.StartsWith(namespacePrefix, StringComparison.Ordinal))
            {
                return Describe(frame);
            }
        }

        return null;
    }

    private static string? Describe(System.Diagnostics.StackFrame frame)
    {
        var method = frame.GetMethod();
        if (method is null)
        {
            return null;
        }

        var type = method.DeclaringType;
        return type is null ? method.Name : $"{type.FullName ?? type.Name}.{method.Name}";
    }
}
