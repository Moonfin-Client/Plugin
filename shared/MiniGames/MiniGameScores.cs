using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Moonfin.LiveTv;

namespace Moonfin.MiniGames;

/// <summary>One user's best run at one game, as stored.</summary>
public class MiniGameScore
{
    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("userName")]
    public string UserName { get; set; } = string.Empty;

    [JsonPropertyName("score")]
    public int Score { get; set; }

    [JsonPropertyName("at")]
    public DateTime At { get; set; }
}

/// <summary>A row of a game's board, as the apps get it.</summary>
public class MiniGameBoardEntry
{
    [JsonPropertyName("userName")]
    public string UserName { get; set; } = string.Empty;

    [JsonPropertyName("score")]
    public int Score { get; set; }

    [JsonPropertyName("at")]
    public string At { get; set; } = string.Empty;
}

/// <summary>Where a posted run landed. Null when it missed the board.</summary>
public class MiniGameRank
{
    [JsonPropertyName("rank")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? Rank { get; set; }
}

/// <summary>Where the boards are kept, every game's rows by game id.</summary>
public interface IMiniGameScoreStore
{
    Dictionary<string, List<MiniGameScore>> Load();

    void Save(Dictionary<string, List<MiniGameScore>> boards);
}

/// <summary>Every board in one file, written with the server's own safe-write helper.</summary>
public sealed class FileMiniGameScoreStore : IMiniGameScoreStore
{
    private readonly string _folder;
    private readonly IStateFile _files;

    public FileMiniGameScoreStore(string folder, IStateFile files)
    {
        _folder = folder;
        _files = files;
    }

    private string FilePath => Path.Combine(_folder, "scores.json");

    public Dictionary<string, List<MiniGameScore>> Load()
    {
        try
        {
            return _files.Read(FilePath, json => JsonSerializer.Deserialize<Dictionary<string, List<MiniGameScore>>>(json))
                ?? new Dictionary<string, List<MiniGameScore>>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, List<MiniGameScore>>();
        }
    }

    public void Save(Dictionary<string, List<MiniGameScore>> boards)
    {
        Directory.CreateDirectory(_folder);
        _files.Write(FilePath, JsonSerializer.Serialize(boards));
    }
}

/// <summary>
/// The top 3 of each After Hours game. Each user keeps one row per game holding their best, so
/// one person can't hold every spot, and a tie goes to whoever got there first.
/// </summary>
public class MiniGameScoreService
{
    public const int BoardSize = 3;

    public const int MaxScore = 9_999_999;

    private static readonly HashSet<string> GameIds = new()
    {
        "buffer-run", "fast-forward", "cable-crawl", "case-stack", "pixel-purge",
    };

    private readonly IMiniGameScoreStore _store;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();

    public MiniGameScoreService(IMiniGameScoreStore store, Func<DateTime>? utcNow = null)
    {
        _store = store;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public static bool IsGame(string? gameId) => gameId != null && GameIds.Contains(gameId);

    /// <summary>
    /// The score in a body like {"score": 18420}, or null when it's missing or isn't a whole
    /// number from 0 to <see cref="MaxScore"/>.
    /// </summary>
    public static int? ReadScore(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in body.EnumerateObject())
        {
            if (string.Equals(property.Name, "score", StringComparison.OrdinalIgnoreCase))
            {
                var value = property.Value;
                return value.ValueKind == JsonValueKind.Number
                    && value.TryGetDecimal(out var score)
                    && score == decimal.Truncate(score)
                    && score >= 0
                    && score <= MaxScore
                        ? (int)score
                        : null;
            }
        }

        return null;
    }

    public IReadOnlyList<MiniGameBoardEntry> Top(string gameId)
    {
        lock (_gate)
        {
            _store.Load().TryGetValue(gameId, out var rows);
            return Ranked(rows ?? Enumerable.Empty<MiniGameScore>())
                .Take(BoardSize)
                .Select(r => new MiniGameBoardEntry
                {
                    UserName = r.UserName,
                    Score = r.Score,
                    At = r.At.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                })
                .ToList();
        }
    }

    /// <summary>
    /// Records a finished run and returns its place on the board, or null when it isn't the
    /// user's new best or doesn't make the top 3.
    /// </summary>
    public int? Submit(string gameId, Guid userId, string userName, int score)
    {
        var id = userId.ToString("N");
        lock (_gate)
        {
            var boards = _store.Load();
            if (!boards.TryGetValue(gameId, out var rows) || rows == null)
            {
                rows = new List<MiniGameScore>();
                boards[gameId] = rows;
            }

            var best = rows.FirstOrDefault(r => r.UserId == id);
            if (best != null)
            {
                if (best.Score >= score)
                {
                    return null;
                }

                rows.Remove(best);
            }

            var row = new MiniGameScore { UserId = id, UserName = userName, Score = score, At = _utcNow() };
            rows.Add(row);
            _store.Save(boards);

            var rank = Ranked(rows).TakeWhile(r => r != row).Count() + 1;
            return rank <= BoardSize ? rank : null;
        }
    }

    // OrderBy is stable and each new best is appended, so a tie goes to the earlier run.
    private static IEnumerable<MiniGameScore> Ranked(IEnumerable<MiniGameScore> rows) =>
        rows.OrderByDescending(r => r.Score);
}
