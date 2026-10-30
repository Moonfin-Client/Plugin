// Package pluginconfig holds the Jellyfin plugin's PluginConfiguration
// defaults, shared by the plugin and the form generator.
package pluginconfig

// Defaults is PluginConfiguration as a fresh install has it, with null fields
// left out as Jellyfin does.
func Defaults() map[string]any {
	return map[string]any{
		"EnableSettingsSync": true, "SeerrEnabled": false, "JellyseerrEnabled": false,
		"MdblistOfficialListsEnabled": true, "ImdbListsEnabled": true, "MdblistOfficialListsMaxItems": 250,
		"RecommendationsProviderEnabled": true, "StudioLogosEnabled": true, "StudioLogosMaxAgeDays": 30,
		"AnimeMarkersEnabled": false, "AnimeMarkerLibraryIds": []any{}, "AnimeMarkerRecapLookup": true,
		"AnimeMarkerMaxAgeDays": 30, "AnimeMarkerVerboseLogging": false, "AnimeAudioMarkersEnabled": false,
		"AnimeAudioMarkersMovies": false, "AnimeAudioSeparateDualAudio": false, "AnimeAudioTrustSelectedLibraries": false,
		"AnimeMarkerPlacement": "below", "AnimeMarkerShowFiller": true, "AnimeMarkerShowMixed": true,
		"AnimeMarkerShowRecap": true, "AnimeMarkerShowMangaCanon": false, "AnimeMarkerShowAnimeCanon": false,
		"WebEnableWebRtcScan": true, "PushRelayUrl": "https://push.moonfin.io/send",
		"GamesEnabled": false, "GameLibraryIds": []any{}, "GamesIsolateWebApp": false, "GamesMetadataEnabled": true,
		"GamesMetadataDbUrlBase": "https://cdn.jsdelivr.net/gh/libretro/libretro-database@master/rdb/",
		"GamesLaunchBoxEnabled":  true, "GamesLaunchBoxUrl": "https://gamesdb.launchbox-app.com/Metadata.zip",
	}
}
