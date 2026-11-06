using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Moonfin.LiveTv;

namespace Moonfin.Calendar;

/// <summary>A Remind me from the calendar, kept until the episode or movie lands in the library.</summary>
[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public sealed class CalendarReminder
{
    [JsonPropertyName("key")]
    public string? Key { get; set; }

    /// <summary>"episode" or "movie".</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("tvdbId")]
    public int? TvdbId { get; set; }

    [JsonPropertyName("tmdbId")]
    public int? TmdbId { get; set; }

    [JsonPropertyName("imdbId")]
    public string? ImdbId { get; set; }

    [JsonPropertyName("seasonNumber")]
    public int? SeasonNumber { get; set; }

    [JsonPropertyName("episodeNumber")]
    public int? EpisodeNumber { get; set; }

    /// <summary>The series or movie name.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>The episode name.</summary>
    [JsonPropertyName("subtitle")]
    public string? Subtitle { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime? CreatedAt { get; set; }

    internal CalendarReminder Copy() => (CalendarReminder)MemberwiseClone();
}

/// <summary>An episode or movie that was added to the library or updated there.</summary>
public sealed class CalendarArrival
{
    /// <summary>For an episode the ids are its series' ids.</summary>
    public CalendarArrival(bool isEpisode, CalendarProviderIds ids, int? season, int? episode, int? episodeEnd)
    {
        IsEpisode = isEpisode;
        Ids = ids;
        Season = season;
        Episode = episode;
        EpisodeEnd = episodeEnd;
    }

    public bool IsEpisode { get; }

    public CalendarProviderIds Ids { get; }

    public int? Season { get; }

    public int? Episode { get; }

    public int? EpisodeEnd { get; }
}

public static class CalendarReminders
{
    public const int MaxPerUser = 500;

    public const string TooManyError = "A user can keep up to 500 reminders";

    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(365);

    private const int MaxText = 500;

    private static readonly Regex ImdbPattern = new Regex(@"^tt\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The key the client computes too, or null when the reminder can't have one.</summary>
    public static string? KeyFor(CalendarReminder reminder)
    {
        switch (reminder.Kind)
        {
            case "episode":
                return reminder.TvdbId > 0 && reminder.SeasonNumber >= 0 && reminder.EpisodeNumber >= 0
                    ? FormattableString.Invariant($"episode:{reminder.TvdbId}:{reminder.SeasonNumber}:{reminder.EpisodeNumber}")
                    : null;
            case "movie":
                if (reminder.TmdbId > 0) return FormattableString.Invariant($"movie:{reminder.TmdbId}");
                return string.IsNullOrWhiteSpace(reminder.ImdbId) ? null : "movie:imdb:" + reminder.ImdbId!.Trim();
            default:
                return null;
        }
    }

    /// <summary>The reminder as it is stored, with its key, or null and the reason it can't be.</summary>
    public static CalendarReminder? Normalize(CalendarReminder? input, out string? error)
    {
        error = null;
        if (input == null)
        {
            error = "A reminder body is required";
            return null;
        }

        var kind = input.Kind?.Trim().ToLowerInvariant();
        var imdb = string.IsNullOrWhiteSpace(input.ImdbId) ? null : input.ImdbId!.Trim().ToLowerInvariant();
        var title = input.Title?.Trim();
        var subtitle = string.IsNullOrWhiteSpace(input.Subtitle) ? null : input.Subtitle!.Trim();
        if (imdb != null && !ImdbPattern.IsMatch(imdb))
        {
            error = "imdbId must look like tt1234567";
            return null;
        }

        if (string.IsNullOrEmpty(title))
        {
            error = "title is required";
            return null;
        }

        if (title!.Length > MaxText || (subtitle?.Length ?? 0) > MaxText)
        {
            error = "title and subtitle are limited to 500 characters";
            return null;
        }

        var isEpisode = kind == "episode";
        var reminder = new CalendarReminder
        {
            Kind = kind,
            TvdbId = isEpisode && input.TvdbId > 0 ? input.TvdbId : null,
            TmdbId = input.TmdbId > 0 ? input.TmdbId : null,
            ImdbId = imdb,
            SeasonNumber = isEpisode ? input.SeasonNumber : null,
            EpisodeNumber = isEpisode ? input.EpisodeNumber : null,
            Title = title,
            Subtitle = isEpisode ? subtitle : null,
        };
        reminder.Key = KeyFor(reminder);
        if (reminder.Key == null)
        {
            error = "An episode reminder needs tvdbId, seasonNumber and episodeNumber, and a movie reminder needs tmdbId or imdbId";
            return null;
        }

        return reminder;
    }

    /// <summary>Whether this arrival is what the reminder waits for. An episode matches on any of its series' ids.</summary>
    public static bool Matches(CalendarReminder reminder, CalendarArrival arrival)
    {
        if ((reminder.Kind == "episode") != arrival.IsEpisode) return false;

        var sameTitle = SameId(reminder.TvdbId, arrival.Ids.Tvdb)
            || SameId(reminder.TmdbId, arrival.Ids.Tmdb)
            || (reminder.ImdbId != null && string.Equals(reminder.ImdbId, arrival.Ids.Imdb, StringComparison.OrdinalIgnoreCase));
        if (!sameTitle) return false;
        if (!arrival.IsEpisode) return true;

        return reminder.SeasonNumber == arrival.Season
            && arrival.Episode <= reminder.EpisodeNumber
            && reminder.EpisodeNumber <= (arrival.EpisodeEnd ?? arrival.Episode);
    }

    public static (string Title, string Body) Message(CalendarReminder reminder)
    {
        var title = reminder.Title + " is in your library";
        if (reminder.Kind != "episode") return (title, "Ready to watch");

        var body = FormattableString.Invariant($"S{reminder.SeasonNumber}E{reminder.EpisodeNumber}");
        return (title, reminder.Subtitle == null ? body : body + " " + reminder.Subtitle);
    }

    private static bool SameId(int? id, string? other) =>
        id > 0 && other != null && string.Equals(id.Value.ToString(CultureInfo.InvariantCulture), other, StringComparison.Ordinal);
}

/// <summary>
/// Each user's reminders in their own file under the plugin data folder. The files are read once
/// and then kept in memory, since a library scan raises item events by the thousand and every one
/// of them is checked against every reminder.
/// </summary>
public sealed class CalendarReminderStore
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _folder;
    private readonly IStateFile _files;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new object();
    private Dictionary<Guid, List<CalendarReminder>>? _byUser;

    public CalendarReminderStore(string folder, IStateFile files, Func<DateTime>? now = null)
    {
        _folder = folder;
        _files = files;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public bool IsEmpty
    {
        get
        {
            lock (_gate)
            {
                return Loaded().Values.All(list => list.Count == 0);
            }
        }
    }

    public List<CalendarReminder> List(Guid userId)
    {
        lock (_gate)
        {
            return Loaded().TryGetValue(userId, out var list) ? list.Select(r => r.Copy()).ToList() : new List<CalendarReminder>();
        }
    }

    /// <summary>Stores a normalized reminder under its key, keeping the first createdAt. Null when the user is at the limit.</summary>
    public CalendarReminder? Upsert(Guid userId, CalendarReminder reminder)
    {
        lock (_gate)
        {
            var all = Loaded();
            var list = all.TryGetValue(userId, out var existing) ? new List<CalendarReminder>(existing) : new List<CalendarReminder>();
            var index = list.FindIndex(r => r.Key == reminder.Key);
            if (index < 0 && list.Count >= CalendarReminders.MaxPerUser) return null;

            var stored = reminder.Copy();
            stored.CreatedAt = index >= 0 ? list[index].CreatedAt : _now();
            if (index >= 0) list[index] = stored;
            else list.Add(stored);

            Save(userId, list);
            all[userId] = list;
            return stored.Copy();
        }
    }

    /// <summary>True when the reminder was there, so a reminder two events race for fires once.</summary>
    public bool Remove(Guid userId, string key)
    {
        lock (_gate)
        {
            var all = Loaded();
            if (!all.TryGetValue(userId, out var existing) || !existing.Any(r => r.Key == key)) return false;

            var list = existing.Where(r => r.Key != key).ToList();
            Save(userId, list);
            all[userId] = list;
            return true;
        }
    }

    /// <summary>Every user's reminders this arrival answers.</summary>
    public List<(Guid UserId, CalendarReminder Reminder)> Matching(CalendarArrival arrival)
    {
        lock (_gate)
        {
            return Loaded()
                .SelectMany(pair => pair.Value.Where(r => CalendarReminders.Matches(r, arrival)).Select(r => (pair.Key, r.Copy())))
                .ToList();
        }
    }

    private Dictionary<Guid, List<CalendarReminder>> Loaded()
    {
        if (_byUser != null) return _byUser;

        var all = new Dictionary<Guid, List<CalendarReminder>>();
        if (Directory.Exists(_folder))
        {
            var cutoff = _now() - CalendarReminders.MaxAge;
            foreach (var path in Directory.EnumerateFiles(_folder, "*.json", SearchOption.TopDirectoryOnly))
            {
                if (!Guid.TryParse(Path.GetFileNameWithoutExtension(path), out var userId)) continue;

                List<CalendarReminder>? list = null;
                try
                {
                    list = _files.Read(path, text => JsonSerializer.Deserialize<List<CalendarReminder>>(text, JsonOptions));
                }
                catch (Exception)
                {
                    // An unreadable file loses that user's reminders rather than every user's.
                }

                all[userId] = (list ?? new List<CalendarReminder>()).Where(r => r?.Key != null && r.CreatedAt > cutoff).ToList();
            }
        }

        return _byUser = all;
    }

    private void Save(Guid userId, List<CalendarReminder> list)
    {
        Directory.CreateDirectory(_folder);
        _files.Write(Path.Combine(_folder, userId + ".json"), JsonSerializer.Serialize(list, JsonOptions));
    }
}
