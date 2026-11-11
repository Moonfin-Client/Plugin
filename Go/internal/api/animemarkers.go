package api

import (
	"cmp"
	"context"
	"log/slog"
	"net/http"
	"os"
	"slices"
	"strconv"
	"strings"
	"sync/atomic"
	"time"

	"github.com/Moonfin-Client/Plugin/Go/internal/anime"
)

// Anime markers put AnimeFillerList's filler and canon categories and
// MyAnimeList's recap flags on anime episodes, plus subbed and dubbed pills
// from the audio tracks.

// AnimeCatalog is a host whose series, episodes and audio tracks the anime
// markers can read.
type AnimeCatalog interface {
	// AnimeSeries lists the series in the libraries with these ids, or in
	// every library when there are none, read as the server owner.
	AnimeSeries(ctx context.Context, libraryIDs []string) ([]AnimeItem, error)
	// AnimeItems reads these items as the caller, with their audio tracks,
	// leaving out any they can't see.
	AnimeItems(ctx context.Context, caller Identity, ids []string) ([]AnimeItem, error)
	// AnimeEpisodes lists a series' episodes as the caller.
	AnimeEpisodes(ctx context.Context, caller Identity, seriesID string) ([]AnimeItem, error)
}

// AnimeItem is a series, episode or movie as the anime markers read it.
type AnimeItem struct {
	ID string
	// Type is the host's name for the kind of item. The markers know show,
	// episode and movie.
	Type                 string
	Title, OriginalTitle string
	Year                 int
	LibraryID            string
	// SeriesID and SeasonID are an episode's series and season.
	SeriesID, SeasonID string
	// Season and Index number an episode, and are 0 when it has no number.
	Season, Index int
	AirDate       time.Time
	// ProviderIDs is keyed by provider in any capitalization of Jellyfin's
	// names, such as tvdb, tmdb and imdb.
	ProviderIDs  map[string]string
	Genres, Tags []string
	// AudioLanguages is the language of each audio track. Only AnimeItems reads them.
	AudioLanguages []string
}

type animeMarkers struct {
	catalog AnimeCatalog
	src     *anime.Sources
	syncing atomic.Bool
	// audio holds a cachedAudio per caller and series.
	audio ttlCache
}

type cachedAudio struct {
	episodes, seasons map[string]anime.AudioKind
}

// newAnimeMarkers is nil, leaving the markers off, without a folder for their
// caches or a catalog that can list series.
func newAnimeMarkers(dir string, catalog Catalog) *animeMarkers {
	c, ok := catalog.(AnimeCatalog)
	if dir == "" || !ok {
		return nil
	}
	if err := os.MkdirAll(dir, 0o700); err != nil {
		slog.Error("anime markers: make their folder, so they're off", "error", err)
		return nil
	}
	return &animeMarkers{catalog: c, src: anime.New(dir), audio: ttlCache{m: map[string]cached{}}}
}

func (s *Server) animeMarkerRoutes() {
	if s.anime == nil {
		return
	}
	s.mux.HandleFunc("GET /Moonfin/AnimeMarkers/Series", s.user(s.handleAnimeSeries))
	s.mux.HandleFunc("GET /Moonfin/AnimeMarkers/Items", s.user(s.handleAnimeItems))
	s.mux.HandleFunc("GET /Moonfin/AnimeMarkers/Diagnostics", s.admin(s.handleAnimeDiagnostics))
	s.mux.HandleFunc("GET /Moonfin/AnimeMarkers/Preview", s.admin(s.handleAnimePreview))
	s.mux.HandleFunc("POST /Moonfin/AnimeMarkers/Refresh", s.admin(s.handleAnimeRefresh))
	s.mux.HandleFunc("GET /Moonfin/AnimeMarkers/Log", s.admin(s.handleAnimeLog))
	s.mux.HandleFunc("POST /Moonfin/AnimeMarkers/ClearLog", s.admin(s.handleAnimeClearLog))
	s.mux.HandleFunc("POST /Moonfin/AnimeMarkers/ClearCache", s.admin(s.handleAnimeClearCache))
	// The admin page's Preview finds a series through Jellyfin's item search,
	// which host.js answers from here.
	s.mux.HandleFunc("GET /Moonfin/AnimeMarkers/Search", s.admin(s.handleAnimeSearch))
}

