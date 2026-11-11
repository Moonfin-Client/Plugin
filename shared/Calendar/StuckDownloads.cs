using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Moonfin.LiveTv;

namespace Moonfin.Calendar;

/// <summary>What the last check knew about one download that was stuck, stalled or already notified.</summary>
public sealed class StuckDownloadRecord
{
    /// <summary>When the download was first seen import blocked or failed.</summary>
    [JsonPropertyName("stuckSince")]
    public DateTime? StuckSince { get; set; }

    /// <summary>When the download was first seen stalled without progress since.</summary>
    [JsonPropertyName("stalledSince")]
    public DateTime? StalledSince { get; set; }

    /// <summary>The bytes left at the last check, to tell a stalled download from a slow one.</summary>
    [JsonPropertyName("sizeLeft")]
    public long? SizeLeft { get; set; }

    /// <summary>When the client was first seen holding a stalled download back, so that time isn't counted.</summary>
    [JsonPropertyName("heldSince")]
    public DateTime? HeldSince { get; set; }

    [JsonPropertyName("notifiedAt")]
    public DateTime? NotifiedAt { get; set; }

    /// <summary>Who was already told while someone else couldn't be, so a later check only retries the rest.</summary>
    [JsonPropertyName("told")]
    public List<Guid>? Told { get; set; }
}

/// <summary>A download that has been stuck long enough to tell people about.</summary>
public sealed class StuckDownload
{
    public StuckDownload(string key, bool isSonarr, string title, int? tmdbId, int? tvdbId, IReadOnlyList<int> seasons, string reason, string? detail)
    {
        Key = key;
        IsSonarr = isSonarr;
        Title = title;
        TmdbId = tmdbId;
        TvdbId = tvdbId;
        Seasons = seasons;
        Reason = reason;
        Detail = detail;
    }

    public string Key { get; }

    public bool IsSonarr { get; }

    /// <summary>The movie or series name.</summary>
    public string Title { get; }

    public int? TmdbId { get; }

    public int? TvdbId { get; }

    /// <summary>The seasons the download holds, empty for a movie.</summary>
    public IReadOnlyList<int> Seasons { get; }

    /// <summary>Why it's stuck in a few words, such as "import blocked".</summary>
    public string Reason { get; }

    /// <summary>The arr's own message, which may hold server paths.</summary>
    public string? Detail { get; }
}

/// <summary>The downloads already notified and when each first got stuck, in one small file.</summary>
public sealed class StuckDownloadStore
{
    private readonly string _path;
    private readonly IStateFile _files;

    public StuckDownloadStore(string path, IStateFile files)
    {
        _path = path;
        _files = files;
    }

    public Dictionary<string, StuckDownloadRecord> Load()
    {
        try
        {
            return _files.Read(_path, text => JsonSerializer.Deserialize<Dictionary<string, StuckDownloadRecord>>(text))
                ?? new Dictionary<string, StuckDownloadRecord>();
        }
        catch (Exception)
        {
            // A lost file only means a stuck download may be told about once more.
            return new Dictionary<string, StuckDownloadRecord>();
        }
    }

    public void Save(Dictionary<string, StuckDownloadRecord> records)
    {
        var folder = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(folder)) System.IO.Directory.CreateDirectory(folder);
        _files.Write(_path, JsonSerializer.Serialize(records));
    }
}

/// <summary>What one check needs from the server it runs on.</summary>
public sealed class StuckDownloadRun
{
    /// <summary>
    /// The whole queue of the default Radarr (false) or Sonarr (true). Null when it couldn't be read.
    /// It should stop early once <see cref="Cancellation"/> is set.
    /// </summary>
    public Func<bool, Task<JsonArray?>> ReadQueue { get; set; } = _ => Task.FromResult<JsonArray?>(null);

    /// <summary>Sends a GET to the Seerr API with the admin session, such as "request?take=100".</summary>
    public Func<string, Task<ArrAnswer>> SeerrGet { get; set; } = _ => Task.FromResult(ArrAnswer.Unreachable);

