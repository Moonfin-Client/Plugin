using System.Text.Json.Serialization;

namespace Moonfin.Server.Models;

/// <summary>
/// A single settings profile containing all UI/feature preferences.
/// Used for global, desktop, mobile, and TV profiles.
/// Device-specific profiles store only overrides (nullable fields).
/// </summary>
public class MoonfinSettingsProfile
{
    [JsonPropertyName("desktopMediaBarProvider")]
    public string? DesktopMediaBarProvider { get; set; }

    [JsonPropertyName("seerrEnabled")]
    public bool? SeerrEnabled { get; set; }

    [JsonPropertyName("seerrApiKey")]
    public string? SeerrApiKey { get; set; }

    [JsonPropertyName("seerrBlockNsfw")]
    public bool? SeerrBlockNsfw { get; set; }

    [JsonPropertyName("seerrRows")]
    public SeerrRowsConfig? SeerrRows { get; set; }

    // Legacy jellyseerr* aliases: read old payloads and keep serializing the old keys so
    // clients that haven't migrated to the seerr* keys keep working. Backed by the Seerr* props.
    [JsonPropertyName("jellyseerrEnabled")]
    public bool? JellyseerrEnabledCompat { get => SeerrEnabled; set { if (value != null) { SeerrEnabled = value; } } }

    [JsonPropertyName("jellyseerrApiKey")]
    public string? JellyseerrApiKeyCompat { get => SeerrApiKey; set { if (value != null) { SeerrApiKey = value; } } }

    [JsonPropertyName("jellyseerrBlockNsfw")]
    public bool? JellyseerrBlockNsfwCompat { get => SeerrBlockNsfw; set { if (value != null) { SeerrBlockNsfw = value; } } }

    [JsonPropertyName("jellyseerrRows")]
    public SeerrRowsConfig? JellyseerrRowsCompat { get => SeerrRows; set { if (value != null) { SeerrRows = value; } } }

    [JsonPropertyName("mdblistEnabled")]
    public bool? MdblistEnabled { get; set; }

    [JsonPropertyName("mdblistApiKey")]
    public string? MdblistApiKey { get; set; }

    [JsonPropertyName("mdblistRatingSources")]
    public List<string>? MdblistRatingSources { get; set; }

    [JsonPropertyName("mdblistShowRatingNames")]
    public bool? MdblistShowRatingNames { get; set; }

    [JsonPropertyName("mdblistShowRatingBadges")]
    public bool? MdblistShowRatingBadges { get; set; }

    [JsonPropertyName("tmdbApiKey")]
    public string? TmdbApiKey { get; set; }

    [JsonPropertyName("tmdbEpisodeRatingsEnabled")]
    public bool? TmdbEpisodeRatingsEnabled { get; set; }

    [JsonPropertyName("detailsBackdropOpacity")]
    public int? DetailsBackdropOpacity { get; set; }

    [JsonPropertyName("detailsBackdropBlur")]
    public int? DetailsBackdropBlur { get; set; }

    [JsonPropertyName("navbarPosition")]
    public string? NavbarPosition { get; set; }

    [JsonPropertyName("navbarColor")]
    public string? NavbarColor { get; set; }

    [JsonPropertyName("navbarOpacity")]
    public int? NavbarOpacity { get; set; }

    [JsonPropertyName("navbarAlwaysExpanded")]
    public bool? NavbarAlwaysExpanded { get; set; }

    [JsonPropertyName("focusColor")]
    public string? FocusColor { get; set; }

    [JsonPropertyName("detailScreenStyle")]
    public string? DetailScreenStyle { get; set; }

    [JsonPropertyName("detailExpandedTabs")]
    public bool? DetailExpandedTabs { get; set; }

    [JsonPropertyName("visualTheme")]
    public string? VisualTheme { get; set; }

    [JsonPropertyName("customThemeId")]
    public string? CustomThemeId { get; set; }

    [JsonPropertyName("watchedIndicator")]
    public string? WatchedIndicator { get; set; }

    [JsonPropertyName("cardFocusExpansion")]
    public bool? CardFocusExpansion { get; set; }

    [JsonPropertyName("showShuffleButton")]
    public bool? ShowShuffleButton { get; set; }