type animeSettings struct {
	filler, audio, recapLookup, movies, separateDualAudio, trustLibraries bool
	// libraries is nil when none are chosen, which means every library.
	libraries             []string
	maxAge                time.Duration
	placement, labelStyle string
	shown                 map[anime.Kind]bool
	recap                 bool
}

// animeSettings reads the section's settings and turns the debug log on or off to match.
func (s *Server) animeSettings(ctx context.Context) animeSettings {
	c, err := s.fullConfig(ctx)
	if err != nil {
		c = s.mergeConfig(nil)
	}
	on := func(key string) bool { v, _ := c[key].(bool); return v }
	set := animeSettings{
		filler: on("AnimeMarkersEnabled"), audio: on("AnimeAudioMarkersEnabled"), recapLookup: c["AnimeMarkerRecapLookup"] != false,
		movies: on("AnimeAudioMarkersMovies"), separateDualAudio: on("AnimeAudioSeparateDualAudio"), trustLibraries: on("AnimeAudioTrustSelectedLibraries"),
		maxAge:    time.Duration(max(1, wholeNumber(c["AnimeMarkerMaxAgeDays"], 30))) * 24 * time.Hour,
		placement: cmp.Or(str(c["AnimeMarkerPlacement"]), "below"), labelStyle: cmp.Or(str(c["AnimeAudioLabelStyle"]), "full"),
		shown: map[anime.Kind]bool{anime.Filler: c["AnimeMarkerShowFiller"] != false, anime.Mixed: c["AnimeMarkerShowMixed"] != false,
			anime.MangaCanon: on("AnimeMarkerShowMangaCanon"), anime.AnimeCanon: on("AnimeMarkerShowAnimeCanon")},
		recap: c["AnimeMarkerShowRecap"] != false,
	}
	ids, _ := c["AnimeMarkerLibraryIds"].([]any)
	for _, id := range ids {
		if id, ok := id.(string); ok && strings.TrimSpace(id) != "" {
			set.libraries = append(set.libraries, strings.TrimSpace(id))
		}
	}
	s.anime.src.Log.On.Store(on("AnimeMarkerVerboseLogging"))
	return set
}

