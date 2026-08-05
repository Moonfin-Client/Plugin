using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Emby.Plugins.Moonfin.Models
{
    public class MoonfinSettingsProfile
    {
        [JsonPropertyName("desktopMediaBarProvider")] public string? DesktopMediaBarProvider { get; set; }
        [JsonPropertyName("seerrEnabled")] public bool? SeerrEnabled { get; set; }
        [JsonPropertyName("seerrApiKey")] public string? SeerrApiKey { get; set; }
        [JsonPropertyName("seerrBlockNsfw")] public bool? SeerrBlockNsfw { get; set; }
        [JsonPropertyName("seerrRows")] public SeerrRowsConfig? SeerrRows { get; set; }
        // Legacy jellyseerr* aliases: read old payloads and keep serializing the old keys for un-migrated clients.
        [JsonPropertyName("jellyseerrEnabled")] public bool? JellyseerrEnabledCompat { get => SeerrEnabled; set { if (value != null) { SeerrEnabled = value; } } }
        [JsonPropertyName("jellyseerrApiKey")] public string? JellyseerrApiKeyCompat { get => SeerrApiKey; set { if (value != null) { SeerrApiKey = value; } } }
        [JsonPropertyName("jellyseerrBlockNsfw")] public bool? JellyseerrBlockNsfwCompat { get => SeerrBlockNsfw; set { if (value != null) { SeerrBlockNsfw = value; } } }
        [JsonPropertyName("jellyseerrRows")] public SeerrRowsConfig? JellyseerrRowsCompat { get => SeerrRows; set { if (value != null) { SeerrRows = value; } } }
        [JsonPropertyName("mdblistEnabled")] public bool? MdblistEnabled { get; set; }
        [JsonPropertyName("mdblistApiKey")] public string? MdblistApiKey { get; set; }
        [JsonPropertyName("mdblistRatingSources")] public List<string>? MdblistRatingSources { get; set; }
        [JsonPropertyName("mdblistShowRatingNames")] public bool? MdblistShowRatingNames { get; set; }
        [JsonPropertyName("mdblistShowRatingBadges")] public bool? MdblistShowRatingBadges { get; set; }
        [JsonPropertyName("tmdbApiKey")] public string? TmdbApiKey { get; set; }
        [JsonPropertyName("tmdbEpisodeRatingsEnabled")] public bool? TmdbEpisodeRatingsEnabled { get; set; }
        [JsonPropertyName("detailsBackdropOpacity")] public int? DetailsBackdropOpacity { get; set; }
        [JsonPropertyName("detailsBackdropBlur")] public int? DetailsBackdropBlur { get; set; }
        [JsonPropertyName("navbarPosition")] public string? NavbarPosition { get; set; }
        [JsonPropertyName("navbarColor")] public string? NavbarColor { get; set; }
        [JsonPropertyName("navbarOpacity")] public int? NavbarOpacity { get; set; }
        [JsonPropertyName("focusColor")] public string? FocusColor { get; set; }
        [JsonPropertyName("visualTheme")] public string? VisualTheme { get; set; }
        [JsonPropertyName("customThemeId")] public string? CustomThemeId { get; set; }
        [JsonPropertyName("watchedIndicator")] public string? WatchedIndicator { get; set; }
        [JsonPropertyName("cardFocusExpansion")] public bool? CardFocusExpansion { get; set; }
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
        [JsonPropertyName("mergeRecentRowsByType")] public bool? MergeRecentRowsByType { get; set; }
        [JsonPropertyName("enableFolderView")] public bool? EnableFolderView { get; set; }
        [JsonPropertyName("useDetailedSubHeadings")] public bool? UseDetailedSubHeadings { get; set; }
        [JsonPropertyName("confirmExit")] public bool? ConfirmExit { get; set; }
        [JsonPropertyName("mediaBarMode")] public string? MediaBarMode { get; set; }
        [JsonPropertyName("mediaBarItemCount")] public int? MediaBarItemCount { get; set; }
        [JsonPropertyName("mediaBarOpacity")] public int? MediaBarOpacity { get; set; }
        [JsonPropertyName("mediaBarOverlayColor")] public string? MediaBarOverlayColor { get; set; }
        [JsonPropertyName("mediaBarAutoAdvance")] public bool? MediaBarAutoAdvance { get; set; }
        [JsonPropertyName("mediaBarIntervalMs")] public int? MediaBarIntervalMs { get; set; }
        [JsonPropertyName("mediaBarTrailerPreview")] public bool? MediaBarTrailerPreview { get; set; }
        [JsonPropertyName("mediaBarTrailerAudio")] public bool? MediaBarTrailerAudio { get; set; }
        [JsonPropertyName("episodePreviewEnabled")] public bool? EpisodePreviewEnabled { get; set; }
        [JsonPropertyName("previewAudioEnabled")] public bool? PreviewAudioEnabled { get; set; }
        [JsonPropertyName("mediaBarSourceType")] public string? MediaBarSourceType { get; set; }
        [JsonPropertyName("mediaBarCollectionIds")] public List<string>? MediaBarCollectionIds { get; set; }
        [JsonPropertyName("mediaBarLibraryIds")] public List<string>? MediaBarLibraryIds { get; set; }
        [JsonPropertyName("mediaBarExcludedGenres")] public List<string>? MediaBarExcludedGenres { get; set; }
        [JsonPropertyName("seasonalSurprise")] public string? SeasonalSurprise { get; set; }
        [JsonPropertyName("backdropEnabled")] public bool? BackdropEnabled { get; set; }
        [JsonPropertyName("homeRowsImageTypeOverride")] public bool? HomeRowsImageTypeOverride { get; set; }
        [JsonPropertyName("homeRowsStyle")] public string? HomeRowsStyle { get; set; }
        [JsonPropertyName("modernHomeRowsPadding")] public int? ModernHomeRowsPadding { get; set; }
        [JsonPropertyName("classicHomeRowsPadding")] public int? ClassicHomeRowsPadding { get; set; }
        [JsonPropertyName("fullScreenRows")] public bool? FullScreenRows { get; set; }
        [JsonPropertyName("homeRowsImageType")] public string? HomeRowsImageType { get; set; }
        [JsonPropertyName("homeImageTypeContinueWatching")] public string? HomeImageTypeContinueWatching { get; set; }
        [JsonPropertyName("homeImageUseSeriesImage")] public bool? HomeImageUseSeriesImage { get; set; }
        [JsonPropertyName("posterSize")] public string? PosterSize { get; set; }
        [JsonPropertyName("detailsScreenBlur")] public string? DetailsScreenBlur { get; set; }
        [JsonPropertyName("browsingBlur")] public string? BrowsingBlur { get; set; }
        [JsonPropertyName("themeMusicEnabled")] public bool? ThemeMusicEnabled { get; set; }
        [JsonPropertyName("themeMusicOnHomeRows")] public bool? ThemeMusicOnHomeRows { get; set; }
        [JsonPropertyName("themeMusicVolume")] public int? ThemeMusicVolume { get; set; }
        [JsonPropertyName("blockedRatings")] public List<string>? BlockedRatings { get; set; }
        [JsonPropertyName("homeRowOrder")] public List<string>? HomeRowOrder { get; set; }
        [JsonPropertyName("homeSections")] public List<MoonfinHomeSectionConfig>? HomeSections { get; set; }
        [JsonPropertyName("displayFavoritesRows")] public bool? DisplayFavoritesRows { get; set; }
        [JsonPropertyName("displayCollectionsRows")] public bool? DisplayCollectionsRows { get; set; }
        [JsonPropertyName("displayGenresRows")] public bool? DisplayGenresRows { get; set; }
        [JsonPropertyName("displaySeerrRows")] public bool? DisplaySeerrRows { get; set; }
        [JsonPropertyName("displayPlaylistsRows")] public bool? DisplayPlaylistsRows { get; set; }
        [JsonPropertyName("displayAudioRows")] public bool? DisplayAudioRows { get; set; }
        [JsonPropertyName("favoritesRowSortBy")] public string? FavoritesRowSortBy { get; set; }
        [JsonPropertyName("collectionsRowSortBy")] public string? CollectionsRowSortBy { get; set; }
        [JsonPropertyName("genresRowSortBy")] public string? GenresRowSortBy { get; set; }
        [JsonPropertyName("genresRowItemFilter")] public string? GenresRowItemFilter { get; set; }
        [JsonPropertyName("navbarAlwaysExpanded")] public bool? NavbarAlwaysExpanded { get; set; }
        [JsonPropertyName("mediaBarTrailerCaptions")] public bool? MediaBarTrailerCaptions { get; set; }
        [JsonPropertyName("detailScreenStyle")] public string? DetailScreenStyle { get; set; }
        [JsonPropertyName("detailExpandedTabs")] public bool? DetailExpandedTabs { get; set; }
        [JsonPropertyName("detailButtonOrder")] public List<string>? DetailButtonOrder { get; set; }
        [JsonPropertyName("hiddenDetailButtons")] public List<string>? HiddenDetailButtons { get; set; }
        [JsonPropertyName("seerrRowOrder")] public List<string>? SeerrRowOrder { get; set; }
        [JsonPropertyName("hiddenSeerrRows")] public List<string>? HiddenSeerrRows { get; set; }
        [JsonPropertyName("themeMusicLoop")] public bool? ThemeMusicLoop { get; set; }
        [JsonPropertyName("displaySinceYouWatchedRows")] public bool? DisplaySinceYouWatchedRows { get; set; }
        [JsonPropertyName("sinceYouWatchedSource")] public string? SinceYouWatchedSource { get; set; }
        [JsonPropertyName("sinceYouWatchedSourceType")] public string? SinceYouWatchedSourceType { get; set; }
        [JsonPropertyName("sinceYouWatchedSourceItem")] public string? SinceYouWatchedSourceItem { get; set; }
        [JsonPropertyName("sinceYouWatchedNumRows")] public int? SinceYouWatchedNumRows { get; set; }
        [JsonPropertyName("sinceYouWatchedIncludeWatched")] public bool? SinceYouWatchedIncludeWatched { get; set; }
        [JsonPropertyName("displayRewatchRow")] public bool? DisplayRewatchRow { get; set; }
        [JsonPropertyName("rewatchSortBy")] public string? RewatchSortBy { get; set; }
        [JsonPropertyName("rewatchIncludeMovies")] public bool? RewatchIncludeMovies { get; set; }
        [JsonPropertyName("rewatchIncludeShows")] public bool? RewatchIncludeShows { get; set; }
        [JsonPropertyName("rewatchIncludeCollections")] public bool? RewatchIncludeCollections { get; set; }
        [JsonPropertyName("hiddenContinueWatchingItems")] public string? HiddenContinueWatchingItems { get; set; }
        [JsonPropertyName("hiddenNextUpSeries")] public string? HiddenNextUpSeries { get; set; }

        // Detail screen extras. Clients already sent these two, but there was nowhere to store
        // them, so every value was dropped on arrival.
        // Which item types the media bar shows. Kept apart from mediaBarSourceType, which says
        // where items are drawn from (libraries or collections). The two used to share a key,
        // so a client picking "library" as a source handed that word to clients reading it as
        // an item type, and picking "movies" as a type handed that back as a source.
        [JsonPropertyName("mediaBarContentType")] public string? MediaBarContentType { get; set; }

        [JsonPropertyName("detailShowTechnicalDetails")] public bool? DetailShowTechnicalDetails { get; set; }
        [JsonPropertyName("recommendationSystemSource")] public string? RecommendationSystemSource { get; set; }
        [JsonPropertyName("recommendationsApplyParentalRatingCap")] public bool? RecommendationsApplyParentalRatingCap { get; set; }

        // Screensaver. The admin config page already reads and writes screensaverMode, so
        // without this property the admin's choice was discarded on every save.
        [JsonPropertyName("screensaverEnabled")] public bool? ScreensaverEnabled { get; set; }
        [JsonPropertyName("screensaverMode")] public string? ScreensaverMode { get; set; }
        [JsonPropertyName("screensaverTimeout")] public string? ScreensaverTimeout { get; set; }
        [JsonPropertyName("screensaverDimming")] public int? ScreensaverDimming { get; set; }
        [JsonPropertyName("screensaverClockMode")] public string? ScreensaverClockMode { get; set; }
        [JsonPropertyName("screensaverMaxAgeRating")] public string? ScreensaverMaxAgeRating { get; set; }
        [JsonPropertyName("screensaverRequireRating")] public bool? ScreensaverRequireRating { get; set; }

        // Subtitles. Colours travel as #AARRGGBB strings so clients that store an int and
        // clients that store a CSS colour can both round-trip them without loss.
        [JsonPropertyName("subtitleMode")] public string? SubtitleMode { get; set; }
        [JsonPropertyName("defaultSubtitleLanguage")] public string? DefaultSubtitleLanguage { get; set; }
        [JsonPropertyName("fallbackSubtitleLanguage")] public string? FallbackSubtitleLanguage { get; set; }
        [JsonPropertyName("preferSdhSubtitles")] public bool? PreferSdhSubtitles { get; set; }
        [JsonPropertyName("subtitlesUseEmbeddedStyles")] public bool? SubtitlesUseEmbeddedStyles { get; set; }
        [JsonPropertyName("subtitlesUseEmbeddedFontSizes")] public bool? SubtitlesUseEmbeddedFontSizes { get; set; }
        [JsonPropertyName("pgsDirectPlay")] public bool? PgsDirectPlay { get; set; }
        [JsonPropertyName("assDirectPlay")] public bool? AssDirectPlay { get; set; }
        [JsonPropertyName("subtitlesTextColor")] public string? SubtitlesTextColor { get; set; }
        [JsonPropertyName("subtitleTextStrokeColor")] public string? SubtitleTextStrokeColor { get; set; }
        [JsonPropertyName("subtitlesBackgroundColor")] public string? SubtitlesBackgroundColor { get; set; }
        [JsonPropertyName("subtitlesTextSize")] public double? SubtitlesTextSize { get; set; }
        [JsonPropertyName("subtitlesOffsetPosition")] public double? SubtitlesOffsetPosition { get; set; }
        [JsonPropertyName("subtitlesTextWeight")] public int? SubtitlesTextWeight { get; set; }

        // Audio track selection. Output and passthrough settings are deliberately absent,
        // they describe the hardware attached to one device rather than a user preference.
        [JsonPropertyName("defaultAudioLanguage")] public string? DefaultAudioLanguage { get; set; }
        [JsonPropertyName("fallbackAudioLanguage")] public string? FallbackAudioLanguage { get; set; }
        [JsonPropertyName("preferDefaultAudioTrack")] public bool? PreferDefaultAudioTrack { get; set; }
        [JsonPropertyName("preferAudioDescription")] public bool? PreferAudioDescription { get; set; }
        [JsonPropertyName("audioNightMode")] public bool? AudioNightMode { get; set; }
        [JsonPropertyName("showDescriptionOnPause")] public bool? ShowDescriptionOnPause { get; set; }
        [JsonPropertyName("playerZoomMode")] public string? PlayerZoomMode { get; set; }
        [JsonPropertyName("resumeSubtractDuration")] public string? ResumeSubtractDuration { get; set; }
        [JsonPropertyName("unpauseRewindDuration")] public int? UnpauseRewindDuration { get; set; }
        [JsonPropertyName("skipBackLength")] public int? SkipBackLength { get; set; }
        [JsonPropertyName("skipForwardLength")] public int? SkipForwardLength { get; set; }
        [JsonPropertyName("osdLockEnabled")] public bool? OsdLockEnabled { get; set; }
        [JsonPropertyName("detailButtonOrderTv")] public List<string>? DetailButtonOrderTv { get; set; }
        [JsonPropertyName("detailButtonOrderMobile")] public List<string>? DetailButtonOrderMobile { get; set; }
        [JsonPropertyName("detailButtonOrderDesktop")] public List<string>? DetailButtonOrderDesktop { get; set; }
        [JsonPropertyName("osdButtonOrderTv")] public List<string>? OsdButtonOrderTv { get; set; }
        [JsonPropertyName("osdButtonOrderMobile")] public List<string>? OsdButtonOrderMobile { get; set; }
        [JsonPropertyName("osdButtonOrderDesktop")] public List<string>? OsdButtonOrderDesktop { get; set; }
        [JsonPropertyName("hiddenDetailButtonsTv")] public List<string>? HiddenDetailButtonsTv { get; set; }
        [JsonPropertyName("hiddenDetailButtonsMobile")] public List<string>? HiddenDetailButtonsMobile { get; set; }
        [JsonPropertyName("hiddenDetailButtonsDesktop")] public List<string>? HiddenDetailButtonsDesktop { get; set; }
        [JsonPropertyName("hiddenOsdButtonsTv")] public List<string>? HiddenOsdButtonsTv { get; set; }
        [JsonPropertyName("hiddenOsdButtonsMobile")] public List<string>? HiddenOsdButtonsMobile { get; set; }
        [JsonPropertyName("hiddenOsdButtonsDesktop")] public List<string>? HiddenOsdButtonsDesktop { get; set; }
        [JsonPropertyName("videoStartDelay")] public int? VideoStartDelay { get; set; }
        [JsonPropertyName("liveTvDirectPlayEnabled")] public bool? LiveTvDirectPlayEnabled { get; set; }
        [JsonPropertyName("maxBitrate")] public string? MaxBitrate { get; set; }
        [JsonPropertyName("maxVideoResolution")] public string? MaxVideoResolution { get; set; }
        [JsonPropertyName("cinemaModeEnabled")] public bool? CinemaModeEnabled { get; set; }
        [JsonPropertyName("mediaSegmentCountdown")] public string? MediaSegmentCountdown { get; set; }
        [JsonPropertyName("autoplayNextEpisode")] public bool? AutoplayNextEpisode { get; set; }
        [JsonPropertyName("nextUpBehavior")] public string? NextUpBehavior { get; set; }
        [JsonPropertyName("nextUpTimeout")] public int? NextUpTimeout { get; set; }
        [JsonPropertyName("replaceSkipOutroWithNextUp")] public bool? ReplaceSkipOutroWithNextUp { get; set; }
        [JsonPropertyName("stillWatchingBehavior")] public string? StillWatchingBehavior { get; set; }
        [JsonPropertyName("mediaQueuingEnabled")] public bool? MediaQueuingEnabled { get; set; }
        [JsonPropertyName("resumeLastQueueOnPlay")] public bool? ResumeLastQueueOnPlay { get; set; }

        // SyncPlay. Correction tuning is a user preference, the transport settings aren't.
        [JsonPropertyName("syncPlayEnabled")] public bool? SyncPlayEnabled { get; set; }
        [JsonPropertyName("syncPlayAutoOpen")] public bool? SyncPlayAutoOpen { get; set; }
        [JsonPropertyName("syncPlayAdvancedCorrectionEnabled")] public bool? SyncPlayAdvancedCorrectionEnabled { get; set; }
        [JsonPropertyName("syncPlayEnableSyncCorrection")] public bool? SyncPlayEnableSyncCorrection { get; set; }
        [JsonPropertyName("syncPlayUseSpeedToSync")] public bool? SyncPlayUseSpeedToSync { get; set; }
        [JsonPropertyName("syncPlayUseSkipToSync")] public bool? SyncPlayUseSkipToSync { get; set; }
        [JsonPropertyName("syncPlayMinDelaySpeedToSync")] public double? SyncPlayMinDelaySpeedToSync { get; set; }
        [JsonPropertyName("syncPlayMaxDelaySpeedToSync")] public double? SyncPlayMaxDelaySpeedToSync { get; set; }
        [JsonPropertyName("syncPlaySpeedToSyncDuration")] public double? SyncPlaySpeedToSyncDuration { get; set; }
        [JsonPropertyName("syncPlayMinDelaySkipToSync")] public double? SyncPlayMinDelaySkipToSync { get; set; }
        [JsonPropertyName("syncPlayExtraTimeOffset")] public double? SyncPlayExtraTimeOffset { get; set; }

        // Downloads. Paths and concurrency stay device local, the behaviour toggles sync.
        [JsonPropertyName("defaultDownloadQuality")] public string? DefaultDownloadQuality { get; set; }
        [JsonPropertyName("downloadWifiOnly")] public bool? DownloadWifiOnly { get; set; }
        [JsonPropertyName("reportDownloadsAsActivity")] public bool? ReportDownloadsAsActivity { get; set; }
        [JsonPropertyName("downloadStorageLimitMb")] public int? DownloadStorageLimitMb { get; set; }
        [JsonPropertyName("imdbTop250MoviesEnabled")] public bool? ImdbTop250MoviesEnabled { get; set; }
        [JsonPropertyName("imdbTop250TvShowsEnabled")] public bool? ImdbTop250TvShowsEnabled { get; set; }
        [JsonPropertyName("imdbMostPopularMoviesEnabled")] public bool? ImdbMostPopularMoviesEnabled { get; set; }
        [JsonPropertyName("imdbMostPopularTvShowsEnabled")] public bool? ImdbMostPopularTvShowsEnabled { get; set; }
        [JsonPropertyName("imdbLowestRatedMoviesEnabled")] public bool? ImdbLowestRatedMoviesEnabled { get; set; }
        [JsonPropertyName("imdbTopEnglishMoviesEnabled")] public bool? ImdbTopEnglishMoviesEnabled { get; set; }
        [JsonPropertyName("tmdbPopularMoviesEnabled")] public bool? TmdbPopularMoviesEnabled { get; set; }
        [JsonPropertyName("tmdbTopRatedMoviesEnabled")] public bool? TmdbTopRatedMoviesEnabled { get; set; }
        [JsonPropertyName("tmdbNowPlayingMoviesEnabled")] public bool? TmdbNowPlayingMoviesEnabled { get; set; }
        [JsonPropertyName("tmdbUpcomingMoviesEnabled")] public bool? TmdbUpcomingMoviesEnabled { get; set; }
        [JsonPropertyName("tmdbPopularTvEnabled")] public bool? TmdbPopularTvEnabled { get; set; }
        [JsonPropertyName("tmdbTopRatedTvEnabled")] public bool? TmdbTopRatedTvEnabled { get; set; }
        [JsonPropertyName("tmdbAiringTodayTvEnabled")] public bool? TmdbAiringTodayTvEnabled { get; set; }
        [JsonPropertyName("tmdbOnTheAirTvEnabled")] public bool? TmdbOnTheAirTvEnabled { get; set; }
        [JsonPropertyName("tmdbTrendingMovieDailyEnabled")] public bool? TmdbTrendingMovieDailyEnabled { get; set; }
        [JsonPropertyName("tmdbTrendingMovieWeeklyEnabled")] public bool? TmdbTrendingMovieWeeklyEnabled { get; set; }
        [JsonPropertyName("tmdbTrendingTvDailyEnabled")] public bool? TmdbTrendingTvDailyEnabled { get; set; }
        [JsonPropertyName("tmdbTrendingTvWeeklyEnabled")] public bool? TmdbTrendingTvWeeklyEnabled { get; set; }
        [JsonPropertyName("tmdbTrendingAllWeeklyEnabled")] public bool? TmdbTrendingAllWeeklyEnabled { get; set; }
        [JsonPropertyName("mergeRadarrSonarrCalendars")] public bool? MergeRadarrSonarrCalendars { get; set; }
        [JsonPropertyName("enableRadarrCalendar")] public bool? EnableRadarrCalendar { get; set; }
        [JsonPropertyName("radarrCalendarShowCinema")] public bool? RadarrCalendarShowCinema { get; set; }
        [JsonPropertyName("radarrCalendarShowDigital")] public bool? RadarrCalendarShowDigital { get; set; }
        [JsonPropertyName("radarrCalendarShowPhysical")] public bool? RadarrCalendarShowPhysical { get; set; }
        [JsonPropertyName("radarrCalendarShowDate")] public bool? RadarrCalendarShowDate { get; set; }
        [JsonPropertyName("enableSonarrCalendar")] public bool? EnableSonarrCalendar { get; set; }
        [JsonPropertyName("sonarrCalendarShowEpisodeInfo")] public bool? SonarrCalendarShowEpisodeInfo { get; set; }
        [JsonPropertyName("sonarrCalendarShowDate")] public bool? SonarrCalendarShowDate { get; set; }
        [JsonPropertyName("libraryPosterSize")] public string? LibraryPosterSize { get; set; }
        [JsonPropertyName("playlistPosterSize")] public string? PlaylistPosterSize { get; set; }
        [JsonPropertyName("audioSortOption")] public string? AudioSortOption { get; set; }
        [JsonPropertyName("favoritesViewStyle")] public string? FavoritesViewStyle { get; set; }
        [JsonPropertyName("defaultFavoritesFilter")] public string? DefaultFavoritesFilter { get; set; }
        [JsonPropertyName("displayAudioAlbumArtists")] public bool? DisplayAudioAlbumArtists { get; set; }
        [JsonPropertyName("displayAudioAlbums")] public bool? DisplayAudioAlbums { get; set; }
        [JsonPropertyName("displayAudioArtists")] public bool? DisplayAudioArtists { get; set; }
        [JsonPropertyName("displayAudioFavorites")] public bool? DisplayAudioFavorites { get; set; }
        [JsonPropertyName("displayAudioLastPlayed")] public bool? DisplayAudioLastPlayed { get; set; }
        [JsonPropertyName("displayAudioLatest")] public bool? DisplayAudioLatest { get; set; }
        [JsonPropertyName("displayAudioPlaylists")] public bool? DisplayAudioPlaylists { get; set; }
        [JsonPropertyName("personPageGroupItems")] public bool? PersonPageGroupItems { get; set; }
        [JsonPropertyName("personPageSortOption")] public string? PersonPageSortOption { get; set; }
        [JsonPropertyName("liveTvChannelSortBy")] public string? LiveTvChannelSortBy { get; set; }
        [JsonPropertyName("allGenresImageType")] public string? AllGenresImageType { get; set; }
        [JsonPropertyName("groupItemsIntoCollections")] public bool? GroupItemsIntoCollections { get; set; }
        [JsonPropertyName("showMediaDetailsOnLibraryPage")] public bool? ShowMediaDetailsOnLibraryPage { get; set; }
        [JsonPropertyName("hideBackdropsInLibraries")] public bool? HideBackdropsInLibraries { get; set; }
        [JsonPropertyName("playlistsRowSortBy")] public string? PlaylistsRowSortBy { get; set; }
        [JsonPropertyName("audioRowsSortBy")] public string? AudioRowsSortBy { get; set; }
        [JsonPropertyName("epgMobileView")] public string? EpgMobileView { get; set; }
        [JsonPropertyName("sinceYouWatched1Enabled")] public bool? SinceYouWatched1Enabled { get; set; }
        [JsonPropertyName("sinceYouWatched2Enabled")] public bool? SinceYouWatched2Enabled { get; set; }
        [JsonPropertyName("sinceYouWatched3Enabled")] public bool? SinceYouWatched3Enabled { get; set; }
        [JsonPropertyName("sinceYouWatched4Enabled")] public bool? SinceYouWatched4Enabled { get; set; }
        [JsonPropertyName("sinceYouWatched5Enabled")] public bool? SinceYouWatched5Enabled { get; set; }

        // Account level appearance and behaviour. clockBehavior replaces the v1 showClock
        // bool, so it takes a new name rather than reviving the legacy one.
        [JsonPropertyName("languageOverride")] public string? LanguageOverride { get; set; }
        [JsonPropertyName("userSortBy")] public string? UserSortBy { get; set; }
        [JsonPropertyName("interfaceStyle")] public string? InterfaceStyle { get; set; }
        [JsonPropertyName("glassQuality")] public string? GlassQuality { get; set; }
        [JsonPropertyName("desktopUiScale")] public string? DesktopUiScale { get; set; }
        [JsonPropertyName("preferSystemImeKeyboard")] public bool? PreferSystemImeKeyboard { get; set; }
        [JsonPropertyName("clockBehavior")] public string? ClockBehavior { get; set; }
        [JsonPropertyName("use24HourClock")] public bool? Use24HourClock { get; set; }
        [JsonPropertyName("homeRowInfoOverlay")] public bool? HomeRowInfoOverlay { get; set; }
        [JsonPropertyName("showSeerrButton")] public bool? ShowSeerrButton { get; set; }
        [JsonPropertyName("diagnosticLoggingEnabled")] public bool? DiagnosticLoggingEnabled { get; set; }
        [JsonPropertyName("updateNotificationsEnabled")] public bool? UpdateNotificationsEnabled { get; set; }
    }

    /// <summary>
    /// A Moonfin home section entry. Built-in sections only need Type/Enabled/Order.
    /// Dynamic plugin sections keep their source metadata so newer clients can sync
    /// the full home layout while older clients continue using HomeRowOrder.
    /// </summary>
    public class MoonfinHomeSectionConfig
    {
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
        [JsonPropertyName("order")] public int? Order { get; set; }
        [JsonPropertyName("serverId")] public string? ServerId { get; set; }
        [JsonPropertyName("pluginSource")] public string? PluginSource { get; set; }
        [JsonPropertyName("pluginSection")] public string? PluginSection { get; set; }
        [JsonPropertyName("pluginAdditionalData")] public string? PluginAdditionalData { get; set; }
        [JsonPropertyName("pluginDisplayText")] public string? PluginDisplayText { get; set; }
    }
}
