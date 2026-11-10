using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public sealed class SocketRelayManagerTests : IDisposable
{
    // Upstream points at a closed port, so every relay just sits in reconnecting.
    private readonly SocketRelayManager _manager = new(_ => "http://127.0.0.1:1", new NoOpLogger<SocketRelayManager>());

    public void Dispose() => _manager.Dispose();

    [Fact]
    public async Task AnotherUserWithTheSameDeviceIdGetsTheirOwnRelay()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        Assert.True(_manager.EnsureRelay(alice, "roku", "alice-token")!.Created);
        Assert.True(_manager.EnsureRelay(bob, "roku", "bob-token")!.Created);

        Assert.Equal(2, _manager.Count);
        var alicePoll = await _manager.PollAsync(alice, "roku", "alice-token", 0, TimeSpan.Zero, default);
        Assert.NotNull(alicePoll);
        Assert.NotEqual("gone", alicePoll.State);
        Assert.Null(await _manager.PollAsync(bob, "roku", "alice-token", 0, TimeSpan.Zero, default));
    }

    [Fact]
    public void OneUserIsRefusedPastTheirCap()
    {
        var user = Guid.NewGuid();
        for (var i = 0; i < SocketRelayManager.MaxRelaysPerUser; i++)
        {
            Assert.NotNull(_manager.EnsureRelay(user, $"device-{i}", "token"));
        }

        Assert.Null(_manager.EnsureRelay(user, "one-more", "token"));
        Assert.NotNull(_manager.EnsureRelay(user, "device-0", "token"));
        Assert.NotNull(_manager.EnsureRelay(Guid.NewGuid(), "device-0", "other-token"));
    }

    [Fact]
    public void TheServerIsRefusedPastItsCap()
    {
        for (var i = 0; i < SocketRelayManager.MaxRelays; i++)
        {
            Assert.NotNull(_manager.EnsureRelay(Guid.NewGuid(), "roku", $"token-{i}"));
        }

        Assert.Null(_manager.EnsureRelay(Guid.NewGuid(), "roku", "late-token"));
        Assert.Equal(SocketRelayManager.MaxRelays, _manager.Count);
    }

    [Fact]
    public void ReopeningReusesTheRelay()
    {
        var user = Guid.NewGuid();
        Assert.True(_manager.EnsureRelay(user, "roku", "token")!.Created);

        var again = _manager.EnsureRelay(user, "roku", "token")!;

        Assert.False(again.Created);
        Assert.Equal(0, again.Cursor);
        Assert.Equal(1, _manager.Count);
    }

    [Fact]
    public async Task ANewTokenReplacesAndStopsTheOldRelay()
    {
        var user = Guid.NewGuid();
        _manager.EnsureRelay(user, "roku", "old-token");
        var parked = _manager.PollAsync(user, "roku", "old-token", 0, TimeSpan.FromSeconds(25), default);

        Assert.True(_manager.EnsureRelay(user, "roku", "new-token")!.Created);

        var oldPoll = await parked.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("gone", oldPoll!.State);
        Assert.Equal(1, _manager.Count);
        Assert.Null(await _manager.PollAsync(user, "roku", "old-token", 0, TimeSpan.Zero, default));
        Assert.NotNull(await _manager.PollAsync(user, "roku", "new-token", 0, TimeSpan.Zero, default));
    }

    [Fact]
    public async Task ConcurrentOpensCreateOneRelay()
    {
        var user = Guid.NewGuid();
        using var start = new ManualResetEventSlim();
        var opens = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                return _manager.EnsureRelay(user, "roku", "token");
            }))
            .ToList();

        start.Set();
        var results = await Task.WhenAll(opens);

        Assert.Single(results, r => r!.Created);
        Assert.Equal(1, _manager.Count);
    }

    [Fact]
    public async Task CloseStopsTheRelayAndReleasesAParkedPoll()
    {
        var user = Guid.NewGuid();
        _manager.EnsureRelay(user, "roku", "token");
        var parked = _manager.PollAsync(user, "roku", "token", 0, TimeSpan.FromSeconds(25), default);

        _manager.CloseRelay(user, "roku", "token");

        var result = await parked.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("gone", result!.State);
        Assert.Equal(0, _manager.Count);
    }
}
