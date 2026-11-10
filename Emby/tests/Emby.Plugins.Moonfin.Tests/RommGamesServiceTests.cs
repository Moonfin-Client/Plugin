using System.Reflection;
using Emby.Plugins.Moonfin.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Moonfin.Games;
using Moonfin.Romm;
using Xunit;

namespace Emby.Plugins.Moonfin.Tests;

/// <summary>
/// A RomM-backed library answers the same routes as a folder library, so the apps need no
/// change. These pin what each route returns for it.
/// </summary>
public class RommGamesServiceTests
{
    private const string LibraryId = "8f3e04c1bc361191c16f63a5c8f9c190";

    [Fact]
    public void TheRommLibraryIsListedWithNoFoldersToScan()
    {
        var service = Service();

        var library = Assert.Single(service.GetGameLibraries());
        Assert.Equal(LibraryId, library.Id);
        Assert.Equal("Retro Games", library.Name);
        Assert.Empty(library.Locations);
    }

    [Fact]
    public void ListsPlatformsThatHaveGamesAsSystems()
    {
        var system = Assert.Single(Service().GetSystems(LibraryId));

        Assert.Equal("SNES", system.Id);
        Assert.Equal("Super Nintendo Entertainment System", system.Name);
        Assert.Equal("snes", system.Core);
        Assert.Equal(2, system.GameCount);
    }

    [Fact]
    public void ListsGamesUnderTheirSystemWithTheFileTheAppReceives()
    {
        var games = Service().GetGames(LibraryId, "SNES");

        Assert.Equal(new[] { "Donkey Kong Country 2", "Super Mario World" }, games.Select(game => game.Title));
        var mario = games[1];
        Assert.Equal("romm-12", mario.Id);
        Assert.Equal("SNES", mario.System);
        Assert.Equal("Super Mario World (USA).sfc", mario.FileName);
        Assert.Empty(Service().GetGames(LibraryId, "PlayStation"));
    }

