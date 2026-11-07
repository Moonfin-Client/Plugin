using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Moonfin.SmartSearch;

/// <summary>
/// The TMDB titles smart search can offer through Seerr: the most voted movies and shows, plus the
/// most popular from the past year and the year ahead, which haven't had time to collect votes. Each
/// has its keywords and top cast. Details are cached by id, so a refresh only asks TMDB about titles
/// it hasn't seen, and about recent ones, whose dates, cast and keywords still change.
/// </summary>
public static class TmdbCatalog
{
    private const int MoviePages = 500;
    private const int TvPages = 250;
    private const int RecentMoviePages = 20;
    private const int RecentTvPages = 10;
    private const int Parallelism = 6;

    private static readonly Dictionary<int, string> Genres = new Dictionary<int, string>
    {
        [28] = "Action", [12] = "Adventure", [16] = "Animation", [35] = "Comedy", [80] = "Crime", [99] = "Documentary",
        [18] = "Drama", [10751] = "Family", [14] = "Fantasy", [36] = "History", [27] = "Horror", [10402] = "Music",
        [9648] = "Mystery", [10749] = "Romance", [878] = "Science Fiction", [10770] = "TV Movie", [53] = "Thriller",
        [10752] = "War", [37] = "Western", [10759] = "Action & Adventure", [10762] = "Kids", [10763] = "News",
        [10764] = "Reality", [10765] = "Sci-Fi & Fantasy", [10766] = "Soap", [10767] = "Talk", [10768] = "War & Politics",
    };

    /// <summary>Catalog entries without vectors, paired with the text to embed.</summary>
    public static async Task<List<(SmartEntry Entry, string Text)>> FetchAsync(
        HttpClient http,
        string apiKey,
        string detailsCachePath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var titles = new List<JsonObject>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var recent = new HashSet<string>(StringComparer.Ordinal);
        var from = DateTime.Today.AddYears(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var to = DateTime.Today.AddYears(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var pages = Enumerable.Range(1, MoviePages).Select(p => (Type: "movie", Filter: $"sort_by=vote_count.desc&page={p}", Recent: false))
            .Concat(Enumerable.Range(1, TvPages).Select(p => (Type: "tv", Filter: $"sort_by=vote_count.desc&page={p}", Recent: false)))
            // Theatrical and digital releases only, so a festival premiere doesn't count as the release.
            .Concat(Enumerable.Range(1, RecentMoviePages).Select(p => (Type: "movie", Filter: $"sort_by=popularity.desc&with_release_type=2%7C3%7C4&release_date.gte={from}&release_date.lte={to}&page={p}", Recent: true)))
            // By premiere, since talk shows and soaps air every day but aren't new.
            .Concat(Enumerable.Range(1, RecentTvPages).Select(p => (Type: "tv", Filter: $"sort_by=popularity.desc&first_air_date.gte={from}&first_air_date.lte={to}&page={p}", Recent: true)))
            .ToList();
        var done = 0;
        foreach (var batch in pages.Select((page, i) => (page, i)).GroupBy(x => x.i / Parallelism))
        {
            var results = await Task.WhenAll(batch.Select(async x => (x.page.Recent, List: await DiscoverAsync(http, apiKey, x.page.Type, x.page.Filter, cancellationToken).ConfigureAwait(false)))).ConfigureAwait(false);
            foreach (var (isRecent, list) in results)
            {
                foreach (var title in list)
                {
                    var key = Key(title);
                    if (seen.Add(key))
                    {
                        titles.Add(title);
                    }

                    if (isRecent)
                    {
                        recent.Add(key);
                    }
                }
            }

            done += batch.Count();
            progress?.Report(0.3 * done / pages.Count);
        }

        var cache = LoadDetails(detailsCachePath);
        var toFetch = titles.Where(t => recent.Contains(Key(t)) || !cache.ContainsKey(Key(t))).ToList();
        var fetched = 0;
        using (var gate = new SemaphoreSlim(Parallelism))
        {
            await Task.WhenAll(toFetch.Select(async title =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var details = await DetailsAsync(http, apiKey, title["mediaType"]!.ToString(), (long)title["id"]!, cancellationToken).ConfigureAwait(false);
                    if (details != null)
                    {
                        cache[Key(title)] = details;
                    }
                }
                finally
                {
                    gate.Release();
                    var n = Interlocked.Increment(ref fetched);
                    if (n % 200 == 0)
                    {
                        progress?.Report(0.3 + 0.2 * n / Math.Max(1, toFetch.Count));
                    }
                }
            })).ConfigureAwait(false);
        }

        SaveDetails(detailsCachePath, cache.Where(p => seen.Contains(p.Key)));

        var entries = new List<(SmartEntry, string)>(titles.Count);
        foreach (var title in titles)
        {
            cache.TryGetValue(Key(title), out var details);
            var isMovie = title["mediaType"]!.ToString() == "movie";
            var name = (isMovie ? title["title"] : title["name"])?.ToString() ?? string.Empty;
            var date = (isMovie ? title["releaseDate"] : title["firstAirDate"])?.ToString() ?? string.Empty;
            var year = date.Length >= 4 && int.TryParse(date.Substring(0, 4), out var y) ? y : 0;
            var genres = (title["genreIds"] as JsonArray ?? new JsonArray()).Select(g => Genres.TryGetValue((int)g!, out var n) ? n : string.Empty);
            var cast = details?.Cast ?? Array.Empty<string>();
            var entry = new SmartEntry
            {
                Key = Key(title),
                Kind = isMovie ? SmartQuery.Movie : SmartQuery.Series,
                Title = name,
                YearMin = year,
                YearMax = year,
                Released = DateNumber(isMovie && !string.IsNullOrEmpty(details?.Released) ? details!.Released : date),
                Votes = (int?)title["voteCount"] ?? 0,
                TmdbId = title["id"]!.ToString(),
                People = cast.Select(SmartQuery.NormalizeName).ToArray(),
                Payload = title.ToJsonString(),
            };
            var text = SmartIndex.TitleText(name, year, isMovie ? "Movie" : "TV series", genres, details?.Keywords ?? Array.Empty<string>(), cast, title["overview"]?.ToString());
            entries.Add((entry, text));
        }

        return entries;
    }

