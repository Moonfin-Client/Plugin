using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
/// The TMDB titles smart search can offer through Seerr: the most voted movies and shows, each
/// with its keywords and top cast. Details are cached by id, so a refresh only asks TMDB about
/// titles it hasn't seen.
/// </summary>
public static class TmdbCatalog
{
    private const int MoviePages = 500;
    private const int TvPages = 250;
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
        var pages = Enumerable.Range(1, MoviePages).Select(p => ("movie", p)).Concat(Enumerable.Range(1, TvPages).Select(p => ("tv", p))).ToList();
        var done = 0;
        foreach (var batch in pages.Select((page, i) => (page, i)).GroupBy(x => x.i / Parallelism))
        {
            var results = await Task.WhenAll(batch.Select(x => DiscoverAsync(http, apiKey, x.page.Item1, x.page.Item2, cancellationToken))).ConfigureAwait(false);
            foreach (var list in results)
            {
                foreach (var title in list)
                {
                    if (seen.Add(Key(title)))
                    {
                        titles.Add(title);
                    }
                }
            }

            done += batch.Count();
            progress?.Report(0.3 * done / pages.Count);
        }

        var cache = LoadDetails(detailsCachePath);
        var missing = titles.Where(t => !cache.ContainsKey(Key(t))).ToList();
        var fetched = 0;
        using (var gate = new SemaphoreSlim(Parallelism))
        {
            await Task.WhenAll(missing.Select(async title =>
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
                        progress?.Report(0.3 + 0.2 * n / Math.Max(1, missing.Count));
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

    private static async Task<List<JsonObject>> DiscoverAsync(HttpClient http, string apiKey, string type, int page, CancellationToken cancellationToken)
    {
        var url = $"https://api.themoviedb.org/3/discover/{type}?sort_by=vote_count.desc&include_adult=false&language=en-US&page={page}";
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
        var body = await GetAsync(http, apiKey, $"https://api.themoviedb.org/3/{type}/{id}?append_to_response=keywords,credits&language=en-US", cancellationToken).ConfigureAwait(false);
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

    private static ConcurrentDictionary<string, TitleDetails> LoadDetails(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, TitleDetails>>(File.ReadAllText(path));
                if (loaded != null)
                {
                    return new ConcurrentDictionary<string, TitleDetails>(loaded, StringComparer.Ordinal);
                }
            }
        }
        catch (JsonException)
        {
        }

        return new ConcurrentDictionary<string, TitleDetails>(StringComparer.Ordinal);
    }

    /// <summary>Only keeps titles still in the catalog, so the file doesn't grow forever.</summary>
    private static void SaveDetails(string path, IEnumerable<KeyValuePair<string, TitleDetails>> details)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(details.ToDictionary(p => p.Key, p => p.Value)));
    }

    public sealed class TitleDetails
    {
        public string[] Keywords { get; set; } = Array.Empty<string>();

        public string[] Cast { get; set; } = Array.Empty<string>();
    }
}
