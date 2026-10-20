using System.Collections.Concurrent;
using System.Reflection;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

// Detail (TryLookupAsync) and artwork (LookupArtworkNameAsync) lookups share one per-ROM match state.
public partial class RdbServiceTests
{
    private const string NesPlatform = "Nintendo - Nintendo Entertainment System";
    private const string SmsPlatform = "Sega - Master System - Mark III";
    private const string Sg1000Platform = "Sega - SG-1000";
    private const string FbneoPlatform = "FBNeo - Arcade Games";
    private const string MamePlatform = "MAME";
    private const string YardFightFileName = "10-Yard Fight (U) .nes";
    private const string YardFightTitle = "10-Yard Fight";
    private const string YardFightName = "10-Yard Fight (USA, Europe)";

    private static readonly TimeSpan LookupWait = TimeSpan.FromSeconds(10);
    private static readonly byte[] YardFightBytes = [9, 8, 7, 6, 5, 4, 3, 2];

    private sealed class LookupProbe
    {
        private int _crcComputations;
        private readonly ConcurrentDictionary<(string Platform, bool CrcOnly, string? Title), int> _matches = new();

        public int CrcComputations => Volatile.Read(ref _crcComputations);

        public Func<string, IReadOnlyList<uint>> Crc { get; set; } = RealCrc;

        public Action<string, bool, string?>? OnMatch { get; set; }

        public TaskCompletionSource CrcStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim CrcRelease { get; } = new(false);

        public static IReadOnlyList<uint> RealCrc(string path) => [Crc32(File.ReadAllBytes(path))];

        public int Matches(string platform, bool crcOnly, string? title) =>
            _matches.TryGetValue((platform, crcOnly, title), out var n) ? n : 0;

        public void Attach(RdbService service)
        {
            service.CrcOverrideForTests = path =>
            {
                Interlocked.Increment(ref _crcComputations);
                return Crc(path);
            };
            service.MatchOperationForTests = (platform, crcOnly, title) =>
            {
                _matches.AddOrUpdate((platform, crcOnly, title), 1, static (_, n) => n + 1);
                OnMatch?.Invoke(platform, crcOnly, title);
            };
        }

        // Replaces the checksum with one that blocks until CrcRelease is set, then runs inner.
        public void GateCrc(Func<string, IReadOnlyList<uint>> inner) =>
            Crc = path =>
            {
                CrcStarted.TrySetResult();
                if (!CrcRelease.Wait(LookupWait))
                {
                    throw new TimeoutException("CRC gate was never released.");
                }

                return inner(path);
            };
    }

    private (RdbService Service, LookupProbe Probe) CreateLookupService()
    {
        var service = new RdbService(new ThrowingHttpClientFactory(), new NoOpLogger<RdbService>(), _root)
        {
            ConfigOverrideForTests = new global::Moonfin.Server.PluginConfiguration
            {
                GamesMetadataEnabled = true,
                GamesMetadataDbUrlBase = string.Empty,
            },
        };
        var probe = new LookupProbe();
        probe.Attach(service);
        return (service, probe);
    }

    private string WriteRom(string fileName, byte[] bytes)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // A cold index makes the detail path return null without caching, so detail tests warm it first.
    private static async Task WarmIndexAsync(RdbService service, string platform)
    {
        var method = typeof(RdbService).GetMethod("GetIndexAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("RdbService.GetIndexAsync not found; has it been renamed?");
        var task = method.Invoke(service, [platform, service.ConfigOverrideForTests]) as Task
            ?? throw new InvalidOperationException("GetIndexAsync did not return a Task.");
        await task.WaitAsync(LookupWait);
        Assert.NotNull(task.GetType().GetProperty("Result")!.GetValue(task));
    }

    private static Task<ArtworkNameLookupResult> LookupArtwork(
        RdbService service, string[] platforms, string romPath, string? title, CancellationToken ct = default) =>
        service.LookupArtworkNameAsync(platforms, romPath, title, ct);

    [Fact]
    public async Task LookupArtworkNameAsync_CancelledDuringIndexBuild_StopsWaitingWithoutCancellingTheBuild()
    {
        var romPath = WriteRom(YardFightFileName, YardFightBytes);
        WriteMinimalRdb(RdbPath(NesPlatform), [(YardFightName, Crc32(YardFightBytes))]);
        var (service, _) = CreateLookupService();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        service.BuildIndexAboutToRunForTests = () =>
        {
            started.TrySetResult();
            Assert.True(release.Wait(LookupWait));
        };

        using var cancellation = new CancellationTokenSource();
        var cancelled = LookupArtwork(service, [NesPlatform], romPath, null, cancellation.Token);
        await started.Task.WaitAsync(LookupWait);
        var successful = LookupArtwork(service, [NesPlatform], romPath, null);
        try
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(LookupWait));
            Assert.False(successful.IsCompleted);
        }
        finally
        {
            release.Set();
            await successful.WaitAsync(LookupWait);
        }

