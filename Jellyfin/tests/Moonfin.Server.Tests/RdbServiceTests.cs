using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public class RdbServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"moonfin-rdb-tests-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("mame", "MAME")]
    [InlineData("arcade", "FBNeo - Arcade Games")]
    [InlineData("nes", "Nintendo - Nintendo Entertainment System")]
    public void TryGetPlatform_MapsSupportedCore(string core, string expectedPlatform)
    {
        var found = RdbService.TryGetPlatform(core, out var platform);

        Assert.True(found);
        Assert.Equal(expectedPlatform, platform);
    }

    // Regression test for the build-failure-poisoning bug (item 4.6): a transient failure (a
    // partially written .rdb, a file locked by a concurrent download, a truncated mirror copy)
    // must never permanently disable metadata for that platform until the server restarts.
    // GetIndexAsync enforces this by only ever inserting a platform into _indexes AFTER a build
    // succeeds -- a failed build inserts nothing, so the very next call (holding or waiting on
    // the same per-platform gate) simply retries instead of rethrowing a cached failure.
    //
    // This drives the REAL, private GetIndexAsync method end-to-end (via reflection only to reach the
    // private method itself -- the assertion path is entirely production code). RdbService's
    // data-folder resolution normally goes through the static MoonfinPlugin.Instance singleton,
    // which nothing in this test project sets up, so this uses the `internal RdbService(...,
    // string dataFolderPath)` test constructor (mirroring the existing internal-constructor
    // pattern in ArcadeCompatibilityService) to point LocalPath at a fixture folder instead.
    [Fact]
    public async Task GetIndexAsync_RecoversAfterATransientBuildFailure()
    {
        const string platform = "test-platform";
        var rdbPath = Path.Combine(_root, "gamemeta", platform + ".rdb");
        Directory.CreateDirectory(Path.GetDirectoryName(rdbPath)!);
        File.WriteAllBytes(rdbPath, [0]); // placeholder; overwritten below once locked

        var service = new RdbService(new ThrowingHttpClientFactory(), new NoOpLogger<RdbService>(), _root);
        var getIndexMethod = typeof(RdbService).GetMethod("GetIndexAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("RdbService.GetIndexAsync not found; has it been renamed?");

        // Simulate "a file locked by a concurrent download" (the exact transient failure the fix
        // targets, per its own comment): hold an exclusive lock on the .rdb file so File.Exists
        // still passes (GetIndexAsync proceeds into the gate and calls BuildIndex) but the read
        // inside BuildIndex (via RdbReader.ReadAll -> File.ReadAllBytes) throws a
        // sharing-violation IOException. If GetIndexAsync ever inserted a result into _indexes before
        // or regardless of that failure, the assertions below would catch it.
        var firstCallFailed = false;
        using (new FileStream(rdbPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try
            {
                await InvokeGetIndexAsync(getIndexMethod, service, platform);
            }
            catch (Exception)
            {
                // Deliberately catching broadly rather than pattern-matching a specific exception
                // shape: MethodInfo.Invoke's wrapping behavior is a reflection implementation
                // detail, and the fix under test guards against any transient BuildIndex failure,
                // not specifically a sharing violation.
                firstCallFailed = true;
            }
        }

        Assert.True(firstCallFailed, "Expected the first GetIndexAsync call to surface the locked-file failure.");

        // Lock released. Simulate the transient failure clearing (e.g. the concurrent download
        // finishing): overwrite with a minimal but valid, empty .rdb file (8-byte magic + 8-byte
        // zero metadata offset, no records).
        var validEmptyRdb = new byte[16];
        "RARCHDB\0"u8.CopyTo(validEmptyRdb);
        File.WriteAllBytes(rdbPath, validEmptyRdb);

        // Second call must retry (not rethrow a cached exception) and succeed. If someone made
        // GetIndexAsync publish a placeholder/result into _indexes before or despite a failed build,
        // this call would return the poisoned result (or rethrow) and the assertion below would
        // fail.
        var result = await InvokeGetIndexAsync(getIndexMethod, service, platform);
        Assert.NotNull(result);
    }

    // Regression test for the thundering-herd bug on the cold thumbnail path: GameThumbService
    // calls into RdbService.GetIndexAsync once per poster/thumbnail request, and a cold arcade
    // library's first paint can fire many of these concurrently for the SAME platform (one per
    // tile rendering on screen). A per-platform single-flight gate serializes the build so only
    // one caller actually runs BuildIndex.
    //
    // An earlier round of this fix released every caller from one shared gate simultaneously,
    // which does NOT reliably reproduce the bug it was meant to catch: an earlier (buggy)
    // implementation wrapped the build in a Lazy<PlatformIndex> and published that Lazy into
    // _indexes via GetOrAdd BEFORE calling .Value (i.e. before the build had run or completed).
    // A second caller arriving while the first was still building would hit GetIndexAsync's fast-path
    // TryGetValue check, find the already-published-but-not-yet-built Lazy, and call .Value on
    // it directly -- completely bypassing the semaphore. Because that Lazy used
    // LazyThreadSafetyMode.PublicationOnly, calling .Value concurrently on a not-yet-published
    // instance does not block; it re-runs the factory (BuildIndex) independently. Releasing all
    // callers from one ManualResetEventSlim at once masked this: every thread's own fast-path
    // check raced *before* any of them got far enough to publish anything into _indexes, so they
    // all happened to queue on the gate honestly in that specific test shape.
    //
    // This version instead uses STAGGERED arrival, matching how the reviewer who caught the
    // regression reproduced it: one caller is let all the way into BuildIndex (confirmed via the
    // BuildIndexAboutToRunForTests test hook, which blocks it there deterministically) before any
    // other caller so much as calls GetIndexAsync. Only once the first call is definitely "in flight"
    // are the remaining callers started and given a real window to race in. If a bug of this
    // exact shape were reintroduced, one or more of the later callers would find a
    // not-yet-complete something in _indexes on their fast path and build independently, and
    // BuildIndexCallCountForTests would be >1.
    [Fact]
    public async Task GetIndexAsync_StaggeredConcurrentCallers_BuildIndexOnlyOnce()
    {
        const string platform = "concurrent-platform";
        var rdbPath = Path.Combine(_root, "gamemeta", platform + ".rdb");
        Directory.CreateDirectory(Path.GetDirectoryName(rdbPath)!);
        // Minimal but valid, empty .rdb file (8-byte magic + 8-byte zero metadata offset, no
        // records) -- same fixture shape used by the transient-failure test above.
        var validEmptyRdb = new byte[16];
        "RARCHDB\0"u8.CopyTo(validEmptyRdb);
        File.WriteAllBytes(rdbPath, validEmptyRdb);

        var service = new RdbService(new ThrowingHttpClientFactory(), new NoOpLogger<RdbService>(), _root);
        var getIndexMethod = typeof(RdbService).GetMethod("GetIndexAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("RdbService.GetIndexAsync not found; has it been renamed?");

        using var buildStarted = new ManualResetEventSlim(false);
        using var releaseBuild = new ManualResetEventSlim(false);

        // Holds the FIRST build "in flight" (inside GetIndexAsync's gate, past the double-checked
        // recheck, genuinely mid-BuildIndex) for as long as it takes to launch and race in the
        // later callers below, rather than relying on BuildIndex's own (fast, in-memory) work to
        // take long enough on its own.
        service.BuildIndexAboutToRunForTests = () =>
        {
            buildStarted.Set();
            releaseBuild.Wait(TimeSpan.FromSeconds(5));
        };

        var firstTask = InvokeGetIndexAsync(getIndexMethod, service, platform);

        Assert.True(
            buildStarted.Wait(TimeSpan.FromSeconds(5)),
            "The first caller never reached BuildIndex; the test setup itself is broken.");

        // Now that the first caller is confirmed to be genuinely mid-build, launch several more
        // concurrent callers for the SAME platform. This is the exact window in which the
        // earlier (buggy) implementation let a second caller slip past the gate.
        const int laterCallers = 8;
        // RdbService's PlatformIndex return type is a private nested record not visible by name
        // from the test project, so results are held as object? here; only non-null-ness matters.
        var laterTasks = new Task<object?>[laterCallers];
        for (var i = 0; i < laterCallers; i++)
        {
            laterTasks[i] = InvokeGetIndexAsync(getIndexMethod, service, platform);
        }

        // Give the later callers a real chance to reach (and, if the bug were present, bypass)
        // the gate before the first build is allowed to finish.
        Thread.Sleep(200);
        releaseBuild.Set();

        var firstResult = await firstTask;
        var laterResults = await Task.WhenAll(laterTasks);

        Assert.NotNull(firstResult);
        Assert.All(laterResults, r => Assert.NotNull(r));
        Assert.Equal(1, service.BuildIndexCallCountForTests);
    }

    // Regression test for the bounds-check gap in RdbReader's MessagePack index walk: a truncated
    // file used to throw an IndexOutOfRangeException from whatever raw offset the parser happened
    // to run off the end of the buffer at. It must now throw a clean FormatException instead, no
    // matter which internal read (byte, span, map/array count) is the one that runs out of data.
    [Fact]
    public void ReadAll_TruncatedFixmapHeader_ThrowsFormatException_NotIndexOutOfRange()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "truncated.rdb");
        using (var ms = new MemoryStream())
        {
            ms.Write("RARCHDB\0"u8.ToArray());
            ms.Write(new byte[8]); // metadata offset = 0 (records run to end of file)
            ms.WriteByte(0x82); // fixmap header claiming 2 key/value entries, but none follow
            File.WriteAllBytes(path, ms.ToArray());
        }

        var ex = Assert.Throws<FormatException>(() => RdbReader.ReadAll(path));
        Assert.Contains("Truncated", ex.Message);
    }

    // Same bounds-check gap, but truncated mid-string (a str8 length byte claims more bytes than
    // the file actually has) rather than mid-map -- exercises ReadSpanChecked's separate call site.
    [Fact]
    public void ReadAll_StringLengthExceedsRemainingData_ThrowsFormatException()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "truncated-string.rdb");
        using (var ms = new MemoryStream())
        {
            ms.Write("RARCHDB\0"u8.ToArray());
            ms.Write(new byte[8]);
            ms.WriteByte(0x81); // fixmap, 1 entry
            ms.WriteByte(0xa4); // fixstr, 4 bytes ("name")
            ms.Write("name"u8.ToArray());
            ms.WriteByte(0xd9); // str8
            ms.WriteByte(200); // claims 200 bytes; file has none of them
            File.WriteAllBytes(path, ms.ToArray());
        }

        var ex = Assert.Throws<FormatException>(() => RdbReader.ReadAll(path));
        Assert.Contains("Truncated", ex.Message);
    }

    // Regression test for the uncapped RdbReader.ReadAll(File.ReadAllBytes) read: a corrupt or
    // hostile .rdb whose actual on-disk size exceeds the configured ceiling must be rejected
    // before being loaded fully into memory, matching GamesService.MaxExtractedRomBytes's role for
    // ROM extraction. Uses the maxFileBytes test seam rather than a real multi-MB fixture.
    [Fact]
    public void ReadAll_FileExceedingTheCap_ThrowsRdbTooLargeException()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "oversized.rdb");
        var bytes = new byte[16 + 100];
        "RARCHDB\0"u8.CopyTo(bytes);
        File.WriteAllBytes(path, bytes);

        var ex = Assert.Throws<RdbTooLargeException>(() => RdbReader.ReadAll(path, maxFileBytes: 50));

        Assert.Equal(50, ex.MaxBytes);
        Assert.Equal(bytes.Length, ex.ActualBytes);
    }

    // Regression test for the corrupt-file hot-loop bug (this task's item 3): unlike a download
    // failure (which sets a backoff -- see EnsureDownloadedAsync_DoesNotRetryImmediatelyAfterAFailure
    // above), a parse failure previously never populated _indexes[platform], so every subsequent
    // lookup re-parsed the same corrupt file. GetIndexAsync must now record the same kind of
    // backoff on a parse failure, so a second call inside the window does not attempt another
    // build at all (proven here via BuildIndexCallCountForTests staying at 1).
    [Fact]
    public async Task GetIndexAsync_CorruptRdb_BacksOffAndDoesNotReparseOnTheNextLookup()
    {
        const string platform = "corrupt-platform";
        var rdbPath = Path.Combine(_root, "gamemeta", platform + ".rdb");
        Directory.CreateDirectory(Path.GetDirectoryName(rdbPath)!);
        using (var ms = new MemoryStream())
        {
            ms.Write("RARCHDB\0"u8.ToArray());
            ms.Write(new byte[8]);
            ms.WriteByte(0x82); // fixmap header claiming 2 entries, but none follow: parse fails
            File.WriteAllBytes(rdbPath, ms.ToArray());
        }

        var service = new RdbService(new ThrowingHttpClientFactory(), new NoOpLogger<RdbService>(), _root);
        var getIndexMethod = typeof(RdbService).GetMethod("GetIndexAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("RdbService.GetIndexAsync not found; has it been renamed?");

        var first = await InvokeGetIndexAsync(getIndexMethod, service, platform);
        Assert.Null(first);
        Assert.Equal(1, service.BuildIndexCallCountForTests);

        // The file is still corrupt (nothing "fixed" it, unlike the transient-failure test above),
        // so a naive retry would fail identically. The backoff must skip the attempt entirely
        // rather than re-run BuildIndex and fail again.
        var second = await InvokeGetIndexAsync(getIndexMethod, service, platform);
        Assert.Null(second);
        Assert.Equal(1, service.BuildIndexCallCountForTests);
    }

    // Regression test for the unbounded-retry bug on the RDB download path: a failed download
    // used to leave no memory of the failure, so the very next artwork/lookup request for the
    // same platform would kick off another full download attempt against the same broken
    // mirror. EnsureDownloadedAsync now records a backoff window on failure (see DownloadAsync's
    // catch block) so a second call inside that window is a no-op instead of a second attempt.
    // This drives the real private EnsureDownloadedAsync end-to-end via reflection, with an
    // IHttpClientFactory whose handler fails synchronously so both calls resolve quickly and
    // deterministically -- no network access and no timing/sleep dependency.
    [Fact]
    public async Task EnsureDownloadedAsync_DoesNotRetryImmediatelyAfterAFailure()
    {
        const string platform = "backoff-platform";
        var factory = new CountingFailingHttpClientFactory();
        var service = new RdbService(factory, new NoOpLogger<RdbService>(), _root);
        var config = new global::Moonfin.Server.PluginConfiguration
        {
            GamesMetadataDbUrlBase = "http://mirror.invalid/rdb",
        };

        var method = typeof(RdbService).GetMethod("EnsureDownloadedAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("RdbService.EnsureDownloadedAsync not found; has it been renamed?");

        var first = (Task)(method.Invoke(service, [platform, config])
            ?? throw new InvalidOperationException("EnsureDownloadedAsync did not return a Task."));
        await first;

        Assert.Equal(1, factory.CreateCount);

        var second = (Task)(method.Invoke(service, [platform, config])
            ?? throw new InvalidOperationException("EnsureDownloadedAsync did not return a Task."));
        await second;

        Assert.Equal(1, factory.CreateCount);
    }

    // Regression test for the unbounded-cache bug: _lookupCache used to grow forever, one entry
    // per distinct ROM path ever opened, for the life of the process. It is now capped and cleared
    // on capacity, the same pattern already used by ArcadeCompatibilityService's resolution cache
    // and GameThumbService's miss cache. This drives the real CacheLookup method (via reflection,
    // since _lookupCache and CacheLookup are private) directly rather than TryLookup end-to-end,
    // so the test does not need a real metadata index or ROM files -- only the capacity/eviction
    // bookkeeping is under test here.
    [Fact]
    public void CacheLookup_ClearsTheWholeCache_OnceItReachesCapacity()
    {
        var service = new RdbService(new ThrowingHttpClientFactory(), new NoOpLogger<RdbService>(), _root);

        var cacheField = typeof(RdbService).GetField("_lookupCache", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("RdbService._lookupCache not found; has it been renamed?");
        var maxField = typeof(RdbService).GetField("MaxLookupCacheEntries", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("RdbService.MaxLookupCacheEntries not found; has it been renamed?");
        var cacheLookupMethod = typeof(RdbService).GetMethod("CacheLookup", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("RdbService.CacheLookup not found; has it been renamed?");

        var cache = cacheField.GetValue(service)
            ?? throw new InvalidOperationException("_lookupCache was null.");
        var max = (int)(maxField.GetValue(service) ?? throw new InvalidOperationException("MaxLookupCacheEntries was null."));
        var countProperty = cache.GetType().GetProperty("Count")
            ?? throw new InvalidOperationException("ConcurrentDictionary.Count not found.");

        var cachedLookupType = typeof(RdbService).GetNestedType("CachedLookup", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RdbService.CachedLookup not found; has it been renamed?");

        // Fill exactly to capacity with distinct keys; nothing should be evicted yet.
        // CachedLookup's trailing Platform/Siblings fields are passed explicitly rather than
        // relying on Activator's optional-parameter binding.
        for (var i = 0; i < max; i++)
        {
            var lookup = Activator.CreateInstance(cachedLookupType, 0L, 0L, null, null, null);
            cacheLookupMethod.Invoke(service, [$"rom-{i}.zip", lookup]);
        }

        Assert.Equal(max, countProperty.GetValue(cache));

        // One more distinct key past capacity must clear the whole cache (the documented
        // clear-on-capacity pattern), not evict just one entry or grow past the cap.
        var overflowLookup = Activator.CreateInstance(cachedLookupType, 0L, 0L, null, null, null);
        cacheLookupMethod.Invoke(service, ["rom-overflow.zip", overflowLookup]);

        Assert.Equal(1, countProperty.GetValue(cache));
    }

    // ---- Artwork-resolution unification tests -----------------------------------------------
    //
    // These prove GamesService/GameThumbService/RdbService share one resolution chain (CRC ->
    // normalized filename -> normalized title -> fuzzy prefix) for every system, via the real
    // private Match method both TryLookup and ResolveArtworkNameAsync call into. Driven via
    // reflection since Match/PlatformIndex are private and MoonfinPlugin.Instance is unavailable here.

    [Fact]
    public void Match_CartridgeRomWithNonMatchingFilename_ResolvesViaCrc()
    {
        // The ROM's own filename ("ninjagaiden.sms") does not appear anywhere in the index --
        // only its content CRC does. A filename-only resolver would find nothing here.
        Directory.CreateDirectory(_root);
        var romBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var romPath = Path.Combine(_root, "ninjagaiden.sms");
        File.WriteAllBytes(romPath, romBytes);
        var crc = Crc32(romBytes);

        const string canonicalName = "Ninja Gaiden (Europe, Brazil) (En)";
        var byCrc = new Dictionary<uint, RdbRecord> { [crc] = new RdbRecord { Name = canonicalName } };
        var byName = new Dictionary<string, RdbRecord>(StringComparer.Ordinal); // deliberately empty

        var service = new RdbService(new ThrowingHttpClientFactory(), new NoOpLogger<RdbService>(), _root);
        var index = CreatePlatformIndex(byCrc, byName);
        var matchMethod = GetMatchMethod();

        var result = (RdbRecord?)matchMethod.Invoke(service, [index, romPath, "Some Unrelated Title"]);

        Assert.NotNull(result);
        Assert.Equal(canonicalName, result!.Name);
    }

    [Fact]
    public void Match_ArcadeZip_StillResolvesViaFilenameStep_WhenCrcMisses()
    {
        // Regression guard: an arcade ZIP has no single content CRC libretro's .rdb would carry,
        // so the CRC step must miss and the normalized filename step must still resolve it.
        Directory.CreateDirectory(_root);
        var romPath = ZipFixtures.WriteZip(_root, "atetris.zip", ("chip.bin", new byte[] { 10, 20, 30, 40 }));

        const string canonicalName = "Atari Tetris";
        var byCrc = new Dictionary<uint, RdbRecord>(); // no entry can match a whole-archive CRC
        var byName = new Dictionary<string, RdbRecord>(StringComparer.Ordinal)
        {
            ["atetris"] = new RdbRecord { Name = canonicalName, RomName = "atetris" },
        };

        var service = new RdbService(new ThrowingHttpClientFactory(), new NoOpLogger<RdbService>(), _root);
        var index = CreatePlatformIndex(byCrc, byName);
        var matchMethod = GetMatchMethod();

        // Title deliberately doesn't match either, so a pass proves the filename step resolved it.
        var result = (RdbRecord?)matchMethod.Invoke(service, [index, romPath, "Not The Title"]);

        Assert.NotNull(result);
        Assert.Equal(canonicalName, result!.Name);
    }

    // ---- Sibling artwork name tests (MatchWithSiblings) -------------------------------------
    //
    // Match's fuzzy-prefix step collapses matches to a single closest record -- right for detail
    // metadata but wrong for artwork, where the closest record can lack art upstream even though
    // a same-title sibling has it. MatchWithSiblings reuses the same prefix comparison but
    // returns the primary plus its siblings.

    [Fact]
    public void MatchWithSiblings_BestMatchHasNoArtSibling_StillListsTheGenuineSiblingEntry()
    {
        // The primary fuzzy match (closest by length) can lack art while a sibling entry with a
        // larger length diff has it; MatchWithSiblings must still list the sibling as a candidate.
        Directory.CreateDirectory(_root);
        var romPath = Path.Combine(_root, "Great Golf.sms");
        File.WriteAllBytes(romPath, [9, 9, 9, 9]); // CRC deliberately matches neither record below

        const string japanName = "Great Golf (Japan) (En)";
        const string worldName = "Great Golf / Masters Golf (World)";
        var byCrc = new Dictionary<uint, RdbRecord>(); // CRC step must miss for both records
        var byName = new Dictionary<string, RdbRecord>(StringComparer.Ordinal)
        {
            ["greatgolfjapanen"] = new RdbRecord { Name = japanName },
            ["greatgolfmastersgolfworld"] = new RdbRecord { Name = worldName },
        };

        var service = new RdbService(new ThrowingHttpClientFactory(), new NoOpLogger<RdbService>(), _root);
        var index = CreatePlatformIndex(byCrc, byName);
        var results = InvokeMatchWithSiblings(service, index, romPath, "Great Golf");

        Assert.Equal(2, results.Count);
        // Best match (closest by length) is always first, unchanged from Match's own contract.
        Assert.Equal(japanName, results[0].Name);
        Assert.Equal(worldName, results[1].Name);
    }

    [Fact]
    public void MatchWithSiblings_NoSiblingsPresent_ReturnsOnlyThePrimaryMatch()
    {
        // No prefix-related entries besides the primary: result must be the one-entry list Match
        // itself would produce, no phantom siblings.
        Directory.CreateDirectory(_root);
        var romPath = Path.Combine(_root, "ninjagaiden.sms");
        var romBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        File.WriteAllBytes(romPath, romBytes);
        var crc = Crc32(romBytes);

        const string canonicalName = "Ninja Gaiden (Europe, Brazil) (En)";
        var byCrc = new Dictionary<uint, RdbRecord> { [crc] = new RdbRecord { Name = canonicalName } };
        var byName = new Dictionary<string, RdbRecord>(StringComparer.Ordinal); // deliberately empty

        var service = new RdbService(new ThrowingHttpClientFactory(), new NoOpLogger<RdbService>(), _root);
        var index = CreatePlatformIndex(byCrc, byName);
        var results = InvokeMatchWithSiblings(service, index, romPath, "Some Unrelated Title");

        Assert.Single(results);
        Assert.Equal(canonicalName, results[0].Name);
    }

    [Fact]
    public void MatchWithSiblings_MoreSiblingsThanTheCap_ReturnsExactlyTheCappedCount()
    {
        // Writes more prefix-related entries than the cap allows; result must never exceed
        // 1 (primary) + MaxSiblingArtworkNamesPerRequest.
        Directory.CreateDirectory(_root);
        var romPath = Path.Combine(_root, "Great Golf.sms");
        File.WriteAllBytes(romPath, [9, 9, 9, 9]);

        var byCrc = new Dictionary<uint, RdbRecord>();
        var byName = new Dictionary<string, RdbRecord>(StringComparer.Ordinal)
        {
            ["greatgolf"] = new RdbRecord { Name = "Great Golf" }, // exact match => the primary
        };
        // One sibling per extra letter appended, giving each a distinct, deterministic length diff.
        var extraSiblingCount = RdbService.MaxSiblingArtworkNamesPerRequest + 4;
        for (var i = 0; i < extraSiblingCount; i++)
        {
            var suffix = new string((char)('a' + (i % 26)), i + 1);
            var key = "greatgolf" + suffix;
            byName[key] = new RdbRecord { Name = "Great Golf Variant " + i };
        }

        var service = new RdbService(new ThrowingHttpClientFactory(), new NoOpLogger<RdbService>(), _root);
        var index = CreatePlatformIndex(byCrc, byName);
        var results = InvokeMatchWithSiblings(service, index, romPath, null);

        Assert.Equal(1 + RdbService.MaxSiblingArtworkNamesPerRequest, results.Count);
        Assert.Equal("Great Golf", results[0].Name);
    }

    [Fact]
    public void MatchWithSiblings_UnrelatedGameInTheSameIndex_IsNeverReturnedAsASibling()
    {
        // A sibling must share a genuine base-title prefix relationship, not just live in the
        // same index. "Great Golf" and "Great Baseball" share no prefix, so the latter must never
        // appear in the former's sibling list.
        Directory.CreateDirectory(_root);
        var romPath = Path.Combine(_root, "Great Golf.sms");
        File.WriteAllBytes(romPath, [9, 9, 9, 9]);

        const string worldName = "Great Golf / Masters Golf (World)";
        var byCrc = new Dictionary<uint, RdbRecord>();
        var byName = new Dictionary<string, RdbRecord>(StringComparer.Ordinal)
        {
            ["greatgolfmastersgolfworld"] = new RdbRecord { Name = worldName },
            ["greatbaseball"] = new RdbRecord { Name = "Great Baseball" },
        };

        var service = new RdbService(new ThrowingHttpClientFactory(), new NoOpLogger<RdbService>(), _root);
        var index = CreatePlatformIndex(byCrc, byName);
        var results = InvokeMatchWithSiblings(service, index, romPath, null);

        Assert.DoesNotContain(results, r => r.Name == "Great Baseball");
        Assert.Contains(results, r => r.Name == worldName);
    }

    [Fact]
    public void MatchWithSiblings_RomFilenameRegionDiffersFromDatabaseRegion_StillDiscoversSiblings()
    {
        // Sibling discovery compares base titles, not the ROM's filename: here the file names a
        // different region ("USA, Europe") than the primary match found via CRC ("Japan"), which
        // share no filename-prefix relationship. Base-title comparison must still find the
        // siblings since it compares the primary match's own base title, never the filename.
        Directory.CreateDirectory(_root);
        var romBytes = new byte[] { 3, 1, 4, 1, 5, 9 };
        var romPath = Path.Combine(_root, "Great Golf (USA, Europe).sms");
        File.WriteAllBytes(romPath, romBytes);
        var crc = Crc32(romBytes);

        const string japanName = "Great Golf (Japan) (En)";
        const string worldBetaName = "Great Golf (World) (Beta)";
        const string koreaName = "Great Golf (Korea) (En) (Unl)";
        const string mastersGolfName = "Great Golf / Masters Golf (World)";

        // Same RdbRecord instance under both its CRC key and name key, mirroring real BuildIndex
        // output, so MatchWithSiblings' reference-identity dedup behaves as it would for real data.
        var japanRecord = new RdbRecord { Name = japanName };
        var byCrc = new Dictionary<uint, RdbRecord> { [crc] = japanRecord };
        var byName = new Dictionary<string, RdbRecord>(StringComparer.Ordinal)
        {
            ["greatgolfjapanen"] = japanRecord,
            ["greatgolfworldbeta"] = new RdbRecord { Name = worldBetaName },
            ["greatgolfkoreaenunl"] = new RdbRecord { Name = koreaName },
            ["greatgolfmastersgolfworld"] = new RdbRecord { Name = mastersGolfName },
        };

        var service = new RdbService(new ThrowingHttpClientFactory(), new NoOpLogger<RdbService>(), _root);
        var index = CreatePlatformIndex(byCrc, byName);
        var results = InvokeMatchWithSiblings(service, index, romPath, "Great Golf (USA, Europe)");

        // Primary (the CRC hit) is always first, unchanged.
        Assert.Equal(japanName, results[0].Name);
        // All three siblings must be discovered despite none sharing a filename prefix.
        Assert.Contains(results, r => r.Name == worldBetaName);
        Assert.Contains(results, r => r.Name == koreaName);
        Assert.Contains(results, r => r.Name == mastersGolfName);
        Assert.Equal(4, results.Count);
    }

    [Fact]
    public void ExtractBaseTitle_StripsTrailingQualifiers_ButPreservesAnEmbeddedSlash()
    {
        // Trailing "(...)" qualifier groups strip off (multiple groups go one at a time), while
        // an embedded "/" combo-release separator is part of the title and must survive untouched.
        var method = typeof(RdbService).GetMethod("ExtractBaseTitle", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("RdbService.ExtractBaseTitle not found; has it been renamed?");

        Assert.Equal("Great Golf", (string)method.Invoke(null, ["Great Golf (USA, Europe)"])!);
        Assert.Equal("Great Golf", (string)method.Invoke(null, ["Great Golf (Japan) (En)"])!);
        Assert.Equal(
            "Great Golf / Masters Golf",
            (string)method.Invoke(null, ["Great Golf / Masters Golf (World)"])!);
        // No trailing qualifier at all -- must come back unchanged (just trimmed).
        Assert.Equal("Great Golf", (string)method.Invoke(null, ["Great Golf"])!);
    }

    [Fact]
    public void MatchWithSiblings_MoreBaseTitleSiblingsThanTheCap_ReturnsExactlyTheCappedCount()
    {
        // Same bounds requirement as MatchWithSiblings_MoreSiblingsThanTheCap_..., but through
        // realistic region-tagged names to prove the cap is enforced by base-title comparison.
        Directory.CreateDirectory(_root);
        var romPath = Path.Combine(_root, "Great Golf.sms");
        File.WriteAllBytes(romPath, [9, 9, 9, 9]);

        var byCrc = new Dictionary<uint, RdbRecord>();
        var byName = new Dictionary<string, RdbRecord>(StringComparer.Ordinal)
        {
            ["greatgolf"] = new RdbRecord { Name = "Great Golf" }, // exact filename match => primary
        };

        var regions = new[] { "Japan", "World", "Korea", "Europe", "Asia", "Brazil", "USA", "China", "Taiwan" };
        Assert.True(regions.Length > RdbService.MaxSiblingArtworkNamesPerRequest, "Test fixture must offer more region siblings than the cap to actually exercise it.");
        foreach (var region in regions)
        {
            var name = $"Great Golf ({region})";
            byName[NormalizeNameForTest(name)] = new RdbRecord { Name = name };
        }

        var service = new RdbService(new ThrowingHttpClientFactory(), new NoOpLogger<RdbService>(), _root);
        var index = CreatePlatformIndex(byCrc, byName);
        var results = InvokeMatchWithSiblings(service, index, romPath, null);

        Assert.Equal(1 + RdbService.MaxSiblingArtworkNamesPerRequest, results.Count);
        Assert.Equal("Great Golf", results[0].Name);
        // Every returned sibling must be a genuine "Great Golf" region variant.
        Assert.All(results.Skip(1), r => Assert.StartsWith("Great Golf (", r.Name));
    }

    // Minimal ad-hoc normalizer mirroring RdbService's private NormalizeName, kept separate so
    // this test doesn't need reflection to build its ByName dictionary keys.
    private static string NormalizeNameForTest(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
    }

    [Fact]
    public async Task ResolveArtworkNameAsync_ReturnsNullWithoutThrowing_WhenPluginConfigurationIsUnavailable()
    {
        // MoonfinPlugin.Instance is unavailable in this test process, exercising the same
        // "config missing" path a cold real server hits; must return null promptly, not throw.
        Directory.CreateDirectory(_root);
        var romPath = Path.Combine(_root, "somegame.nes");
        File.WriteAllBytes(romPath, [1, 2, 3, 4]);

        var service = new RdbService(new ThrowingHttpClientFactory(), new NoOpLogger<RdbService>(), _root);
        var candidates = RdbService.GetCandidatePlatforms("nes", coreWasDefaulted: false, fuzzyDirectoryCores: Array.Empty<string>());

        var task = service.ResolveArtworkNameAsync(candidates, romPath, "Some Game", CancellationToken.None);
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(task, completed);
        var result = await task;
        Assert.Null(result);
    }

    // ---- Multi-candidate artwork platform tests --------------------------------------------
    //
    // ResolveArtworkNameAsync tries an ordered list of platforms (from GetCandidatePlatforms), so
    // a ROM filed under the "wrong" system folder can still resolve via a secondary platform.
    // Driven through the real private ResolveArtworkNameCoreAsync via reflection since
    // MoonfinPlugin.Instance is unavailable in this test process.

    [Fact]
    public async Task ResolveArtworkNameAsync_Sg1000RomFiledUnderMasterSystemFolder_ResolvesViaSecondaryPlatform()
    {
        // A user's "Master System" folder actually contains an SG-1000 game: the Master System
        // index has nothing for this ROM, but its CRC is present in the SG-1000 index, which
        // GetCandidatePlatforms includes as a family platform for segaMS.
        var romBytes = new byte[] { 5, 10, 15, 20, 25, 30 };
        var romPath = Path.Combine(_root, "N-Sub.sg");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(romPath, romBytes);
        var crc = Crc32(romBytes);

        WriteMinimalRdb(RdbPath("Sega - Master System - Mark III"), Array.Empty<(string, uint)>());
        const string canonicalName = "N-Sub (Europe)";
        WriteMinimalRdb(RdbPath("Sega - SG-1000"), new[] { (canonicalName, crc) });

        var service = new RdbService(new ThrowingHttpClientFactory(), new NoOpLogger<RdbService>(), _root);
        var candidates = RdbService.GetCandidatePlatforms("segaMS", coreWasDefaulted: false, fuzzyDirectoryCores: Array.Empty<string>());

        // Sanity check on the candidate list itself: primary first, SG-1000 present as a family
        // candidate (this is what GetCandidatePlatforms/PlatformFamily is documented to do).
        Assert.Equal("Sega - Master System - Mark III", candidates[0]);
        Assert.Contains("Sega - SG-1000", candidates);

        var result = await InvokeResolveArtworkNameCoreAsync(service, candidates, romPath, "N-Sub");

        Assert.NotNull(result);
        var (names, platform) = (ArtworkNameResolution)result!;
        Assert.Equal(canonicalName, names[0]);
        // The thumbnail URL must be built against the platform that ACTUALLY matched, not the
        // (wrong, primary) folder-implied one -- see ResolveArtworkNameAsync's doc.
        Assert.Equal("Sega - SG-1000", platform);
    }

    [Fact]
    public async Task ResolveArtworkNameAsync_PrimaryPlatformHit_NeverLoadsSecondaryCandidateIndexes()
    {
        // Regression guard for the "no extra cost in the common case" requirement: when the
        // primary platform already has the answer, GetCandidatePlatforms' extra family/fuzzy
        // candidates must never even be consulted -- proven here by never creating a
        // "Sega - SG-1000.rdb" fixture file at all. If ResolveArtworkNameCoreAsync tried to load
        // it anyway (even just to find it missing), that would still mean doing MORE than a
        // single-platform lookup for a system that needed no fuzziness at all.
        var romBytes = new byte[] { 1, 1, 2, 3, 5, 8 };
        var romPath = Path.Combine(_root, "Alex Kidd.sms");
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(romPath, romBytes);
        var crc = Crc32(romBytes);

        const string canonicalName = "Alex Kidd in Miracle World (USA, Europe)";
        WriteMinimalRdb(RdbPath("Sega - Master System - Mark III"), new[] { (canonicalName, crc) });
        // Deliberately NOT writing Sega - SG-1000.rdb or Sega - Game Gear.rdb at all.

        var service = new RdbService(new ThrowingHttpClientFactory(), new NoOpLogger<RdbService>(), _root);
        var candidates = RdbService.GetCandidatePlatforms("segaMS", coreWasDefaulted: false, fuzzyDirectoryCores: Array.Empty<string>());

        var result = await InvokeResolveArtworkNameCoreAsync(service, candidates, romPath, "Alex Kidd");

        Assert.NotNull(result);
        var (names, platform) = (ArtworkNameResolution)result!;
        Assert.Equal(canonicalName, names[0]);
        Assert.Equal("Sega - Master System - Mark III", platform);

        // BuildIndexCallCountForTests counts every real index build across every platform this
        // service instance has ever built. Exactly 1 proves only the primary candidate's index
        // was ever built -- the family candidates were never even attempted.
        Assert.Equal(1, service.BuildIndexCallCountForTests);
    }

    [Fact]
    public void ResolveSystemCore_FuzzyDirectoryName_ResolvesToTheRightCore()
    {
        // "Sega Master System" is not a verbatim SystemNameToCore alias (the table has
        // "mastersystem" and "sms"), but normalizes to "segamastersystem", which unambiguously
        // contains the "mastersystem" alias -- this is exactly the case the task's fuzzy-directory
        // requirement targets ("we can't count on the user correctly putting roms in a system
        // folder... Ideally system would be a fuzzy match").
        var core = GamesService.ResolveSystemCore("Sega Master System", Array.Empty<string>(), out var wasDefaulted);

        Assert.Equal("segaMS", core);
        Assert.False(wasDefaulted);
    }

    [Fact]
    public void ResolveSystemCore_AmbiguousFuzzyDirectoryName_DoesNotGuessAWrongCore()
    {
        // "GbaPsx" normalizes to "gbapsx" (6 chars), which fuzzy-contains BOTH the "gba" alias
        // (core gba, |3-6|=3) and the "psx" alias (core psx, |3-6|=3) at an EQUAL distance -- a
        // genuine tie between two different cores. TryFuzzyMatchSystemCore's ambiguity guard
        // (modeled on RdbService.Match's own fuzzy-prefix ambiguity guard) must fall through
        // rather than pick either one, so this must land on the safe "nes" default, NOT "gba" or
        // "psx".
        var core = GamesService.ResolveSystemCore("GbaPsx", Array.Empty<string>(), out var wasDefaulted);

        Assert.Equal("nes", core);
        Assert.True(wasDefaulted);
        Assert.NotEqual("gba", core);
        Assert.NotEqual("psx", core);
    }

    private string RdbPath(string platform) => Path.Combine(_root, "gamemeta", platform + ".rdb");

    private static async Task<object?> InvokeResolveArtworkNameCoreAsync(
        RdbService service,
        IReadOnlyList<string> candidatePlatforms,
        string romPath,
        string? title)
    {
        var method = typeof(RdbService).GetMethod("ResolveArtworkNameCoreAsync", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("RdbService.ResolveArtworkNameCoreAsync not found; has it been renamed?");
        var config = new global::Moonfin.Server.PluginConfiguration();
        var task = method.Invoke(service, [candidatePlatforms, romPath, title, config, CancellationToken.None]) as Task
            ?? throw new InvalidOperationException("ResolveArtworkNameCoreAsync did not return a Task.");
        await task;
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    // Minimal writer for the libretro .rdb format RdbReader parses: 8-byte magic ("RARCHDB\0"),
    // 8-byte big-endian metadata offset (0 here, meaning "records run to end of file" per
    // RdbReader.ReadAll), then one MessagePack fixmap per game with "name" (fixstr) and "crc"
    // (bin8, 4 bytes big-endian) entries -- the only two fields these tests need out of the full
    // RdbRecord shape.
    private static void WriteMinimalRdb(string path, IReadOnlyList<(string Name, uint Crc)> games)
    {
        using var ms = new MemoryStream();
        ms.Write("RARCHDB\0"u8.ToArray());
        ms.Write(new byte[8]); // metadata offset = 0

        foreach (var (name, crc) in games)
        {
            WriteFixMapGameRecord(ms, name, crc);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ms.ToArray());
    }

    private static void WriteFixMapGameRecord(MemoryStream ms, string name, uint crc)
    {
        ms.WriteByte(0x82); // fixmap, 2 entries
        WriteFixStr(ms, "name");
        WriteFixStr(ms, name);
        WriteFixStr(ms, "crc");
        ms.WriteByte(0xc4); // bin8
        ms.WriteByte(4);
        var crcBytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        ms.Write(crcBytes);
    }

    // Writes either a msgpack fixstr (<= 31 bytes) or a str8 (<= 255 bytes, RdbReader's 0xd9
    // case) depending on length -- canonical libretro display names routinely carry No-Intro
    // region tags that push them past the fixstr limit (e.g. "Alex Kidd in Miracle World (USA,
    // Europe)"), so fixture strings must not be artificially capped at 31 bytes.
    private static void WriteFixStr(MemoryStream ms, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        if (bytes.Length <= 31)
        {
            ms.WriteByte((byte)(0xa0 | bytes.Length));
        }
        else if (bytes.Length <= 255)
        {
            ms.WriteByte(0xd9);
            ms.WriteByte((byte)bytes.Length);
        }
        else
        {
            throw new InvalidOperationException("Test fixture strings must fit in a msgpack str8 (<= 255 bytes).");
        }

        ms.Write(bytes);
    }

    private static MethodInfo GetMatchMethod() =>
        typeof(RdbService).GetMethod("Match", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("RdbService.Match not found; has it been renamed?");

    private static IReadOnlyList<RdbRecord> InvokeMatchWithSiblings(RdbService service, object index, string romPath, string? title)
    {
        var method = typeof(RdbService).GetMethod("MatchWithSiblings", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("RdbService.MatchWithSiblings not found; has it been renamed?");
        return (IReadOnlyList<RdbRecord>)(method.Invoke(service, [index, romPath, title])
            ?? throw new InvalidOperationException("MatchWithSiblings returned null; it must always return a (possibly empty) list."));
    }

    private static object CreatePlatformIndex(
        Dictionary<uint, RdbRecord> byCrc,
        Dictionary<string, RdbRecord> byName)
    {
        var platformIndexType = typeof(RdbService).GetNestedType("PlatformIndex", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RdbService.PlatformIndex not found; has it been renamed?");
        return Activator.CreateInstance(platformIndexType, byCrc, byName)
            ?? throw new InvalidOperationException("Failed to construct PlatformIndex via reflection.");
    }

    // Same CRC32 (IEEE 802.3 / zlib polynomial, init/final XOR 0xFFFFFFFF) as RdbService's own
    // Crc32File, reimplemented independently here so the fixture's expected CRC isn't just
    // echoing production code back at itself.
    private static uint Crc32(byte[] data)
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static async Task<object?> InvokeGetIndexAsync(MethodInfo method, RdbService service, string platform)
    {
        var task = method.Invoke(service, [platform, null]) as Task
            ?? throw new InvalidOperationException("GetIndexAsync did not return a Task.");
        await task;
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    private sealed class CountingFailingHttpClientFactory : IHttpClientFactory
    {
        public int CreateCount { get; private set; }

        public HttpClient CreateClient(string name)
        {
            CreateCount++;
            return new HttpClient(new ThrowingHandler());
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Simulated mirror failure.");
    }

}