    [JsonPropertyName("showGenresButton")]
    public bool? ShowGenresButton { get; set; }

    [JsonPropertyName("showFavoritesButton")]
    public bool? ShowFavoritesButton { get; set; }

    [JsonPropertyName("showCastButton")]
    public bool? ShowCastButton { get; set; }

    [JsonPropertyName("showSyncPlayButton")]
    public bool? ShowSyncPlayButton { get; set; }

    [JsonPropertyName("showLibrariesInToolbar")]
    public bool? ShowLibrariesInToolbar { get; set; }

    [JsonPropertyName("shuffleContentType")]
    public string? ShuffleContentType { get; set; }

    [JsonPropertyName("mergeContinueWatchingNextUp")]
    public bool? MergeContinueWatchingNextUp { get; set; }

    [JsonPropertyName("enableMultiServerLibraries")]
    public bool? EnableMultiServerLibraries { get; set; }

    [JsonPropertyName("enableFolderView")]
    public bool? EnableFolderView { get; set; }

    [JsonPropertyName("useDetailedSubHeadings")]
    public bool? useDetailedSubHeadings { get; set; }

    [JsonPropertyName("confirmExit")]
    public bool? ConfirmExit { get; set; }

    [JsonPropertyName("mediaBarMode")]
    public string? MediaBarMode { get; set; }

    [JsonPropertyName("mediaBarItemCount")]
    public int? MediaBarItemCount { get; set; }

    [JsonPropertyName("mediaBarOpacity")]
    public int? MediaBarOpacity { get; set; }

    [JsonPropertyName("mediaBarOverlayColor")]
    public string? MediaBarOverlayColor { get; set; }

    [JsonPropertyName("mediaBarAutoAdvance")]
    public bool? MediaBarAutoAdvance { get; set; }

    [JsonPropertyName("mediaBarIntervalMs")]
    public int? MediaBarIntervalMs { get; set; }

    [JsonPropertyName("mediaBarTrailerPreview")]
    public bool? MediaBarTrailerPreview { get; set; }

    [JsonPropertyName("mediaBarTrailerAudio")]
    public bool? MediaBarTrailerAudio { get; set; }

    [JsonPropertyName("episodePreviewEnabled")]
    public bool? EpisodePreviewEnabled { get; set; }

    [JsonPropertyName("previewAudioEnabled")]
    public bool? PreviewAudioEnabled { get; set; }

    [JsonPropertyName("mediaBarSourceType")]
    public string? MediaBarSourceType { get; set; }

    [JsonPropertyName("mediaBarCollectionIds")]
    public List<string>? MediaBarCollectionIds { get; set; }

    [JsonPropertyName("mediaBarLibraryIds")]
    public List<string>? MediaBarLibraryIds { get; set; }

    [JsonPropertyName("mediaBarExcludedGenres")]
    public List<string>? MediaBarExcludedGenres { get; set; }

    [JsonPropertyName("seasonalSurprise")]
    public string? SeasonalSurprise { get; set; }

    [JsonPropertyName("backdropEnabled")]
    public bool? BackdropEnabled { get; set; }

    [JsonPropertyName("homeRowsImageTypeOverride")]
    public bool? HomeRowsImageTypeOverride { get; set; }

    [JsonPropertyName("homeRowsStyle")]
    public string? HomeRowsStyle { get; set; }

    [JsonPropertyName("fullScreenRows")]
    public bool? FullScreenRows { get; set; }

    [JsonPropertyName("homeRowsImageType")]
    public string? HomeRowsImageType { get; set; }

    [JsonPropertyName("homeImageTypeContinueWatching")]
    public string? HomeImageTypeContinueWatching { get; set; }

    [JsonPropertyName("homeImageUseSeriesImage")]
    public bool? HomeImageUseSeriesImage { get; set; }

    [JsonPropertyName("posterSize")]
    public string? PosterSize { get; set; }

    [JsonPropertyName("detailsScreenBlur")]
    public string? DetailsScreenBlur { get; set; }

    [JsonPropertyName("browsingBlur")]
    public string? BrowsingBlur { get; set; }

    [JsonPropertyName("themeMusicEnabled")]
    public bool? ThemeMusicEnabled { get; set; }

