namespace Spirectl.Sts2;


public interface IBridgeRuntimeMainThreadInvoker
{
    T Invoke<T>(Func<T> action);
}

public sealed class InlineBridgeRuntimeMainThreadInvoker : IBridgeRuntimeMainThreadInvoker
{
    public static InlineBridgeRuntimeMainThreadInvoker Instance { get; } = new();

    private InlineBridgeRuntimeMainThreadInvoker()
    {
    }

    public T Invoke<T>(Func<T> action)
        => action();
}
