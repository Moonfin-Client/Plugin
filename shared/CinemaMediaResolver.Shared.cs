using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

#if MOONFIN_JELLYFIN
namespace Moonfin.Server.Services;
#else
namespace Emby.Plugins.Moonfin.Services;
#endif

// Jellyfin and Emby share this lookup logic, but handle access checks and cancellation separately.
public sealed partial class CinemaMediaResolver
{
    public sealed record MediaName(string Title, int? Year);

    private async Task<Resolution> ResolveItemAsync(Video item, BaseItem? owner,
        string? expectedMediaType, Func<string, ItemLookupInfo> parseStandard,
        CancellationToken cancellationToken, bool allowFilenameSearch)
    {
        // Check the trailer and its owner before falling back to playback context.
        var explicitType = ProviderValue(item.ProviderIds, "TmdbMediaType");
        if (explicitType != null && explicitType is not ("movie" or "tv")) return new(null);
        var itemType = item is Movie ? "movie" : null;
        var ownerType = owner is Movie ? "movie" : owner is Series ? "tv" : null;
        var types = new[] { explicitType, itemType, ownerType }.Where(t => t != null).Distinct().ToArray();
        if (types.Length > 1) return new(null);
        var type = types.FirstOrDefault();
        var direct = PositiveTmdb(item.ProviderIds);
        var owned = owner == null ? null : PositiveTmdb(owner.ProviderIds);
        if (direct.HasValue && owned.HasValue && direct != owned) return new(null);
        var hasDirectId = ProviderValue(item.ProviderIds, "Tmdb") != null;
        if (hasDirectId && !direct.HasValue) return new(null);
        // When a trailer has no media type, use the type of the movie or episode being played.
        var directType = type ?? expectedMediaType;
        if ((direct ?? owned) is int id && directType is "movie" or "tv")
        {
            return new(id, directType);
        }

        // Read provider IDs embedded in trailer filenames.
        var fromPath = owner == null ? ParseTrailerFilenameIdentity(item.Path) : null;
        var fromName = owner == null ? ParseTrailerFilenameIdentity(item.Name, isPath: false) : null;
        if (fromPath.HasValue && fromName.HasValue &&
            (fromPath.Value.MediaType != fromName.Value.MediaType ||
             fromPath.Value.TmdbId != fromName.Value.TmdbId ||
             fromPath.Value.TvdbId != fromName.Value.TvdbId))
            return new(null);
        var filenameIdentity = fromPath ?? fromName;
        if (filenameIdentity is { } named && type != null && type != named.MediaType)
            return new(null);

        var itemTvdb = PositiveId(item.ProviderIds, "Tvdb");
        var ownerTvdb = owner == null ? null : PositiveId(owner.ProviderIds, "Tvdb");
        if (itemTvdb.HasValue && ownerTvdb.HasValue && itemTvdb != ownerTvdb)
            return new(null);
        if (filenameIdentity?.TvdbId is int fileTvdb &&
            ((itemTvdb.HasValue && itemTvdb != fileTvdb) ||
             (ownerTvdb.HasValue && ownerTvdb != fileTvdb)))
            return new(null);
        var tvdbId = itemTvdb ?? ownerTvdb ?? filenameIdentity?.TvdbId;

        if (filenameIdentity?.TmdbId is int movieId)
        {
            if (tvdbId.HasValue || (direct.HasValue && direct != movieId)) return new(null);
            return new(movieId, "movie");
        }

        // For searches, use the media type from metadata or the filename before falling back to playback context.
        var searchType = type ?? filenameIdentity?.MediaType ?? (tvdbId.HasValue ? "tv" : expectedMediaType);
        if (searchType is not ("movie" or "tv") || (tvdbId.HasValue && searchType != "tv"))
            return new(null);

        if (tvdbId is int externalId)
        {
            // Leave the title empty so this request only maps the TVDB ID.
            var results = await AwaitSearch(_providers.GetRemoteSearchResults<Series, SeriesInfo>(
                new RemoteSearchQuery<SeriesInfo>
                {
                    SearchInfo = new SeriesInfo
                    {
                        Name = string.Empty,
                        ProviderIds = new()
                        {
                            ["Tvdb"] = externalId.ToString(CultureInfo.InvariantCulture),
                        },
                    },
                    SearchProviderName = "TheMovieDb",
                    IncludeDisabledProviders = false,
                },
                cancellationToken), cancellationToken).ConfigureAwait(false);
            var mappedResults = results.ToArray();
            if (mappedResults.Length > 0)
            {
                var mappedId = UniqueTvdbTmdbId(externalId, mappedResults);
                if (!mappedId.HasValue || (direct.HasValue && direct != mappedId)) return new(null);
                return new(mappedId.Value, "tv");
            }
        }
        // Filename searches are optional. Attached trailers can still use their owner's title.
        if (owner == null && !allowFilenameSearch) return new(null);

        // When a trailer is attached, search using its owner's title.
        if (owner != null && (string.IsNullOrWhiteSpace(owner.Name) ||
            owner.Name.Length > 200 || NormalizeTitle(owner.Name).Length == 0)) return new(null);
        MediaName?[] candidates;
        if (owner != null)
            candidates = new[] { new MediaName(owner.Name, owner.ProductionYear) };
        else if (filenameIdentity is { } n)
            candidates = new[] { new MediaName(n.Title, null) };
        else
            candidates = new[] { ParseName(item.Path, parseStandard), ParseName(item.Name, parseStandard, isPath: false) };
        var names = candidates.OfType<MediaName>().ToArray();
        // Try removing a trailing video ID before searching the full title.
        var ordered = owner == null && filenameIdentity == null
            ? names.SelectMany(OrderedNames)
            : names;
        foreach (var name in ordered
            .GroupBy(n => (NormalizeTitle(n.Title), n.Year))
            .Select(group => group.First()))
        {
            // Clean up punctuation before searching TMDB.
            var searchName = Regex.Replace(name.Title, @"[\W_-[·]]+", " ");
            IEnumerable<RemoteSearchResult> results;
            if (searchType == "movie")
            {
                results = await AwaitSearch(_providers.GetRemoteSearchResults<Movie, MovieInfo>(new RemoteSearchQuery<MovieInfo>
                {
                    SearchInfo = new MovieInfo { Name = searchName, Year = name.Year },
                    SearchProviderName = "TheMovieDb",
                    IncludeDisabledProviders = false,
                }, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                results = await AwaitSearch(_providers.GetRemoteSearchResults<Series, SeriesInfo>(new RemoteSearchQuery<SeriesInfo>
                {
                    SearchInfo = new SeriesInfo { Name = searchName, Year = name.Year },
                    SearchProviderName = "TheMovieDb",
                    IncludeDisabledProviders = false,
                }, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            // Keep the provider's result order, but require a matching year when one is available.
            var first = results.FirstOrDefault(r => !name.Year.HasValue ||
                (r.ProductionYear ?? r.PremiereDate?.Year) == name.Year);
            var matchId = PositiveTmdb(first?.ProviderIds);
            if (!matchId.HasValue) continue;
            if (direct.HasValue && direct != matchId) return new(null);
            return new(matchId.Value, searchType);
        }
        return new(null);
    }

    private static string? ProviderValue(IDictionary<string, string>? ids, string key) =>
        ids?.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    private static int? PositiveId(IDictionary<string, string>? ids, string key)
    {
        var value = ProviderValue(ids, key);
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
    }

    public static int? PositiveTmdb(IDictionary<string, string>? ids) => PositiveId(ids, "Tmdb");

    /// <summary>Use the TMDB ID only when the TVDB lookup results agree.</summary>
    public static int? UniqueTvdbTmdbId(int tvdbId, IEnumerable<RemoteSearchResult> results)
    {
        int? found = null;
        foreach (var result in results)
        {
            var reportedTvdb = ProviderValue(result.ProviderIds, "Tvdb");
            if (reportedTvdb != null &&
                (!int.TryParse(reportedTvdb, NumberStyles.None, CultureInfo.InvariantCulture, out var reported) || reported != tvdbId))
                return null;
            var id = PositiveTmdb(result.ProviderIds);
            if (!id.HasValue || (found.HasValue && found != id)) return null;
            found = id;
        }
        return found;
    }

    public static MediaName? ParseName(string? pathOrName, Func<string, ItemLookupInfo> parseStandard, bool isPath = true)
    {
        if (string.IsNullOrWhiteSpace(pathOrName)) return null;
        var filename = isPath ? pathOrName.Replace('\\', '/').Split('/').Last() : pathOrName;
        filename = Regex.Replace(filename, @"\.(mp4|mkv|avi|mov|webm|m4v|ts|m2ts)$", "", RegexOptions.IgnoreCase)
            .Normalize(NormalizationForm.FormKC).Replace('⧸', '/').Replace('⁄', '/').Replace('∕', '/');
        // Remove the uploader from filenames like "Uploader - Title [video ID]".
        var prefixed = Regex.Match(filename,
            @"^.+?\s+-\s+(?<title>.+?)\s+\[[A-Za-z0-9_-]{10,12}\]$");
        if (prefixed.Success) filename = prefixed.Groups["title"].Value;
        var parsed = parseStandard(filename);
        var title = parsed.Name ?? "";
        var year = parsed.Year;

        // Use the last Trailer or Teaser label so those words aren't removed when they're part of the title.
        var trailerLabels = Regex.Matches(title,
            @"[\s._:|-]+(?:(?:official|final|theatrical)[\s._:|-]+)?(?:teaser[\s._:|-]+trailer|trailer|teaser)(?![\p{L}\p{N}])",
            RegexOptions.IgnoreCase);
        if (trailerLabels.Count > 0)
            title = title.Substring(0, trailerLabels[trailerLabels.Count - 1].Index);

        // The server's parser may remove Trailer but leave Official or Final.
        if (Regex.IsMatch(filename, @"(?:official|final|theatrical)[\s._:|-]+(?:teaser[\s._:|-]+)?trailer\b", RegexOptions.IgnoreCase))
            title = Regex.Replace(title, @"[\s._:|-]+(?:official|final|theatrical)$", "", RegexOptions.IgnoreCase);
        title = Regex.Replace(title,
            @"[\s._:|-]+(?:watch[\s._:|-]+at[\s._:|-]+home|watch[\s._:|-]+now|on[\s._:|-]+digital)$",
            "", RegexOptions.IgnoreCase);
        // Read years joined directly to the title when the server's parser misses them.
        if (!year.HasValue)
        {
            var compact = Regex.Match(title, @"^(?<title>.*\p{L})(?<year>19\d{2}|20\d{2})$");
            if (compact.Success && int.TryParse(compact.Groups["year"].Value, out var candidate)
                && candidate <= DateTime.UtcNow.Year + 3)
            {
                title = compact.Groups["title"].Value;
                year = candidate;
            }
        }

        var seriesTitle = Regex.Replace(title, @"[\s._:|-]+(?:season[\s._|-]*|s)\d{1,3}$", "", RegexOptions.IgnoreCase);
        if (seriesTitle != title)
        {
            title = seriesTitle;
            year = null;
        }

        title = title.Replace('.', ' ').Replace('_', ' ').Trim(' ', '-', '–', ':', '|');
        // Promotional labels alone don't identify a movie or series.
        if (Regex.IsMatch(NormalizeTitle(title),
            @"\A(?:(?:OFFICIAL|FINAL|THEATRICAL)?(?:TEASERTRAILER|TRAILER|TEASER)|OFFICIAL|FINAL|THEATRICAL|INTRO|PREROLL" +
            @"|COMINGSOON|FIRSTLOOK|SPECIALLOOK|NOWPLAYING|WATCHNOW|AVAILABLENOW|NOWSTREAMING|STREAMINGNOW" +
            @"|DIGITALRELEASE|ONDIGITAL|WATCHATHOME|INTHEATERS|ONLYINTHEATERS)\d{0,2}\z"))
            return null;
        if (year is < 1900 || year > DateTime.UtcNow.Year + 3) year = null;
        if (title.Length == 0 || title.Length > 200 || NormalizeTitle(title).Length == 0
            || Regex.IsMatch(title, @"\A[0-9a-f]{32,64}\z", RegexOptions.IgnoreCase)) return null;
        return new MediaName(title, year);
    }

    private static IEnumerable<MediaName> OrderedNames(MediaName source)
    {
        var title = source.Title.Trim(' ', '|');
        var suffix = Regex.Match(title, @"[\s._-]+(?<id>[A-Za-z0-9_-]{8,})$");
        var token = suffix.Groups["id"].Value;
        var mixedCaseId = token.Length >= 10 && token.All(char.IsLetter) &&
            Regex.IsMatch(token, @"[A-Z]{2}") && Regex.IsMatch(token, @"[a-z]{2,}[A-Z]");
        if (suffix.Success && token.Any(char.IsLetter) &&
            (token.Count(char.IsDigit) > 1 || Regex.IsMatch(token, @"\d[A-Za-z]") || mixedCaseId))
            yield return new MediaName(title.Substring(0, suffix.Index), source.Year);

        yield return source;
    }

    // Read movie TMDB IDs and series TVDB IDs from trailer filenames.
    public static (string Title, string MediaType, int? TmdbId, int? TvdbId)? ParseTrailerFilenameIdentity(
        string? pathOrName, bool isPath = true)
    {
        if (string.IsNullOrWhiteSpace(pathOrName)) return null;
        var filename = isPath ? pathOrName.Replace('\\', '/').Split('/').Last() : pathOrName;
        var match = Regex.Match(filename,
            @"^(?<title>.+)_(?<source>tvdb|tmdb)?(?<id>[1-9]\d*)_trailer(?:\.(?:mp4|mkv|avi|mov|webm|m4v|ts|m2ts))?$",
            RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return null;
        var source = match.Groups["source"].Value;
        // A four-digit number may be a release year rather than a TMDB ID.
        if (source.Length == 0 && id >= 1900 && id <= DateTime.UtcNow.Year + 3) return null;
        var title = match.Groups["title"].Value.Replace('_', ' ').Trim();
        if (title.Length == 0 || title.Length > 200 || NormalizeTitle(title).Length == 0) return null;
        var isTv = source.Equals("tvdb", StringComparison.OrdinalIgnoreCase);
        return (title, isTv ? "tv" : "movie", isTv ? (int?)null : id, isTv ? id : (int?)null);
    }

    private static string NormalizeTitle(string title) => string.Concat(
        title.Normalize(NormalizationForm.FormKC).ToUpperInvariant().Where(char.IsLetterOrDigit));
}
