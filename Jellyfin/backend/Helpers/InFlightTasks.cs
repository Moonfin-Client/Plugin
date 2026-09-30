using System.Collections.Concurrent;

namespace Moonfin.Server.Helpers;

/// <summary>
/// Runs one task per key at a time. Asking for a key that is still running hands back the
/// running task instead of starting another, and the key frees up once it finishes.
/// </summary>
internal sealed class InFlightTasks<T>
{
    private readonly ConcurrentDictionary<string, Lazy<Task<T>>> _running = new();

    public Task<T> GetOrStart(string key, Func<Task<T>> start)
    {
        // The async wrapper turns a start that throws into a failed task, so a bad start
        // still frees its key.
        var entry = new Lazy<Task<T>>(async () => await start().ConfigureAwait(false));
        var current = _running.GetOrAdd(key, entry);
        if (ReferenceEquals(current, entry))
        {
            _ = entry.Value.ContinueWith(
                _ => _running.TryRemove(KeyValuePair.Create(key, entry)),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return current.Value;
    }
}
