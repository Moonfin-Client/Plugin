using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Moonfin.MiniGames.Tests;

public class MiniGameScoreServiceTests
{
    private static readonly Guid Ann = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Cat = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Dan = Guid.Parse("44444444-4444-4444-4444-444444444444");

    // Round-trips through JSON like the file stores, so the stored shape is what's tested.
    private sealed class MemoryStore : IMiniGameScoreStore
    {
        public string Json { get; private set; } = "{}";

        public int Saves { get; private set; }

        public Dictionary<string, List<MiniGameScore>> Load() =>
            JsonSerializer.Deserialize<Dictionary<string, List<MiniGameScore>>>(Json)!;

        public void Save(Dictionary<string, List<MiniGameScore>> boards)
        {
            Json = JsonSerializer.Serialize(boards);
            Saves++;
        }
    }

    private readonly MemoryStore _store = new();
    private DateTime _now = new(2026, 11, 6, 21, 4, 0, 250, DateTimeKind.Utc);

    private MiniGameScoreService Service() => new(_store, () => _now);

    private static int? Read(string json) => MiniGameScoreService.ReadScore(JsonDocument.Parse(json).RootElement);

    [Theory]
    [InlineData("buffer-run", true)]
    [InlineData("fast-forward", true)]
    [InlineData("cable-crawl", true)]
    [InlineData("case-stack", true)]
    [InlineData("pixel-purge", true)]
    [InlineData("Buffer-Run", false)]
    [InlineData("marquee-hop", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyTheFiveGamesExist(string? gameId, bool expected) =>
        Assert.Equal(expected, MiniGameScoreService.IsGame(gameId));

    [Theory]
    [InlineData("{\"score\":18420}", 18420)]
    [InlineData("{\"score\":0}", 0)]
    [InlineData("{\"score\":9999999}", 9999999)]
    [InlineData("{\"Score\":5}", 5)]
    [InlineData("{\"score\":120.0}", 120)]
    [InlineData("{\"score\":-1}", null)]
    [InlineData("{\"score\":10000000}", null)]
    [InlineData("{\"score\":1.5}", null)]
    [InlineData("{\"score\":\"100\"}", null)]
    [InlineData("{\"score\":null}", null)]
    [InlineData("{\"score\":1e400}", null)]
    [InlineData("{}", null)]
    [InlineData("[100]", null)]
    [InlineData("100", null)]
    public void AScoreIsAWholeNumberInRange(string body, int? expected) =>
        Assert.Equal(expected, Read(body));

    [Fact]
    public void AnEmptyBoardHasNoRows() =>
        Assert.Empty(Service().Top("buffer-run"));

    [Fact]
    public void TheBoardIsTheTopThreeBestFirst()
    {
        var scores = Service();
        Assert.Equal(1, scores.Submit("buffer-run", Ann, "ann", 100));
        Assert.Equal(1, scores.Submit("buffer-run", Bob, "bob", 300));
        Assert.Equal(2, scores.Submit("buffer-run", Cat, "cat", 200));
        Assert.Null(scores.Submit("buffer-run", Dan, "dan", 50));

        var board = scores.Top("buffer-run");
        Assert.Equal(new[] { "bob", "cat", "ann" }, board.Select(r => r.UserName));
        Assert.Equal(new[] { 300, 200, 100 }, board.Select(r => r.Score));
        Assert.Equal("2026-11-06T21:04:00Z", board[0].At);
    }

    [Fact]
    public void EachUserKeepsOneRowWithTheirBest()
    {
        var scores = Service();
        scores.Submit("case-stack", Ann, "ann", 100);
        scores.Submit("case-stack", Ann, "ann", 300);
        scores.Submit("case-stack", Ann, "ann", 200);
        scores.Submit("case-stack", Bob, "bob", 150);

        var board = scores.Top("case-stack");
        Assert.Equal(2, board.Count);
        Assert.Equal(("ann", 300), (board[0].UserName, board[0].Score));
        Assert.Equal(("bob", 150), (board[1].UserName, board[1].Score));
    }

    [Fact]
    public void ARunBelowTheUsersBestMissesAndWritesNothing()
    {
        var scores = Service();
        scores.Submit("pixel-purge", Ann, "ann", 300);
        var saves = _store.Saves;

        Assert.Null(scores.Submit("pixel-purge", Ann, "ann", 200));
        Assert.Null(scores.Submit("pixel-purge", Ann, "ann", 300));
        Assert.Equal(saves, _store.Saves);
    }

    [Fact]
    public void ATieGoesToTheEarlierRun()
    {
        var scores = Service();
        scores.Submit("cable-crawl", Ann, "ann", 500);
        _now = _now.AddMinutes(1);
        Assert.Equal(2, scores.Submit("cable-crawl", Bob, "bob", 500));

        Assert.Equal(new[] { "ann", "bob" }, scores.Top("cable-crawl").Select(r => r.UserName));
    }

    [Fact]
    public void ATieInsideTheSameSecondStillGoesToTheEarlierRun()
    {
        var scores = Service();
        scores.Submit("cable-crawl", Ann, "ann", 10);
        scores.Submit("cable-crawl", Bob, "bob", 500);
        scores.Submit("cable-crawl", Ann, "ann", 500);

        Assert.Equal(new[] { "bob", "ann" }, scores.Top("cable-crawl").Select(r => r.UserName));
    }

    [Fact]
    public void ANewBestReportsItsPlace()
    {
        var scores = Service();
        scores.Submit("fast-forward", Ann, "ann", 300);
        scores.Submit("fast-forward", Bob, "bob", 200);
        scores.Submit("fast-forward", Cat, "cat", 100);

        Assert.Null(scores.Submit("fast-forward", Dan, "dan", 90));
        Assert.Equal(3, scores.Submit("fast-forward", Dan, "dan", 150));
        Assert.Equal(1, scores.Submit("fast-forward", Cat, "cat", 400));
        Assert.Equal(new[] { "cat", "ann", "bob" }, scores.Top("fast-forward").Select(r => r.UserName));
    }

    [Fact]
    public void GamesKeepTheirOwnBoards()
    {
        var scores = Service();
        scores.Submit("buffer-run", Ann, "ann", 100);
        scores.Submit("pixel-purge", Bob, "bob", 200);

        Assert.Equal(new[] { "ann" }, scores.Top("buffer-run").Select(r => r.UserName));
        Assert.Equal(new[] { "bob" }, scores.Top("pixel-purge").Select(r => r.UserName));
    }

    [Fact]
    public void TheStoredFileHoldsEveryUsersBest()
    {
        var scores = Service();
        foreach (var (user, name, score) in new[] { (Ann, "ann", 1), (Bob, "bob", 2), (Cat, "cat", 3), (Dan, "dan", 4) })
        {
            scores.Submit("case-stack", user, name, score);
        }

        var stored = _store.Load()["case-stack"];
        Assert.Equal(4, stored.Count);
        Assert.Contains(stored, r => r.UserId == Ann.ToString("N") && r.Score == 1);
        Assert.Contains("\"userName\":\"dan\"", _store.Json);
    }

    [Fact]
    public void AMissedRunStillSerializesItsRank() =>
        Assert.Equal("{\"rank\":null}", JsonSerializer.Serialize(
            new MiniGameRank(),
            new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull }));
}