    /// <summary>The server's administrators.</summary>
    public Func<IEnumerable<Guid>> Admins { get; set; } = () => Array.Empty<Guid>();

    /// <summary>Whether a requester wants updates on their requests.</summary>
    public Func<Guid, bool> RequesterWantsIt { get; set; } = _ => true;

    /// <summary>
    /// Sends a notification: user, title, the in-app body, the push body and route. The push body never
    /// holds server paths, since push goes out through a relay or Firebase.
    /// </summary>
    public Action<Guid, string, string, string, string> Deliver { get; set; } = (_, _, _, _, _) => { };

    public Action<string> Log { get; set; } = _ => { };

    public DateTime NowUtc { get; set; } = DateTime.UtcNow;

    public TimeSpan StalledThreshold { get; set; } = TimeSpan.FromHours(StuckDownloads.DefaultStalledHours);

    public CancellationToken Cancellation { get; set; }
}

/// <summary>
/// Finds Radarr and Sonarr downloads that are stuck and tells the requester and the admins, once
/// per download. Radarr and Sonarr retry an import on their own, so a download only counts once
/// it's still stuck on a later check.
/// </summary>
public static class StuckDownloads
{
    public const int DefaultStalledHours = 6;

    public const string NotificationTitle = "Download stuck";

    /// <summary>How often the check runs.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long an import blocked or failed download has to stay that way. Shorter than the interval,
    /// so the next check counts, but long enough that a check run by hand right after doesn't.
    /// </summary>
    public static readonly TimeSpan ConfirmAfter = TimeSpan.FromMinutes(10);

    private const int QueuePageSize = 1000;
    private const int MaxQueuePages = 20;
    private const int RequestPageSize = 100;
    private const int MaxRequestPages = 20;
    private const int MaxDetailLength = 160;

