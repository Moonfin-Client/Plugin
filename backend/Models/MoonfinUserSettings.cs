using System.Text.Json.Serialization;

namespace Moonfin.Server.Models;

/// <summary>
/// Moonfin user settings synced across clients.
/// Based on: https://github.com/Moonfin-Client/AndroidTV-FireTV/blob/master/docs/FEATURES.md
/// </summary>
public class MoonfinUserSettings
{
    /// <summary>Settings schema version for migration support.</summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Unix timestamp of last settings update.</summary>
    [JsonPropertyName("lastUpdated")]
    public long? LastUpdated { get; set; }

    /// <summary>Client ID that last updated these settings.</summary>
    [JsonPropertyName("lastUpdatedBy")]
    public string? LastUpdatedBy { get; set; }

    /// <summary>Whether Jellyseerr integration is enabled for this user.</summary>
    [JsonPropertyName("jellyseerrEnabled")]
    public bool? JellyseerrEnabled { get; set; }

    /// <summary>User's Jellyseerr API key for requests.</summary>
    [JsonPropertyName("jellyseerrApiKey")]
    public string? JellyseerrApiKey { get; set; }

    /// <summary>Configuration for Jellyseerr discovery rows.</summary>
    [JsonPropertyName("jellyseerrRows")]
    public JellyseerrRowsConfig? JellyseerrRows { get; set; }

    /// <summary>Whether MDBList ratings integration is enabled.</summary>
    [JsonPropertyName("mdblistEnabled")]
    public bool? MdblistEnabled { get; set; }

    /// <summary>User's MDBList API key.</summary>
    [JsonPropertyName("mdblistApiKey")]
    public string? MdblistApiKey { get; set; }

    /// <summary>
    /// Which MDBList rating sources to display.
    /// Values: imdb, tmdb, trakt, tomatoes, popcorn, metacritic, metacriticuser,
    /// letterboxd, rogerebert, myanimelist, anilist
    /// </summary>
    [JsonPropertyName("mdblistRatingSources")]
    public List<string>? MdblistRatingSources { get; set; }

    /// <summary>User's TMDB API key for additional metadata.</summary>
    [JsonPropertyName("tmdbApiKey")]
    public string? TmdbApiKey { get; set; }

    /// <summary>Whether the custom navigation bar is enabled.</summary>
    [JsonPropertyName("navbarEnabled")]
    public bool? NavbarEnabled { get; set; }

    /// <summary>Whether the custom details page is enabled.</summary>
    [JsonPropertyName("detailsPageEnabled")]
    public bool? DetailsPageEnabled { get; set; }

    /// <summary>Navbar position (top, bottom, left, right).</summary>
    [JsonPropertyName("navbarPosition")]
    public string? NavbarPosition { get; set; }

    /// <summary>Show clock in the navigation bar.</summary>
    [JsonPropertyName("showClock")]
    public bool? ShowClock { get; set; }

    /// <summary>Use 24-hour time format.</summary>
    [JsonPropertyName("use24HourClock")]
    public bool? Use24HourClock { get; set; }

    /// <summary>Show shuffle button in toolbar.</summary>
    [JsonPropertyName("showShuffleButton")]
    public bool? ShowShuffleButton { get; set; }

    /// <summary>Show genres button in toolbar.</summary>
    [JsonPropertyName("showGenresButton")]
    public bool? ShowGenresButton { get; set; }

    /// <summary>Show favorites button in toolbar.</summary>
    [JsonPropertyName("showFavoritesButton")]
    public bool? ShowFavoritesButton { get; set; }

    /// <summary>Show cast/remote playback button in toolbar.</summary>
    [JsonPropertyName("showCastButton")]
    public bool? ShowCastButton { get; set; }

    /// <summary>Show SyncPlay button in toolbar.</summary>
    [JsonPropertyName("showSyncPlayButton")]
    public bool? ShowSyncPlayButton { get; set; }

    /// <summary>Show libraries in toolbar.</summary>
    [JsonPropertyName("showLibrariesInToolbar")]
    public bool? ShowLibrariesInToolbar { get; set; }

    /// <summary>Content type for shuffle (movies, episodes, all).</summary>
    [JsonPropertyName("shuffleContentType")]
    public string? ShuffleContentType { get; set; }

    /// <summary>Merge Continue Watching and Next Up rows.</summary>
    [JsonPropertyName("mergeContinueWatchingNextUp")]
    public bool? MergeContinueWatchingNextUp { get; set; }

    /// <summary>Enable multi-server library aggregation.</summary>
    [JsonPropertyName("enableMultiServerLibraries")]
    public bool? EnableMultiServerLibraries { get; set; }

