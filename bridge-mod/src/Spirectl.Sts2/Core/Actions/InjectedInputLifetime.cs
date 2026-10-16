namespace Spirectl.Sts2.Core.Actions;

// Main-thread-only ownership of injected edges. Register before injection: a synchronous
// input callback may revoke access while the press is still on the stack.
internal sealed class InjectedInputLifetime
{
    private readonly Dictionary<string, Action> _releases = new(StringComparer.Ordinal);
    private bool _active;
    private bool _cancelling;
    private bool _pending;
    private bool _cancellationFailed;
    public long Generation { get; private set; }

    public bool Run(long generation, Action inject)
    {
        if (_cancelling || _cancellationFailed || generation != Generation) return false;
        _active = true;
        _pending = true;
        inject();
        return generation == Generation;
    }

    public bool Send(long generation, string id, bool pressed, Action inject, Action release)
        => Run(generation, () =>
        {
            if (pressed) _releases[id] = release;
            else _releases.Remove(id);
            inject();
        });

    public void Complete(Action flush)
    {
        if (!_pending || _cancelling || _cancellationFailed) return;
        flush();
        _pending = false;
        _active = _releases.Count != 0;
    }

    public void Cancel(Func<IDisposable> suppressDelivery, Action flush)
    {
        Generation++;
        if (_cancelling || !_active) return;
        _cancelling = true;
        _cancellationFailed = true;
        try
        {
            using (suppressDelivery())
            {
                var releases = _releases.Values.ToArray();
                List<Exception>? errors = null;
                // A queued press must reach the input state before its cancellation release.
                // Delivery is already disabled, so buffered presses cannot reach controls.
                try { flush(); }
                catch (Exception ex) { (errors ??= []).Add(ex); }
                foreach (var release in releases)
                {
                    try { release(); }
                    catch (Exception ex) { (errors ??= []).Add(ex); }
                }
                try { flush(); }
                catch (Exception ex) { (errors ??= []).Add(ex); }
                // Keep ownership on failure so a later revocation signal can retry safely.
                if (errors is not null) throw new AggregateException(errors);
            }
            _releases.Clear();
            _active = false;
            _pending = false;
            _cancellationFailed = false;
        }
        finally { _cancelling = false; }
    }
}

// The handled flag must be set before disabling GUI input: Godot drops mouse focus
// during that setter and may synthesize a release even before input is disabled.
internal sealed class InjectedInputDeliverySuppression : IDisposable
{
    private readonly Action<bool> _setDisabled;
    private readonly bool _wasDisabled;

    public InjectedInputDeliverySuppression(Func<bool> getDisabled, Action<bool> setDisabled, Action markHandled)
    {
        _setDisabled = setDisabled;
        _wasDisabled = getDisabled();
        markHandled();
        try { setDisabled(true); }
        catch
        {
            setDisabled(_wasDisabled);
            throw;
        }
    }

    public void Dispose() => _setDisabled(_wasDisabled);
}
