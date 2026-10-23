using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Moonfin.LiveTv;

/// <summary>
/// Identifies an episode or movie across airings. A movie matches on its nonempty name and
/// description, with no conflicting year or content ids. An episode's persisted identity is the
/// series plus season and episode number. The episode title is kept beside it as an anchor and
/// only ever checked for compatibility, so it never changes which key an airing has.
///
/// The series is the guide's series id when there is one ("id:" namespace), otherwise the
/// normalized series name ("name:" namespace). Two sides that both carry an id compare ids, and
/// when either lacks one they compare names. A multi-episode airing is keyed by its first episode
/// number.
/// </summary>
public sealed class EpisodeKey
{
    // Absent on older persisted episode keys.
    public bool IsMovie { get; set; }

    public string? Description { get; set; }

    public int? ProductionYear { get; set; }

    public Dictionary<string, string> ContentIds { get; set; } = new();

    public static EpisodeKey? FromProgram(GuideProgram program)
    {
        if (!program.IsMovie || (program.Season.HasValue && program.Episode.HasValue))
        {
            return Create(program.SeriesId, program.SeriesName, program.Season, program.Episode, program.EpisodeTitle);
        }

        return string.IsNullOrWhiteSpace(program.SeriesName) || string.IsNullOrWhiteSpace(program.Overview)
            ? null
            : new EpisodeKey
            {
                IsMovie = true,
                SeriesName = program.SeriesName,
                Description = program.Overview,
                ProductionYear = program.ProductionYear,
                ContentIds = new Dictionary<string, string>(program.ContentIds),
            };
    }