// handleAnimeSeries answers for one series' episodes from the cache alone, so
// an app can ask on every episode list without waiting on an outside site.
// A series whose table hasn't been fetched answers pending, and the feature
// being off answers enabled false rather than an error.
func (s *Server) handleAnimeSeries(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()
	set := s.animeSettings(ctx)
	log := s.anime.src.Log
	seriesID := strings.TrimSpace(r.URL.Query().Get("seriesId"))
	log.Printf("request  seriesId=%s from=%s", seriesID, r.UserAgent())
	if !set.filler && !set.audio {
		log.Printf("  -> answered enabled=false (both marker features are off in settings)")
		writeJSON(w, http.StatusOK, M{"enabled": false, "matched": false, "episodes": M{}, "seasons": M{}})
		return
	}
	series, ok := s.animeSeries(w, r, seriesID)
	if !ok {
		return
	}
	s.anime.src.EnsureCatalog(ctx)
	log.Printf("  series=%q filler=%t audio=%t", series.Title, set.filler, set.audio)

	var episodes []AnimeItem
	readEpisodes := func() ([]AnimeItem, error) {
		if episodes != nil {
			return episodes, nil
		}
		var err error
		episodes, err = s.anime.catalog.AnimeEpisodes(ctx, callerOf(r), series.ID)
		return episodes, err
	}

	result := seriesMarkers{markers: map[string]*anime.Episode{}}
	if set.filler {
		if err := s.anime.markersFor(series, set.maxAge, readEpisodes, &result); err != nil {
			logError(r, "anime markers: filler lookup", err)
			log.Printf("  !! filler lookup failed: %v", err)
		}
	}
	audio := cachedAudio{}
	switch {
	case !set.audio:
	case !audioCandidate(series, nil, set):
		log.Printf("  audio skipped: this series is not in a selected library and does not look like anime")
	default:
		started := time.Now()
		var err error
		if audio, err = s.anime.seriesAudio(ctx, callerOf(r), series, readEpisodes); err != nil {
			logError(r, "anime markers: audio lookup", err)
			log.Printf("  !! audio lookup failed: %v", err)
		}
		log.Printf("  audio pass took %d ms", time.Since(started).Milliseconds())
	}

	log.Printf("  -> matched=%s pending=%t markers=%d matchedBy=%s recapKnown=%t audioEpisodes=%d audioSeasons=%d",
		cmp.Or(result.slug, "(nothing)"), result.pending, len(result.markers), cmp.Or(result.mode, "-"), result.recapKnown, len(audio.episodes), len(audio.seasons))

	out := M{}
	for id, marker := range result.markers {
		row := M{"kind": nil, "recap": marker.Recap && set.recap, "audio": nil}
		if set.shown[marker.Kind] {
			row["kind"] = marker.Kind
		}
		out[id] = row
	}
	for id, kind := range audio.episodes {
		row, ok := out[id].(M)
		if !ok {
			row = M{"kind": nil, "recap": false}
			out[id] = row
		}
		row["audio"] = anime.CollapseAudio(kind, set.separateDualAudio)
	}
	seasons := M{}
	for id, kind := range audio.seasons {
		seasons[id] = M{"audio": anime.CollapseAudio(kind, set.separateDualAudio)}
	}
	writeJSON(w, http.StatusOK, M{
		"enabled": true, "matched": result.slug != "", "slug": nilIfEmpty(result.slug), "title": nilIfEmpty(result.title),
		"pending": result.pending, "recapKnown": result.recapKnown, "episodes": out, "seasons": seasons,
		"placement": set.placement, "labelStyle": set.labelStyle,
	})
}

// animeSeries reads the series a request names as the caller, answering the
// request itself when there's none. The 404 carries an error so the apps can
// tell an unknown series from a missing route.
func (s *Server) animeSeries(w http.ResponseWriter, r *http.Request, id string) (AnimeItem, bool) {
	if id == "" {
		writeJSON(w, http.StatusBadRequest, M{"error": "Missing or malformed seriesId"})
		return AnimeItem{}, false
	}
	items, err := s.anime.catalog.AnimeItems(r.Context(), callerOf(r), []string{id})
	if err != nil {
		logError(r, "anime markers: read the series", err)
		writeJSON(w, http.StatusBadGateway, M{"error": s.name + " didn't answer."})
		return AnimeItem{}, false
	}
	for _, it := range items {
		if it.ID == id && it.Type == "show" {
			return it, true
		}
	}
	s.anime.src.Log.Printf("  -> 404, no series in the library has that id")
	writeJSON(w, http.StatusNotFound, M{"error": "Series not found"})
	return AnimeItem{}, false
}

