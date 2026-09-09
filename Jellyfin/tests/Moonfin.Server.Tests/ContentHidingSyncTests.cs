using Moonfin.Server.Models;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// Hiding an item from Continue Watching or Next Up syncs as a whole map of item id to the time it
/// was hidden, and unhiding is expressed by dropping the key rather than by any kind of tombstone.
/// The server therefore has to remember what each profile was last handed, or it can't tell a key
/// a client removed from one it was never sent.
/// </summary>
public sealed class ContentHidingSyncTests : IDisposable
{
    private readonly string _dataPath;
    private readonly MoonfinSettingsService _service;

    public ContentHidingSyncTests()
    {
        _dataPath = Path.Combine(Path.GetTempPath(), "moonfin-hiding-tests-" + Guid.NewGuid().ToString("N"));
        _service = new MoonfinSettingsService(new NoOpLogger<MoonfinSettingsService>(), _dataPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    private Task PushAsync(Guid userId, string profile, string? hiddenContinueWatching = null, string? hiddenNextUp = null) =>
        _service.SaveProfileAsync(
            userId,
            profile,
            new MoonfinSettingsProfile
            {
                HiddenContinueWatchingItems = hiddenContinueWatching,
                HiddenNextUpSeries = hiddenNextUp,
            },
            profile + "-client",
            notifySettingsChanged: false);

    /// <summary>
    /// A client pulling its resolved profile, which is the only point at which it learns about
    /// hides made on another device.
    /// </summary>
    private async Task PullAsync(Guid userId, string profile)
    {
        var resolved = await _service.GetResolvedProfileAsync(userId, profile);
        Assert.NotNull(resolved);
        await _service.RecordHiddenContentBaselineAsync(userId, profile, resolved!);
    }

    private async Task<string> GlobalHiddenAsync(Guid userId)
    {
        var settings = await _service.GetUserSettingsAsync(userId);
        return settings?.Global?.HiddenContinueWatchingItems ?? string.Empty;
    }

    [Fact]
    public async Task DevicesThatHaveNotPulledKeepEachOthersHides()
    {
        var userId = Guid.NewGuid();

        await PushAsync(userId, "tv", "{\"a\":\"2026-09-01T01:00:00Z\"}");

        // Mobile hid something of its own without ever pulling, so it has no idea "a" exists and
        // leaving it out of the push says nothing about whether the user still wants it hidden.
        await PushAsync(userId, "mobile", "{\"b\":\"2026-09-01T02:00:00Z\"}");

        var hidden = await GlobalHiddenAsync(userId);
        Assert.Contains("\"a\"", hidden, StringComparison.Ordinal);
        Assert.Contains("\"b\"", hidden, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnhidingAfterAPullClearsTheItemEverywhere()
    {
        var userId = Guid.NewGuid();

        await PushAsync(userId, "tv", "{\"a\":\"2026-09-01T01:00:00Z\"}");
        await PullAsync(userId, "tv");

        // The user unhid "a" on the TV, which reaches the server as a push without that key.
        await PushAsync(userId, "tv", "{}");

        Assert.DoesNotContain("\"a\"", await GlobalHiddenAsync(userId), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplayingAHiddenItemClearsIt()
    {
        var userId = Guid.NewGuid();

        await PushAsync(userId, "tv", "{\"a\":\"2026-09-01T01:00:00Z\",\"b\":\"2026-09-01T01:05:00Z\"}");
        await PullAsync(userId, "tv");

        // Playing a hidden item drops it from the client's map on its own, with no user action and
        // nothing else to signal the change, so a push that is simply missing the key has to stick
        // or the series stays out of Continue Watching while it's being watched.
        await PushAsync(userId, "tv", "{\"b\":\"2026-09-01T01:05:00Z\"}");

        var hidden = await GlobalHiddenAsync(userId);
        Assert.DoesNotContain("\"a\"", hidden, StringComparison.Ordinal);
        Assert.Contains("\"b\"", hidden, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnhidingOnlyClearsWhatThatDeviceHadBeenTold()
    {
        var userId = Guid.NewGuid();

        await PushAsync(userId, "tv", "{\"a\":\"2026-09-01T01:00:00Z\"}");
        await PullAsync(userId, "mobile");

        // The TV hides something more after mobile's last pull, so mobile's next push predates it.
        await PushAsync(userId, "tv", "{\"a\":\"2026-09-01T01:00:00Z\",\"c\":\"2026-09-01T03:00:00Z\"}");

        // Mobile unhides "a" and hides "b". It knew about "a" so that removal counts, and it has
        // never seen "c" so the omission there doesn't.
        await PushAsync(userId, "mobile", "{\"b\":\"2026-09-01T02:00:00Z\"}");

        var hidden = await GlobalHiddenAsync(userId);
        Assert.DoesNotContain("\"a\"", hidden, StringComparison.Ordinal);
        Assert.Contains("\"b\"", hidden, StringComparison.Ordinal);
        Assert.Contains("\"c\"", hidden, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HiddenNextUpSeriesMergesTheSameWay()
    {
        var userId = Guid.NewGuid();

        await PushAsync(userId, "tv", hiddenNextUp: "{\"a\":\"2026-09-01T01:00:00Z\"}");
        await PushAsync(userId, "mobile", hiddenNextUp: "{\"b\":\"2026-09-01T02:00:00Z\"}");

        var settings = await _service.GetUserSettingsAsync(userId);
        var hidden = settings?.Global?.HiddenNextUpSeries ?? string.Empty;
        Assert.Contains("\"a\"", hidden, StringComparison.Ordinal);
        Assert.Contains("\"b\"", hidden, StringComparison.Ordinal);

        await PullAsync(userId, "tv");
        await PushAsync(userId, "tv", hiddenNextUp: "{\"b\":\"2026-09-01T02:00:00Z\"}");

        settings = await _service.GetUserSettingsAsync(userId);
        Assert.DoesNotContain("\"a\"", settings?.Global?.HiddenNextUpSeries ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecondHideOfTheSameItemKeepsTheLaterTimestamp()
    {
        var userId = Guid.NewGuid();

        await PushAsync(userId, "global", "{\"a\":\"2026-09-01T01:00:00.123456Z\"}");

        // Comparing these as text puts the shorter one first, because its Z sorts above the digits
        // the longer one still has, so the tie-break has to read them as dates.
        await PushAsync(userId, "tv", "{\"a\":\"2026-09-01T01:00:00.123Z\"}");

        Assert.Contains("123456", await GlobalHiddenAsync(userId), StringComparison.Ordinal);
    }

    /// <summary>
    /// The baselines are the server's own bookkeeping, so a save that only writes them has to stay
    /// out of the change broadcast. Sending one anyway tells every client to pull settings that
    /// didn't change, and the pull and push that follow keep an echo loop alive.
    /// </summary>
    [Fact]
    public async Task ASaveThatOnlyMovesABaselineDoesNotBroadcast()
    {
        var userId = Guid.NewGuid();

        await _service.SaveProfileAsync(
            userId,
            "mobile",
            new MoonfinSettingsProfile { CinemaModeEnabled = true },
            "mobile-client",
            notifySettingsChanged: false);
        await PushAsync(userId, "tv", "{\"a\":\"2026-09-01T01:00:00Z\"}");

        var channel = _service.RegisterSseChannel(userId);

        // Mobile pushes the hide it already agrees with, which leaves everything a client reads
        // exactly as it was and only fills in mobile's baseline.
        await _service.SaveProfileAsync(
            userId,
            "mobile",
            new MoonfinSettingsProfile { HiddenContinueWatchingItems = "{\"a\":\"2026-09-01T01:00:00Z\"}" },
            "mobile-client",
            notifySettingsChanged: true);

        Assert.False(channel.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ASaveWithoutHiddenItemsLeavesThemAlone()
    {
        var userId = Guid.NewGuid();

        await PushAsync(userId, "tv", "{\"a\":\"2026-09-01T01:00:00Z\"}");
        await PullAsync(userId, "tv");

        await _service.SaveProfileAsync(
            userId,
            "tv",
            new MoonfinSettingsProfile { CinemaModeEnabled = true },
            "tv-client",
            notifySettingsChanged: false);

        Assert.Contains("\"a\"", await GlobalHiddenAsync(userId), StringComparison.Ordinal);
    }
}
