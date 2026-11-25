using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Moonfin.Calendar;

/// <summary>One Sonarr or Radarr server as Seerr lists it in its arr settings.</summary>
public sealed class ArrServerInfo
{
    public ArrServerInfo(string id, string name, bool is4k, bool isDefault, JsonElement element)
    {
        Id = id;
        Name = name;
        Is4k = is4k;
        IsDefault = isDefault;
        Element = element;
    }

    /// <summary>Seerr's id for the server, or its place in the list when Seerr sent none.</summary>
    public string Id { get; }

    public string Name { get; }

    public bool Is4k { get; }

    public bool IsDefault { get; }

    /// <summary>The server's settings as Seerr sent them, hostname, port and API key included.</summary>
    public JsonElement Element { get; }
}

/// <summary>
/// Which of the Sonarr or Radarr servers set up in Seerr are read, and in what order. The first
/// one is the primary: the server a request without a server id goes to, as before several
/// servers were read.
/// </summary>
public static class ArrServers
{
    /// <summary>The property naming the server an element came from, on calendar elements and queue records.</summary>
    public const string ServerProperty = "moonfinArrServerId";

    /// <summary>The property set to true on queue records from a 4K server, for the stuck download check.</summary>
    public const string Is4kProperty = "moonfinArrIs4k";

    /// <summary>
    /// The regular default first, then the other regular servers in Seerr's order, then the 4K
    /// servers in Seerr's order. 4K servers are left out when include4k is false, unless they're
    /// all there is.
    /// </summary>
    public static List<ArrServerInfo> Order(JsonElement servers, bool include4k)
    {
        var all = new List<ArrServerInfo>();
        if (servers.ValueKind != JsonValueKind.Array) return all;

        var index = 0;
        foreach (var server in servers.EnumerateArray())
        {
            if (server.ValueKind == JsonValueKind.Object) all.Add(Info(server.Clone(), index));
            index++;
        }

        // OrderBy is stable, so servers that tie keep Seerr's order.
        var regular = all.Where(s => !s.Is4k).OrderBy(s => s.IsDefault ? 0 : 1).ToList();
        var fourK = all.Where(s => s.Is4k).ToList();
        if (regular.Count == 0) return fourK;
        return include4k ? regular.Concat(fourK).ToList() : regular;
    }

    /// <summary>The server with this id, or the primary when id is empty. Null for an id that isn't listed.</summary>
    public static T? Pick<T>(IReadOnlyList<T> servers, Func<T, string> idOf, string? id)
        where T : class
    {
        if (servers.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(id)) return servers[0];
        return servers.FirstOrDefault(s => string.Equals(idOf(s), id!.Trim(), StringComparison.Ordinal));
    }