// handleAnimeItems is the subbed or dubbed verdict for each of up to 200 items.
func (s *Server) handleAnimeItems(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()
	set := s.animeSettings(ctx)
	if !set.audio {
		writeJSON(w, http.StatusOK, M{"enabled": false, "items": M{}})
		return
	}
	var ids []string
	for _, id := range strings.Split(r.URL.Query().Get("ids"), ",") {
		if id = strings.TrimSpace(id); id != "" && len(ids) < 200 {
			ids = append(ids, id)
		}
	}
	if len(ids) == 0 {
		writeJSON(w, http.StatusBadRequest, M{"error": "Missing ids"})
		return
	}
	items, err := s.anime.catalog.AnimeItems(ctx, callerOf(r), ids)
	if err != nil {
		logError(r, "anime markers: read the items", err)
		writeJSON(w, http.StatusBadGateway, M{"error": s.name + " didn't answer."})
		return
	}
	byID := map[string]AnimeItem{}
	var seriesIDs []string
	for _, it := range items {
		byID[it.ID] = it
		if it.Type == "episode" && it.SeriesID != "" && !slices.Contains(seriesIDs, it.SeriesID) {
			seriesIDs = append(seriesIDs, it.SeriesID)
		}
	}
	// An episode carries almost none of the genres and ids that say anime,
	// so it's judged by its series too.
	series := map[string]AnimeItem{}
	if len(seriesIDs) > 0 {
		shows, err := s.anime.catalog.AnimeItems(ctx, callerOf(r), seriesIDs)
		if err != nil {
			logError(r, "anime markers: read the series", err)
		}
		for _, show := range shows {
			series[show.ID] = show
		}
	}

	answer := M{}
	var notFound, moviesSkipped, notCandidate, noAudioTags, seriesOrFolder int
	for _, id := range ids {
		it, ok := byID[id]
		switch {
		case !ok:
			notFound++
		// A series or a folder has no file of its own, so no audio to read.
		case it.Type != "movie" && it.Type != "episode":
			seriesOrFolder++
		// Movies are opted in on their own, since a mixed library is where a
		// pill most likely lands on something that isn't anime.
		case it.Type == "movie" && !set.movies:
			moviesSkipped++
		default:
			var show *AnimeItem
			if found, ok := series[it.SeriesID]; ok {
				show = &found
			}
			kind := anime.ClassifyAudio(it.AudioLanguages)
			switch {
			case !audioCandidate(it, show, set):
				notCandidate++
			case kind == "":
				noAudioTags++
			default:
				answer[it.ID] = M{"audio": anime.CollapseAudio(kind, set.separateDualAudio)}
			}
		}
	}
	s.anime.src.Log.Printf("items  asked about %d, answered %d (skipped: %d series/folders, %d movies with the movie option off, "+
		"%d outside the chosen libraries or not anime, %d with no audio language tags, %d unknown ids)",
		len(ids), len(answer), seriesOrFolder, moviesSkipped, notCandidate, noAudioTags, notFound)
	writeJSON(w, http.StatusOK, M{"enabled": true, "items": answer, "placement": set.placement, "labelStyle": set.labelStyle})
}

// handleAnimeDiagnostics says how many series matched a show, how many wait on
// a fetch and which matched nothing, for the admin page and bug reports.
func (s *Server) handleAnimeDiagnostics(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()
	set := s.animeSettings(ctx)
	src := s.anime.src
	src.EnsureCatalog(ctx)
	index, catalogShows := src.Catalog()
	if index == nil {
		writeJSON(w, http.StatusOK, M{"enabled": set.filler, "catalogLoaded": false,
			"error": "The AnimeFillerList show catalog could not be downloaded. Check that the server can reach animefillerlist.com."})
		return
	}
	candidates, err := s.anime.catalog.AnimeSeries(ctx, set.libraries)
	if err != nil {
		logError(r, "anime markers: list the series", err)
		writeJSON(w, http.StatusBadGateway, M{"error": s.name + " didn't list its series."})
		return
	}
	fresh := src.Tables.FreshSlugs(set.maxAge)
	matches := []M{}
	unmatched := []string{}
	pending := map[string]bool{}
	for _, series := range candidates {
		show, _, _ := s.anime.matchSeries(series)
		if show == nil {
			unmatched = append(unmatched, cmp.Or(series.Title, series.ID))
			continue
		}
		if !fresh[show.Slug] {
			pending[show.Slug] = true
		}
		matches = append(matches, M{"series": series.Title, "slug": show.Slug, "title": show.Title, "cached": fresh[show.Slug]})
	}
	shows, episodes, flagged := src.Tables.Counts()
	var downloadedAt any
	if at, ok := src.CatalogSavedAt(); ok {
		downloadedAt = at
	}
	writeJSON(w, http.StatusOK, M{
		"enabled": set.filler, "catalogLoaded": true, "catalogShows": catalogShows, "catalogDownloadedAt": downloadedAt,
		"libraryFilterActive": set.libraries != nil, "configuredLibraryCount": len(set.libraries), "resolvedLibraryCount": len(set.libraries),
		"seriesConsidered": len(candidates), "seriesMatched": len(matches), "showsPending": len(pending),
		"showsCached": shows, "episodesClassified": episodes, "episodesFlagged": flagged,
		// Capped so a large library can't turn the admin page into a wall of text.
		"matches": matches[:min(200, len(matches))], "unmatchedCount": len(unmatched), "unmatched": unmatched[:min(50, len(unmatched))],
	})
}

