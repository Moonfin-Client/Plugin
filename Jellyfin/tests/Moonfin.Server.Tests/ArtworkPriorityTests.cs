using System.Runtime.CompilerServices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moonfin.Server.Models;
using Moonfin.Server.Services;
using Xunit;

namespace Moonfin.Server.Tests;

/// <summary>
/// Cost regression tests for the priority-hint path behind
/// <c>POST Moonfin/Games/{libraryId}/ArtworkPriority</c>.
///
/// <para>
/// <see cref="GamesService.GetGames"/> is an uncached recursive filesystem enumeration of every
/// system folder that resolves a core and a title per ROM. The priority endpoint accepts up to
/// 128 items with up to 3 roles each, and it used to resolve every membership check and every
/// promote through <c>TryCreateCandidate(libraryId, gameId, kind)</c>, which walked the whole
/// library each time: 128 + 384 = 512 complete walks for one authenticated request. That is an
/// authenticated denial of service on any large or network-mounted library, so the walk count for
/// a maximal request is pinned here rather than left to review.
/// </para>
/// </summary>
public sealed class ArtworkPriorityTests : IDisposable
{
    private const int MaxArtworkPriorityItems = 128;
    private static readonly string[] ArtworkRoles = ["boxart", "snap", "title"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"moonfin-artwork-priority-{Guid.NewGuid():N}");

    /// <summary>
    /// Replays the exact sequence <c>GamesController.PostArtworkPriority</c> issues for a maximal
    /// request (one membership check per item, then one promote per item-role pair) and asserts the
    /// whole batch costs at most one library enumeration.
    /// </summary>
    [Fact]
    public async Task PriorityBatch_EnumeratesTheLibraryAtMostOnce()
    {
        var harness = CreateHarness(gameCount: 200);
        var gameIds = harness.Games
            .GetGames(harness.LibraryId, null)
            .Take(MaxArtworkPriorityItems)
            .Select(game => game.Id)
            .ToList();
        Assert.Equal(MaxArtworkPriorityItems, gameIds.Count);

        // Only the request itself is measured; building the fixture above is setup cost.
        harness.Games.ResetGetGamesCallCount();

        var lookup = harness.Service.CreateGameLookup(harness.LibraryId);
        foreach (var gameId in gameIds)
        {
            Assert.True(harness.Service.IsCurrentGameMember(harness.LibraryId, gameId, lookup));
        }

        foreach (var gameId in gameIds)
        {
            foreach (var role in ArtworkRoles)
            {
                await harness.Service.PromoteAsync(harness.LibraryId, gameId, role, lookup, CancellationToken.None);
            }
        }

        Assert.InRange(harness.Games.GetGamesCallCount, 0, 1);
    }

    /// <summary>
    /// The batched snapshot must not change which games are considered members. A snapshot resolves
    /// exactly what the per-call filesystem path resolves, including rejecting an unknown game id.
    /// </summary>
    [Fact]
    public void GameLookup_MatchesTheUncachedMembershipDecision()
    {
        var harness = CreateHarness(gameCount: 8);
        var lookup = harness.Service.CreateGameLookup(harness.LibraryId);

        foreach (var game in harness.Games.GetGames(harness.LibraryId, null))
        {
            Assert.Equal(
                harness.Service.IsCurrentGameMember(harness.LibraryId, game.Id),
                harness.Service.IsCurrentGameMember(harness.LibraryId, game.Id, lookup));
        }

        var unknown = GamePathResolver.EncodeToken(Path.Combine(_root, "Games", "SNES", "Not Installed.sfc"));
        Assert.False(harness.Service.IsCurrentGameMember(harness.LibraryId, unknown));
        Assert.False(harness.Service.IsCurrentGameMember(harness.LibraryId, unknown, lookup));

        // A snapshot taken against a different library must never answer for this one. It is
        // ignored and the uncached resolve runs instead, so the answer stays correct (true)
        // rather than becoming a false negative from the wrong inventory.
        var foreign = harness.Service.CreateGameLookup(Guid.NewGuid().ToString());
        Assert.True(harness.Service.IsCurrentGameMember(harness.LibraryId, lookup.Games.Keys.First(), foreign));
    }

    /// <summary>An unknown library resolves to an empty snapshot without touching the filesystem.</summary>
    [Fact]
    public void GameLookup_ForAnUnknownLibraryCostsNothing()
    {
        var harness = CreateHarness(gameCount: 4);
        harness.Games.ResetGetGamesCallCount();

            var lookup = harness.Service.CreateGameLookup(Guid.NewGuid().ToString());

        Assert.Empty(lookup.Games);
        Assert.Equal(0, harness.Games.GetGamesCallCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private Harness CreateHarness(int gameCount)
    {
        var libraryRoot = Path.Combine(_root, "Games");
        var systemRoot = Path.Combine(libraryRoot, "SNES");
        Directory.CreateDirectory(systemRoot);
        for (var i = 0; i < gameCount; i++)
        {
            File.WriteAllBytes(Path.Combine(systemRoot, $"Game {i:D4}.sfc"), [1, 2, 3]);
        }

        var libraryId = Guid.NewGuid().ToString();
        var libraryManager = new FakeLibraryManager(
        [
            new VirtualFolderInfo
            {
                Name = "Games",
                ItemId = libraryId,
                Locations = [libraryRoot],
            },
        ]);

        var games = new CountingGamesService(libraryManager);
        var catalog = new GameArtworkCatalog(Path.Combine(_root, "catalog"));
        var thumbs = (GameThumbService)RuntimeHelpers.GetUninitializedObject(typeof(GameThumbService));
        var service = new GameArtworkReconciliationService(
            games,
            thumbs,
            catalog,
            NullLogger<GameArtworkReconciliationService>.Instance);

        return new Harness(libraryId, games, service);
    }

    private sealed record Harness(string LibraryId, CountingGamesService Games, GameArtworkReconciliationService Service);

    /// <summary>
    /// Real <see cref="GamesService"/> behavior with a counter on the one expensive member, so the
    /// assertions above measure actual filesystem enumerations rather than a mock's expectations.
    /// </summary>
    private sealed class CountingGamesService : GamesService
    {
        private int _getGamesCallCount;

        public CountingGamesService(ILibraryManager libraryManager)
            : base(libraryManager)
        {
        }

        public int GetGamesCallCount => Volatile.Read(ref _getGamesCallCount);

        public void ResetGetGamesCallCount() => Volatile.Write(ref _getGamesCallCount, 0);

        public override IReadOnlyList<GameSummary> GetGames(string libraryId, string? systemId)
        {
            Interlocked.Increment(ref _getGamesCallCount);
            return base.GetGames(libraryId, systemId);
        }
    }
}
