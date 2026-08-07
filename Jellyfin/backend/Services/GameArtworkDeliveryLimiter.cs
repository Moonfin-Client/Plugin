namespace Moonfin.Server.Services;

/// <summary>
/// Bounds the number of artwork response bodies the plugin streams at one time.
/// </summary>
/// <remarks>
/// This applies to both the versioned artwork endpoint and its legacy thumbnail
/// compatibility endpoint. It deliberately does not cover ROM delivery or background artwork
/// acquisition/thumbnail work. Waiting callers are asynchronous and are released when the
/// response completes or its client disconnects.
///
/// The permit is held for the entire response body transfer, so a slow or non-reading client can
/// occupy a slot indefinitely. Two guards keep one authenticated user from turning that into a
/// denial of service for everyone else: a per-user cap (<see cref="MaximumConcurrentDeliveriesPerUser"/>,
/// enforced via the shared <see cref="PerUserConcurrencyLimiter"/>) and a bounded acquisition wait
/// (<see cref="AcquisitionTimeout"/>) so a caller that cannot get a slot fails fast with a
/// <see cref="ArtworkDeliveryUnavailableException"/> instead of queuing forever.
/// </remarks>
public sealed class GameArtworkDeliveryLimiter
{
    /// <summary>
    /// The server-wide number of concurrent artwork files that may be streamed.
    /// </summary>
    /// <remarks>
    /// Twenty-four permits four active six-request client viewports while preserving headroom
    /// for the rest of Jellyfin and preventing a client burst from opening unbounded file streams.
    /// </remarks>
    public const int MaximumConcurrentDeliveries = 24;

    /// <summary>
    /// The number of concurrent artwork deliveries a single user may hold at once.
    /// </summary>
    /// <remarks>
    /// Matches the "six-request client viewport" the global cap's comment already sizes for, so
    /// one user opening the maximum simultaneous requests a real client makes still leaves
    /// headroom (24 / 6 = four such viewports) for every other user, rather than one user being
    /// able to consume every server-wide permit.
    /// </remarks>
    public const int MaximumConcurrentDeliveriesPerUser = 6;

    /// <summary>
    /// How long <see cref="AcquireAsync"/> waits for a free slot before giving up.
    /// </summary>
    /// <remarks>
    /// Long enough that a normal, briefly-saturated pool recovers as in-flight responses finish;
    /// short enough that a saturated pool does not accumulate unbounded pending ASP.NET requests
    /// behind it. A caller that times out gets a 503 with a matching Retry-After instead of a
    /// request that never completes.
    /// </remarks>
    public static readonly TimeSpan AcquisitionTimeout = TimeSpan.FromSeconds(5);

    private const string PerUserBucket = "ArtworkDelivery";

    private readonly SemaphoreSlim _slots;
    private readonly PerUserConcurrencyLimiter _perUserLimiter;
    private readonly int _maximumConcurrentDeliveriesPerUser;
    private readonly TimeSpan _acquisitionTimeout;

    /// <summary>
    /// Initializes a new instance of the <see cref="GameArtworkDeliveryLimiter"/> class.
    /// </summary>
    /// <param name="perUserLimiter">
    /// The shared per-user accounting component. Injected (rather than owned) so other endpoints
    /// guarding a concurrency budget can reuse the same instance under their own bucket name.
    /// </param>
    public GameArtworkDeliveryLimiter(PerUserConcurrencyLimiter perUserLimiter)
        : this(MaximumConcurrentDeliveries, MaximumConcurrentDeliveriesPerUser, perUserLimiter, AcquisitionTimeout)
    {
    }

    internal GameArtworkDeliveryLimiter(
        int maximumConcurrentDeliveries,
        int maximumConcurrentDeliveriesPerUser = MaximumConcurrentDeliveriesPerUser,
        PerUserConcurrencyLimiter? perUserLimiter = null,
        TimeSpan? acquisitionTimeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConcurrentDeliveries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumConcurrentDeliveriesPerUser, 1);
        _slots = new SemaphoreSlim(maximumConcurrentDeliveries, maximumConcurrentDeliveries);
        _perUserLimiter = perUserLimiter ?? new PerUserConcurrencyLimiter();
        _maximumConcurrentDeliveriesPerUser = maximumConcurrentDeliveriesPerUser;
        _acquisitionTimeout = acquisitionTimeout ?? AcquisitionTimeout;
    }

    /// <summary>
    /// Asynchronously reserves one response-body stream until the returned lease is disposed.
    /// </summary>
    /// <param name="userId">
    /// The requesting user, used to enforce <see cref="MaximumConcurrentDeliveriesPerUser"/>.
    /// A <see langword="null"/> id (no resolvable claim) is not tracked per-user and is bounded
    /// only by the server-wide pool.
    /// </param>
    /// <param name="cancellationToken">Cancelled when the caller gives up (for example, request abort).</param>
    /// <exception cref="ArtworkDeliveryUnavailableException">
    /// Thrown when the caller already holds its per-user share of the pool, or when no slot frees
    /// up within <see cref="AcquisitionTimeout"/>.
    /// </exception>
    public async Task<IDisposable> AcquireAsync(Guid? userId, CancellationToken cancellationToken)
    {
        var userLease = _perUserLimiter.TryAcquire(PerUserBucket, userId, _maximumConcurrentDeliveriesPerUser);
        if (userLease is null)
        {
            throw new ArtworkDeliveryUnavailableException(_acquisitionTimeout);
        }

        using var timeoutSource = new CancellationTokenSource(_acquisitionTimeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await _slots.WaitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The caller's own token is still live: this is the acquisition timeout firing, not
            // the caller giving up. Surface it as a fast failure rather than propagating a bare
            // OperationCanceledException the controller has no reason to expect here.
            userLease.Dispose();
            throw new ArtworkDeliveryUnavailableException(_acquisitionTimeout);
        }
        catch
        {
            userLease.Dispose();
            throw;
        }

        return new Lease(_slots, userLease);
    }

    private sealed class Lease : IDisposable
    {
        private readonly SemaphoreSlim _slots;
        private readonly IDisposable _userLease;
        private int _released;

        public Lease(SemaphoreSlim slots, IDisposable userLease)
        {
            _slots = slots;
            _userLease = userLease;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _userLease.Dispose();
                _slots.Release();
            }
        }
    }
}
