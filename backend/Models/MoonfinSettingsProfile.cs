using System.Text.Json.Serialization;

namespace Moonfin.Server.Models;

/// <summary>
/// A single settings profile containing all UI/feature preferences.
/// Used for global, desktop, mobile, and TV profiles.
/// Device-specific profiles store only overrides (nullable fields).
/// </summary>
public class MoonfinSettingsProfile
{
    [JsonPropertyName("jellyseerrEnabled")]
    public bool? JellyseerrEnabled { get; set; }

    [JsonPropertyName("jellyseerrApiKey")]
    public string? JellyseerrApiKey { get; set; }

    [JsonPropertyName("jellyseerrRows")]
    public JellyseerrRowsConfig? JellyseerrRows { get; set; }

    [JsonPropertyName("mdblistEnabled")]
    public bool? MdblistEnabled { get; set; }

    [JsonPropertyName("mdblistApiKey")]
    public string? MdblistApiKey { get; set; }

    [JsonPropertyName("mdblistRatingSources")]
    public List<string>? MdblistRatingSources { get; set; }

    [JsonPropertyName("tmdbApiKey")]
    public string? TmdbApiKey { get; set; }

    [JsonPropertyName("tmdbEpisodeRatingsEnabled")]
    public bool? TmdbEpisodeRatingsEnabled { get; set; }

    [JsonPropertyName("navbarEnabled")]
    public bool? NavbarEnabled { get; set; }

    [JsonPropertyName("detailsPageEnabled")]
    public bool? DetailsPageEnabled { get; set; }

    [JsonPropertyName("navbarPosition")]
    public string? NavbarPosition { get; set; }

    [JsonPropertyName("showClock")]
    public bool? ShowClock { get; set; }

    [JsonPropertyName("use24HourClock")]
    public bool? Use24HourClock { get; set; }

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

    [JsonPropertyName("confirmExit")]
    public bool? ConfirmExit { get; set; }

    [JsonPropertyName("mediaBarEnabled")]
    public bool? MediaBarEnabled { get; set; }

    [JsonPropertyName("mediaBarContentType")]
    public string? MediaBarContentType { get; set; }

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

    [JsonPropertyName("seasonalSurprise")]
    public string? SeasonalSurprise { get; set; }

    [JsonPropertyName("backdropEnabled")]
    public bool? BackdropEnabled { get; set; }

    [JsonPropertyName("homeRowsImageTypeOverride")]
    public bool? HomeRowsImageTypeOverride { get; set; }

    [JsonPropertyName("homeRowsImageType")]
    public string? HomeRowsImageType { get; set; }

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
}
