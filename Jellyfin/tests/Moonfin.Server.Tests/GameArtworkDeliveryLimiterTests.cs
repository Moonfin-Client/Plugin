using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class GameArtworkDeliveryLimiterTests
{
    private static readonly Guid UserA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task AcquireAsync_WaitsForAReleasedSlotAndReleasesOnlyOnce()
    {
        var limiter = new GameArtworkDeliveryLimiter(maximumConcurrentDeliveries: 2, maximumConcurrentDeliveriesPerUser: 2);
        using var first = await limiter.AcquireAsync(UserA, CancellationToken.None);
        using var second = await limiter.AcquireAsync(UserB, CancellationToken.None);

        var waiting = limiter.AcquireAsync(UserA, CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        first.Dispose();
        var third = await waiting.WaitAsync(TimeSpan.FromSeconds(1));

        first.Dispose();
        var stillWaiting = limiter.AcquireAsync(UserB, CancellationToken.None);
        Assert.False(stillWaiting.IsCompleted);

        third.Dispose();
        using var fourth = await stillWaiting.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task AcquireAsync_HonorsCancellationWhileWaiting()
    {
        var limiter = new GameArtworkDeliveryLimiter(maximumConcurrentDeliveries: 1, maximumConcurrentDeliveriesPerUser: 1);
        using var lease = await limiter.AcquireAsync(UserA, CancellationToken.None);
        using var cancellationSource = new CancellationTokenSource();

        var waiting = limiter.AcquireAsync(UserB, cancellationSource.Token);
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public async Task AcquireAsync_TimesOutWhenSaturatedInsteadOfHangingForever()
    {
        // A tiny acquisition timeout keeps this deterministic and fast: the point under test is
        // that a saturated pool fails fast with a dedicated exception, not the exact duration.
        var limiter = new GameArtworkDeliveryLimiter(
            maximumConcurrentDeliveries: 1,
            maximumConcurrentDeliveriesPerUser: 10,
            acquisitionTimeout: TimeSpan.FromMilliseconds(50));
        using var held = await limiter.AcquireAsync(UserA, CancellationToken.None);

        var attempt = limiter.AcquireAsync(UserB, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<ArtworkDeliveryUnavailableException>(() => attempt)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(ex.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public async Task AcquireAsync_OneUserCannotStarveAnother()
    {
        // Global pool has plenty of room; only the per-user cap should bind for UserA.
        var limiter = new GameArtworkDeliveryLimiter(
            maximumConcurrentDeliveries: 10,
            maximumConcurrentDeliveriesPerUser: 2,
            acquisitionTimeout: TimeSpan.FromMilliseconds(50));

        using var userAFirst = await limiter.AcquireAsync(UserA, CancellationToken.None);
        using var userASecond = await limiter.AcquireAsync(UserA, CancellationToken.None);

        // UserA is already at its per-user cap; a third concurrent request from UserA must fail
        // fast rather than consume a global slot that a different user could otherwise use.
        await Assert.ThrowsAsync<ArtworkDeliveryUnavailableException>(
            () => limiter.AcquireAsync(UserA, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(2));

        // UserB is unaffected: plenty of global slots remain and UserB has no per-user usage yet.
        using var userBLease = await limiter.AcquireAsync(UserB, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
    }
}