    // The client is holding these back on purpose or can't be reached, so they aren't stalled downloads.
    private static readonly HashSet<string> HeldStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "queued", "paused", "delay", "downloadClientUnavailable",
    };

    private static readonly HashSet<string> StuckStates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "importBlocked", "failedPending", "failed",
    };

    /// <summary>
    /// One check: read both queues, update the store, and notify what has been stuck long enough. A
    /// download is only marked notified once everyone was told, so a run that can't read Seerr's
    /// requests, finds nobody to tell, or can't reach someone, tries again on the next one. Whoever
    /// was already told isn't told twice.
    /// </summary>
    public static async Task<int> RunAsync(StuckDownloadRun run, StuckDownloadStore store)
    {
        var radarr = await run.ReadQueue(false).ConfigureAwait(false);
        if (run.Cancellation.IsCancellationRequested) return 0;
        var sonarr = await run.ReadQueue(true).ConfigureAwait(false);
        if (radarr == null && sonarr == null) return 0;

        var (next, due) = Scan(store.Load(), radarr, sonarr, run.NowUtc, run.StalledThreshold);
        var notified = 0;
        if (due.Count > 0 && !run.Cancellation.IsCancellationRequested)
        {
            var requests = await ReadProcessingRequestsAsync(run.SeerrGet).ConfigureAwait(false);
            if (requests == null)
            {
                run.Log("Seerr's requests couldn't be read in full, so " + due.Count.ToString(CultureInfo.InvariantCulture) + " stuck downloads wait for the next check");
                due.Clear();
            }

            var admins = new HashSet<Guid>(run.Admins());
            foreach (var download in due)
            {
                // Whatever was delivered before shutdown is still saved below, so it isn't sent again.
                if (run.Cancellation.IsCancellationRequested) break;
                if (Notify(run, download, requests, admins, next[download.Key])) notified++;
            }
        }

        store.Save(next);
        return notified;
    }

    // Tells everyone who hasn't been told yet. True once nobody is left.
    private static bool Notify(StuckDownloadRun run, StuckDownload download, JsonArray? requests, HashSet<Guid> admins, StuckDownloadRecord record)
    {
        var (requesters, requestTmdbId) = Requesters(requests, download);
        var route = Route(download, download.TmdbId ?? requestTmdbId);
        var redacted = Body(download, redact: true);
        var told = new HashSet<Guid>(record.Told ?? new List<Guid>());
        var recipients = admins.Select(admin => (User: admin, Body: Body(download, redact: false)))
            .Concat(requesters.Where(r => !admins.Contains(r) && run.RequesterWantsIt(r)).Select(r => (User: r, Body: redacted)))
            .Where(r => !told.Contains(r.User))
            .ToList();

        int toAdmins = 0, toRequesters = 0, failed = 0;
        foreach (var (user, body) in recipients)
        {
            if (run.Cancellation.IsCancellationRequested)
            {
                failed++;
                continue;
            }

            if (!TryDeliver(run, user, body, redacted, route))
            {
                failed++;
                continue;
            }

            told.Add(user);
            if (admins.Contains(user)) toAdmins++;
            else toRequesters++;
        }

        if (told.Count == 0)
        {
            run.Log($"{download.Key} is stuck ({download.Reason}), but there was nobody to tell");
            return false;
        }

        run.Log($"{download.Key} is stuck ({download.Reason}), told {toAdmins} admins and {toRequesters} requesters");
        if (failed > 0)
        {
            record.Told = told.ToList();
            run.Log($"{download.Key}: {failed.ToString(CultureInfo.InvariantCulture)} more to tell on the next check");
            return false;
        }

        record.NotifiedAt = run.NowUtc;
        record.Told = null;
        return true;
    }

    // One recipient that can't be reached doesn't stop the others.
    private static bool TryDeliver(StuckDownloadRun run, Guid user, string body, string pushBody, string route)
    {
        try
        {
            run.Deliver(user, NotificationTitle, body, pushBody, route);
            return true;
        }
        catch (Exception ex)
        {
            run.Log("Couldn't notify " + user.ToString("N") + ": " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// The whole queue, page by page, with the movie or series on each entry. Null when any page
    /// couldn't be read, so a download missing from a partial read isn't taken for one that left.
    /// </summary>
    public static async Task<JsonArray?> ReadQueueAsync(ArrSend send, bool isSonarr, CancellationToken cancellationToken = default)
    {
        var all = new JsonArray();
        for (var page = 1; page <= MaxQueuePages; page++)
        {
            if (cancellationToken.IsCancellationRequested) return null;

            var path = "api/v3/queue?page=" + page.ToString(CultureInfo.InvariantCulture)
                + "&pageSize=" + QueuePageSize.ToString(CultureInfo.InvariantCulture)
                + (isSonarr ? "&includeSeries=true&includeEpisode=true" : "&includeMovie=true");
            var answer = await send(HttpMethod.Get, path, null).ConfigureAwait(false);
            if (!answer.Ok || answer.Body == null) return null;

            JsonObject? body;
            try
            {
                body = JsonNode.Parse(answer.Body) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }

            if (!(body?["records"] is JsonArray records)) return null;
            var onPage = records.Count;
            foreach (var record in records.ToList())
            {
                records.Remove(record);
                all.Add(record);
            }

            var total = Int(body["totalRecords"]);
            if (total == null || onPage == 0 || all.Count >= total) return all;
        }

        return null;
    }

    /// <summary>
    /// The store after this check, and the downloads to notify now. A null queue couldn't be read,
    /// so what was known about it is kept as it was. Everything else that left the queue is dropped.
    /// </summary>
    internal static (Dictionary<string, StuckDownloadRecord> Next, List<StuckDownload> Due) Scan(
        IReadOnlyDictionary<string, StuckDownloadRecord> previous,
        JsonArray? radarr,
        JsonArray? sonarr,
        DateTime now,
        TimeSpan stalledThreshold)
    {
        var next = new Dictionary<string, StuckDownloadRecord>();
        var due = new List<StuckDownload>();

        foreach (var (records, isSonarr) in new[] { (radarr, false), (sonarr, true) })
        {
            if (records == null)
            {
                foreach (var pair in previous.Where(p => p.Key.StartsWith(Service(isSonarr) + ":", StringComparison.Ordinal)))
                {
                    next[pair.Key] = pair.Value;
                }

                continue;
            }

            foreach (var group in records.OfType<JsonObject>().GroupBy(r => Key(r, isSonarr)))
            {
                previous.TryGetValue(group.Key, out var before);
                var (record, download) = Check(group.Key, group.ToList(), isSonarr, before, now, stalledThreshold);
                if (record != null) next[group.Key] = record;
                if (download != null) due.Add(download);
            }
        }

        return (next, due);
    }

    private static (StuckDownloadRecord? Record, StuckDownload? Due) Check(
        string key,
        List<JsonObject> entries,
        bool isSonarr,
        StuckDownloadRecord? before,
        DateTime now,
        TimeSpan stalledThreshold)
    {
        var record = new StuckDownloadRecord { NotifiedAt = before?.NotifiedAt, Told = before?.Told };
        string? reason = null;
        string? detail = null;
        var sizeLeft = entries.Select(e => Long(e["sizeleft"])).Max();
        var progressed = sizeLeft != null && before?.SizeLeft != null && sizeLeft < before.SizeLeft;

        var blocked = entries.FirstOrDefault(IsBlocked);
        if (blocked != null)
        {
            record.StuckSince = before?.StuckSince ?? now;
            if (before?.StuckSince != null && now - record.StuckSince.Value >= ConfirmAfter)
            {
                var state = Text(blocked["trackedDownloadState"]);
                reason = string.Equals(state, "failedPending", StringComparison.OrdinalIgnoreCase) || string.Equals(state, "failed", StringComparison.OrdinalIgnoreCase)
                    ? "download failed"
                    : "import blocked";
                detail = FirstMessage(blocked) ?? Text(blocked["errorMessage"]);
            }
        }
        else if (entries.FirstOrDefault(IsStalled) is JsonObject stalled)
        {
            record.SizeLeft = sizeLeft;
            record.StalledSince = !progressed && before?.StalledSince != null ? Resumed(before, now) : now;
            if (before?.StalledSince != null && now - record.StalledSince.Value >= stalledThreshold)
            {
                reason = "no progress for " + Hours(now - record.StalledSince.Value);
                detail = Text(stalled["errorMessage"]) ?? FirstMessage(stalled);
            }
        }
        else if (before?.StalledSince != null && !progressed && sizeLeft != null && before.SizeLeft != null && entries.Any(IsDownloading))
        {
            // qBittorrent flips a stalled torrent to downloading whenever a peer connects without
            // sending anything, so the clock only starts over once bytes actually arrive.
            record.SizeLeft = sizeLeft;
            record.StalledSince = Resumed(before, now);
            if (now - record.StalledSince.Value >= stalledThreshold)
            {
                reason = "no progress for " + Hours(now - record.StalledSince.Value);
            }
        }
        else if (before?.StalledSince != null && entries.Any(IsHeld))
        {
            // The client is holding a stalled download back, so its clock waits until it resumes.
            record.SizeLeft = before.SizeLeft;
            record.StalledSince = before.StalledSince;
            record.HeldSince = before.HeldSince ?? now;
        }

        if (record.StuckSince == null && record.StalledSince == null && record.NotifiedAt == null && record.Told == null) return (null, null);
        if (reason == null || record.NotifiedAt != null) return (record, null);

        return (record, Describe(key, entries, isSonarr, reason, detail));
    }

    // Radarr and Sonarr leave an import that found no eligible file, or whose file was rejected, at
    // importPending with a warning and retry it forever, so that counts as blocked as well.
    private static bool IsBlocked(JsonObject entry)
    {
        var state = Text(entry["trackedDownloadState"]);
        if (state == null) return false;
        if (StuckStates.Contains(state)) return true;

        return string.Equals(state, "importPending", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Text(entry["trackedDownloadStatus"]), "warning", StringComparison.OrdinalIgnoreCase);
    }

    // Stalled means the download client says so, or a warning on a download that isn't moving. The
    // client's warning is in status (qBittorrent's stalled or metadata-less torrents), the arr's own in
    // trackedDownloadStatus. An import waiting on something isn't a stalled download, so only
    // downloads still downloading count, and not ones the client is holding back.
    private static bool IsStalled(JsonObject entry)
    {
        if (!IsDownloading(entry)) return false;

        return (Text(entry["errorMessage"])?.IndexOf("stalled", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
            || string.Equals(Text(entry["status"]), "warning", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Text(entry["trackedDownloadStatus"]), "warning", StringComparison.OrdinalIgnoreCase);
    }

    // Still downloading, and not held back by the client.
    private static bool IsDownloading(JsonObject entry) => InDownloadState(entry) && !HasHeldStatus(entry);

    // Still downloading as far as the arr knows, but the client is holding it back.
    private static bool IsHeld(JsonObject entry) => InDownloadState(entry) && HasHeldStatus(entry);

    private static bool InDownloadState(JsonObject entry)
    {
        var state = Text(entry["trackedDownloadState"]);
        return state == null || string.Equals(state, "downloading", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasHeldStatus(JsonObject entry) => Text(entry["status"]) is string status && HeldStatuses.Contains(status);

    // The stalled clock, moved on past any time the client spent holding the download back.
    private static DateTime Resumed(StuckDownloadRecord before, DateTime now) =>
        before.HeldSince is DateTime held ? before.StalledSince!.Value + (now - held) : before.StalledSince!.Value;

    private static StuckDownload Describe(string key, List<JsonObject> entries, bool isSonarr, string reason, string? detail)
    {
        var first = entries[0];
        var title = first[isSonarr ? "series" : "movie"] as JsonObject;
        var seasons = isSonarr
            ? entries.Select(e => Int(e["seasonNumber"]) ?? Int((e["episode"] as JsonObject)?["seasonNumber"]))
                .Where(s => s != null).Select(s => s!.Value).Distinct().OrderBy(s => s).ToList()
            : new List<int>();

        return new StuckDownload(
            key,
            isSonarr,
            Text(title?["title"]) ?? Text(first["title"]) ?? "A download",
            Int(title?["tmdbId"]),
            isSonarr ? Int(title?["tvdbId"]) : null,
            seasons,
            reason,
            detail);
    }

    /// <summary>
    /// "Title is stuck: why, message". For non-admins and push the paths are cut down and the
    /// message shortened. Admins see it whole in the app, since the path is what they need.
    /// </summary>
    internal static string Body(StuckDownload download, bool redact)
    {
        var name = download.Seasons.Count == 1
            ? download.Title + " season " + download.Seasons[0].ToString(CultureInfo.InvariantCulture)
            : download.Title;
        var detail = download.Detail?.Trim();
        if (!string.IsNullOrEmpty(detail) && redact) detail = ArrQueue.RedactPaths(detail!);
        if (redact && detail != null && detail.Length > MaxDetailLength) detail = detail.Substring(0, MaxDetailLength).TrimEnd() + "...";

        return string.IsNullOrEmpty(detail)
            ? name + " is stuck: " + download.Reason
            : name + " is stuck: " + download.Reason + ", " + detail;
    }

    /// <summary>The title's Seerr page, or the requests list when its TMDB id isn't known.</summary>
    internal static string Route(StuckDownload download, int? tmdbId) =>
        tmdbId is int id
            ? "/seerr/media/" + id.ToString(CultureInfo.InvariantCulture) + "?mediaType=" + (download.IsSonarr ? "tv" : "movie")
            : "/seerr/requests";

    /// <summary>
    /// Every request Seerr is still working on. Null when any page couldn't be read, or there were
    /// more than it reads, so a requester on a missing page isn't left out of a notification.
    /// </summary>
    internal static async Task<JsonArray?> ReadProcessingRequestsAsync(Func<string, Task<ArrAnswer>> seerrGet)
    {
        var all = new JsonArray();
        for (var page = 0; page < MaxRequestPages; page++)
        {
            var answer = await seerrGet(
                "request?take=" + RequestPageSize.ToString(CultureInfo.InvariantCulture)
                + "&skip=" + (page * RequestPageSize).ToString(CultureInfo.InvariantCulture)
                + "&filter=processing&sort=added").ConfigureAwait(false);
            if (!answer.Ok || answer.Body == null) return null;

            JsonArray? results;
            try
            {
                results = (JsonNode.Parse(answer.Body) as JsonObject)?["results"] as JsonArray;
            }
            catch (JsonException)
            {
                return null;
            }

            if (results == null) return null;
            foreach (var result in results.ToList())
            {
                results.Remove(result);
                all.Add(result);
            }

            if (all.Count < (page + 1) * RequestPageSize) return all;
        }

        return null;
    }

    /// <summary>
    /// The server users who asked for this title, and the title's TMDB id from their request. A 4K
    /// request goes to the 4K arr, which isn't read, and a request for other seasons isn't this download.
    /// </summary>
    internal static (List<Guid> Users, int? TmdbId) Requesters(JsonArray? requests, StuckDownload download)
    {
        var users = new List<Guid>();
        int? tmdbId = null;
        if (requests == null) return (users, null);

        foreach (var request in requests.OfType<JsonObject>())
        {
            if (Bool(request["is4k"]) == true) continue;

            var media = request["media"] as JsonObject;
            if (!string.Equals(Text(media?["mediaType"]), download.IsSonarr ? "tv" : "movie", StringComparison.OrdinalIgnoreCase)) continue;

            var matches = download.IsSonarr
                ? (download.TvdbId != null && Int(media?["tvdbId"]) == download.TvdbId) || (download.TmdbId != null && Int(media?["tmdbId"]) == download.TmdbId)
                : download.TmdbId != null && Int(media?["tmdbId"]) == download.TmdbId;
            if (!matches) continue;

            if (download.Seasons.Count > 0 && request["seasons"] is JsonArray seasons && seasons.Count > 0
                && !seasons.OfType<JsonObject>().Any(s => Int(s["seasonNumber"]) is int n && download.Seasons.Contains(n)))
            {
                continue;
            }

            tmdbId ??= Int(media?["tmdbId"]);
            if (Guid.TryParse(Text((request["requestedBy"] as JsonObject)?["jellyfinUserId"]), out var user) && !users.Contains(user))
            {
                users.Add(user);
            }
        }

        return (users, tmdbId);
    }

    // Episodes of a season pack share one download id, so they make one download.
    private static string Key(JsonObject entry, bool isSonarr)
    {
        var downloadId = Text(entry["downloadId"]);
        return Service(isSonarr) + ":" + (string.IsNullOrEmpty(downloadId)
            ? "queue-" + (Int(entry["id"]) ?? 0).ToString(CultureInfo.InvariantCulture)
            : downloadId!.ToLowerInvariant());
    }

    private static string? FirstMessage(JsonObject entry) =>
        (entry["statusMessages"] as JsonArray ?? new JsonArray())
            .OfType<JsonObject>()
            .SelectMany(m => (m["messages"] as JsonArray ?? new JsonArray()).Select(Text))
            .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));

    private static string Hours(TimeSpan span)
    {
        var hours = (int)Math.Floor(span.TotalHours);
        return hours == 1 ? "1 hour" : hours.ToString(CultureInfo.InvariantCulture) + " hours";
    }

    private static string Service(bool isSonarr) => isSonarr ? "sonarr" : "radarr";

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? Int(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : (int?)null;

    private static long? Long(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<long>(out var whole)) return whole;
        return value.TryGetValue<double>(out var number) ? (long)number : (long?)null;
    }

    private static bool? Bool(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : (bool?)null;
}