// handleAnimePreview shows which of a series' episodes each marker landed on.
func (s *Server) handleAnimePreview(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()
	set := s.animeSettings(ctx)
	src := s.anime.src
	series, ok := s.animeSeries(w, r, strings.TrimSpace(r.URL.Query().Get("seriesId")))
	if !ok {
		return
	}
	src.EnsureCatalog(ctx)
	src.Log.Printf("preview  series=%q (from the admin page)", series.Title)
	if err := s.anime.refreshAliases(ctx, []AnimeItem{series}); err != nil {
		logError(r, "anime markers: look up titles", err)
	}
	show, aliasKey, _ := s.anime.matchSeries(series)
	if show == nil {
		src.Log.Printf("  -> matched nothing on AnimeFillerList, by title or by provider id")
		writeJSON(w, http.StatusOK, M{"series": series.Title, "matched": false,
			"hint": "No AnimeFillerList show has this title, and none of its provider ids lead to one. Check its AniList, AniDB or MyAnimeList id."})
		return
	}
	if aliasKey != "" {
		src.Log.Printf("  -> matched %s through %s", show.Slug, aliasKey)
	}
	entry := src.Tables.Get(show.Slug, set.maxAge)
	if entry == nil {
		writeJSON(w, http.StatusOK, M{"series": series.Title, "matched": true, "slug": show.Slug, "title": show.Title, "pending": true,
			"hint": "Matched, but not fetched yet. Use Fetch now, or wait for the nightly sync."})
		return
	}
	episodes, err := s.anime.catalog.AnimeEpisodes(ctx, callerOf(r), series.ID)
	if err != nil {
		logError(r, "anime markers: list the episodes", err)
		writeJSON(w, http.StatusBadGateway, M{"error": s.name + " didn't list the episodes."})
		return
	}
	matched := matchEpisodes(episodes, entry)

	// One row per episode, with how many library items sit behind it.
	rows := []M{}
	marked, filler := 0, 0
	for _, slot := range matched.slots {
		first := slot.items[0]
		marker := matched.bySlot[slot.key]
		row := M{"season": slot.key.Season, "index": slot.key.Index, "absolute": slot.number, "siteNumber": nil,
			"name": first.Title, "kind": nil, "recap": false, "marked": marker != nil, "copies": len(slot.items)}
		if marker != nil {
			// The site's number differs from the absolute one only when the air dates did the pairing.
			row["siteNumber"], row["kind"], row["recap"] = marker.Number, marker.Kind, marker.Recap
			marked++
			if marker.Kind == anime.Filler {
				filler++
			}
		}
		rows = append(rows, row)
	}
	slices.SortStableFunc(rows, func(a, b M) int {
		return cmp.Or(cmp.Compare(a["season"].(int), b["season"].(int)), cmp.Compare(a["index"].(int), b["index"].(int)))
	})
	writeJSON(w, http.StatusOK, M{
		"series": series.Title, "matched": true, "slug": show.Slug, "title": show.Title, "pending": false,
		"recapKnown": entry.RecapMalID != nil, "numbering": matched.numbering, "matchedBy": matched.result.Mode,
		"datedEpisodes": matched.result.DatedEpisodes, "numberAgreements": matched.result.NumberAgreements,
		"airDatesKnown": entry.Format >= anime.CurrentFormat, "showEpisodes": len(entry.Episodes),
		"libraryEpisodes": len(rows), "libraryFiles": matched.items, "markedEpisodes": marked, "fillerEpisodes": filler, "episodes": rows,
	})
}