    /// <summary>Enable folder-based library view.</summary>
    [JsonPropertyName("enableFolderView")]
    public bool? EnableFolderView { get; set; }

    /// <summary>Show confirm exit dialog.</summary>
    [JsonPropertyName("confirmExit")]
    public bool? ConfirmExit { get; set; }

    /// <summary>Enable the media bar on home screen.</summary>
    [JsonPropertyName("mediaBarEnabled")]
    public bool? MediaBarEnabled { get; set; }

    /// <summary>Content type to display in media bar.</summary>
    [JsonPropertyName("mediaBarContentType")]
    public string? MediaBarContentType { get; set; }

    /// <summary>Number of items to show in media bar.</summary>
    [JsonPropertyName("mediaBarItemCount")]
    public int? MediaBarItemCount { get; set; }

    /// <summary>Media bar background opacity (0-100).</summary>
    [JsonPropertyName("mediaBarOpacity")]
    public int? MediaBarOpacity { get; set; }

    /// <summary>Hex color for media bar overlay.</summary>
    [JsonPropertyName("mediaBarOverlayColor")]
    public string? MediaBarOverlayColor { get; set; }

    /// <summary>Whether media bar auto-advances slides.</summary>
    [JsonPropertyName("mediaBarAutoAdvance")]
    public bool? MediaBarAutoAdvance { get; set; }

    /// <summary>Auto-advance interval in milliseconds.</summary>
    [JsonPropertyName("mediaBarIntervalMs")]
    public int? MediaBarIntervalMs { get; set; }

    /// <summary>Seasonal visual effect (none, winter, spring, summer, fall, halloween).</summary>
    [JsonPropertyName("seasonalSurprise")]
    public string? SeasonalSurprise { get; set; }

    /// <summary>Enable backdrop images.</summary>
    [JsonPropertyName("backdropEnabled")]
    public bool? BackdropEnabled { get; set; }

    /// <summary>Override default home rows image type.</summary>
    [JsonPropertyName("homeRowsImageTypeOverride")]
    public bool? HomeRowsImageTypeOverride { get; set; }

    /// <summary>Image type for home rows (poster, thumb, banner).</summary>
    [JsonPropertyName("homeRowsImageType")]
    public string? HomeRowsImageType { get; set; }

    /// <summary>Blur intensity for details screen background.</summary>
    [JsonPropertyName("detailsScreenBlur")]
    public string? DetailsScreenBlur { get; set; }

    /// <summary>Blur intensity for browsing screen backgrounds.</summary>
    [JsonPropertyName("browsingBlur")]
    public string? BrowsingBlur { get; set; }

    /// <summary>Enable theme music playback.</summary>
    [JsonPropertyName("themeMusicEnabled")]
    public bool? ThemeMusicEnabled { get; set; }

    /// <summary>Play theme music when browsing home rows.</summary>
    [JsonPropertyName("themeMusicOnHomeRows")]
    public bool? ThemeMusicOnHomeRows { get; set; }

    /// <summary>Theme music volume (0-100).</summary>
    [JsonPropertyName("themeMusicVolume")]
    public int? ThemeMusicVolume { get; set; }

    /// <summary>List of content ratings to block.</summary>
    [JsonPropertyName("blockedRatings")]
    public List<string>? BlockedRatings { get; set; }

    /// <summary>Client-specific settings (keyed by client ID).</summary>
    [JsonPropertyName("clientSpecific")]
    public Dictionary<string, string>? ClientSpecific { get; set; }
}

/// <summary>
/// Jellyseerr discovery rows configuration.
/// </summary>
public class JellyseerrRowsConfig
{
    /// <summary>Show trending movies row.</summary>
    [JsonPropertyName("trendingMovies")]
    public bool? TrendingMovies { get; set; }

    /// <summary>Show trending TV shows row.</summary>
    [JsonPropertyName("trendingTv")]
    public bool? TrendingTv { get; set; }

    /// <summary>Show popular movies row.</summary>
    [JsonPropertyName("popularMovies")]
    public bool? PopularMovies { get; set; }

    /// <summary>Show popular TV shows row.</summary>
    [JsonPropertyName("popularTv")]
    public bool? PopularTv { get; set; }

    /// <summary>Show upcoming movies row.</summary>
    [JsonPropertyName("upcomingMovies")]
    public bool? UpcomingMovies { get; set; }

    /// <summary>Show upcoming TV shows row.</summary>
    [JsonPropertyName("upcomingTv")]
    public bool? UpcomingTv { get; set; }

    /// <summary>Custom order of discovery rows.</summary>
    [JsonPropertyName("rowOrder")]
    public List<string>? RowOrder { get; set; }
}