    private static ArrServerInfo Info(JsonElement server, int index)
    {
        var id = server.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.Number && idValue.TryGetInt64(out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : "idx" + index.ToString(CultureInfo.InvariantCulture);
        var name = server.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
            ? nameValue.GetString() ?? string.Empty
            : string.Empty;
        return new ArrServerInfo(id, name, IsTrue(server, "is4k"), IsTrue(server, "isDefault"), server);
    }

    private static bool IsTrue(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
}

/// <summary>One server's answer to the calendar request.</summary>
public sealed class ArrCalendarPart
{
    public ArrCalendarPart(string serverId, int statusCode, byte[]? body)
    {
        ServerId = serverId;
        StatusCode = statusCode;
        Body = body;
    }

    public string ServerId { get; }

    public int StatusCode { get; }

    public byte[]? Body { get; }
}

/// <summary>The calendars of every server read, as one answer.</summary>
public sealed class ArrCalendarCombined
{
    internal ArrCalendarCombined(int? passThrough, byte[]? body, IReadOnlyList<string> failed)
    {
        PassThrough = passThrough;
        Body = body;
        Failed = failed;
    }

    /// <summary>
    /// The part to hand back as it is: the only server's answer, or the primary's when no server
    /// answered. Null when Body holds the merged calendar.
    /// </summary>
    public int? PassThrough { get; }

    /// <summary>The merged calendar, set when PassThrough is null.</summary>
    public byte[]? Body { get; }

    /// <summary>The servers that couldn't be read. A merged calendar with any is partial.</summary>
    public IReadOnlyList<string> Failed { get; }

    public bool Partial => PassThrough == null && Failed.Count > 0;
}

/// <summary>
/// Joins the calendars of several Sonarr or Radarr servers, and folds the copies of one episode
/// or movie that more than one of them holds, such as a regular and a 4K Radarr.
/// </summary>
public static class ArrCalendarMerge
{
    /// <summary>
    /// The servers' calendars as one array, each element marked with its server. A single server's
    /// answer is handed back untouched. When no server answered, the primary's error is.
    /// </summary>
    public static ArrCalendarCombined Combine(IReadOnlyList<ArrCalendarPart> parts)
    {
        if (parts.Count == 0) throw new ArgumentException("At least one server is needed", nameof(parts));
        if (parts.Count == 1) return new ArrCalendarCombined(0, null, Array.Empty<string>());

        var merged = new JsonArray();
        var failed = new List<string>();
        foreach (var part in parts)
        {
            var array = part.StatusCode == 200 ? ParseArray(part.Body) : null;
            if (array == null)
            {
                failed.Add(part.ServerId);
                continue;
            }

            var elements = array.ToList();
            array.Clear();
            foreach (var element in elements)
            {
                if (element is JsonObject obj) obj[ArrServers.ServerProperty] = part.ServerId;
                merged.Add(element);
            }
        }

        return failed.Count == parts.Count
            ? new ArrCalendarCombined(0, null, failed)
            : new ArrCalendarCombined(null, Encoding.UTF8.GetBytes(merged.ToJsonString()), failed);
    }

    /// <summary>
    /// Keeps one element per episode (Sonarr: TVDB id, season and episode) or movie (Radarr: TMDB
    /// id, then IMDb id). The first one wins, so the primary server's copy is kept. It counts as
    /// downloaded when any copy is, and gets the library ids of every copy. Runs after the
    /// annotation, so copies the user can't see are already gone. A calendar from a single server
    /// is handed back untouched.
    /// </summary>
    public static byte[] Dedupe(byte[] body, bool isSonarr)
    {
        var array = ParseArray(body);
        if (array == null || array.Count < 2) return body;
        if (!array.OfType<JsonObject>().Any(e => e.ContainsKey(ArrServers.ServerProperty))) return body;

        var elements = array.ToList();
        array.Clear();
        var kept = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var node in elements)
        {
            var key = node is JsonObject element ? KeyOf(element, isSonarr) : null;
            if (key == null)
            {
                array.Add(node);
                continue;
            }

            var current = (JsonObject)node!;
            if (kept.TryGetValue(key, out var first))
            {
                Fold(first, current);
                continue;
            }

            kept[key] = current;
            array.Add(current);
        }

        return Encoding.UTF8.GetBytes(array.ToJsonString());
    }

    private static void Fold(JsonObject into, JsonObject copy)
    {
        if (IsTrue(copy["hasFile"])) into["hasFile"] = true;

        foreach (var name in new[] { "moonfinItemId", "moonfinEpisodeId" })
        {
            if (into[name] is null && copy[name] is JsonNode value) into[name] = value.DeepClone();
        }

        if (copy["moonfinLibraryIds"] is JsonArray more)
        {
            var libraries = into["moonfinLibraryIds"] as JsonArray;
            if (libraries == null)
            {
                libraries = new JsonArray();
                into["moonfinLibraryIds"] = libraries;
            }

            var have = new HashSet<string>(libraries.Select(Text).OfType<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var id in more.Select(Text).OfType<string>())
            {
                if (have.Add(id)) libraries.Add(JsonValue.Create(id));
            }
        }
    }

    private static string? KeyOf(JsonObject element, bool isSonarr)
    {
        if (!isSonarr)
        {
            var tmdb = Id(element["tmdbId"]);
            if (tmdb != null) return "tmdb:" + tmdb;
            var imdb = Id(element["imdbId"]);
            return imdb == null ? null : "imdb:" + imdb.ToLowerInvariant();
        }

        var tvdb = Id((element["series"] as JsonObject)?["tvdbId"]);
        var season = Text(element["seasonNumber"]);
        var episode = Text(element["episodeNumber"]);
        return tvdb == null || season == null || episode == null ? null : "tvdb:" + tvdb + "|" + season + "|" + episode;
    }

    // Sonarr and Radarr send 0 for an id they don't have.
    private static string? Id(JsonNode? node)
    {
        var text = Text(node)?.Trim();
        return string.IsNullOrEmpty(text) || text == "0" ? null : text;
    }

    internal static string? Text(JsonNode? node)
    {
        if (!(node is JsonValue value)) return null;
        if (value.TryGetValue<string>(out var text)) return text;
        return value.TryGetValue<long>(out var number) ? number.ToString(CultureInfo.InvariantCulture) : null;
    }

    private static bool IsTrue(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    internal static JsonArray? ParseArray(byte[]? body)
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
}

/// <summary>
/// Joins one title's queue as several Sonarr or Radarr servers report it. Queue ids are only
/// unique within one server, so each item names its server. A removal that doesn't name one is
/// sent to the server that holds the id (see ArrQueue.LocateAsync).
/// </summary>
public static class ArrQueueMerge
{
    /// <summary>
    /// answers[i] is servers[i]'s answer; servers[0] is the primary. A single server's answer is
    /// handed back untouched.
    /// </summary>
    public static ArrAnswer Combine(IReadOnlyList<ArrServerInfo> servers, IReadOnlyList<ArrAnswer> answers, bool isAdmin)
    {
        if (servers.Count != answers.Count || answers.Count == 0) throw new ArgumentException("One answer per server is needed", nameof(answers));
        if (answers.Count == 1) return answers[0];

        var read = new List<(ArrServerInfo Server, JsonObject Body)>();
        ArrAnswer? firstFailure = null;
        for (var i = 0; i < answers.Count; i++)
        {
            var body = answers[i].StatusCode == 200 ? ParseObject(answers[i].Body) : null;
            if (body == null)
            {
                firstFailure ??= answers[i];
                continue;
            }

            read.Add((servers[i], body));
        }

        if (read.Count == 0) return firstFailure ?? answers[0];

        var holding = read.Where(r => r.Body["inArr"] is JsonValue v && v.TryGetValue<bool>(out var inArr) && inArr).ToList();

        // An empty queue reads as nothing downloading, so it's only said when every server was read.
        if (holding.Count == 0 && firstFailure != null) return firstFailure;

        var result = (JsonObject)(holding.Count > 0 ? holding[0].Body : read[0].Body).DeepClone();
        foreach (var flag in new[] { "hasFile", "searching" })
        {
            var values = holding.Select(h => h.Body[flag]).OfType<JsonValue>().ToList();
            if (values.Count > 0) result[flag] = values.Any(v => v.TryGetValue<bool>(out var b) && b);
        }

        var items = new JsonArray();
        foreach (var (server, body) in holding)
        {
            foreach (var item in (body["items"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var copy = (JsonObject)item.DeepClone();
                copy["server"] = server.Id;
                copy["serverName"] = server.Name;
                copy["canRemove"] = isAdmin;
                items.Add(copy);
            }
        }

        result["items"] = items;
        result["canRemove"] = isAdmin;
        if (firstFailure != null) result["partial"] = true;
        return new ArrAnswer(200, Encoding.UTF8.GetBytes(result.ToJsonString()));
    }

    private static JsonObject? ParseObject(byte[]? body)
    {
        if (body == null || body.Length == 0) return null;
        try
        {
            return JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
