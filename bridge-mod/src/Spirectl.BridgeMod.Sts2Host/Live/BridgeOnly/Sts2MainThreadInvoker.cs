namespace Spirectl.Sts2.Live;


public sealed class Sts2MainThreadInvoker : IBridgeRuntimeMainThreadInvoker
{
    public T Invoke<T>(Func<T> action)
        => Sts2MainThreadDispatcher.Invoke(action);
}
