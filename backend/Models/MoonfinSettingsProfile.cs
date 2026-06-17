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

    [JsonPropertyName("jellyseerrEnabled")]
    public bool? JellyseerrEnabled { get; set; }

    [JsonPropertyName("jellyseerrApiKey")]
    public string? JellyseerrApiKey { get; set; }

    [JsonPropertyName("jellyseerrBlockNsfw")]
    public bool? JellyseerrBlockNsfw { get; set; }

    [JsonPropertyName("jellyseerrRows")]
    public JellyseerrRowsConfig? JellyseerrRows { get; set; }

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

    [JsonPropertyName("focusColor")]
    public string? FocusColor { get; set; }

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

    [JsonPropertyName("blockedRatings")]
    public List<string>? BlockedRatings { get; set; }

    [JsonPropertyName("homeRowOrder")]
    public List<string>? HomeRowOrder { get; set; }

    [JsonPropertyName("displayFavoritesRows")]
    public bool? DisplayFavoritesRows { get; set; }

    [JsonPropertyName("displayCollectionsRows")]
    public bool? DisplayCollectionsRows { get; set; }

    [JsonPropertyName("displayGenresRows")]
    public bool? DisplayGenresRows { get; set; }

    [JsonPropertyName("displaySeerrRows")]
    public bool? DisplaySeerrRows { get; set; }

    [JsonPropertyName("favoritesRowSortBy")]
    public string? FavoritesRowSortBy { get; set; }

    [JsonPropertyName("collectionsRowSortBy")]
    public string? CollectionsRowSortBy { get; set; }

    [JsonPropertyName("genresRowSortBy")]
    public string? GenresRowSortBy { get; set; }

    [JsonPropertyName("genresRowItemFilter")]
    public string? GenresRowItemFilter { get; set; }
}