    private static string Key(JsonObject title) => title["mediaType"] + ":" + title["id"];

    private static int DateNumber(string? date) =>
        date != null && date.Length >= 10 && DateTime.TryParseExact(date.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? SmartQuery.DateNumber(d)
            : 0;

    private static async Task<List<JsonObject>> DiscoverAsync(HttpClient http, string apiKey, string type, string filter, CancellationToken cancellationToken)
    {
        var url = $"https://api.themoviedb.org/3/discover/{type}?{filter}&include_adult=false&language=en-US";
        var body = await GetAsync(http, apiKey, url, cancellationToken).ConfigureAwait(false);
        var list = new List<JsonObject>();
        if (body?["results"] is not JsonArray results)
        {
            return list;
        }

        foreach (var r in results.OfType<JsonObject>())
        {
            // Seerr's own search results use these camelCase names, so clients can reuse their parsing.
            var title = new JsonObject
            {
                ["id"] = (long?)r["id"],
                ["mediaType"] = type,
                ["overview"] = r["overview"]?.ToString(),
                ["posterPath"] = r["poster_path"]?.ToString(),
                ["backdropPath"] = r["backdrop_path"]?.ToString(),
                ["voteAverage"] = (double?)r["vote_average"],
                ["voteCount"] = (int?)r["vote_count"],
                ["originalLanguage"] = r["original_language"]?.ToString(),
                ["genreIds"] = r["genre_ids"]?.DeepClone(),
            };
            if (type == "movie")
            {
                title["title"] = r["title"]?.ToString();
                title["releaseDate"] = r["release_date"]?.ToString();
            }
            else
            {
                title["name"] = r["name"]?.ToString();
                title["firstAirDate"] = r["first_air_date"]?.ToString();
            }

            list.Add(title);
        }

        return list;
    }

    private static async Task<TitleDetails?> DetailsAsync(HttpClient http, string apiKey, string type, long id, CancellationToken cancellationToken)
    {
        var movie = type == "movie";
        var body = await GetAsync(http, apiKey, $"https://api.themoviedb.org/3/{type}/{id}?append_to_response=keywords,credits{(movie ? ",release_dates" : string.Empty)}&language=en-US", cancellationToken).ConfigureAwait(false);
        if (body == null)
        {
            return null;
        }

        // Movies list keywords under "keywords", shows under "results".
        var keywords = (body["keywords"]?["keywords"] ?? body["keywords"]?["results"]) as JsonArray;
        var cast = body["credits"]?["cast"] as JsonArray;
        return new TitleDetails
        {
            Keywords = keywords?.Select(k => k?["name"]?.ToString() ?? string.Empty).Where(k => k.Length > 0).Take(12).ToArray() ?? Array.Empty<string>(),
            Cast = cast?.Select(c => c?["name"]?.ToString() ?? string.Empty).Where(c => c.Length > 0).Take(5).ToArray() ?? Array.Empty<string>(),
            Released = movie
                ? ((body["release_dates"]?["results"] as JsonArray) ?? new JsonArray())
                    .SelectMany(country => (country?["release_dates"] as JsonArray) ?? new JsonArray())
                    .Where(r => (int?)r?["type"] is 2 or 3 or 4)
                    .Select(r => r?["release_date"]?.ToString() ?? string.Empty)
                    .Where(d => d.Length >= 10)
                    .OrderBy(d => d, StringComparer.Ordinal)
                    .FirstOrDefault() ?? string.Empty
                : string.Empty,
        };
    }

    private static async Task<JsonNode?> GetAsync(HttpClient http, string apiKey, string url, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (apiKey.StartsWith("eyJ", StringComparison.Ordinal))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }
            else
            {
                request.RequestUri = new Uri(url + "&api_key=" + Uri.EscapeDataString(apiKey));
            }

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == (HttpStatusCode)429)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return JsonNode.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        }

        return null;
    }

    // Read and written as JSON nodes rather than serialized types, so the trimmed helper the Go
    // hosts run can't lose the properties.
    private static ConcurrentDictionary<string, TitleDetails> LoadDetails(string path)
    {
        var cache = new ConcurrentDictionary<string, TitleDetails>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject saved)
            {
                foreach (var pair in saved)
                {
                    cache[pair.Key] = new TitleDetails { Keywords = Strings(pair.Value?["Keywords"]), Cast = Strings(pair.Value?["Cast"]), Released = pair.Value?["Released"]?.ToString() ?? string.Empty };
                }
            }
        }
        catch (JsonException)
        {
        }

        return cache;
    }

    /// <summary>Only keeps titles still in the catalog, so the file doesn't grow forever.</summary>
    private static void SaveDetails(string path, IEnumerable<KeyValuePair<string, TitleDetails>> details)
    {
        var saved = new JsonObject();
        foreach (var pair in details)
        {
            saved[pair.Key] = new JsonObject { ["Keywords"] = JsonList(pair.Value.Keywords), ["Cast"] = JsonList(pair.Value.Cast), ["Released"] = pair.Value.Released };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, saved.ToJsonString());
    }

    private static string[] Strings(JsonNode? node) =>
        (node as JsonArray)?.Select(v => (string?)v).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).ToArray() ?? Array.Empty<string>();

    private static JsonArray JsonList(string[] values) => new JsonArray(values.Select(v => (JsonNode?)v).ToArray());

    private sealed class TitleDetails
    {
        public string[] Keywords { get; set; } = Array.Empty<string>();

        public string[] Cast { get; set; } = Array.Empty<string>();

        public string Released { get; set; } = string.Empty;
    }
}
