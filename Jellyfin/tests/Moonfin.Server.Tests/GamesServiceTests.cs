using System.Security.Cryptography;
using System.Text.Json;
using MediaBrowser.Model.Entities;
using Moonfin.Server.Models;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

public class GamesServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"moonfin-gamessvc-{Guid.NewGuid():N}");

    // The four tests below pin the wire shape (property names/nesting) of the game-artwork DTOs
    // that GamesController's clients depend on. Each covers one DTO in isolation so a future
    // rename/shape change points at exactly the model that broke, rather than one broad test that
    // could fail for any of four unrelated reasons.

    [Fact]
    public void GameArtworkCapabilities_SerializesFlagsUnderTheirCamelCaseWireNames()
    {
        var capabilities = new GameArtworkCapabilities
        {
            ProtocolVersion = 2,
            Manifest = true,
            VersionedAssets = true,
            PriorityHints = true,
            SystemPreviews = true
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(capabilities));

        Assert.True(document.RootElement.GetProperty("manifest").GetBoolean());
        Assert.True(document.RootElement.GetProperty("versionedAssets").GetBoolean());
    }

    [Fact]
    public void GameArtworkManifest_SerializesPerRoleArtworkStateUnderTheGameEntry()
    {
        var manifest = new GameArtworkManifest
        {
            Generation = "artwork-7",
            Entries =
            [
                new GameArtworkManifestEntry
                {
                    GameId = "zelda",
                    Artwork = new Dictionary<string, GameArtworkDescriptor>
                    {
                        ["boxart"] = new() { State = "thumbnailReady", Revision = "rev-1" },
                        ["snap"] = new() { State = "missing" }
                    }
                }
            ]
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(manifest));

        Assert.Equal(
            "thumbnailReady",
            document.RootElement.GetProperty("entries")[0].GetProperty("artwork").GetProperty("boxart").GetProperty("state").GetString());
    }

    [Fact]
    public void GameArtworkPriorityRequest_SerializesKnownGenerationAndPerGameRoleList()
    {
        var priority = new GameArtworkPriorityRequest
        {
            SystemId = "nes",
            KnownGeneration = "artwork-7",
            Items = [new GameArtworkPriorityItem { GameId = "zelda", Roles = ["boxart", "snap"] }]
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(priority));

        Assert.Equal("artwork-7", document.RootElement.GetProperty("knownGeneration").GetString());
        Assert.Equal("snap", document.RootElement.GetProperty("items")[0].GetProperty("roles")[1].GetString());
    }

    [Fact]
    public void GameSystem_SerializesPreviewArtworkPanelsAdditively_WithoutARetryHintWhenReady()
    {
        var system = new GameSystem
        {
            Id = "nes",
            PreviewArtwork = new GameSystemPreviewArtwork
            {
                SelectionGeneration = "inventory-7",
                Panels =
                [
                    new GameSystemPreviewPanel
                    {
                        GameId = "zelda",
                        Artwork = new GameArtworkDescriptor
                        {
                            State = "thumbnailReady",
                            Url = "/Moonfin/Games/retro/Artwork/zelda/boxart/rev-1",
                            Revision = "rev-1"
                        }
                    }
                ]
            }
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(system));
        var preview = document.RootElement.GetProperty("previewArtwork");
        var panel = preview.GetProperty("panels")[0];

        Assert.Equal("inventory-7", preview.GetProperty("selectionGeneration").GetString());
        Assert.Equal("zelda", panel.GetProperty("gameId").GetString());
        Assert.Equal("thumbnailReady", panel.GetProperty("artwork").GetProperty("state").GetString());
        // "Additive" contract: optional fields that don't apply to a ready-state descriptor (like
        // a retry hint, which only makes sense while artwork generation is pending) must be
        // omitted from the wire payload entirely, not serialized as null/0.
        Assert.False(panel.GetProperty("artwork").TryGetProperty("retryAfterSeconds", out _));
    }

    [Theory]
    [InlineData("MAME", "mame")]
    [InlineData("mame", "mame")]
    [InlineData("Arcade", "arcade")]
    [InlineData("ARCADE", "arcade")]
    public void ResolveSystemCore_RecognizesArcadeSystems(string systemName, string expected)
    {
        var core = GamesService.ResolveSystemCore(systemName, Array.Empty<string>());

        Assert.Equal(expected, core);
    }

    [Theory]
    [InlineData("Sega Master System", ".sms", "segaMS", false)]
    [InlineData("Unknown System", ".gba", "gba", false)]
    [InlineData("GbaPsx", ".zip", "nes", true)]
    public void GameSystemCoreResolver_PreservesStrictAliasFuzzyExtensionAndDefaultSelection(
        string systemName,
        string extension,
        string expectedCore,
        bool expectedDefaulted)
    {
        var core = GameSystemCoreResolver.ResolveSystemCore(
            systemName,
            [$"game{extension}"],
            out var wasDefaulted);

        Assert.Equal(expectedCore, core);
        Assert.Equal(expectedDefaulted, wasDefaulted);
    }

    [Fact]
    public void GamePathResolver_RejectsMalformedAndPrefixCollisionTokens()
    {
        var libraryRoot = Path.Combine(_root, "Games");
        var prefixCollisionRoot = Path.Combine(_root, "GamesElsewhere");
        Directory.CreateDirectory(libraryRoot);
        Directory.CreateDirectory(prefixCollisionRoot);
        var collisionFile = Path.Combine(prefixCollisionRoot, "outside.nes");
        File.WriteAllBytes(collisionFile, [1]);
        var resolver = CreatePathResolver(libraryRoot);

        Assert.Null(resolver.ResolveGameFile("library", "not-a-token"));
        Assert.Null(resolver.ResolveGameFile("library", GamePathResolver.EncodeToken(collisionFile)));
    }

    [Fact]
    public void GamePathResolver_ResolvesNestedAndLooseFilesAcrossLibraryRoots()
    {
        var primaryRoot = Path.Combine(_root, "GamesOne");
        var secondaryRoot = Path.Combine(_root, "GamesTwo");
        var nestedDirectory = Path.Combine(secondaryRoot, "Nintendo", "Game Folder");
        Directory.CreateDirectory(nestedDirectory);
        var nestedRom = Path.Combine(nestedDirectory, "game.nes");
        File.WriteAllBytes(nestedRom, [1]);
        var looseRom = Path.Combine(primaryRoot, "SNES", "loose.sfc");
        Directory.CreateDirectory(Path.GetDirectoryName(looseRom)!);
        File.WriteAllBytes(looseRom, [2]);
        var resolver = CreatePathResolver(primaryRoot, secondaryRoot);

        var nested = resolver.ResolveGameFile("library", GamePathResolver.EncodeToken(nestedRom));
        var loose = resolver.ResolveGameFile("library", GamePathResolver.EncodeToken(looseRom));

        Assert.NotNull(nested);
        Assert.Equal("Nintendo", nested!.SystemName);
        Assert.NotNull(loose);
        Assert.Equal("SNES", loose!.SystemName);
    }

    [Fact]
    public void GamePathResolver_PreservesExistingBiosAllowanceBehavior()
    {
        var libraryRoot = Path.Combine(_root, "Games");
        var systemDirectory = Path.Combine(libraryRoot, "PSX");
        Directory.CreateDirectory(systemDirectory);
        var bios = Path.Combine(systemDirectory, "scph1001.bin");
        var unknown = Path.Combine(systemDirectory, "notes.txt");
        File.WriteAllBytes(bios, [1]);
        File.WriteAllText(unknown, "note");
        var resolver = CreatePathResolver(libraryRoot);

        Assert.Equal(bios, resolver.ResolveFilePath("library", GamePathResolver.EncodeToken(bios), allowBios: false));
        Assert.Null(resolver.ResolveFilePath("library", GamePathResolver.EncodeToken(unknown), allowBios: false));
        Assert.Equal(unknown, resolver.ResolveFilePath("library", GamePathResolver.EncodeToken(unknown), allowBios: true));
    }

    [Theory]
    [InlineData("mame", "atetris.zip", true)]
    [InlineData("mame", "ATETRIS.ZIP", true)]
    [InlineData("mame", "atetris.7z", false)]
    [InlineData("arcade", "atetris.zip", true)]
    [InlineData("arcade", "atetris.7z", false)]
    [InlineData("nes", "game.zip", false)]
    public void ShouldPreserveArchiveForCore_OnlyPreservesArcadeZip(
        string core,
        string fileName,
        bool expected)
    {
        Assert.Equal(
            expected,
            GamesService.ShouldPreserveArchiveForCore(core, fileName));
    }

    [Theory]
    [InlineData("mame", "atetris.zip", true)]
    [InlineData("mame", "atetris.7z", false)]
    [InlineData("mame", "atetris.nes", false)]
    [InlineData("arcade", "atetris.zip", true)]
    [InlineData("arcade", "atetris.7z", false)]
    [InlineData("arcade", "atetris.nes", false)]
    [InlineData("nes", "game.zip", true)]
    [InlineData("nes", "game.nes", true)]
    public void IsPlayableFileForCore_RestrictsArcadeCoresToZip(
        string core,
        string fileName,
        bool expected)
    {
        Assert.Equal(
            expected,
            GamesService.IsPlayableFileForCore(core, fileName));
    }

    [Fact]
    public void ExtractRomFromArchive_StillExtractsSingleRomArchives()
    {
        var archivePath = ZipFixtures.WriteZip(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip", ("game.nes", [1, 2, 3, 4]));
        try
        {
            var rom = GamesService.ExtractRomFromArchive(archivePath);

            Assert.Equal([1, 2, 3, 4], rom);
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    // The ROM endpoint serves the unpacked entry rather than the archive, and the game detail
    // reports its name and size so the client knows what it is receiving. Naming the archive
    // instead left clients saving raw ROM bytes under a .zip name and failing to unpack them.
    [Fact]
    public void GetExtractedRomInfo_NamesTheEntryThatWillBeServed()
    {
        var archivePath = ZipFixtures.WriteZip(
            Path.GetTempPath(),
            $"{Guid.NewGuid():N}.zip",
            ("readme.txt", [9, 9]),
            ("Super Game (Europe).sfc", [1, 2, 3, 4]));
        try
        {
            var info = GamesService.GetExtractedRomInfo(archivePath);

            Assert.NotNull(info);
            Assert.Equal("Super Game (Europe).sfc", info.Value.Name);
            Assert.Equal(GamesService.ExtractRomFromArchive(archivePath)!.Length, info.Value.Length);
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public void GetExtractedRomInfo_ReturnsNullForAnUnreadableArchive()
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        File.WriteAllBytes(archivePath, [0, 1, 2, 3]);
        try
        {
            Assert.Null(GamesService.GetExtractedRomInfo(archivePath));
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    // Regression test for the unbounded-extraction bug: ExtractRomFromArchive used to CopyTo an
    // in-memory MemoryStream with no ceiling, so any authenticated non-admin user's ROM request
    // (GamesController.StreamExtractedRom) could force an arbitrarily large allocation per
    // request. This proves the zip path now throws RomTooLargeException once the entry's
    // decompressed size crosses GamesService.MaxExtractedRomBytes, using a small custom limit-free
    // way to prove the *enforcement*, not the specific 512 MB constant: rather than actually
    // writing out over half a gigabyte, this drives CopyToLimited's real per-byte accounting via
    // reflection with a tiny limit, which is the same code path ExtractRomFromArchive itself uses.
    [Fact]
    public void CopyToLimited_ThrowsRomTooLargeException_WhenSourceExceedsTheLimit()
    {
        var method = typeof(GamesService).GetMethod("CopyToLimited", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("GamesService.CopyToLimited not found; has it been renamed?");

        using var source = new MemoryStream(new byte[] { 1, 2, 3, 4, 5 });
        using var destination = new MemoryStream();

        var ex = Assert.Throws<System.Reflection.TargetInvocationException>(
            () => method.Invoke(null, new object[] { source, destination, 3L }));

        Assert.IsType<RomTooLargeException>(ex.InnerException);
        var tooLarge = (RomTooLargeException)ex.InnerException!;
        Assert.Equal(3L, tooLarge.MaxBytes);
    }

    // Proves the shared pre-check both archive formats call (GamesService.CheckEntrySizeOrThrow)
    // rejects an entry whose advertised size already exceeds the ceiling, without needing to
    // construct a multi-hundred-megabyte fixture archive just to exercise this comparison.
    [Fact]
    public void CheckEntrySizeOrThrow_ThrowsRomTooLargeException_WhenEntrySizeExceedsTheLimit()
    {
        var method = typeof(GamesService).GetMethod("CheckEntrySizeOrThrow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("GamesService.CheckEntrySizeOrThrow not found; has it been renamed?");

        var ex = Assert.Throws<System.Reflection.TargetInvocationException>(
            () => method.Invoke(null, new object[] { 100L, 99L }));

        Assert.IsType<RomTooLargeException>(ex.InnerException);
        var tooLarge = (RomTooLargeException)ex.InnerException!;
        Assert.Equal(100L, tooLarge.ActualBytes);
        Assert.Equal(99L, tooLarge.MaxBytes);

        // Exactly at the limit must still pass -- this is a ceiling, not a strict-less-than bound.
        method.Invoke(null, new object[] { 99L, 99L });
    }

    // ExtractRomFromArchive itself, for a genuinely oversized archive, still succeeds below the
    // real (512 MB) ceiling -- this guards against the size-cap change accidentally breaking the
    // ordinary small-ROM case the enforcement now sits in front of.
    [Fact]
    public void ExtractRomFromArchive_StillSucceeds_WellUnderMaxExtractedRomBytes()
    {
        var archivePath = ZipFixtures.WriteZip(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip", ("game.nes", new byte[] { 9, 8, 7 }));
        try
        {
            var rom = GamesService.ExtractRomFromArchive(archivePath);

            Assert.Equal(new byte[] { 9, 8, 7 }, rom);
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public async Task SetCoreOverrideAsync_RoundTripsOverride_SetReadBackThenClear()
    {
        var (service, libraryId, gameId) = CreateArcadeGameFixture(bothCoresMatch: true);
        var userId = Guid.NewGuid();

        // Baseline: no override yet. FBNeo is preferred when both pinned DATs validate the ROM.
        var before = await service.GetGameAsync(libraryId, gameId, userId, CancellationToken.None);
        Assert.NotNull(before);
        Assert.Equal("arcade", before!.RecommendedCore);
        Assert.Equal(["arcade", "mame"], before.AvailableCores);
        Assert.Null(before.UserCoreOverride);
        Assert.Equal("arcade", before.Core);

        // Set: choose the non-recommended, but still validated, core.
        var afterSet = await service.SetCoreOverrideAsync(libraryId, gameId, userId, "mame", CancellationToken.None);
        Assert.NotNull(afterSet);
        Assert.Equal("mame", afterSet!.UserCoreOverride);
        Assert.Equal("mame", afterSet.Core);

        // Read back: a separate GetGameAsync call sees the persisted override.
        var reread = await service.GetGameAsync(libraryId, gameId, userId, CancellationToken.None);
        Assert.NotNull(reread);
        Assert.Equal("mame", reread!.UserCoreOverride);
        Assert.Equal("mame", reread.Core);

        // Clear: a null core removes the override and reverts to the recommendation.
        var afterClear = await service.SetCoreOverrideAsync(libraryId, gameId, userId, null, CancellationToken.None);
        Assert.NotNull(afterClear);
        Assert.Null(afterClear!.UserCoreOverride);
        Assert.Equal("arcade", afterClear.Core);
    }

    [Fact]
    public async Task SetCoreOverrideAsync_CoreNotInAvailableCores_ThrowsArgumentException()
    {
        var (service, libraryId, gameId) = CreateArcadeGameFixture(bothCoresMatch: true);
        var userId = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.SetCoreOverrideAsync(libraryId, gameId, userId, "not-a-real-core", CancellationToken.None));
    }

    [Fact]
    public async Task SetCoreOverrideAsync_MameValidatedSet_AllowsExplicitExperimentalFbneo()
    {
        var (service, libraryId, gameId) = CreateArcadeGameFixture(bothCoresMatch: false);
        var userId = Guid.NewGuid();

        var before = await service.GetGameAsync(libraryId, gameId, userId, CancellationToken.None);
        Assert.NotNull(before);
        Assert.Equal("mame", before!.RecommendedCore);
        Assert.Equal(["mame"], before.AvailableCores);

        var afterSet = await service.SetCoreOverrideAsync(libraryId, gameId, userId, "arcade", CancellationToken.None);
        Assert.NotNull(afterSet);
        Assert.Equal("arcade", afterSet!.UserCoreOverride);
        Assert.Equal("arcade", afterSet.Core);

        var reread = await service.GetGameAsync(libraryId, gameId, userId, CancellationToken.None);
        Assert.NotNull(reread);
        Assert.Equal("arcade", reread!.UserCoreOverride);
        Assert.Equal("arcade", reread.Core);
    }

    [Fact]
    public async Task SetBackendOverrideAsync_RoundTripsEmulatorJs_WithoutChangingCore()
    {
        var (service, libraryId, gameId) = CreateArcadeGameFixture(bothCoresMatch: true);
        var userId = Guid.NewGuid();

        var before = await service.GetGameAsync(libraryId, gameId, userId, CancellationToken.None);
        Assert.NotNull(before);
        Assert.Null(before!.UserBackendOverride);
        Assert.True(before.BackendOverrideSupported);
        Assert.Equal("arcade", before.Core);

        var afterSet = await service.SetBackendOverrideAsync(
            libraryId, gameId, userId, "emulatorjs", CancellationToken.None);
        Assert.NotNull(afterSet);
        Assert.Equal("emulatorjs", afterSet!.UserBackendOverride);
        Assert.Equal("arcade", afterSet.Core);

        var reread = await service.GetGameAsync(libraryId, gameId, userId, CancellationToken.None);
        Assert.NotNull(reread);
        Assert.Equal("emulatorjs", reread!.UserBackendOverride);

        var afterClear = await service.SetBackendOverrideAsync(
            libraryId, gameId, userId, null, CancellationToken.None);
        Assert.NotNull(afterClear);
        Assert.Null(afterClear!.UserBackendOverride);
    }

    [Fact]
    public async Task SetBackendOverrideAsync_RejectsUnknownBackend()
    {
        var (service, libraryId, gameId) = CreateArcadeGameFixture(bothCoresMatch: true);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.SetBackendOverrideAsync(
                libraryId, gameId, Guid.NewGuid(), "not-a-player", CancellationToken.None));
    }

    [Fact]
    public void TryGetCached_OnFreshArcadeCompatibilityService_ReturnsFalse_WithoutTouchingTheArchive()
    {
        // ArcadeCompatibilityService.TryGetCached must consult ONLY the in-memory resolution
        // cache -- it must never call ReadArchiveContents (hash the archive). A fresh service
        // instance (Resolve was never invoked) proves this directly: the "archive" path below
        // does not exist on disk at all, so any code path that tried to open/hash it would throw
        // (ZipFile.OpenRead on a missing file throws). TryGetCached must not.
        Directory.CreateDirectory(_root);
        var service = new ArcadeCompatibilityService(
            Path.Combine(_root, "missing-fbneo.dat"),
            Path.Combine(_root, "missing-mame.dat"));
        var neverCreatedArchive = Path.Combine(_root, "never-created.zip");

        var hit = service.TryGetCached(neverCreatedArchive, out _);

        Assert.False(hit);
    }

    [Fact]
    public void ResolveThumbSource_ColdArcadeCache_FallsBackToBothCatalogs_WithoutHashing()
    {
        // Both pinned DATs validate this fixture's ROM (see CreateArcadeGameFixture), so IF
        // ResolveThumbSource still ran the full hashing Resolve()/ResolveArcadeCompatibility path
        // here (a regression back to the pre-fix behavior), the result would be the validated,
        // FBNeo-preferred list ["arcade", "mame"]. GetGameAsync/SetCoreOverrideAsync are
        // deliberately never called first, so ArcadeCompatibilityService's resolution cache stays
        // cold for this archive going into the assertion below.
        var (service, libraryId, gameId) = CreateArcadeGameFixture(bothCoresMatch: true);

        var source = service.ResolveThumbSource(libraryId, gameId);

        Assert.NotNull(source);
        // Cache-miss fallback per the fixed contract: the system's default core ("mame", from the
        // MAME system folder) plus both arcade thumbnail catalogs, distinct -- never the validated
        // (and differently-ordered) result a fresh hash would have produced.
        Assert.Equal(["mame", "arcade"], source!.Cores);
    }

    private (GamesService Service, string LibraryId, string GameId) CreateArcadeGameFixture(bool bothCoresMatch)
    {
        Directory.CreateDirectory(_root);
        var libraryRoot = Path.Combine(_root, "Games");
        var systemDir = Path.Combine(libraryRoot, "MAME");
        Directory.CreateDirectory(systemDir);

        var romBytes = new byte[] { 10, 20, 30, 40 };
        var romPath = ZipFixtures.WriteZip(systemDir, "atetris.zip", ("chip.bin", romBytes));

        var sha1 = Convert.ToHexString(SHA1.HashData(romBytes)).ToLowerInvariant();
        var mameDatPath = Path.Combine(_root, "mame.dat");
        File.WriteAllText(
            mameDatPath,
            $"<datafile><game name=\"atetris\"><rom name=\"chip.bin\" size=\"{romBytes.Length}\" sha1=\"{sha1}\" /></game></datafile>");

        string fbneoDatPath;
        if (bothCoresMatch)
        {
            fbneoDatPath = Path.Combine(_root, "fbneo.dat");
            File.WriteAllText(
                fbneoDatPath,
                $"<datafile><game name=\"atetris\"><rom name=\"chip.bin\" size=\"{romBytes.Length}\" sha1=\"{sha1}\" /></game></datafile>");
        }
        else
        {
            fbneoDatPath = Path.Combine(_root, "missing-fbneo.dat");
        }

        var arcadeCompatibility = new ArcadeCompatibilityService(fbneoDatPath, mameDatPath);
        var arcadeOverrides = new ArcadeCoreOverrideService(Path.Combine(_root, "overrides"));
        var backendOverrides = new GameBackendOverrideService(Path.Combine(_root, "backend-overrides"));

        var libraryItemId = Guid.NewGuid().ToString();
        var libraryManager = new FakeLibraryManager(new List<VirtualFolderInfo>
        {
            new()
            {
                Name = "Games",
                ItemId = libraryItemId,
                Locations = new[] { libraryRoot },
            },
        });

        var service = new GamesService(
            libraryManager,
            rdb: null,
            launchBox: null,
            arcadeCompatibility: arcadeCompatibility,
            arcadeCoreOverrides: arcadeOverrides,
            gameBackendOverrides: backendOverrides);

        var games = service.GetGames(libraryItemId, "MAME");
        var gameId = Assert.Single(games).Id;

        return (service, libraryItemId, gameId);
    }

    private static GamePathResolver CreatePathResolver(params string[] locations)
    {
        return new GamePathResolver(() =>
        [
            new GameLibrary
            {
                Id = "library",
                Name = "Games",
                Locations = locations.ToList(),
            },
        ]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