    [JsonPropertyName("themeMusicOnHomeRows")]
    public bool? ThemeMusicOnHomeRows { get; set; }

    [JsonPropertyName("themeMusicVolume")]
    public int? ThemeMusicVolume { get; set; }

    [JsonPropertyName("themeMusicLoop")]
    public bool? ThemeMusicLoop { get; set; }

    [JsonPropertyName("blockedRatings")]
    public List<string>? BlockedRatings { get; set; }

    [JsonPropertyName("homeRowOrder")]
    public List<string>? HomeRowOrder { get; set; }

    [JsonPropertyName("homeSections")]
    public List<MoonfinHomeSectionConfig>? HomeSections { get; set; }

    [JsonPropertyName("displayFavoritesRows")]
    public bool? DisplayFavoritesRows { get; set; }

    [JsonPropertyName("displayCollectionsRows")]
    public bool? DisplayCollectionsRows { get; set; }

    [JsonPropertyName("displayGenresRows")]
    public bool? DisplayGenresRows { get; set; }

    [JsonPropertyName("displaySeerrRows")]
    public bool? DisplaySeerrRows { get; set; }

    [JsonPropertyName("displayPlaylistsRows")]
    public bool? DisplayPlaylistsRows { get; set; }

    [JsonPropertyName("displayAudioRows")]
    public bool? DisplayAudioRows { get; set; }

    [JsonPropertyName("displaySinceYouWatchedRows")]
    public bool? DisplaySinceYouWatchedRows { get; set; }

    [JsonPropertyName("sinceYouWatchedSource")]
    public string? SinceYouWatchedSource { get; set; }

    [JsonPropertyName("sinceYouWatchedSourceType")]
    public string? SinceYouWatchedSourceType { get; set; }

    [JsonPropertyName("sinceYouWatchedSourceItem")]
    public string? SinceYouWatchedSourceItem { get; set; }

    [JsonPropertyName("sinceYouWatchedNumRows")]
    public int? SinceYouWatchedNumRows { get; set; }

    [JsonPropertyName("sinceYouWatchedIncludeWatched")]
    public bool? SinceYouWatchedIncludeWatched { get; set; }

    [JsonPropertyName("displayRewatchRow")]
    public bool? DisplayRewatchRow { get; set; }

    [JsonPropertyName("rewatchSortBy")]
    public string? RewatchSortBy { get; set; }

    [JsonPropertyName("rewatchIncludeMovies")]
    public bool? RewatchIncludeMovies { get; set; }

    [JsonPropertyName("rewatchIncludeShows")]
    public bool? RewatchIncludeShows { get; set; }

    [JsonPropertyName("rewatchIncludeCollections")]
    public bool? RewatchIncludeCollections { get; set; }

    [JsonPropertyName("favoritesRowSortBy")]
    public string? FavoritesRowSortBy { get; set; }

    [JsonPropertyName("collectionsRowSortBy")]
    public string? CollectionsRowSortBy { get; set; }

    [JsonPropertyName("genresRowSortBy")]
    public string? GenresRowSortBy { get; set; }

    [JsonPropertyName("genresRowItemFilter")]
    public string? GenresRowItemFilter { get; set; }

    [JsonPropertyName("hiddenContinueWatchingItems")]
    public string? HiddenContinueWatchingItems { get; set; }

    [JsonPropertyName("hiddenNextUpSeries")]
    public string? HiddenNextUpSeries { get; set; }
}


/// <summary>
/// A Moonfin home section entry. Built-in sections only need Type/Enabled/Order.
/// Dynamic plugin sections keep their source metadata so newer clients can sync
/// the full home layout while older clients continue using HomeRowOrder.
/// </summary>
public class MoonfinHomeSectionConfig
{
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; set; }

    [JsonPropertyName("order")]
    public int? Order { get; set; }

    [JsonPropertyName("serverId")]
    public string? ServerId { get; set; }

    [JsonPropertyName("pluginSource")]
    public string? PluginSource { get; set; }

    [JsonPropertyName("pluginSection")]
    public string? PluginSection { get; set; }

    [JsonPropertyName("pluginAdditionalData")]
    public string? PluginAdditionalData { get; set; }

    [JsonPropertyName("pluginDisplayText")]
    public string? PluginDisplayText { get; set; }
}
