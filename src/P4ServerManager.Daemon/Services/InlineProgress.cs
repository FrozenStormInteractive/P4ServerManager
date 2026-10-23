namespace P4ServerManager.Daemon.Services;

/// <summary>
/// Calls the handler on the reporting thread, unlike <see cref="Progress{T}"/> which posts to the thread pool and can
/// deliver the reports out of order.
/// </summary>
public sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value)
    {
        handler(value);
    }
}
