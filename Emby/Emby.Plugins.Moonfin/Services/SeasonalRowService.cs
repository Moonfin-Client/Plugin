using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.Moonfin.Api;
using Emby.Plugins.Moonfin.Models;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using Moonfin.Seasonal;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Builds the seasonal Home row: the holiday active for the viewer's country, the movies they
    /// own for it in a daily order, and popular TMDB titles they could request through Seerr.
    /// </summary>
    public class SeasonalRowService
    {
        /// <summary>A row with fewer titles than this is skipped for the next active holiday.</summary>
        private const int MinItems = 6;

        private const int MaxOwned = 40;
        private const int MaxSuggestions = 40;
        private const int DiscoverPages = 5;
        private const int ProviderIdChunk = 100;
        private static readonly TimeSpan SuggestionsTtl = TimeSpan.FromDays(30);

        private readonly IReadOnlyList<SeasonalHoliday> _holidays;
        private readonly IServerConfigurationManager _configManager;
        private readonly MoonfinSettingsService _settings;
        private readonly ServerRatingLimitService _ratingLimits;
        private readonly TmdbRatingService _ratings;
        private readonly SeasonalKeywordStoreService _keywords;
        private readonly SeasonalSuggestionsCacheService _suggestions;
        private readonly SeerrAvailabilityService _seerr;
        private readonly ILogger _logger;
        private readonly HttpClient _client = MoonfinHttp.CreateClient(TimeSpan.FromSeconds(15), "Moonfin/1.0");

        public SeasonalRowService(
            IServerConfigurationManager configManager,
            MoonfinSettingsService settings,
            ServerRatingLimitService ratingLimits,
            TmdbRatingService ratings,
            SeasonalKeywordStoreService keywords,
            SeasonalSuggestionsCacheService suggestions,
            SeerrAvailabilityService seerr,
            ILogger logger)
        {
            _holidays = SeasonalRules.LoadEmbedded(typeof(SeasonalRowService).Assembly);
            _configManager = configManager;
            _settings = settings;
            _ratingLimits = ratingLimits;
            _ratings = ratings;
            _keywords = keywords;
            _suggestions = suggestions;
            _seerr = seerr;
            _logger = logger;
        }

        public async Task<SeasonalRowResponse> BuildAsync(User user, string? requestedCountry, DateTime now, CancellationToken cancellationToken)
        {
            var country = SeasonalCountry.Resolve(requestedCountry, _configManager.Configuration.MetadataCountryCode);
            var response = new SeasonalRowResponse { Country = country };

            var profile = await _settings.GetResolvedProfileAsync(user.Id, "global").ConfigureAwait(false);
            var active = SeasonalSelector.Active(_holidays, now, country, profile?.SeasonalRowHiddenHolidays);
            if (active.Count == 0) return response;

            var libraryManager = PluginServices.LibraryManager;
            if (libraryManager == null) return response;

            var apiKey = string.IsNullOrWhiteSpace(profile?.TmdbApiKey) ? Plugin.Instance?.Configuration?.TmdbApiKey : profile!.TmdbApiKey;
            var limit = _ratingLimits.ForUser(user);
            var seerrReady = !string.IsNullOrWhiteSpace(apiKey) && await _seerr.IsReadyForUserAsync(user.Id).ConfigureAwait(false);

            foreach (var candidate in active)
            {
                var holiday = candidate.Holiday;
                // The query already applied the server's limits. This also drops unrated titles,
                // which the server lets through unless the user blocks them as a class.
                var owned = OwnedMovies(libraryManager, holiday, user).Where(item => limit.IsAllowed(item.OfficialRating)).ToList();
                var suggestions = seerrReady
                    ? await SuggestionsAsync(holiday, apiKey!, OwnedTmdbIds(owned), limit, now, cancellationToken).ConfigureAwait(false)
                    : new List<CustomRowItem>();

                if (owned.Count + suggestions.Count < MinItems) continue;

                var shuffled = DailyShuffle.Shuffle(owned, DailyShuffle.Seed(user.Id.ToString("N"), now)).Take(MaxOwned);
                response.Holiday = holiday.Id;
                response.Items = shuffled.Select(item => SettingsService.MapItemToDto(item, user)).ToList();
                response.Suggestions = suggestions;
                return response;
            }

            return response;
        }

        /// <summary>
        /// The user's movies for the holiday, found through the keyword store and queried as the
        /// user so the server applies their limits. Emby items are addressed by provider id here
        /// because the store keeps TMDB ids while Emby's own item ids are internal numbers.
        /// </summary>
        private List<BaseItem> OwnedMovies(ILibraryManager libraryManager, SeasonalHoliday holiday, User user)
        {
            var tmdbIds = _keywords.TmdbIdsWithAnyKeyword(SeasonalKeywordMatcher.KeywordIds(holiday));
            var found = new Dictionary<Guid, BaseItem>();
            for (var i = 0; i < tmdbIds.Count; i += ProviderIdChunk)
            {
                var query = new InternalItemsQuery(user)
                {
                    IncludeItemTypes = new[] { "Movie" },
                    IsVirtualItem = false,
                    Recursive = true,
                    AnyProviderIdEquals = tmdbIds.Skip(i).Take(ProviderIdChunk)
                        .Select(id => new KeyValuePair<string, string>("Tmdb", id.ToString()))
                        .ToList()
                };

                foreach (var item in libraryManager.GetItemsResult(query).Items)
                {
                    if (!found.ContainsKey(item.Id) && item.IsVisible(user)) found[item.Id] = item;
                }
            }

            return found.Values.OrderBy(i => i.Id).ToList();
        }

        private HashSet<long> OwnedTmdbIds(IEnumerable<BaseItem> owned)
        {
            var ids = _keywords.AllTmdbIds();
            foreach (var item in owned)
            {
                string? tmdbId = null;
                item.ProviderIds?.TryGetValue("Tmdb", out tmdbId);
                if (long.TryParse(tmdbId, out var id)) ids.Add(id);
            }
            return ids;
        }

        private async Task<List<CustomRowItem>> SuggestionsAsync(
            SeasonalHoliday holiday,
            string apiKey,
            HashSet<long> ownedTmdbIds,
            RatingLimit limit,
            DateTime now,
            CancellationToken cancellationToken)
        {
            var key = holiday.Id + ":" + now.Year;
            var all = _suggestions.TryGet(key, SuggestionsTtl, DiscoverPages);
            if (all == null)
            {
                all = await DiscoverAsync(holiday, apiKey, cancellationToken).ConfigureAwait(false);
                await _ratings.FillRatingsAsync(all, apiKey, cancellationToken).ConfigureAwait(false);
                if (all.Count > 0)
                {
                    _suggestions.Set(key, all, DiscoverPages);
                    await _suggestions.FlushAsync().ConfigureAwait(false);
                }
            }

            return all
                .Where(item => !(item.Id.HasValue && ownedTmdbIds.Contains(item.Id.Value)))
                .Where(item => limit.IsAllowed(item.OfficialRating))
                .Take(MaxSuggestions)
                .ToList();
        }

        private async Task<List<CustomRowItem>> DiscoverAsync(SeasonalHoliday holiday, string apiKey, CancellationToken cancellationToken)
        {
            var keywords = string.Join("|", holiday.Keywords.Select(k => k.Id));
            var items = new List<CustomRowItem>();
            var seen = new HashSet<long>();
            for (var page = 1; page <= DiscoverPages; page++)
            {
                var url = "https://api.themoviedb.org/3/discover/movie?with_keywords=" + keywords + "&sort_by=popularity.desc&vote_count.gte=" + holiday.MinVotes + "&include_adult=false&page=" + page;
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                MoonfinHttp.ApplyTmdbAuth(request, apiKey);
                using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.Warn("TMDB discover answered " + (int)response.StatusCode + " for " + holiday.Id);
                    break;
                }

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) break;

                var added = 0;
                foreach (var result in results.EnumerateArray())
                {
                    if (!result.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.Number) continue;

                    var id = idProp.GetInt64();
                    if (!seen.Add(id)) continue;

                    var releaseDate = result.TryGetProperty("release_date", out var rd) ? rd.GetString() : null;
                    items.Add(new CustomRowItem
                    {
                        Id = id,
                        Name = result.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
                        Type = "Movie",
                        ProductionYear = releaseDate != null && releaseDate.Length >= 4 && int.TryParse(releaseDate.Substring(0, 4), out var year) ? year : (int?)null,
                        Rank = items.Count + 1,
                        ProviderIds = new CustomRowItemProviderIds { Tmdb = id.ToString() },
                        PosterUrl = result.TryGetProperty("poster_path", out var poster) ? poster.GetString() : null,
                        BackdropUrl = result.TryGetProperty("backdrop_path", out var backdrop) ? backdrop.GetString() : null
                    });
                    added++;
                }

                if (added == 0) break;
            }

            await FillDetailsAsync(items, apiKey, cancellationToken).ConfigureAwait(false);
            return items;
        }

        // Discover leaves out what the card under a focused suggestion shows, so each title is
        // looked up once per cache period. The wide image prefers a backdrop in the server's
        // metadata language, since those carry the title the way a library thumb does.
        private async Task FillDetailsAsync(List<CustomRowItem> items, string apiKey, CancellationToken cancellationToken)
        {
            var language = MetadataLanguage(_configManager.Configuration.PreferredMetadataLanguage);
            foreach (var item in items)
            {
                var url = "https://api.themoviedb.org/3/movie/" + item.Id + "?append_to_response=images&include_image_language=" + language + ",null";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                MoonfinHttp.ApplyTmdbAuth(request, apiKey);
                using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                ApplyDetails(item, doc.RootElement, language);
            }
        }

        internal static string MetadataLanguage(string? configured)
        {
            var code = (configured ?? string.Empty).Split('-')[0].Trim().ToLowerInvariant();
            return code.Length == 2 ? code : "en";
        }

        internal static void ApplyDetails(CustomRowItem item, JsonElement details, string language)
        {
            if (details.TryGetProperty("overview", out var overview) && overview.ValueKind == JsonValueKind.String)
                item.Overview = overview.GetString();

            if (details.TryGetProperty("vote_average", out var vote) && vote.ValueKind == JsonValueKind.Number && vote.GetDouble() > 0)
                item.Rating = Math.Round(vote.GetDouble(), 1);

            if (details.TryGetProperty("runtime", out var runtime) && runtime.ValueKind == JsonValueKind.Number && runtime.GetInt32() > 0)
                item.RunTimeTicks = runtime.GetInt32() * TimeSpan.TicksPerMinute;

            if (details.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
            {
                var names = genres.EnumerateArray()
                    .Select(g => g.TryGetProperty("name", out var name) ? name.GetString() : null)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .ToList();
                if (names.Count > 0) item.Genres = names;
            }

            if (details.TryGetProperty("images", out var images) && images.TryGetProperty("backdrops", out var backdrops) && backdrops.ValueKind == JsonValueKind.Array)
            {
                var titled = backdrops.EnumerateArray().FirstOrDefault(b =>
                    b.TryGetProperty("iso_639_1", out var lang) && string.Equals(lang.GetString(), language, StringComparison.OrdinalIgnoreCase));
                if (titled.ValueKind == JsonValueKind.Object && titled.TryGetProperty("file_path", out var path) && path.ValueKind == JsonValueKind.String)
                    item.BackdropUrl = path.GetString();
            }
        }
    }
}
