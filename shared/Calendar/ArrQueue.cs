using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Moonfin.Calendar;

/// <summary>What a Sonarr or Radarr answered: its status code and body. A null body means it sent none.</summary>
public sealed class ArrAnswer
{
    public ArrAnswer(int statusCode, byte[]? body)
    {
        StatusCode = statusCode;
        Body = body;
    }

    public int StatusCode { get; }

    public byte[]? Body { get; }

    /// <summary>No address of the arr could be reached, so it answered nothing.</summary>
    public static ArrAnswer Unreachable { get; } = new ArrAnswer(0, null);

    public bool Ok => StatusCode >= 200 && StatusCode < 300;
}

/// <summary>
/// Sends one request to the arr: the method, the path after the base address (such as
/// "api/v3/queue/details?movieId=1") and an optional JSON body.
/// </summary>
public delegate Task<ArrAnswer> ArrSend(HttpMethod method, string pathAndQuery, byte[]? jsonBody);

/// <summary>
/// The Sonarr and Radarr download queue for one title, and the admin action that drops a stuck
/// download, blocklists its release and lets the arr grab another. Seerr's own download status
/// leaves out why a download is stuck, so this reads the arr directly.
/// </summary>
public static class ArrQueue
{
    public const string MissingIdError = "A positive tmdbId (Radarr) or tvdbId (Sonarr) is required";

    // Radarr and Sonarr queues are small, so one page holds a whole queue.
    private const int QueuePageSize = 1000;

    // An absolute path inside a message: /downloads/x, C:\downloads\x or \\nas\share\x. Folder names
    // often hold spaces, so a path runs to the next quote or the end of the message, and the
    // words after it are cut with it rather than any of the path being left.
    private static readonly Regex AbsolutePath = new Regex(
        @"(?<=^|[\s'""(\[])(?:/[^\s'""/]|[A-Za-z]:\\|\\\\[^\s'""\\])[^'""]*",
        RegexOptions.CultureInvariant);

    /// <summary>The query name the arr looks a title up by.</summary>
    public static string IdName(bool isSonarr) => isSonarr ? "tvdbId" : "tmdbId";

    /// <summary>
    /// The title's queue entries, and whether the arr has the title at all. A 200 always means the
    /// arr was read, so an empty list is an empty queue and not a server that couldn't be reached.
    /// </summary>
    public static async Task<ArrAnswer> ReadAsync(ArrSend send, bool isSonarr, string? titleId, bool isAdmin)
    {
        var id = PositiveId(titleId);
        if (id == null) return Error(400, MissingIdError);

        var lookup = await send(HttpMethod.Get, (isSonarr ? "api/v3/series?tvdbId=" : "api/v3/movie?tmdbId=") + id, null).ConfigureAwait(false);
        if (!lookup.Ok) return Failed(lookup, isSonarr);

        if (!TryReadTitle(lookup.Body, isSonarr, out var title)) return Unreadable(isSonarr);

        var service = Service(isSonarr);
        var result = new JsonObject
        {
            ["service"] = service,
            ["inArr"] = title != null,
            ["canRemove"] = isAdmin,
        };

        var items = new JsonArray();
        if (title != null)
        {
            result["monitored"] = title.Monitored;
            if (!isSonarr)
            {
                result["hasFile"] = title.HasFile;
                result["isAvailable"] = title.IsAvailable;
            }

            var details = await send(HttpMethod.Get, DetailsPath(isSonarr, title.ArrId), null).ConfigureAwait(false);
            if (!details.Ok) return Failed(details, isSonarr);

            var records = ParseArray(details.Body);
            if (records == null) return Unreadable(isSonarr);

            foreach (var record in records.OfType<JsonObject>())
            {
                items.Add(Shape(record, isSonarr, isAdmin));
            }
        }

        result["items"] = items;
        return new ArrAnswer(200, Encoding.UTF8.GetBytes(result.ToJsonString()));
    }