// handleAnimeRefresh fetches one series' show page now instead of waiting for the nightly sync.
func (s *Server) handleAnimeRefresh(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()
	src := s.anime.src
	s.animeSettings(ctx)
	series, ok := s.animeSeries(w, r, strings.TrimSpace(r.URL.Query().Get("seriesId")))
	if !ok {
		return
	}
	src.EnsureCatalog(ctx)
	if err := s.anime.refreshAliases(ctx, []AnimeItem{series}); err != nil {
		logError(r, "anime markers: look up titles", err)
	}
	show, _, _ := s.anime.matchSeries(series)
	if show == nil {
		writeJSON(w, http.StatusOK, M{"matched": false, "series": series.Title})
		return
	}
	episodes, ok := src.FetchEpisodes(ctx, show.Slug)
	if !ok || len(episodes) == 0 {
		slog.WarnContext(ctx, "anime markers: the refresh returned nothing usable", "slug", show.Slug)
		writeJSON(w, http.StatusOK, M{"matched": true, "slug": show.Slug, "fetched": false})
		return
	}
	src.Tables.Set(&anime.CacheEntry{Slug: show.Slug, Title: show.Title, Episodes: episodes})
	if err := src.Tables.Flush(); err != nil {
		logError(r, "anime markers: save the cache", err)
	}
	writeJSON(w, http.StatusOK, M{"matched": true, "slug": show.Slug, "fetched": true, "episodes": len(episodes)})
}

// handleAnimeLog is the tail of the marker log, so a reproduction can be read
// on the admin page.
func (s *Server) handleAnimeLog(w http.ResponseWriter, r *http.Request) {
	s.animeSettings(r.Context())
	log := s.anime.src.Log
	lines, err := strconv.Atoi(r.URL.Query().Get("lines"))
	if err != nil {
		lines = 200
	}
	var size any
	if n, ok := log.Size(); ok {
		size = n
	}
	writeJSON(w, http.StatusOK, M{"enabled": log.On.Load(), "path": log.Path(), "sizeBytes": size, "lines": log.Tail(min(max(lines, 1), 2000))})
}

func (s *Server) handleAnimeClearLog(w http.ResponseWriter, _ *http.Request) {
	writeJSON(w, http.StatusOK, M{"cleared": s.anime.src.Log.Clear()})
}

// handleAnimeClearCache drops every cached show table so the next sync
// fetches them all again. The show catalog is kept.
func (s *Server) handleAnimeClearCache(w http.ResponseWriter, r *http.Request) {
	n, err := s.anime.src.Tables.Clear()
	if err != nil {
		logError(r, "anime markers: clear the cache", err)
	}
	writeJSON(w, http.StatusOK, M{"cleared": n})
}

// handleAnimeSearch finds the series a name means, shaped like Jellyfin's
// item search: an exact title first, then the first that contains the name.
func (s *Server) handleAnimeSearch(w http.ResponseWriter, r *http.Request) {
	want := anime.Normalize(r.URL.Query().Get("term"))
	items := []M{}
	if want == "" {
		writeJSON(w, http.StatusOK, M{"Items": items, "TotalRecordCount": 0})
		return
	}
	all, err := s.anime.catalog.AnimeSeries(r.Context(), nil)
	if err != nil {
		logError(r, "anime markers: list the series", err)
		writeError(w, http.StatusBadGateway, s.name+" didn't list its series.")
		return
	}
	var best *AnimeItem
	for i, series := range all {
		title := anime.Normalize(series.Title)
		if title == want {
			best = &all[i]
			break
		}
		if best == nil && strings.Contains(title, want) {
			best = &all[i]
		}
	}
	if best != nil {
		items = append(items, M{"Id": best.ID, "Name": best.Title, "Type": "Series", "ProductionYear": best.Year})
	}
	writeJSON(w, http.StatusOK, M{"Items": items, "TotalRecordCount": len(items)})
}