    public static bool CompatibleMetadata(int? year, IReadOnlyDictionary<string, string> ids,
        int? otherYear, IReadOnlyDictionary<string, string> otherIds)
    {
        if (year.HasValue && otherYear.HasValue && year != otherYear)
        {
            return false;
        }

        foreach (var id in ids)
        {
            var other = otherIds.FirstOrDefault(p => string.Equals(p.Key, id.Key, StringComparison.OrdinalIgnoreCase)).Value;
            if (!string.IsNullOrWhiteSpace(id.Value) && !string.IsNullOrWhiteSpace(other)
                && !string.Equals(id.Value.Trim(), other.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    public string? SeriesId { get; set; }

    public string SeriesName { get; set; } = string.Empty;

    public int Season { get; set; }

    public int Episode { get; set; }

    /// <summary>The title of the airing this key came from. Compared, never keyed on.</summary>
    public string? EpisodeTitle { get; set; }

    /// <summary>The persisted identity: movie title and description, or series, season and episode.</summary>
    [JsonIgnore]
    public string Id => IsMovie
        ? $"movie:{NormalizeName(SeriesName)}|{NormalizeName(Description)}"
        : SeriesId != null
        ? $"id:{SeriesId.Trim().ToLowerInvariant()}|{Season}|{Episode}"
        : $"name:{NormalizeName(SeriesName)}|{Season}|{Episode}";

    /// <summary>Builds a key, or null when the airing has no season and episode numbers.</summary>
    public static EpisodeKey? Create(string? seriesId, string? seriesName, int? season, int? episode, string? episodeTitle)
    {
        if (season is null || episode is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(seriesId) && string.IsNullOrWhiteSpace(seriesName))
        {
            return null;
        }

        return new EpisodeKey
        {
            SeriesId = string.IsNullOrWhiteSpace(seriesId) ? null : seriesId.Trim(),
            SeriesName = seriesName?.Trim() ?? string.Empty,
            Season = season.Value,
            Episode = episode.Value,
            EpisodeTitle = string.IsNullOrWhiteSpace(episodeTitle) ? null : episodeTitle.Trim(),
        };
    }

    /// <summary>
    /// The movie rule: both names and descriptions present and equal once normalized, with no
    /// conflicting year or content ids.
    /// </summary>
    public static bool SameMovie(string? name, string? description, int? year, IReadOnlyDictionary<string, string> ids,
        string? otherName, string? otherDescription, int? otherYear, IReadOnlyDictionary<string, string> otherIds) =>
        !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(otherName)
        && !string.IsNullOrWhiteSpace(description) && !string.IsNullOrWhiteSpace(otherDescription)
        && NormalizeName(name) == NormalizeName(otherName)
        && NormalizeName(description) == NormalizeName(otherDescription)
        && CompatibleMetadata(year, ids, otherYear, otherIds);

    public static string NormalizeName(string? name) =>
        string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    /// <summary>Same series, season and episode. Titles are not looked at.</summary>
    public bool SameEpisode(EpisodeKey other)
    {
        if (IsMovie || other.IsMovie)
        {
            return IsMovie && other.IsMovie
                && SameMovie(SeriesName, Description, ProductionYear, ContentIds,
                    other.SeriesName, other.Description, other.ProductionYear, other.ContentIds);
        }

        if (Season != other.Season || Episode != other.Episode)
        {
            return false;
        }

        return SeriesId != null && other.SeriesId != null
            ? string.Equals(SeriesId, other.SeriesId, StringComparison.OrdinalIgnoreCase)
            : NormalizeName(SeriesName) == NormalizeName(other.SeriesName);
    }

    /// <summary>
    /// A title is compatible with this key's anchor title when either is missing or they are the
    /// same. Always checked against the anchor, never chained, so two differently titled airings
    /// can't both join through an untitled one.
    /// </summary>
    public bool TitleCompatible(string? otherTitle) =>
        EpisodeTitle == null || string.IsNullOrWhiteSpace(otherTitle)
        || string.Equals(EpisodeTitle, otherTitle.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Same episode, with a compatible title.</summary>
    public bool Matches(EpisodeKey other) => SameEpisode(other) && TitleCompatible(other.EpisodeTitle);
}

/// <summary>
/// One file a recording wrote, and what the plugin could observe of it. A retry or restart of the
/// same airing writes another.
/// </summary>
public sealed class RecordingFragment
{
    public string Path { get; set; } = string.Empty;

    /// <summary>The server process the last sample was taken in. A different one means a restart.</summary>
    public string ProcessId { get; set; } = string.Empty;

    public DateTime FirstSampleAt { get; set; }

    public DateTime LastSampleAt { get; set; }

    public long LastSize { get; set; }

    /// <summary>Seconds between samples in which the file was seen to grow.</summary>
    public double CoveredSeconds { get; set; }

    /// <summary>
    /// Seconds the plugin couldn't observe while the recording may have been running: before the
    /// first sample, missed samples, and after the last one.
    /// </summary>
    public double UnknownSeconds { get; set; }

    public bool Ended { get; set; }

    /// <summary>Library items attached to this file, by path or by the same file renamed in place.</summary>
    public List<Guid> ItemIds { get; set; } = new();
}

public static class RecordingVerdict
{
    public const string Complete = "complete";
    public const string Incomplete = "incomplete";

    /// <summary>The unobserved time decides the verdict either way, so nothing is flagged.</summary>
    public const string Unknown = "unknown";
}

/// <summary>What the plugin captured about one timer while it was recording.</summary>
public sealed class RecordingSnapshot
{
    /// <summary>The service's own timer id, which the recordings manager keys active recordings by.</summary>
    public string TimerId { get; set; } = string.Empty;

    /// <summary>The guide program's id, in the form the server's API takes it.</summary>
    public string? ProgramId { get; set; }

    /// <summary>The id the Live TV API knows the timer by, which a cancellation may carry instead.</summary>
    public string? ApiId { get; set; }

    /// <summary>The timer was cancelled while it recorded, so a short file is what the user asked for.</summary>
    public bool StoppedByUser { get; set; }

    public string? ChannelName { get; set; }

    /// <summary>The program's title, so the log can name a recording without episode numbers.</summary>
    public string? Title { get; set; }

    public DateTime WindowStart { get; set; }

    public DateTime WindowEnd { get; set; }

    public double RuntimeMinutes { get; set; }

    public double PrePaddingMinutes { get; set; }

    public double PostPaddingMinutes { get; set; }

    public EpisodeKey? Episode { get; set; }

    public List<RecordingFragment> Fragments { get; set; } = new();

    public DateTime? JudgedAt { get; set; }

    public string? Verdict { get; set; }

    public double ExpectedMinutes => PrePaddingMinutes + RuntimeMinutes + PostPaddingMinutes;

    /// <summary>The best single file's estimated coverage, in minutes.</summary>
    public double BestCoveredMinutes => Fragments.Count == 0 ? 0 : Fragments.Max(f => f.CoveredSeconds) / 60;
}

/// <summary>A recording judged incomplete, and the library items its files became.</summary>
public sealed class IncompleteRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string TimerId { get; set; } = string.Empty;

    /// <summary>The best single file's estimated coverage.</summary>
    public double RecordedMinutes { get; set; }

    public double ExpectedMinutes { get; set; }

    public EpisodeKey? Episode { get; set; }

    /// <summary>The program's title, so the log can name a recording without episode numbers.</summary>
    public string? Title { get; set; }

    public List<RecordingFragment> Fragments { get; set; } = new();

    /// <summary>When the recording was judged. Renamed files attach up to a day after this.</summary>
    public DateTime JudgedAt { get; set; }

    /// <summary>Items the tag was added to. A tag missing from one of these means the user dismissed it.</summary>
    public List<Guid> TaggedItemIds { get; set; } = new();

    public string? RequestId { get; set; }

    public bool Dismissed { get; set; }

    public DateTime? DismissedAt { get; set; }

    [JsonIgnore]
    public IEnumerable<Guid> ItemIds => Fragments.SelectMany(f => f.ItemIds);
}

public static class CompleteShowingState
{
    public const string Waiting = "waiting";
    public const string Scheduled = "scheduled";
    public const string Done = "done";
    public const string GaveUp = "gaveUp";
    public const string NotFound = "notFound";
    public const string CancelledByUser = "cancelledByUser";

    /// <summary>The scheduled showing recorded, but too little of it was observed to tell.</summary>
    public const string Unverified = "unverified";

    /// <summary>Keep as Complete closed this automatic request.</summary>
    public const string Dismissed = "dismissed";

    public static bool IsOpen(string state) => state is Waiting or Scheduled;
}

/// <summary>A request to record a complete showing of one episode. One open request per episode.</summary>
public sealed class CompleteShowingRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public EpisodeKey Episode { get; set; } = new();

    public string State { get; set; } = CompleteShowingState.Waiting;

    /// <summary>Opened by a user from the Record prompt, so it survives the automatic setting being turned off.</summary>
    public bool Explicit { get; set; }

    public Guid? RequestedByUserId { get; set; }

    internal CompleteShowingRequest Copy() => (CompleteShowingRequest)MemberwiseClone();

    public DateTime OpenedAt { get; set; }

    /// <summary>
    /// Scheduled showings, created or adopted, that recorded incomplete or never recorded. The
    /// second one ends the request.
    /// </summary>
    public int FailedAttempts { get; set; }

    /// <summary>
    /// A showing the plugin decided to schedule, saved before the server is asked, so a crash
    /// between the two is reconciled against the server's timers instead of scheduling again.
    /// </summary>
    public string? PendingProgramId { get; set; }

    public string? ScheduledProgramId { get; set; }

    public DateTime? ScheduledStart { get; set; }

    public DateTime? ScheduledEnd { get; set; }

    public string? ScheduledChannelName { get; set; }

    public DateTime? RecordedAt { get; set; }

    public DateTime? ClosedAt { get; set; }
}

/// <summary>Everything the feature persists, kept in one file.</summary>
public sealed class IncompleteRecordingStoreData
{
    public List<RecordingSnapshot> Snapshots { get; set; } = new();

    public List<IncompleteRecord> Records { get; set; } = new();

    public List<CompleteShowingRequest> Requests { get; set; } = new();

    public DateTime? LastRequestSearchAt { get; set; }

    /// <summary>Complete copies whose incomplete tag couldn't be removed yet. Each run tries again.</summary>
    public List<Guid> PendingUntags { get; set; } = new();
}

/// <summary>A timer as the schedule cache holds it.</summary>
public sealed class ScheduledTimer
{
    /// <summary>Whether a user made or cancelled the timer by hand; null when the server doesn't say.</summary>
    public bool? IsManual { get; set; }

    /// <summary>The API id, which some cancellation events use instead of the service's timer id.</summary>
    public string? ApiId { get; set; }

    /// <summary>The service's own timer id.</summary>
    public string TimerId { get; set; } = string.Empty;

    public string? ProgramId { get; set; }

    public string? ChannelName { get; set; }

    public DateTime StartDate { get; set; }

    public DateTime EndDate { get; set; }

    public int PrePaddingSeconds { get; set; }

    public int PostPaddingSeconds { get; set; }

    /// <summary>New, InProgress, Cancelled or Completed, as the server reports it.</summary>
    public string Status { get; set; } = string.Empty;

    public DateTime WindowStart => StartDate.AddSeconds(-PrePaddingSeconds);

    public DateTime WindowEnd => EndDate.AddSeconds(PostPaddingSeconds);
}

/// <summary>The server's internal timer behind an active recording.</summary>
public sealed class ActiveRecordingTimer
{
    public int RetryCount { get; set; }

    public DateTime StartDate { get; set; }

    public DateTime EndDate { get; set; }

    public int PrePaddingSeconds { get; set; }

    public int PostPaddingSeconds { get; set; }

    public string? SeriesId { get; set; }

    public string? Name { get; set; }

    public int? SeasonNumber { get; set; }

    public int? EpisodeNumber { get; set; }

    public string? EpisodeTitle { get; set; }
}

/// <summary>One airing in the guide.</summary>
public sealed class GuideProgram
{
    /// <summary>The program's library item, which visibility is checked on.</summary>
    public Guid ItemId { get; set; }

    public bool IsMovie { get; set; }

    public string? Overview { get; set; }

    public int? ProductionYear { get; set; }

    public Dictionary<string, string> ContentIds { get; set; } = new();

    public bool HasMatchingMetadata(GuideProgram other) =>
        EpisodeKey.SameMovie(SeriesName, Overview, ProductionYear, ContentIds,
            other.SeriesName, other.Overview, other.ProductionYear, other.ContentIds);

    public string Id { get; set; } = string.Empty;

    public string? SeriesId { get; set; }

    public string? SeriesName { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }

    public string? EpisodeTitle { get; set; }

    public DateTime StartDate { get; set; }

    public DateTime EndDate { get; set; }

    public string? ChannelName { get; set; }

    public bool IsHD { get; set; }

    public string? ChannelNumber { get; set; }

    public EpisodeKey? Key => EpisodeKey.FromProgram(this);
}

/// <summary>The parts of a library item the attach step looks at.</summary>
public sealed class LibraryItem
{
    public Guid Id { get; set; }

    public string? Path { get; set; }

    public DateTime DateCreated { get; set; }

    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();
}

public sealed class CompleteShowingOptions
{
    [JsonPropertyName("canSearch")]
    public bool CanSearch { get; set; }

    [JsonPropertyName("showing")]
    public SuggestedShowing? Showing { get; set; }
}

public sealed class SuggestedShowing
{
    [JsonPropertyName("cancellationVersion")]
    public string? CancellationVersion { get; set; }

    [JsonPropertyName("programId")]
    public string ProgramId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("startDate")]
    public DateTime StartDate { get; set; }

    [JsonPropertyName("channelName")]
    public string? ChannelName { get; set; }

    [JsonPropertyName("channelNumber")]
    public string? ChannelNumber { get; set; }

    /// <summary>A timer already records this showing, whoever made it; confirming adopts it.</summary>
    [JsonPropertyName("alreadyScheduled")]
    public bool AlreadyScheduled { get; set; }

    public static SuggestedShowing FromProgram(GuideProgram program) => new()
    {
        ProgramId = program.Id,
        Name = program.SeriesName ?? string.Empty,
        Description = program.Overview ?? string.Empty,
        StartDate = program.StartDate,
        ChannelName = program.ChannelName,
        ChannelNumber = program.ChannelNumber,
    };
}

public enum ScheduleResult
{
    Created,
    AlreadyScheduled,
    Failed,
}

/// <summary>Server-wide settings for the feature.</summary>
public sealed class IncompleteRecordingSettings
{
    // True when absent, so a client that doesn't send it can't turn detection off.
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("autoRecordComplete")]
    public bool AutoRecordComplete { get; set; }

    [JsonPropertyName("thresholdPercent")]
    public int ThresholdPercent { get; set; } = IncompleteRecordingRules.DefaultThresholdPercent;

    [JsonPropertyName("deleteIncompleteCopies")]
    public bool DeleteIncompleteCopies { get; set; }
}

/// <summary>What a client shows for one incomplete item.</summary>
public sealed class IncompleteRecordingStateDto
{
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Estimated recording coverage of the best single file, in minutes.</summary>
    [JsonPropertyName("recordedMinutes")]
    public double RecordedMinutes { get; set; }

    [JsonPropertyName("expectedMinutes")]
    public double ExpectedMinutes { get; set; }

    /// <summary>scheduled, recorded, notFound, or null when there is nothing to say.</summary>
    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("scheduledStart")]
    public DateTime? ScheduledStart { get; set; }

    [JsonPropertyName("channelName")]
    public string? ChannelName { get; set; }

    [JsonPropertyName("recordedAt")]
    public DateTime? RecordedAt { get; set; }
}