    /// <summary>
    /// Removes the download from the download client, blocklists the release and lets the arr search
    /// for another. Every queue entry that shares the download, such as the episodes of a season
    /// pack, goes with it.
    /// </summary>
    public static async Task<ArrAnswer> RemoveAsync(ArrSend send, bool isSonarr, int queueId)
    {
        if (queueId <= 0) return Error(400, "A positive queue id is required");

        var queue = await send(HttpMethod.Get, "api/v3/queue?page=1&pageSize=" + QueuePageSize.ToString(CultureInfo.InvariantCulture), null).ConfigureAwait(false);
        if (!queue.Ok) return Failed(queue, isSonarr);

        var ids = RemovalIds(queue.Body, queueId);
        if (ids == null) return Unreadable(isSonarr);
        if (ids.Count == 0) return Error(404, "That download is no longer in the " + Service(isSonarr) + " queue");

        const string Flags = "removeFromClient=true&blocklist=true&skipRedownload=false";
        var removed = ids.Count == 1
            ? await send(HttpMethod.Delete, "api/v3/queue/" + ids[0].ToString(CultureInfo.InvariantCulture) + "?" + Flags, null).ConfigureAwait(false)
            : await send(HttpMethod.Delete, "api/v3/queue/bulk?" + Flags, JsonSerializer.SerializeToUtf8Bytes(new { ids })).ConfigureAwait(false);
        // A 404 means the arr no longer has the entry, which is what was asked for. It also covers a
        // removal that landed but answered too slowly and was sent again.
        if (!removed.Ok && removed.StatusCode != 404) return Failed(removed, isSonarr);

        var body = new JsonObject { ["removed"] = new JsonArray(ids.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray()) };
        return new ArrAnswer(200, Encoding.UTF8.GetBytes(body.ToJsonString()));
    }

