using System.Collections.Concurrent;

namespace Moonfin.Server.Services;

/// <summary>
/// Caps how many concurrent operations a single authenticated user may have in flight against a
/// named, shared resource pool, so one user cannot exhaust a server-wide limit and starve every
/// other user.
/// </summary>
/// <remarks>
/// Registered as a singleton so it can be shared by more than one consumer. Each consumer picks
/// its own <c>bucket</c> name and its own per-user cap, so independent features (for example
/// artwork delivery and a future artwork-priority endpoint sharing "the same bucket") keep
/// independent — or, if a caller deliberately reuses a bucket name, shared — counters without any
/// coupling beyond that string. Unauthenticated/unknown callers (a <see langword="null"/> user id
/// passed to <see cref="TryAcquire"/>) are not tracked and always succeed; the caller's own
/// global limit is what bounds those.
/// </remarks>
public sealed class PerUserConcurrencyLimiter
{
    private readonly ConcurrentDictionary<(string Bucket, Guid UserId), Counter> _counts = new();

    /// <summary>
    /// Attempts to reserve one slot for <paramref name="userId"/> within <paramref name="bucket"/>.
    /// </summary>
    /// <returns>
    /// A disposable that releases the reservation when disposed, or <see langword="null"/> if the
    /// user already holds <paramref name="maxPerUser"/> concurrent reservations in this bucket.
    /// </returns>
    public IDisposable? TryAcquire(string bucket, Guid? userId, int maxPerUser)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucket);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPerUser, 1);

        if (userId is not { } id)
        {
            return NoOpReservation.Instance;
        }

        var key = (bucket, id);
        var counter = _counts.GetOrAdd(key, static _ => new Counter());
        var updated = Interlocked.Increment(ref counter.Value);
        if (updated > maxPerUser)
        {
            Interlocked.Decrement(ref counter.Value);
            return null;
        }

        return new Reservation(counter);
    }

    private sealed class Counter
    {
        public int Value;
    }

    private sealed class Reservation : IDisposable
    {
        private Counter? _counter;

        public Reservation(Counter counter)
        {
            _counter = counter;
        }

        public void Dispose()
        {
            var counter = Interlocked.Exchange(ref _counter, null);
            if (counter != null)
            {
                Interlocked.Decrement(ref counter.Value);
            }
        }
    }

    private sealed class NoOpReservation : IDisposable
    {
        public static readonly NoOpReservation Instance = new();

        public void Dispose()
        {
        }
    }
}