        Assert.Equal(YardFightName, (await successful).Resolution?.Names[0]);
        Assert.Equal(1, service.BuildIndexCallCountForTests);
    }

    [Fact]
    public async Task LookupArtworkNameAsync_AfterDetailLookupOnTheSameRom_ResolvesTheCanonicalName()
    {
        var romPath = WriteRom(YardFightFileName, YardFightBytes);
        WriteMinimalRdb(RdbPath(NesPlatform), [(YardFightName, Crc32(YardFightBytes))]);
        var (service, _) = CreateLookupService();
        await WarmIndexAsync(service, NesPlatform);

        var record = await service.TryLookupAsync("nes", romPath, YardFightTitle);
        var candidates = RdbService.GetCandidatePlatforms("nes", coreWasDefaulted: false, fuzzyDirectoryCores: Array.Empty<string>());
        var artwork = await service.LookupArtworkNameAsync(candidates, romPath, YardFightTitle);

        Assert.Equal(YardFightName, record?.Name);
        Assert.Equal(YardFightName, artwork.Resolution?.Names[0]);
        Assert.Equal(NesPlatform, artwork.Resolution?.Platform);
    }

    [Fact]
    public async Task LookupArtworkNameAsync_AfterDetailLookupMatchedByNameOnly_ListsTheSiblings()
    {
        var romPath = WriteRom(YardFightFileName, YardFightBytes);
        WriteMinimalRdb(RdbPath(NesPlatform), [("10-Yard Fight (USA)", 1u), (YardFightName, 2u)]);
        var (service, _) = CreateLookupService();
        await WarmIndexAsync(service, NesPlatform);

        var record = await service.TryLookupAsync("nes", romPath, YardFightTitle);
        var artwork = await LookupArtwork(service, [NesPlatform], romPath, YardFightTitle);

        Assert.NotNull(record);
        Assert.NotNull(artwork.Resolution);
        Assert.Contains("10-Yard Fight (USA)", artwork.Resolution!.Names);
        Assert.Contains(YardFightName, artwork.Resolution.Names);
    }

    [Fact]
    public async Task LookupArtworkNameAsync_ListGainsASecondPlatformHoldingTheCrc_ResolvesViaThatPlatform()
    {
        var romPath = WriteRom("mystery.sg", YardFightBytes);
        WriteMinimalRdb(RdbPath(SmsPlatform), Array.Empty<(string, uint)>());
        WriteMinimalRdb(RdbPath(Sg1000Platform), [("Mystery Game (Japan)", Crc32(YardFightBytes))]);
        var (service, _) = CreateLookupService();

        var narrow = await LookupArtwork(service, [SmsPlatform], romPath, "Mystery Game");
        var wide = await LookupArtwork(service, [SmsPlatform, Sg1000Platform], romPath, "Mystery Game");

        Assert.False(narrow.IsIndexPending);
        Assert.Null(narrow.Resolution);
        Assert.Equal("Mystery Game (Japan)", wide.Resolution?.Names[0]);
        Assert.Equal(Sg1000Platform, wide.Resolution?.Platform);
    }

    [Fact]
    public async Task LookupArtworkNameAsync_ListLosesThePlatformHoldingTheCrc_NoLongerResolves()
    {
        var romPath = WriteRom("mystery.sg", YardFightBytes);
        WriteMinimalRdb(RdbPath(SmsPlatform), Array.Empty<(string, uint)>());
        WriteMinimalRdb(RdbPath(Sg1000Platform), [("Mystery Game (Japan)", Crc32(YardFightBytes))]);
        var (service, _) = CreateLookupService();

        var wide = await LookupArtwork(service, [SmsPlatform, Sg1000Platform], romPath, "Mystery Game");
        var narrow = await LookupArtwork(service, [SmsPlatform], romPath, "Mystery Game");
        var onlyHolder = await LookupArtwork(service, [Sg1000Platform], romPath, "Mystery Game");

        Assert.Equal(Sg1000Platform, wide.Resolution?.Platform);
        Assert.Null(narrow.Resolution);
        Assert.Equal(Sg1000Platform, onlyHolder.Resolution?.Platform);
    }

    [Fact]
    public async Task LookupArtworkNameAsync_ReorderedPlatformLists_NameMatchAndSiblingsFollowTheFirstPlatform()
    {
        // The ROM CRC is in neither index, so each list answers from name matching on its own first platform.
        var romPath = WriteRom("pacman.zip", YardFightBytes);
        WriteMinimalRdb(RdbPath(FbneoPlatform), [("Pac-Man (USA)", 1u), ("Pac-Man (Europe)", 2u)]);
        WriteMinimalRdb(RdbPath(MamePlatform), [("PacMan (USA)", 3u), ("PacMan (Europe)", 4u)]);
        var (service, _) = CreateLookupService();

        var fbneoFirst = await LookupArtwork(service, [FbneoPlatform, MamePlatform], romPath, "pacman");
        var mameFirst = await LookupArtwork(service, [MamePlatform, FbneoPlatform], romPath, "pacman");
        var fbneoAgain = await LookupArtwork(service, [FbneoPlatform, MamePlatform], romPath, "pacman");

        Assert.Equal(FbneoPlatform, fbneoFirst.Resolution?.Platform);
        Assert.Equal(new[] { "Pac-Man (USA)", "Pac-Man (Europe)" }, fbneoFirst.Resolution?.Names);
        Assert.Equal(MamePlatform, mameFirst.Resolution?.Platform);
        Assert.Equal(new[] { "PacMan (USA)", "PacMan (Europe)" }, mameFirst.Resolution?.Names);
        Assert.Equal(new[] { "Pac-Man (USA)", "Pac-Man (Europe)" }, fbneoAgain.Resolution?.Names);
    }

    [Fact]
    public async Task LookupArtworkNameAsync_NameHitCachedForOnePlatform_IsNotServedToAListWithoutItAsFirst()
    {
        var romPath = WriteRom("Quest Game.nes", YardFightBytes);
        WriteMinimalRdb(RdbPath(NesPlatform), [("Quest Game", 1u)]);
        WriteMinimalRdb(RdbPath(Sg1000Platform), Array.Empty<(string, uint)>());
        var (service, _) = CreateLookupService();
        await WarmIndexAsync(service, NesPlatform);

        var record = await service.TryLookupAsync("nes", romPath, "Quest Game");
        var withoutNes = await LookupArtwork(service, [Sg1000Platform], romPath, "Quest Game");
        // NES is present but not first, so only its CRC may match.
        var nesSecond = await LookupArtwork(service, [Sg1000Platform, NesPlatform], romPath, "Quest Game");

        Assert.NotNull(record);
        Assert.Null(withoutNes.Resolution);
        Assert.False(withoutNes.IsIndexPending);
        Assert.Null(nesSecond.Resolution);
    }

    [Fact]
    public async Task TryLookupAsync_AfterArtworkMissWithADifferentFirstPlatform_StillFindsTheDetailPlatformMatch()
    {
        var romPath = WriteRom("Quest Game.nes", YardFightBytes);
        WriteMinimalRdb(RdbPath(NesPlatform), [("Quest Game", 1u)]);
        WriteMinimalRdb(RdbPath(SmsPlatform), Array.Empty<(string, uint)>());
        var (service, _) = CreateLookupService();
        await WarmIndexAsync(service, NesPlatform);

        var artwork = await LookupArtwork(service, [SmsPlatform, NesPlatform], romPath, "Quest Game");
        var record = await service.TryLookupAsync("nes", romPath, "Quest Game");

        Assert.Null(artwork.Resolution);
        Assert.Equal("Quest Game", record?.Name);
    }

    [Fact]
    public async Task TryLookupAsync_AfterArtworkMissForTheSameScope_AlsoMisses()
    {
        var romPath = WriteRom("Quest Game.nes", YardFightBytes);
        WriteMinimalRdb(RdbPath(NesPlatform), [("Something Else Entirely", 1u)]);
        var (service, _) = CreateLookupService();
        await WarmIndexAsync(service, NesPlatform);

        var artwork = await LookupArtwork(service, [NesPlatform], romPath, "Quest Game");
        var record = await service.TryLookupAsync("nes", romPath, "Quest Game");

        Assert.Null(artwork.Resolution);
        Assert.False(artwork.IsIndexPending);
        Assert.Null(record);
    }

    [Fact]
    public async Task TryLookupAsync_NameMatchForOneTitle_IsNotServedForAnotherTitle()
    {
        var romPath = WriteRom("rom12345.nes", YardFightBytes);
        WriteMinimalRdb(RdbPath(NesPlatform), [("Alpha Quest", 1u), ("Beta Quest", 2u)]);
        var (service, _) = CreateLookupService();
        await WarmIndexAsync(service, NesPlatform);

        var alpha = await service.TryLookupAsync("nes", romPath, "Alpha Quest");
        var beta = await service.TryLookupAsync("nes", romPath, "Beta Quest");
        var alphaAgain = await service.TryLookupAsync("nes", romPath, "Alpha Quest");

        Assert.Equal("Alpha Quest", alpha?.Name);
        Assert.Equal("Beta Quest", beta?.Name);
        Assert.Equal("Alpha Quest", alphaAgain?.Name);
    }

    [Fact]
    public async Task LookupArtworkNameAsync_NameMatchForOneTitle_IsNotServedForAnotherTitle()
    {
        var romPath = WriteRom("rom12345.nes", YardFightBytes);
        WriteMinimalRdb(RdbPath(NesPlatform), [("Alpha Quest", 1u), ("Beta Quest", 2u)]);
        var (service, _) = CreateLookupService();

        var alpha = await LookupArtwork(service, [NesPlatform], romPath, "Alpha Quest");
        var beta = await LookupArtwork(service, [NesPlatform], romPath, "Beta Quest");

        Assert.Equal("Alpha Quest", alpha.Resolution?.Names[0]);
        Assert.Equal("Beta Quest", beta.Resolution?.Names[0]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OverlappingDetailAndArtworkLookups_ForTheSameKey_ShareOneChecksumAndOneMatch(bool detailFirst)
    {
        var romPath = WriteRom(YardFightFileName, YardFightBytes);
        WriteMinimalRdb(RdbPath(NesPlatform), [(YardFightName, Crc32(YardFightBytes))]);
        var (service, probe) = CreateLookupService();
        await WarmIndexAsync(service, NesPlatform);
        probe.GateCrc(LookupProbe.RealCrc);

        Task<RdbRecord?> StartDetail() => service.TryLookupAsync("nes", romPath, YardFightTitle);
        Task<ArtworkNameLookupResult> StartArtwork() => LookupArtwork(service, [NesPlatform], romPath, YardFightTitle);

        // The second lookup starts while the first search is still in flight on the gated checksum.
        Task<RdbRecord?> detail;
        Task<ArtworkNameLookupResult> artwork;
        if (detailFirst)
        {
            detail = StartDetail();
            await probe.CrcStarted.Task.WaitAsync(LookupWait);
            artwork = StartArtwork();
        }
        else
        {
            artwork = StartArtwork();
            await probe.CrcStarted.Task.WaitAsync(LookupWait);
            detail = StartDetail();
        }

        probe.CrcRelease.Set();
        var record = await detail.WaitAsync(LookupWait);
        var names = await artwork.WaitAsync(LookupWait);

        Assert.Equal(YardFightName, record?.Name);
        Assert.Equal(YardFightName, names.Resolution?.Names[0]);
        Assert.Equal(1, probe.CrcComputations);
        Assert.Equal(1, probe.Matches(NesPlatform, false, "10yardfight"));

        var detailAgain = await StartDetail();
        var artworkAgain = await StartArtwork();
        Assert.Equal(YardFightName, detailAgain?.Name);
        Assert.Equal(YardFightName, artworkAgain.Resolution?.Names[0]);
        Assert.Equal(1, probe.CrcComputations);
        Assert.Equal(1, probe.Matches(NesPlatform, false, "10yardfight"));
    }

    [Fact]
    public async Task LookupArtworkNameAsync_CancelledWaiterBesideASuccessfulOne_OnlyTheCancelledOneThrows()
    {
        var romPath = WriteRom(YardFightFileName, YardFightBytes);
        WriteMinimalRdb(RdbPath(NesPlatform), [(YardFightName, Crc32(YardFightBytes))]);
        var (service, probe) = CreateLookupService();
        await WarmIndexAsync(service, NesPlatform);
        probe.GateCrc(LookupProbe.RealCrc);
        using var cts = new CancellationTokenSource();

        var cancelled = LookupArtwork(service, [NesPlatform], romPath, YardFightTitle, cts.Token);
        var surviving = LookupArtwork(service, [NesPlatform], romPath, YardFightTitle);
        await probe.CrcStarted.Task.WaitAsync(LookupWait);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(LookupWait));
        probe.CrcRelease.Set();
        var result = await surviving.WaitAsync(LookupWait);
        var third = await LookupArtwork(service, [NesPlatform], romPath, YardFightTitle);

        Assert.Equal(YardFightName, result.Resolution?.Names[0]);
        Assert.Equal(YardFightName, third.Resolution?.Names[0]);
        Assert.Equal(1, probe.CrcComputations);
        Assert.Equal(1, probe.Matches(NesPlatform, false, "10yardfight"));
    }

    [Fact]
    public async Task LookupArtworkNameAsync_ChecksumFailsOnce_FallsBackToNameAndRecomputesNextTime()
    {
        var romPath = WriteRom("Foo Quest.nes", YardFightBytes);
        WriteMinimalRdb(RdbPath(NesPlatform), [("Foo Quest", Crc32(YardFightBytes))]);
        var (service, probe) = CreateLookupService();
        await WarmIndexAsync(service, NesPlatform);
        var calls = 0;
        probe.Crc = path => Interlocked.Increment(ref calls) == 1 ? throw new IOException("unreadable") : LookupProbe.RealCrc(path);

        var first = await LookupArtwork(service, [NesPlatform], romPath, "Foo Quest");
        var second = await LookupArtwork(service, [NesPlatform], romPath, "Foo Quest");
        var third = await service.TryLookupAsync("nes", romPath, "Foo Quest");

        Assert.Equal("Foo Quest", first.Resolution?.Names[0]);
        Assert.True(first.IsIndexPending);
        Assert.False(first.Resolution!.PrimaryIsCrcMatch);
        Assert.Equal("Foo Quest", second.Resolution?.Names[0]);
        Assert.False(second.IsIndexPending);
        Assert.True(second.Resolution!.PrimaryIsCrcMatch);
        Assert.Equal("Foo Quest", third?.Name);
        Assert.Equal(2, probe.CrcComputations);
    }

    [Fact]
    public async Task LookupArtworkNameAsync_ChecksumFailsWithNoNameMatch_IsPendingNotAMiss()
    {
        var romPath = WriteRom("rom12345.nes", YardFightBytes);
        WriteMinimalRdb(RdbPath(NesPlatform), [("Foo Quest", Crc32(YardFightBytes))]);
        var (service, probe) = CreateLookupService();
        await WarmIndexAsync(service, NesPlatform);
        var calls = 0;
        probe.Crc = path => Interlocked.Increment(ref calls) == 1 ? throw new IOException("unreadable") : LookupProbe.RealCrc(path);

        var first = await LookupArtwork(service, [NesPlatform], romPath, "rom12345");
        var second = await LookupArtwork(service, [NesPlatform], romPath, "rom12345");

        Assert.True(first.IsIndexPending);
        Assert.Null(first.Resolution);
        Assert.Equal("Foo Quest", second.Resolution?.Names[0]);
    }

    [Fact]
    public async Task LookupArtworkNameAsync_EveryWaiterCancelsBeforeTheChecksumFaults_LaterLookupRecomputes()
    {
        var romPath = WriteRom("Foo Quest.nes", YardFightBytes);
        WriteMinimalRdb(RdbPath(NesPlatform), [("Foo Quest", Crc32(YardFightBytes))]);
        var (service, probe) = CreateLookupService();
        await WarmIndexAsync(service, NesPlatform);
        var calls = 0;
        probe.GateCrc(path => Interlocked.Increment(ref calls) == 1 ? throw new IOException("unreadable") : LookupProbe.RealCrc(path));
        // Fires once the faulted checksum has been observed, when no caller is left waiting.
        var failureObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.OnMatch = (_, _, _) => failureObserved.TrySetResult();
        using var cts = new CancellationTokenSource();

        var cancelled = LookupArtwork(service, [NesPlatform], romPath, "Foo Quest", cts.Token);
        await probe.CrcStarted.Task.WaitAsync(LookupWait);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(LookupWait));
        probe.CrcRelease.Set();
        await failureObserved.Task.WaitAsync(LookupWait);

        // A different title is a new match key, so only the checksum instance can be reused.
        var later = await LookupArtwork(service, [NesPlatform], romPath, "Foo Quest Again");

        Assert.Equal("Foo Quest", later.Resolution?.Names[0]);
        Assert.Equal(2, probe.CrcComputations);
    }

    [Fact]
    public async Task TryLookupAsync_MoreTitlesThanTheRetentionLimit_StaysCorrectAndEvictsOldestOnly()
    {
        const int titleCount = 20;
        var romPath = WriteRom("rom12345.nes", YardFightBytes);
        WriteMinimalRdb(RdbPath(NesPlatform), Enumerable.Range(0, titleCount).Select(i => ($"Title {i:00}", (uint)(i + 1))).ToArray());
        var (service, probe) = CreateLookupService();
        await WarmIndexAsync(service, NesPlatform);

        async Task AssertLookup(int i)
        {
            var record = await service.TryLookupAsync("nes", romPath, $"Title {i:00}");
            Assert.Equal($"Title {i:00}", record?.Name);
        }

        int TotalMatches() => Enumerable.Range(0, titleCount).Sum(i => probe.Matches(NesPlatform, false, $"title{i:00}"));

        for (var i = 0; i < titleCount; i++)
        {
            await AssertLookup(i);
        }

        Assert.Equal(titleCount, TotalMatches());

        // The 16 most recent titles are retained, so re-asking for them runs no new match.
        for (var i = 4; i < titleCount; i++)
        {
            await AssertLookup(i);
        }

        Assert.Equal(titleCount, TotalMatches());

        // The oldest title was evicted, so it is matched again.
        await AssertLookup(3);
        Assert.Equal(2, probe.Matches(NesPlatform, false, "title03"));
        Assert.Equal(1, probe.CrcComputations);
    }

    [Fact]
    public async Task TryLookupAsync_EvictionsWhileASearchIsInFlight_KeepTheInFlightSearchForASecondCaller()
    {
        const int otherTitles = 20;
        var romPath = WriteRom("rom12345.nes", YardFightBytes);
        var games = Enumerable.Range(0, otherTitles).Select(i => ($"Title {i:00}", (uint)(i + 1))).ToList();
        games.Add(("Held Title", 999u));
        WriteMinimalRdb(RdbPath(NesPlatform), games);
        var (service, probe) = CreateLookupService();
        await WarmIndexAsync(service, NesPlatform);

        var heldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var heldGate = new ManualResetEventSlim(false);
        probe.OnMatch = (_, _, title) =>
        {
            if (title == "heldtitle")
            {
                heldStarted.TrySetResult();
                if (!heldGate.Wait(LookupWait))
                {
                    throw new TimeoutException("Held search was never released.");
                }
            }
        };

        var first = service.TryLookupAsync("nes", romPath, "Held Title");
        await heldStarted.Task.WaitAsync(LookupWait);
        for (var i = 0; i < otherTitles; i++)
        {
            var other = await service.TryLookupAsync("nes", romPath, $"Title {i:00}").WaitAsync(LookupWait);
            Assert.Equal($"Title {i:00}", other?.Name);
        }

        var second = service.TryLookupAsync("nes", romPath, "Held Title");
        heldGate.Set();
        var firstRecord = await first.WaitAsync(LookupWait);
        var secondRecord = await second.WaitAsync(LookupWait);

        Assert.Equal("Held Title", firstRecord?.Name);
        Assert.Equal("Held Title", secondRecord?.Name);
        Assert.Equal(1, probe.Matches(NesPlatform, false, "heldtitle"));
        Assert.Equal(1, probe.CrcComputations);
    }

    [Fact]
    public async Task TryLookupAsync_AfterArtworkResolvedTheSamePlatformAndTitle_ReusesTheMatchWithoutAChecksum()
    {
        var romPath = WriteRom(YardFightFileName, YardFightBytes);
        WriteMinimalRdb(RdbPath(NesPlatform), [(YardFightName, Crc32(YardFightBytes))]);
        var (service, probe) = CreateLookupService();
        await WarmIndexAsync(service, NesPlatform);

        var artwork = await LookupArtwork(service, [NesPlatform], romPath, YardFightTitle);
        var record = await service.TryLookupAsync("nes", romPath, YardFightTitle);

        Assert.Equal(YardFightName, artwork.Resolution?.Names[0]);
        Assert.Equal(YardFightName, record?.Name);
        Assert.Equal(1, probe.CrcComputations);
        Assert.Equal(1, probe.Matches(NesPlatform, false, "10yardfight"));
    }

    [Fact]
    public async Task LookupArtworkNameAsync_ColdIndex_IsPendingWithoutChecksumAndResolvesOnceLoaded()
    {
        var romPath = WriteRom(YardFightFileName, YardFightBytes);
        var (service, probe) = CreateLookupService();

        var cold = await LookupArtwork(service, [NesPlatform], romPath, YardFightTitle);
        Assert.True(cold.IsIndexPending);
        Assert.Equal(0, probe.CrcComputations);

        WriteMinimalRdb(RdbPath(NesPlatform), [(YardFightName, Crc32(YardFightBytes))]);
        var warm = await LookupArtwork(service, [NesPlatform], romPath, YardFightTitle);

        Assert.False(warm.IsIndexPending);
        Assert.Equal(YardFightName, warm.Resolution?.Names[0]);
    }

    [Fact]
    public async Task TryLookupAsync_ColdIndex_ReturnsNullWithoutCachingAndResolvesOnceLoaded()
    {
        var romPath = WriteRom(YardFightFileName, YardFightBytes);
        var (service, probe) = CreateLookupService();

        var cold = await service.TryLookupAsync("nes", romPath, YardFightTitle);
        Assert.Null(cold);
        Assert.Equal(0, probe.CrcComputations);

        WriteMinimalRdb(RdbPath(NesPlatform), [(YardFightName, Crc32(YardFightBytes))]);
        await WarmIndexAsync(service, NesPlatform);
        var warm = await service.TryLookupAsync("nes", romPath, YardFightTitle);

        Assert.Equal(YardFightName, warm?.Name);
    }
}
