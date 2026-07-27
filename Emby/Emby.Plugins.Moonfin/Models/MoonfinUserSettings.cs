using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Emby.Plugins.Moonfin.Models
{
    public class MoonfinUserSettings
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = 2;

        [JsonPropertyName("lastUpdated")]
        public long? LastUpdated { get; set; }

        [JsonPropertyName("lastUpdatedBy")]
        public string? LastUpdatedBy { get; set; }

        [JsonPropertyName("syncEnabled")]
        public bool SyncEnabled { get; set; } = true;

        [JsonPropertyName("global")]
        public MoonfinSettingsProfile? Global { get; set; }

        [JsonPropertyName("desktop")]
        public MoonfinSettingsProfile? Desktop { get; set; }

        [JsonPropertyName("mobile")]
        public MoonfinSettingsProfile? Mobile { get; set; }

        [JsonPropertyName("tv")]
        public MoonfinSettingsProfile? Tv { get; set; }

        [JsonPropertyName("seerrEnabled")] public bool? SeerrEnabled { get; set; }
        [JsonPropertyName("seerrApiKey")] public string? SeerrApiKey { get; set; }
        [JsonPropertyName("seerrRows")] public SeerrRowsConfig? SeerrRows { get; set; }
        // Legacy jellyseerr* aliases so pre-rename payloads still deserialize.
        [JsonPropertyName("jellyseerrEnabled")] public bool? JellyseerrEnabledCompat { get => SeerrEnabled; set { if (value != null) { SeerrEnabled = value; } } }
        [JsonPropertyName("jellyseerrApiKey")] public string? JellyseerrApiKeyCompat { get => SeerrApiKey; set { if (value != null) { SeerrApiKey = value; } } }
        [JsonPropertyName("jellyseerrRows")] public SeerrRowsConfig? JellyseerrRowsCompat { get => SeerrRows; set { if (value != null) { SeerrRows = value; } } }
        [JsonPropertyName("mdblistEnabled")] public bool? MdblistEnabled { get; set; }
        [JsonPropertyName("mdblistApiKey")] public string? MdblistApiKey { get; set; }
        [JsonPropertyName("mdblistRatingSources")] public List<string>? MdblistRatingSources { get; set; }
        [JsonPropertyName("tmdbApiKey")] public string? TmdbApiKey { get; set; }
        [JsonPropertyName("tmdbEpisodeRatingsEnabled")] public bool? TmdbEpisodeRatingsEnabled { get; set; }
        [JsonPropertyName("navbarEnabled")] public bool? NavbarEnabled { get; set; }
        [JsonPropertyName("detailsPageEnabled")] public bool? DetailsPageEnabled { get; set; }
        [JsonPropertyName("detailsBackdropOpacity")] public int? DetailsBackdropOpacity { get; set; }
        [JsonPropertyName("detailsBackdropBlur")] public int? DetailsBackdropBlur { get; set; }
        [JsonPropertyName("navbarPosition")] public string? NavbarPosition { get; set; }
        [JsonPropertyName("showClock")] public bool? ShowClock { get; set; }
        [JsonPropertyName("use24HourClock")] public bool? Use24HourClock { get; set; }
        [JsonPropertyName("showShuffleButton")] public bool? ShowShuffleButton { get; set; }
        [JsonPropertyName("showGenresButton")] public bool? ShowGenresButton { get; set; }
        [JsonPropertyName("showFavoritesButton")] public bool? ShowFavoritesButton { get; set; }
        [JsonPropertyName("showCastButton")] public bool? ShowCastButton { get; set; }
        [JsonPropertyName("showSyncPlayButton")] public bool? ShowSyncPlayButton { get; set; }
        [JsonPropertyName("showLibrariesInToolbar")] public bool? ShowLibrariesInToolbar { get; set; }
        [JsonPropertyName("shuffleContentType")] public string? ShuffleContentType { get; set; }
        [JsonPropertyName("mergeContinueWatchingNextUp")] public bool? MergeContinueWatchingNextUp { get; set; }
        [JsonPropertyName("nextUpMaxDays")] public int? NextUpMaxDays { get; set; }
        [JsonPropertyName("enableMultiServerLibraries")] public bool? EnableMultiServerLibraries { get; set; }
        [JsonPropertyName("enableFolderView")] public bool? EnableFolderView { get; set; }
        [JsonPropertyName("confirmExit")] public bool? ConfirmExit { get; set; }
        [JsonPropertyName("mediaBarEnabled")] public bool? MediaBarEnabled { get; set; }
        [JsonPropertyName("mediaBarItemCount")] public int? MediaBarItemCount { get; set; }
        [JsonPropertyName("mediaBarOpacity")] public int? MediaBarOpacity { get; set; }
        [JsonPropertyName("mediaBarOverlayColor")] public string? MediaBarOverlayColor { get; set; }
        [JsonPropertyName("mediaBarAutoAdvance")] public bool? MediaBarAutoAdvance { get; set; }
        [JsonPropertyName("mediaBarIntervalMs")] public int? MediaBarIntervalMs { get; set; }
        [JsonPropertyName("mediaBarTrailerPreview")] public bool? MediaBarTrailerPreview { get; set; }
        [JsonPropertyName("mediaBarSourceType")] public string? MediaBarSourceType { get; set; }
        [JsonPropertyName("mediaBarCollectionIds")] public List<string>? MediaBarCollectionIds { get; set; }
        [JsonPropertyName("mediaBarLibraryIds")] public List<string>? MediaBarLibraryIds { get; set; }
        [JsonPropertyName("mediaBarExcludedGenres")] public List<string>? MediaBarExcludedGenres { get; set; }
        [JsonPropertyName("seasonalSurprise")] public string? SeasonalSurprise { get; set; }
        [JsonPropertyName("backdropEnabled")] public bool? BackdropEnabled { get; set; }
        [JsonPropertyName("homeRowsImageTypeOverride")] public bool? HomeRowsImageTypeOverride { get; set; }
        [JsonPropertyName("homeRowsImageType")] public string? HomeRowsImageType { get; set; }
        [JsonPropertyName("detailsScreenBlur")] public string? DetailsScreenBlur { get; set; }
        [JsonPropertyName("browsingBlur")] public string? BrowsingBlur { get; set; }
        [JsonPropertyName("themeMusicEnabled")] public bool? ThemeMusicEnabled { get; set; }
        [JsonPropertyName("themeMusicOnHomeRows")] public bool? ThemeMusicOnHomeRows { get; set; }
        [JsonPropertyName("themeMusicVolume")] public int? ThemeMusicVolume { get; set; }
        [JsonPropertyName("blockedRatings")] public List<string>? BlockedRatings { get; set; }
        [JsonPropertyName("clientSpecific")] public System.Collections.Generic.Dictionary<string, string>? ClientSpecific { get; set; }

        [JsonIgnore]
        public bool NeedsMigration => SchemaVersion < 2 && Global == null &&
            (NavbarEnabled != null || MediaBarEnabled != null || MdblistEnabled != null ||
             SeerrEnabled != null || TmdbEpisodeRatingsEnabled != null ||
             NavbarPosition != null || DetailsPageEnabled != null);

        public MoonfinSettingsProfile? GetProfile(string profileName)
        {
            return profileName?.ToLowerInvariant() switch
            {
                "desktop" => Desktop,
                "mobile" => Mobile,
                "tv" => Tv,
                _ => null
            };
        }

        public void SetProfile(string profileName, MoonfinSettingsProfile? profile)
        {
            switch (profileName?.ToLowerInvariant())
            {
                case "desktop": Desktop = profile; break;
                case "mobile": Mobile = profile; break;
                case "tv": Tv = profile; break;
                case "global": Global = profile; break;
            }
        }

        public static readonly string[] ValidProfiles = { "global", "desktop", "mobile", "tv" };
    }

    public class SeerrRowsConfig
    {
        [JsonPropertyName("trendingMovies")] public bool? TrendingMovies { get; set; }
        [JsonPropertyName("trendingTv")] public bool? TrendingTv { get; set; }
        [JsonPropertyName("popularMovies")] public bool? PopularMovies { get; set; }
        [JsonPropertyName("popularTv")] public bool? PopularTv { get; set; }
        [JsonPropertyName("upcomingMovies")] public bool? UpcomingMovies { get; set; }
        [JsonPropertyName("upcomingTv")] public bool? UpcomingTv { get; set; }
        [JsonPropertyName("rowOrder")] public List<string>? RowOrder { get; set; }
    }
}
