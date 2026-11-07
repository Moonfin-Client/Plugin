using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moonfin.Seasonal;
using Moonfin.Server.Helpers;
using Moonfin.Server.Models;

namespace Moonfin.Server.Services;

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
    private static readonly TimeSpan SuggestionsTtl = TimeSpan.FromDays(30);

    private readonly IReadOnlyList<SeasonalHoliday> _holidays;
    private readonly ILibraryManager _libraryManager;
    private readonly IServiceProvider _serviceProvider;
    private readonly IServerConfigurationManager _configManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MoonfinSettingsService _settings;
    private readonly ServerRatingLimitService _ratingLimits;
    private readonly TmdbRatingService _ratings;
    private readonly SeasonalKeywordStoreService _keywords;
    private readonly SeasonalSuggestionsCacheService _suggestions;
    private readonly SeerrAvailabilityService _seerr;
    private readonly ItemCardDtoMapper _mapper;
    private readonly ILogger<SeasonalRowService> _logger;

    public SeasonalRowService(
        ILibraryManager libraryManager,
        IServiceProvider serviceProvider,
        IServerConfigurationManager configManager,
        IHttpClientFactory httpClientFactory,
        MoonfinSettingsService settings,
        ServerRatingLimitService ratingLimits,
        TmdbRatingService ratings,
        SeasonalKeywordStoreService keywords,
        SeasonalSuggestionsCacheService suggestions,
        SeerrAvailabilityService seerr,
        ItemCardDtoMapper mapper,
        ILogger<SeasonalRowService> logger)
    {
        _holidays = SeasonalRules.LoadEmbedded(typeof(SeasonalRowService).Assembly);
        _libraryManager = libraryManager;
        _serviceProvider = serviceProvider;
        _configManager = configManager;
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _ratingLimits = ratingLimits;
        _ratings = ratings;
        _keywords = keywords;
        _suggestions = suggestions;
        _seerr = seerr;
        _mapper = mapper;
        _logger = logger;
    }

    public async Task<SeasonalRowResponse> BuildAsync(Guid userId, string? requestedCountry, bool allYear, DateTime now, CancellationToken cancellationToken)
    {
        var country = SeasonalCountry.Resolve(requestedCountry, _configManager.Configuration.MetadataCountryCode);
        var response = new SeasonalRowResponse { Country = country };

        var profile = await _settings.GetResolvedProfileAsync(userId, "global").ConfigureAwait(false);
        var active = SeasonalSelector.Active(_holidays, now, country, profile?.SeasonalRowHiddenHolidays, allYear);
        if (active.Count == 0)
        {
            return response;
        }

        var user = UserReflection.ResolveUser(_serviceProvider, userId);
        if (user == null)
        {
            return response;
        }

        var apiKey = string.IsNullOrWhiteSpace(profile?.TmdbApiKey) ? MoonfinPlugin.Instance?.Configuration?.TmdbApiKey : profile.TmdbApiKey;
        var limit = _ratingLimits.ForUser(userId);
        var seerrReady = !string.IsNullOrWhiteSpace(apiKey) && await _seerr.IsReadyForUserAsync(userId).ConfigureAwait(false);

        foreach (var candidate in active)
        {
            var holiday = candidate.Holiday;
            // The query already applied the server's limits. This also drops unrated titles,
            // which the server lets through unless the user blocks them as a class.
            var owned = DistinctOwned(OwnedMovies(holiday, user).Where(item => limit.IsAllowed(item.OfficialRating)));
            var suggestions = seerrReady
                ? await SuggestionsAsync(holiday, apiKey!, OwnedKeys(owned, _keywords.AllTmdbIds()), limit, now, cancellationToken).ConfigureAwait(false)
                : new List<CustomRowItem>();

            if (owned.Count + suggestions.Count < MinItems)
            {
                continue;
            }

            var shuffled = DailyShuffle.Shuffle(owned, DailyShuffle.Seed(userId.ToString("N"), now)).Take(MaxOwned);
            response.Holiday = holiday.Id;
            response.Items = shuffled.Select(item => _mapper.Map(item, user)).ToList();
            response.Suggestions = suggestions;
            return response;
        }

        return response;
    }

    /// <summary>
    /// The user's movies for the holiday, by the keyword tags Jellyfin stores and by the plugin's
    /// own keyword store, each queried as the user so the server applies their limits.
    /// </summary>
    private List<BaseItem> OwnedMovies(SeasonalHoliday holiday, object user)
    {
        var found = new Dictionary<Guid, BaseItem>();

        var byTags = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            IsVirtualItem = false,
            Recursive = true,
            Tags = SeasonalKeywordMatcher.KeywordNames(holiday),
            EnableTotalRecordCount = false
        };
        Collect(found, byTags, user);

        var storeIds = _keywords.ItemIdsWithAnyKeyword(SeasonalKeywordMatcher.KeywordIds(holiday))
            .Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty)
            .Where(guid => guid != Guid.Empty && !found.ContainsKey(guid))
            .ToArray();
        if (storeIds.Length > 0)
        {
            var byStore = new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Movie],
                IsVirtualItem = false,
                Recursive = true,
                ItemIds = storeIds,
                EnableTotalRecordCount = false
            };
            Collect(found, byStore, user);
        }

        return found.Values.OrderBy(i => i.Id).ToList();
    }

    private void Collect(Dictionary<Guid, BaseItem> found, InternalItemsQuery query, object user)
    {
        if (!UserReflection.ApplyUser(query, user))
        {
            return;
        }

        foreach (var item in _libraryManager.GetItemsResult(query).Items)
        {
            if (!found.ContainsKey(item.Id) && UserReflection.IsVisibleTo(item, user))
            {
                found[item.Id] = item;
            }
        }
    }

    /// <summary>
    /// The owned titles once each. Two library copies of one movie, like a 4K and a 1080p file in
    /// different libraries, are separate items, so they're matched by their provider ids too.
    /// </summary>
    internal static List<BaseItem> DistinctOwned(IEnumerable<BaseItem> owned)
    {
        return SeasonalTitles.Distinct(owned, IdKeys);
    }

    /// <summary>
    /// What a suggestion the user owns would match: every TMDB id in the library, the ids of the
    /// owned titles, and the name and year of an owned title that has no TMDB id.
    /// </summary>
    internal static HashSet<string> OwnedKeys(IEnumerable<BaseItem> owned, IEnumerable<long> libraryTmdbIds)
    {
        var keys = new HashSet<string>(libraryTmdbIds.Select(id => SeasonalTitles.TmdbKey(id.ToString())!), StringComparer.Ordinal);
        foreach (var item in owned)
        {
            keys.UnionWith(IdKeys(item));
            if (string.IsNullOrWhiteSpace(item.GetProviderId(MetadataProvider.Tmdb)) && SeasonalTitles.NameKey(item.Name, item.ProductionYear) is { } name)
            {
                keys.Add(name);
            }
        }

        return keys;
    }

    private static List<string> IdKeys(BaseItem item)
    {
        return SeasonalTitles.IdKeys(item.GetProviderId(MetadataProvider.Tmdb), item.GetProviderId(MetadataProvider.Imdb));
    }

    /// <summary>
    /// The suggestions this user can see, each title once and none they own. A title they own
    /// matches by TMDB or IMDb id, or by name and year when the owned copy has no TMDB id.
    /// </summary>
    internal static List<CustomRowItem> PickSuggestions(IEnumerable<CustomRowItem> all, ISet<string> ownedKeys, RatingLimit limit, int max)
    {
        var visible = all.Where(item => !IsOwned(item, ownedKeys) && limit.IsAllowed(item.OfficialRating));
        return SeasonalTitles.Distinct(visible, SuggestionIdKeys).Take(max).ToList();
    }

    private static bool IsOwned(CustomRowItem item, ISet<string> ownedKeys)
    {
        var name = SeasonalTitles.NameKey(item.Name, item.ProductionYear);
        return SuggestionIdKeys(item).Any(ownedKeys.Contains) || (name != null && ownedKeys.Contains(name));
    }

    private static List<string> SuggestionIdKeys(CustomRowItem item)
    {
        return SeasonalTitles.IdKeys(item.Id?.ToString() ?? item.ProviderIds?.Tmdb, item.ProviderIds?.Imdb);
    }

    private async Task<List<CustomRowItem>> SuggestionsAsync(
        SeasonalHoliday holiday,
        string apiKey,
        ISet<string> ownedKeys,
        RatingLimit limit,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var key = $"{holiday.Id}:{now.Year}";
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

        return PickSuggestions(all, ownedKeys, limit, MaxSuggestions);
    }

    private async Task<List<CustomRowItem>> DiscoverAsync(SeasonalHoliday holiday, string apiKey, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("MoonfinHttpClient");
        var keywords = string.Join("|", holiday.Keywords.Select(k => k.Id));
        var items = new List<CustomRowItem>();
        var seen = new HashSet<long>();
        for (var page = 1; page <= DiscoverPages; page++)
        {
            var url = $"https://api.themoviedb.org/3/discover/movie?with_keywords={keywords}&sort_by=popularity.desc&vote_count.gte={holiday.MinVotes}&include_adult=false&page={page}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            TmdbRequestHelper.ApplyAuth(request, apiKey);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TMDB discover answered {Status} for {Holiday}", (int)response.StatusCode, holiday.Id);
                break;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var added = 0;
            foreach (var result in results.EnumerateArray())
            {
                if (!result.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                var id = idProp.GetInt64();
                if (!seen.Add(id))
                {
                    continue;
                }

                var releaseDate = result.TryGetProperty("release_date", out var rd) ? rd.GetString() : null;
                items.Add(new CustomRowItem
                {
                    Id = id,
                    Name = result.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
                    Type = "Movie",
                    ProductionYear = releaseDate?.Length >= 4 && int.TryParse(releaseDate[..4], out var year) ? year : null,
                    Rank = items.Count + 1,
                    ProviderIds = new CustomRowItemProviderIds { Tmdb = id.ToString() },
                    PosterUrl = result.TryGetProperty("poster_path", out var poster) ? poster.GetString() : null,
                    BackdropUrl = result.TryGetProperty("backdrop_path", out var backdrop) ? backdrop.GetString() : null
                });
                added++;
            }

            if (added == 0)
            {
                break;
            }
        }

        await FillDetailsAsync(client, items, apiKey, cancellationToken).ConfigureAwait(false);
        return items;
    }

    // Discover leaves out what the card under a focused suggestion shows, so each title is
    // looked up once per cache period. The backdrop stays the one discover gave, since clients
    // show it behind the page and fetch the titled art for a focused card on their own.
    private async Task FillDetailsAsync(HttpClient client, List<CustomRowItem> items, string apiKey, CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            var url = $"https://api.themoviedb.org/3/movie/{item.Id}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            TmdbRequestHelper.ApplyAuth(request, apiKey);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            ApplyDetails(item, doc.RootElement);
        }
    }

    internal static void ApplyDetails(CustomRowItem item, JsonElement details)
    {
        if (details.TryGetProperty("overview", out var overview) && overview.ValueKind == JsonValueKind.String)
        {
            item.Overview = overview.GetString();
        }

        if (details.TryGetProperty("vote_average", out var vote) && vote.ValueKind == JsonValueKind.Number && vote.GetDouble() > 0)
        {
            item.Rating = Math.Round(vote.GetDouble(), 1);
        }

        if (details.TryGetProperty("runtime", out var runtime) && runtime.ValueKind == JsonValueKind.Number && runtime.GetInt32() > 0)
        {
            item.RunTimeTicks = runtime.GetInt32() * TimeSpan.TicksPerMinute;
        }

        if (details.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
        {
            var names = genres.EnumerateArray()
                .Select(g => g.TryGetProperty("name", out var name) ? name.GetString() : null)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .ToList();
            if (names.Count > 0)
            {
                item.Genres = names;
            }
        }

        // Lets a suggestion match an owned copy that only has an IMDb id.
        if (details.TryGetProperty("imdb_id", out var imdb) && imdb.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(imdb.GetString()))
        {
            item.ProviderIds.Imdb = imdb.GetString();
        }
    }
}