    /// <summary>
    /// The queue ids to remove with this one: every entry with its download id, or just this one
    /// when it has none. Empty when the id isn't in the queue, null when the queue page can't be read.
    /// </summary>
    internal static List<int>? RemovalIds(byte[]? queueBody, int queueId)
    {
        JsonArray? records;
        try
        {
            var root = queueBody == null ? null : JsonNode.Parse(queueBody);
            records = (root as JsonObject)?["records"] as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }

        if (records == null) return null;

        var entries = records.OfType<JsonObject>().Select(r => (Id: Int(r["id"]), DownloadId: Text(r["downloadId"]))).ToList();
        var target = entries.FirstOrDefault(e => e.Id == queueId);
        if (target.Id == null) return new List<int>();
        if (string.IsNullOrEmpty(target.DownloadId)) return new List<int> { queueId };

        return entries
            .Where(e => e.Id != null && string.Equals(e.DownloadId, target.DownloadId, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Id!.Value)
            .Distinct()
            .OrderBy(i => i)
            .ToList();
    }

    /// <summary>
    /// The fields a client needs from a queue entry, under the arr's own names. The download folder,
    /// and any server path inside a message, is kept to admins.
    /// </summary>
    internal static JsonObject Shape(JsonObject record, bool isSonarr, bool isAdmin)
    {
        var item = new JsonObject();
        foreach (var name in new[]
        {
            "id", "title", "status", "trackedDownloadState", "trackedDownloadStatus", "size", "sizeleft",
            "timeleft", "estimatedCompletionTime", "added", "downloadId", "protocol", "downloadClient", "indexer",
        })
        {
            if (record[name] is JsonNode value) item[name] = value.DeepClone();
        }

        var quality = Text((record["quality"] as JsonObject)?["quality"]?["name"]);
        if (quality != null) item["quality"] = quality;

        var error = Text(record["errorMessage"]);
        if (!string.IsNullOrEmpty(error)) item["errorMessage"] = isAdmin ? error : RedactPaths(error!);

        var messages = new JsonArray();
        foreach (var message in (record["statusMessages"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var lines = new JsonArray();
            foreach (var line in (message["messages"] as JsonArray ?? new JsonArray()).Select(Text).Where(l => !string.IsNullOrEmpty(l)))
            {
                lines.Add(JsonValue.Create(isAdmin ? line : RedactPaths(line!)));
            }

            var title = Text(message["title"]) ?? string.Empty;
            messages.Add(new JsonObject { ["title"] = isAdmin ? title : RedactPaths(title), ["messages"] = lines });
        }

        item["statusMessages"] = messages;

        if (isSonarr)
        {
            var episode = record["episode"] as JsonObject;
            if (Int(record["episodeId"]) is int episodeId) item["episodeId"] = episodeId;
            if ((Int(record["seasonNumber"]) ?? Int(episode?["seasonNumber"])) is int season) item["seasonNumber"] = season;
            if (Int(episode?["episodeNumber"]) is int number) item["episodeNumber"] = number;
        }

        if (isAdmin && Text(record["outputPath"]) is string outputPath) item["outputPath"] = outputPath;
        return item;
    }

    /// <summary>Each absolute path in a message cut down to its last folder or file name.</summary>
    internal static string RedactPaths(string text) => AbsolutePath.Replace(text, match =>
    {
        var trimmed = match.Value.TrimEnd('/', '\\', '.', ',', ';', ':', ')', ']', ' ');
        var cut = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        return (cut >= 0 ? trimmed.Substring(cut + 1) : trimmed) + match.Value.Substring(trimmed.Length);
    });

    /// <summary>
    /// The first title in a lookup answer, or null in title when the arr doesn't have it. False when
    /// the answer isn't a list.
    /// </summary>
    internal static bool TryReadTitle(byte[]? body, bool isSonarr, out ArrQueueTitle? title)
    {
        title = null;
        var titles = ParseArray(body);
        if (titles == null) return false;

        var first = titles.OfType<JsonObject>().FirstOrDefault(t => Int(t["id"]) > 0);
        if (first == null) return true;

        title = new ArrQueueTitle(
            Int(first["id"])!.Value,
            Bool(first["monitored"]),
            isSonarr ? null : Bool(first["hasFile"]),
            isSonarr ? null : Bool(first["isAvailable"]));
        return true;
    }

    internal static string DetailsPath(bool isSonarr, int arrId) =>
        isSonarr
            ? "api/v3/queue/details?seriesId=" + arrId.ToString(CultureInfo.InvariantCulture) + "&includeEpisode=true"
            : "api/v3/queue/details?movieId=" + arrId.ToString(CultureInfo.InvariantCulture);

    private static string Service(bool isSonarr) => isSonarr ? "sonarr" : "radarr";

    private static string? PositiveId(string? value) =>
        long.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id.ToString(CultureInfo.InvariantCulture)
            : null;

    // The arr's own status would read as this server's, so a 401 from the arr isn't taken for the
    // caller's sign-in failing. Every arr error is a 502 that names the arr's status.
    private static ArrAnswer Failed(ArrAnswer answer, bool isSonarr) =>
        answer.StatusCode == 0
            ? Error(502, "Could not reach the " + Service(isSonarr) + " server")
            : new ArrAnswer(502, JsonSerializer.SerializeToUtf8Bytes(new
        {
            error = "The " + Service(isSonarr) + " server answered " + answer.StatusCode.ToString(CultureInfo.InvariantCulture),
            arrStatus = answer.StatusCode,
        }));

    private static ArrAnswer Unreadable(bool isSonarr) => Error(502, "The " + Service(isSonarr) + " server sent an answer that couldn't be read");

    private static ArrAnswer Error(int statusCode, string message) =>
        new ArrAnswer(statusCode, JsonSerializer.SerializeToUtf8Bytes(new { error = message }));

    private static JsonArray? ParseArray(byte[]? body)
    {
        if (body == null || body.Length == 0) return null;
        try
        {
            return JsonNode.Parse(body) as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? Int(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : (int?)null;

    private static bool? Bool(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : (bool?)null;
}

/// <summary>A movie or series as the arr has it.</summary>
public sealed class ArrQueueTitle
{
    public ArrQueueTitle(int arrId, bool? monitored, bool? hasFile, bool? isAvailable)
    {
        ArrId = arrId;
        Monitored = monitored;
        HasFile = hasFile;
        IsAvailable = isAvailable;
    }

    public int ArrId { get; }

    public bool? Monitored { get; }

    public bool? HasFile { get; }

    public bool? IsAvailable { get; }
}