    [Fact]
    public async Task DetailCarriesRommMetadataAndThePlatformFirmware()
    {
        var detail = await Service().GetGameAsync(LibraryId, "romm-13", userId: null, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal("Donkey Kong Country 2", detail!.Title);
        Assert.Equal(4194304, detail.SizeBytes);
        Assert.Equal("Platform", detail.Genre);
        Assert.Equal(1995, detail.Year);
        Assert.Equal(4.0, detail.Rating);
        var bios = Assert.Single(detail.Bios);
        Assert.Equal("romm-fw-3", bios.Id);
        Assert.Equal("bios.bin", bios.FileName);
    }

    [Fact]
    public async Task AnUnknownGameIsNotFound()
    {
        var service = Service();

        Assert.Null(await service.GetGameAsync(LibraryId, "romm-99", userId: null, CancellationToken.None));
        Assert.Null(await service.GetGameAsync(LibraryId, "not-a-romm-id", userId: null, CancellationToken.None));
    }

    [Fact]
    public void ArtworkComesFromRommAndIsKeyedOnRommsIds()
    {
        var source = Service().ResolveThumbSource(LibraryId, "romm-12");

        Assert.NotNull(source);
        Assert.Equal("Super Mario World (USA).sfc", source!.RomPath);
        Assert.Equal("SNES", source.SystemName);
        Assert.NotNull(source.Remote);
        Assert.Equal("romm/12", source.Remote!.CatalogPath);
        Assert.Equal("15-2026-10-23T17:47:20", source.Remote.Fingerprint);
        Assert.Equal(
            "http://romm.test:8080/assets/romm/resources/roms/10/12/cover/big.png?ts=2026-10-23%2017:47:20",
            source.Remote.UrlFor(GameThumbService.ThumbKind.Boxart));
        Assert.Null(source.Remote.UrlFor(GameThumbService.ThumbKind.Title));
    }

    [Fact]
    public void ALibraryRommHasNeverAnsweredForIsNotReady()
    {
        var service = Service(load: (_, _) => throw new HttpRequestException("RomM is down"));

        Assert.False(service.IsLibraryInventoryReady(LibraryId));
        Assert.True(service.IsLibraryInventoryReady("9bdd941d4d8f6619d69450f8e6a90bbf"));
    }

    [Fact]
    public void WithoutRommSettingsTheLibraryIsAnOrdinaryFolderLibrary()
    {
        var service = new GamesService(new EmbyGameLibraryHost(LibraryManager()), romm: new RommSource(
            () => null,
            () => throw new InvalidOperationException("No RomM call is expected."),
            GameSystemCoreResolver.RommRules));

        Assert.Empty(service.GetGameLibraries());
        Assert.Empty(service.GetSystems(LibraryId));
    }

    private static GamesService Service(Func<RommSettings, CancellationToken, Task<RommSnapshot>>? load = null) =>
        new(new EmbyGameLibraryHost(LibraryManager()), romm: new RommSource(
            () => RommSettings.From("http://romm.test:8080", "rmm_abc", LibraryId),
            () => throw new InvalidOperationException("The test loads through the override."),
            GameSystemCoreResolver.RommRules,
            load ?? ((_, _) => Task.FromResult(Snapshot()))));

    // The library points at a folder that doesn't exist, the way an admin might set up a library
    // only to host RomM's games.
    private static ILibraryManager LibraryManager()
    {
        var manager = DispatchProxy.Create<ILibraryManager, FakeLibraryManager>();
        ((FakeLibraryManager)(object)manager).Folders.Add(new VirtualFolderInfo
        {
            Name = "Retro Games",
            ItemId = LibraryId,
            Locations = new[] { Path.Combine(Path.GetTempPath(), $"moonfin-romm-{Guid.NewGuid():N}") },
        });
        return manager;
    }

    private static RommSnapshot Snapshot()
    {
        var snes = new RommPlatform { Id = 10, SystemId = "SNES", Name = "Super Nintendo Entertainment System", Core = "snes" };
        snes.Firmware.Add(new RommFile { Id = 3, FileName = "bios.bin", SizeBytes = 1024 });
        var psx = new RommPlatform { Id = 8, SystemId = "PlayStation", Name = "PlayStation", Core = "psx" };
        var mario = new RommGame
        {
            Id = 12,
            PlatformId = 10,
            Title = "Super Mario World",
            Rom = new RommFile { Id = 15, FileName = "Super Mario World (USA).sfc", SizeBytes = 524288 },
            ServedName = "Super Mario World (USA).sfc",
            ServedSizeBytes = 524288,
            UpdatedAt = "2026-10-23T17:47:20",
            CoverPath = "/assets/romm/resources/roms/10/12/cover/big.png?ts=2026-10-23 17:47:20",
        };
        var dkc = new RommGame
        {
            Id = 13,
            PlatformId = 10,
            Title = "Donkey Kong Country 2",
            Rom = new RommFile { Id = 17, FileName = "DKC2 (USA).sfc", SizeBytes = 4194304 },
            ServedName = "DKC2 (USA).sfc",
            ServedSizeBytes = 4194304,
            UpdatedAt = "2026-10-23T17:47:21",
            Genre = "Platform",
            Year = 1995,
            Rating = 4.0,
        };
        return new RommSnapshot(new[] { snes, psx }, new[] { mario, dkc }, DateTime.UtcNow);
    }

    /// <summary>
    /// Answers the library calls the game library host makes. Item lookups throw, as they would for an
    /// id the server doesn't know, so the host has to fall back to the folder list.
    /// </summary>
    public class FakeLibraryManager : DispatchProxy
    {
        public List<VirtualFolderInfo> Folders { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "GetVirtualFolders")
            {
                return targetMethod.ReturnType.IsArray ? Folders.ToArray() : Folders;
            }

            throw new NotImplementedException(targetMethod?.Name);
        }
    }
}
