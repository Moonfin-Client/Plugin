package api

import (
	"bytes"
	"cmp"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"encoding/xml"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"maps"
	"math"
	"net/http"
	"net/url"
	"regexp"
	"slices"
	"strconv"
	"strings"
	"sync"
	"time"
)

// RowCatalog is what a host gives the custom rows. Hosts whose catalog can't
// find titles by provider id leave it out, and the rows stay off there.
type RowCatalog interface {
	// LibraryGUIDs maps the imdb://, tmdb:// and tvdb:// guids of the movies,
	// or the shows when series is set, in the libraries the caller can see to
	// their item ids.
	LibraryGUIDs(ctx context.Context, caller Identity, series bool) (map[string]string, error)
}

// rowItem is a title in the shape the Jellyfin plugin sends.
type rowItem struct {
	Name           string   `json:"name"`
	Type           string   `json:"type"`
	ProductionYear int      `json:"productionYear,omitempty"`
	Rank           int      `json:"rank,omitempty"`
	ProviderIDs    rowIDs   `json:"providerIds"`
	UserRating     string   `json:"userRating,omitempty"`
	Rating         float64  `json:"rating,omitempty"`
	OfficialRating string   `json:"officialRating,omitempty"`
	PosterURL      string   `json:"posterUrl,omitempty"`
	BackdropURL    string   `json:"backdropUrl,omitempty"`
	Overview       string   `json:"overview,omitempty"`
	Genres         []string `json:"genres,omitempty"`
	RunTimeTicks   int64    `json:"runTimeTicks,omitempty"`
	LocalItemID    string   `json:"localItemId,omitempty"`
}

type rowIDs struct {
	Imdb string `json:"Imdb,omitempty"`
	Tmdb string `json:"Tmdb,omitempty"`
	Tvdb string `json:"Tvdb,omitempty"`
}

type officialList struct {
	Slug      string `json:"slug"`
	Name      string `json:"name"`
	Mediatype string `json:"mediatype,omitempty"`
	Count     int    `json:"count"`
}

type officialItem struct {
	ID             int64  `json:"id,omitempty"`
	Name           string `json:"name"`
	Type           string `json:"type"`
	ProductionYear int    `json:"productionYear,omitempty"`
	Rank           int    `json:"rank,omitempty"`
	Poster         string `json:"poster,omitempty"`
	ProviderIDs    rowIDs `json:"providerIds"`
}

const (
	rowTTL        = 24 * time.Hour
	rowTitleTTL   = 7 * 24 * time.Hour
	rowLibraryTTL = 10 * time.Minute
	mdbUserMax    = 500
	tmdbParallel  = 6
)

const noOfficialLists = "MDBList's official lists need a server-wide MDBList key and Sync MDBList official lists turned on."

var (
	rowSources    = []string{"imdb", "tmdb", "tmdb_chart", "tmdb_discover", "justwatch", "mdblist", "letterboxd"}
	tmdbChartPath = regexp.MustCompile(`^[a-z_]+(/[a-z_]+){1,2}$`)
	// mdbSlug can't start with a dot, so ".." can't climb out of the list path.
	mdbSlug     = regexp.MustCompile(`^[A-Za-z0-9_-][A-Za-z0-9._-]{0,99}$`)
	languageTag = regexp.MustCompile(`^([A-Za-z]{2,3})(?:-([A-Za-z]{4}))?(?:-([A-Za-z]{2}|[0-9]{3}))?$`)
	imdbCharts  = map[string]string{
		"imdb_top_250_movies":        "TOP_RATED_MOVIES",
		"imdb_top_250_tv_shows":      "TOP_RATED_TV_SHOWS",
		"imdb_most_popular_movies":   "MOST_POPULAR_MOVIES",
		"imdb_most_popular_tv_shows": "MOST_POPULAR_TV_SHOWS",
		"imdb_lowest_rated_movies":   "LOWEST_RATED_MOVIES",
		"imdb_top_english_movies":    "TOP_RATED_ENGLISH_MOVIES",
	}
)

func (s *Server) customRowRoutes() {
	if _, ok := s.catalog.(RowCatalog); !ok {
		return
	}
	s.mux.HandleFunc("GET /Moonfin/CustomRows/Items", s.user(s.handleCustomRowItems))
	s.mux.HandleFunc("GET /Moonfin/MdbList/Lists", s.user(s.handleOfficialLists))
	s.mux.HandleFunc("GET /Moonfin/MdbList/Lists/{slug}/Items", s.user(s.handleOfficialListItems))
}

func (s *Server) handleCustomRowItems(w http.ResponseWriter, r *http.Request) {
	q := r.URL.Query()
	source, typ := strings.ToLower(strings.TrimSpace(q.Get("source"))), strings.ToLower(strings.TrimSpace(q.Get("type")))
	if source == "" || typ == "" || strings.TrimSpace(q.Get("params")) == "" {
		writeError(w, http.StatusBadRequest, "Missing required parameters: source, type, params")
		return
	}
	params := map[string]string{}
	if json.Unmarshal([]byte(q.Get("params")), &params) != nil {
		writeError(w, http.StatusBadRequest, "params must be a JSON object of string values")
		return
	}
	if !slices.Contains(rowSources, source) {
		writeError(w, http.StatusBadRequest, "Unsupported custom row source: "+source)
		return
	}
	// Older apps force a refresh with a "_nocache" param, which mustn't open a cache entry of its own.
	refresh := strings.EqualFold(q.Get("refresh"), "true")
	for k := range params {
		if strings.HasPrefix(k, "_") {
			refresh = true
			delete(params, k)
		}
	}
	resolved, err := s.resolve(r, "global", false)
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	cfg := s.config(r.Context())
	tmdbKey, mdbKey := strings.TrimSpace(str(resolved["tmdbApiKey"])), strings.TrimSpace(str(resolved["mdblistApiKey"]))
	lang := normalizeLanguage(q.Get("language"))
	canonical, _ := json.Marshal(params)
	sum := sha256.Sum256(canonical)
	cacheKey := "row:" + source + ":" + typ + ":" + hex.EncodeToString(sum[:]) + ":" + lang
	// A private list is only visible to its owner's key, so a row fetched with the caller's own key isn't shared.
	if (tmdbKey != "" && tmdbKey != cfg.TmdbAPIKey) || (source == "mdblist" && mdbKey != "" && mdbKey != cfg.MdblistAPIKey) {
		cacheKey += ":" + strconv.Itoa(callerOf(r).UserID)
	}

	// The work goes on when the app stops waiting, so its next try finds the row cached.
	ctx := context.WithoutCancel(r.Context())
	items, err := cachedList(&s.ext.cache, cacheKey, refresh, func() ([]rowItem, error) {
		var items []rowItem
		var err error
		switch source {
		case "imdb":
			items, err = s.imdbChart(ctx, typ)
		case "tmdb":
			items, err = s.tmdbList(ctx, tmdbKey, typ, params["id"], lang)
		case "tmdb_chart":
			items, err = s.tmdbChart(ctx, tmdbKey, typ, lang)
		case "tmdb_discover":
			items, err = s.tmdbDiscover(ctx, tmdbKey, typ, params["region"], lang, time.Now().UTC())
		case "justwatch":
			items, err = s.justWatchChart(ctx, mdbKey, typ, params["region"])
		case "mdblist":
			items, err = s.mdbUserList(ctx, mdbKey, params, lang)
		case "letterboxd":
			items, err = s.letterboxdDiary(ctx, typ, params["user"])
		}
		if err == nil {
			s.fillFromTmdb(ctx, tmdbKey, lang, items)
		}
		return items, err
	})
	if err != nil {
		slog.WarnContext(ctx, "custom row", "source", source, "type", typ, "error", err)
		writeJSON(w, http.StatusOK, M{"success": false, "error": err.Error()})
		return
	}
	if strings.EqualFold(q.Get("includeLocalItemIds"), "true") {
		items = s.withLocalItemIDs(ctx, callerOf(r), items)
	}
	writeJSON(w, http.StatusOK, M{"success": true, "items": items})
}

// rowLocks lets one request fetch a list or read a library while the others
// asking for the same one wait for its answer. A key's lock is dropped once
// nobody holds or waits on it, since callers choose the keys.
var rowLocks = struct {
	sync.Mutex
	m map[string]*rowLock
}{m: map[string]*rowLock{}}

type rowLock struct {
	sync.Mutex
	refs int
}

func lockRow(key string) func() {
	rowLocks.Lock()
	l := rowLocks.m[key]
	if l == nil {
		l = &rowLock{}
		rowLocks.m[key] = l
	}
	l.refs++
	rowLocks.Unlock()
	l.Lock()
	return func() {
		l.Unlock()
		rowLocks.Lock()
		if l.refs--; l.refs == 0 {
			delete(rowLocks.m, key)
		}
		rowLocks.Unlock()
	}
}

// cachedList answers a list from the cache, or fetches it. An empty list
// isn't kept, since it's more often a provider's hiccup than a list with
// nothing on it.
func cachedList[T any](c *ttlCache, key string, refresh bool, fetch func() ([]T, error)) ([]T, error) {
	defer lockRow(key)()
	if v, ok := c.get(key, rowTTL); ok && !refresh {
		return v.([]T), nil
	}
	items, err := fetch()
	if err == nil && len(items) > 0 {
		c.set(key, items)
	}
	return items, err
}

// withLocalItemIDs gives each title the id of the caller's own copy, matched
// by TMDB id first, then IMDb, then TVDB, as the Jellyfin plugin matches.
func (s *Server) withLocalItemIDs(ctx context.Context, caller Identity, items []rowItem) []rowItem {
	catalog, _ := s.catalog.(RowCatalog)
	out := slices.Clone(items)
	indexes := map[bool]map[string]string{}
	for i := range out {
		series := out[i].Type == "Series"
		if _, ok := indexes[series]; !ok {
			indexes[series] = s.libraryIndex(ctx, catalog, caller, series)
		}
		ids := out[i].ProviderIDs
		for _, g := range [][2]string{{"tmdb://", ids.Tmdb}, {"imdb://", ids.Imdb}, {"tvdb://", ids.Tvdb}} {
			if id := indexes[series][g[0]+g[1]]; g[1] != "" && id != "" {
				out[i].LocalItemID = id
				break
			}
		}
	}
	return out
}

// libraryIndex keeps the caller's guids for a few minutes, since a Home
// screen asks for its rows all at once. A library that can't be read matches
// nothing.
func (s *Server) libraryIndex(ctx context.Context, catalog RowCatalog, caller Identity, series bool) map[string]string {
	key := fmt.Sprintf("row-library:%d:%t", caller.UserID, series)
	defer lockRow(key)()
	if v, ok := s.ext.cache.get(key, rowLibraryTTL); ok {
		return v.(map[string]string)
	}
	index, err := catalog.LibraryGUIDs(ctx, caller, series)
	if err != nil {
		slog.WarnContext(ctx, "custom rows: read the caller's library", "error", err)
		return nil
	}
	s.ext.cache.set(key, index)
	return index
}

func (s *Server) imdbChart(ctx context.Context, typ string) ([]rowItem, error) {
	chart, ok := imdbCharts[typ]
	if !ok {
		return nil, errors.New("Invalid chart key: " + typ)
	}
	query := `{ chartTitles(chart: {chartType: ` + chart + `}, first: 250) { edges { node { id titleText { text } primaryImage { url } releaseYear { year } titleType { id } } } } }`
	body, _ := json.Marshal(M{"query": query})
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, s.ext.imdbBase, bytes.NewReader(body))
	if err != nil {
		return nil, err
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Accept", "application/json")
	// IMDb's GraphQL only answers requests that look like they come from its own site.
	req.Header.Set("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")
	req.Header.Set("Origin", "https://www.imdb.com")
	req.Header.Set("Referer", "https://www.imdb.com/")
	var v struct {
		Data struct {
			ChartTitles struct {
				Edges []struct {
					Node struct {
						ID           string                `json:"id"`
						TitleText    struct{ Text string } `json:"titleText"`
						PrimaryImage struct{ URL string }  `json:"primaryImage"`
						ReleaseYear  struct{ Year int }    `json:"releaseYear"`
						TitleType    struct {
							ID string `json:"id"`
						} `json:"titleType"`
					} `json:"node"`
				} `json:"edges"`
			} `json:"chartTitles"`
		} `json:"data"`
	}
	status, raw, err := s.send(req)
	if err != nil || status/100 != 2 {
		return nil, errors.New(providerError("IMDb", status, err))
	}
	if err := json.Unmarshal(raw, &v); err != nil {
		return nil, errors.New("IMDb sent an unreadable chart.")
	}
	items := []rowItem{}
	for _, e := range v.Data.ChartTitles.Edges {
		n := e.Node
		if n.ID == "" {
			continue
		}
		item := rowItem{Name: cmp.Or(n.TitleText.Text, "Unknown Title"), Type: "Movie", ProductionYear: n.ReleaseYear.Year,
			Rank: len(items) + 1, PosterURL: imdbPoster(n.PrimaryImage.URL), ProviderIDs: rowIDs{Imdb: n.ID}}
		if n.TitleType.ID == "tvSeries" || n.TitleType.ID == "tvMiniSeries" {
			item.Type = "Series"
		}
		items = append(items, item)
	}
	return items, nil
}

// imdbPoster asks IMDb's image host for a 500 pixel wide poster in place of
// the original, which runs to a few megabytes.
func imdbPoster(u string) string {
	if i := strings.LastIndex(u, "._V1_."); i >= 0 {
		return u[:i] + "._V1_UX500_." + u[i+len("._V1_."):]
	}
	return u
}

func (s *Server) tmdbList(ctx context.Context, key, typ, id, lang string) ([]rowItem, error) {
	switch {
	case key == "":
		return nil, errors.New("TMDB API key is not configured.")
	case id == "":
		return nil, errors.New("TMDB requires id parameter.")
	case !providerID.MatchString(id):
		return nil, errors.New("Invalid TMDB id.")
	}
	path := "list/" + id
	if typ == "movie_collection" {
		path = "collection/" + id
	}
	var body struct {
		Items []M `json:"items"`
		Parts []M `json:"parts"`
	}
	if msg := s.tmdb(ctx, key, path, langQuery(lang), &body); msg != "" {
		return nil, errors.New(msg)
	}
	return tmdbRowItems(append(body.Items, body.Parts...), "movie"), nil
}

// tmdbChart reads a chart by its TMDB path, such as movie/popular or
// trending/tv/day.
func (s *Server) tmdbChart(ctx context.Context, key, path, lang string) ([]rowItem, error) {
	if key == "" {
		return nil, errors.New("TMDB API key is not configured.")
	}
	if !tmdbChartPath.MatchString(path) {
		return nil, errors.New("Invalid TMDB chart: " + path)
	}
	var body struct {
		Results []M `json:"results"`
	}
	if msg := s.tmdb(ctx, key, path, langQuery(lang), &body); msg != "" {
		return nil, errors.New(msg)
	}
	mediaType := "movie"
	if slices.Contains(strings.Split(path, "/"), "tv") {
		mediaType = "tv"
	}
	return tmdbRowItems(body.Results, mediaType), nil
}

// discoverProviders are TMDB's ids for each streaming service. A service can
// sit under more than one id across regions, and TMDB reads a pipe as "or".
var discoverProviders = map[string]string{
	"netflix": "8", "prime_video": "9|119", "disney_plus": "337", "hbo_max": "1899|384", "apple_tv": "350", "paramount_plus": "531|2303",
}

var regionCode = regexp.MustCompile(`^[A-Za-z]{2}$`)

// chartRegion is the two-letter region an app sent, upper-cased, or US.
func chartRegion(region string) string {
	if r := strings.TrimSpace(region); regionCode.MatchString(r) {
		return strings.ToUpper(r)
	}
	return "US"
}

// discoverPaths mirrors shared/Tmdb/TmdbDiscoverCharts.cs, so every host
// serves the same rows. It's nil for a chart this server doesn't know.
func discoverPaths(name, region string, today time.Time) []string {
	r := chartRegion(region)
	if service, ok := strings.CutPrefix(name, "provider_"); ok {
		ids, ok := discoverProviders[service]
		if !ok {
			return nil
		}
		q := "with_watch_providers=" + ids + "&watch_region=" + r + "&with_watch_monetization_types=flatrate&sort_by=popularity.desc"
		return []string{"discover/movie?" + q, "discover/tv?" + q}
	}
	switch name {
	case "korean_dramas":
		return []string{"discover/tv?with_original_language=ko&with_genres=18&sort_by=popularity.desc"}
	case "anime":
		return []string{"discover/tv?with_original_language=ja&with_genres=16&sort_by=popularity.desc"}
	case "new_digital_movies":
		return []string{"discover/movie?with_release_type=4&region=" + r + "&release_date.gte=" + today.AddDate(0, 0, -60).Format(time.DateOnly) +
			"&release_date.lte=" + today.Format(time.DateOnly) + "&sort_by=popularity.desc"}
	case "hidden_gems":
		return []string{"discover/movie?vote_average.gte=7.5&vote_count.gte=300&vote_count.lte=2000&without_genres=99,10770&sort_by=vote_average.desc"}
	}
	return nil
}

// tmdbDiscover reads one of Moonbase's named discover charts. A streaming
// service's chart reads its movies and its shows and merges them by popularity.
func (s *Server) tmdbDiscover(ctx context.Context, key, name, region, lang string, today time.Time) ([]rowItem, error) {
	if key == "" {
		return nil, errors.New("TMDB API key is not configured.")
	}
	paths := discoverPaths(name, region, today)
	if paths == nil {
		return nil, errors.New("Unsupported TMDB discover chart: " + name)
	}
	var results []M
	for _, p := range paths {
		path, query, _ := strings.Cut(p, "?")
		q, _ := url.ParseQuery(query)
		maps.Copy(q, langQuery(lang))
		var body struct {
			Results []M `json:"results"`
		}
		if msg := s.tmdb(ctx, key, path, q, &body); msg != "" {
			return nil, errors.New(msg)
		}
		kind := "movie"
		if strings.HasSuffix(path, "/tv") {
			kind = "tv"
		}
		for _, r := range body.Results {
			if text(r, "media_type") == "" {
				r["media_type"] = kind
			}
			results = append(results, r)
		}
	}
	if len(paths) > 1 {
		slices.SortStableFunc(results, func(a, b M) int {
			pa, _ := num(a["popularity"])
			pb, _ := num(b["popularity"])
			return cmp.Compare(pb, pa)
		})
	}
	return tmdbRowItems(results, "movie"), nil
}

// justWatchCountry is the country MDBList's JustWatch charts cover for a
// region, or US for the rest.
func justWatchCountry(region string) string {
	switch r := chartRegion(region); r {
	case "US", "GB", "CA", "AU", "DE":
		return r
	case "UK":
		return "GB"
	}
	return "US"
}

// justWatchChart reads a JustWatch streaming chart through MDBList, which
// matches each title to its ids.
func (s *Server) justWatchChart(ctx context.Context, key, typ, region string) ([]rowItem, error) {
	switch {
	case key == "":
		return nil, errors.New("MDBList API key is not configured.")
	case typ != "movie" && typ != "show":
		return nil, errors.New("Unsupported JustWatch chart: " + typ)
	}
	q := url.Values{"apikey": {key}, "country": {justWatchCountry(region)}, "period": {"daily"}, "x": {"20"}}
	var page struct {
		Results []any `json:"results"`
	}
	status, err := s.getJSON(ctx, s.ext.mdblistBase+"/justwatch/streaming-charts/"+typ+"?"+q.Encode(), "", &page)
	if err != nil || status/100 != 2 {
		return nil, errors.New(providerError("MDBList", status, err))
	}
	// A title MDBList couldn't match carries no ids, so it can't become a card.
	items := []rowItem{}
	for _, it := range appendMdb(nil, page.Results, typ) {
		if it.ProviderIDs.Tmdb == "" && it.ProviderIDs.Imdb == "" {
			continue
		}
		it.Rank = len(items) + 1
		items = append(items, it)
	}
	return items, nil
}

// tmdbRowItems reads TMDB results. Only trending and lists mark each one's
// media type, so the rest take fallback.
func tmdbRowItems(results []M, fallback string) []rowItem {
	items := []rowItem{}
	for _, r := range results {
		mediaType := cmp.Or(text(r, "media_type"), fallback)
		id, ok := num(r["id"])
		if !ok || mediaType == "person" {
			continue
		}
		item := rowItem{Name: cmp.Or(text(r, "title"), text(r, "name")), Type: "Movie", Rank: len(items) + 1,
			ProductionYear: yearOf(cmp.Or(text(r, "release_date"), text(r, "first_air_date"))),
			PosterURL:      text(r, "poster_path"), BackdropURL: text(r, "backdrop_path"),
			ProviderIDs: rowIDs{Tmdb: strconv.FormatInt(int64(id), 10)}}
		if mediaType == "tv" {
			item.Type = "Series"
		}
		items = append(items, item)
	}
	return items
}

func (s *Server) mdbUserList(ctx context.Context, key string, params map[string]string, lang string) ([]rowItem, error) {
	user, list := strings.TrimSpace(params["username"]), strings.TrimSpace(params["listname"])
	switch {
	case key == "":
		return nil, errors.New("MDBList API key is not configured.")
	case user == "" || list == "":
		return nil, errors.New("MDBList requires both username and listname parameters.")
	case !mdbSlug.MatchString(user) || !mdbSlug.MatchString(list):
		return nil, errors.New("Invalid MDBList list.")
	}
	return s.mdbListItems(ctx, key, "/lists/"+user+"/"+list+"/items", lang, mdbUserMax, params["mediatype"])
}

// mdbListItems reads an MDBList list a page at a time, up to limit titles in
// list order. A list comes as one array, or as pages of movies and shows, and
// only keeps one of those when only is "movie" or "show".
func (s *Server) mdbListItems(ctx context.Context, key, path, lang string, limit int, only string) ([]rowItem, error) {
	q := url.Values{"apikey": {key}, "limit": {"100"}, "append_to_response": {"poster"}}
	if lang != "" {
		q.Set("lang", lang)
	}
	items := []rowItem{}
	for len(items) < limit {
		var page any
		status, err := s.getJSON(ctx, s.ext.mdblistBase+path+"?"+q.Encode(), "", &page)
		if err != nil || status/100 != 2 {
			return nil, errors.New(providerError("MDBList", status, err))
		}
		fetched, cursor := 0, ""
		switch p := page.(type) {
		case []any:
			fetched = len(p)
			items = appendMdb(items, p, "movie")
		case M:
			movies, _ := p["movies"].([]any)
			shows, _ := p["shows"].([]any)
			fetched = len(movies) + len(shows)
			if only != "show" {
				items = appendMdb(items, movies, "movie")
			}
			if only != "movie" {
				items = appendMdb(items, shows, "show")
			}
			pagination, _ := p["pagination"].(M)
			cursor = text(pagination, "next_cursor")
		}
		// A page of only the other type isn't the end of the list.
		if cursor == "" || fetched == 0 {
			break
		}
		q.Set("cursor", cursor)
	}
	// Movies and shows come in lists of their own, so their ranks put them back in one order.
	slices.SortStableFunc(items, func(a, b rowItem) int { return cmp.Compare(rankOrLast(a.Rank), rankOrLast(b.Rank)) })
	items = items[:min(len(items), limit)]
	for i := range items {
		items[i].Rank = i + 1
	}
	return items, nil
}

func appendMdb(items []rowItem, list []any, bucket string) []rowItem {
	for _, raw := range list {
		m, _ := raw.(M)
		if m == nil {
			continue
		}
		ids, _ := m["ids"].(M)
		item := rowItem{Name: cmp.Or(text(m, "title"), "Unknown"), Type: "Movie", PosterURL: tmdbPath(text(m, "poster")),
			ProviderIDs: rowIDs{Imdb: cmp.Or(text(ids, "imdb"), text(m, "imdb_id")), Tmdb: cmp.Or(idText(ids["tmdb"]), idText(m["id"])),
				Tvdb: cmp.Or(idText(ids["tvdb"]), idText(m["tvdb_id"]))}}
		// A JustWatch chart row names its year "year".
		if n, ok := num(m["release_year"]); ok {
			item.ProductionYear = int(n)
		} else if n, ok := num(m["year"]); ok {
			item.ProductionYear = int(n)
		}
		if n, ok := num(m["rank"]); ok {
			item.Rank = int(n)
		}
		if slices.Contains([]string{"show", "shows", "series", "tv"}, strings.ToLower(cmp.Or(text(m, "mediatype"), bucket))) {
			item.Type = "Series"
		}
		items = append(items, item)
	}
	return items
}

func (s *Server) letterboxdDiary(ctx context.Context, typ, user string) ([]rowItem, error) {
	user = strings.ToLower(strings.TrimSpace(user))
	switch {
	case typ == "user_list" || typ == "watchlist" || typ == "films":
		return nil, errors.New("Direct HTML scraping of Letterboxd watchlists and lists is disabled due to their Terms of Service. Please import your list into MDBList and use the MDBList custom row source instead.")
	case user == "":
		return nil, errors.New("Letterboxd requires user parameter.")
	case !providerID.MatchString(user):
		return nil, errors.New("Invalid Letterboxd user.")
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, s.ext.letterboxdBase+"/"+user+"/rss/", nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("User-Agent", "Moonfin/1.0")
	status, body, err := s.send(req)
	if err != nil || status/100 != 2 {
		return nil, errors.New(providerError("Letterboxd", status, err))
	}
	var feed struct {
		Items []struct {
			Title  string `xml:"filmTitle"`
			Year   string `xml:"filmYear"`
			Rating string `xml:"memberRating"`
			TmdbID string `xml:"movieId"`
		} `xml:"channel>item"`
	}
	if err := xml.Unmarshal(body, &feed); err != nil {
		return nil, errors.New("Letterboxd sent an unreadable feed.")
	}
	items := []rowItem{}
	for _, it := range feed.Items {
		if !providerID.MatchString(it.TmdbID) {
			continue
		}
		item := rowItem{Name: cmp.Or(it.Title, "Unknown"), Type: "Movie", Rank: len(items) + 1, ProviderIDs: rowIDs{Tmdb: it.TmdbID}}
		item.ProductionYear, _ = strconv.Atoi(it.Year)
		if r, err := strconv.ParseFloat(it.Rating, 64); err == nil {
			item.Rating, item.UserRating = r, stars(r)
		}
		items = append(items, item)
	}
	return items, nil
}

// stars writes a Letterboxd rating the way Letterboxd shows it.
func stars(rating float64) string {
	full := int(math.Floor(rating))
	out := strings.Repeat("★", full)
	if rating-float64(full) >= 0.25 {
		out += "½"
	}
	return out
}

type rowTitleInfo struct {
	overview, backdrop, poster, rating, imdb string
	genres                                   []string
	ticks                                    int64
	vote                                     float64
}

// fillFromTmdb gives titles what TMDB holds for them: the TMDB id of an
// IMDb-only title, the description and artwork a focused card shows, and the
// US certification the apps' parental filter judges.
func (s *Server) fillFromTmdb(ctx context.Context, key, lang string, items []rowItem) {
	if key == "" {
		return
	}
	var wg sync.WaitGroup
	gate := make(chan struct{}, tmdbParallel)
	for i := range items {
		wg.Add(1)
		gate <- struct{}{}
		go func() {
			defer func() { <-gate; wg.Done() }()
			s.fillTitle(ctx, key, lang, &items[i])
		}()
	}
	wg.Wait()
}

func (s *Server) fillTitle(ctx context.Context, key, lang string, it *rowItem) {
	typ, id := "movie", it.ProviderIDs.Tmdb
	if it.Type == "Series" {
		typ = "tv"
	}
	if id == "" && providerID.MatchString(it.ProviderIDs.Imdb) {
		found, foundID, ok := s.tmdbFind(ctx, key, it.ProviderIDs.Imdb)
		if !ok || foundID == "" {
			return
		}
		// A title TMDB files as the other kind keeps no TMDB id, so an app doesn't look a show up as a movie.
		if found == typ {
			it.ProviderIDs.Tmdb = foundID
		}
		typ, id = found, foundID
	}
	if !providerID.MatchString(id) {
		return
	}
	info := s.tmdbTitle(ctx, key, typ, id, lang)
	if info == nil {
		return
	}
	it.Overview = cmp.Or(it.Overview, info.overview)
	it.BackdropURL = cmp.Or(it.BackdropURL, info.backdrop)
	it.PosterURL = cmp.Or(it.PosterURL, info.poster)
	it.OfficialRating = cmp.Or(it.OfficialRating, info.rating)
	if len(it.Genres) == 0 {
		it.Genres = info.genres
	}
	if it.RunTimeTicks == 0 {
		it.RunTimeTicks = info.ticks
	}
}

// tmdbTitle reads a title's details with its certifications in one call. A
// title TMDB doesn't have is remembered as nil.
func (s *Server) tmdbTitle(ctx context.Context, key, typ, id, lang string) *rowTitleInfo {
	cacheKey := "row-title:" + typ + ":" + id + ":" + lang
	if v, ok := s.ext.cache.get(cacheKey, rowTitleTTL); ok {
		return v.(*rowTitleInfo)
	}
	q := langQuery(lang)
	q.Set("append_to_response", "release_dates")
	if typ == "tv" {
		q.Set("append_to_response", "content_ratings")
	}
	var d M
	if msg := s.tmdb(ctx, key, typ+"/"+id, q, &d); msg != "" {
		if strings.Contains(msg, "status 404") {
			s.ext.cache.set(cacheKey, (*rowTitleInfo)(nil))
		}
		return nil
	}
	details := titleDetails(d)
	info := &rowTitleInfo{poster: text(d, "poster_path"), rating: usCertification(d), imdb: text(d, "imdb_id")}
	info.overview, _ = details["overview"].(string)
	info.vote, _ = details["rating"].(float64)
	info.backdrop, _ = details["backdropPath"].(string)
	info.genres, _ = details["genres"].([]string)
	info.ticks, _ = details["runTimeTicks"].(int64)
	s.ext.cache.set(cacheKey, info)
	return info
}

// usCertification is a show's US content rating, or a movie's US
// certification from its releases, theatrical first, since a festival
// premiere often carries none.
func usCertification(d M) string {
	for _, field := range []string{"content_ratings", "release_dates"} {
		box, _ := d[field].(M)
		results, _ := box["results"].([]any)
		for _, raw := range results {
			r, _ := raw.(M)
			if !strings.EqualFold(text(r, "iso_3166_1"), "US") {
				continue
			}
			if field == "content_ratings" {
				return text(r, "rating")
			}
			byType, first := map[int]string{}, ""
			releases, _ := r["release_dates"].([]any)
			for _, rel := range releases {
				rm, _ := rel.(M)
				c := text(rm, "certification")
				n, _ := num(rm["type"])
				if c != "" && byType[int(n)] == "" {
					byType[int(n)] = c
					first = cmp.Or(first, c)
				}
			}
			for _, t := range []int{3, 2, 4, 5, 6, 1} {
				if c := byType[t]; c != "" {
					return c
				}
			}
			return first
		}
	}
	return ""
}

// officialListsKey is the server's MDBList key while the official lists are
// on, and the most titles a list keeps.
func (s *Server) officialListsKey(ctx context.Context) (string, int) {
	c, err := s.fullConfig(ctx)
	if err != nil {
		slog.ErrorContext(ctx, "load plugin configuration", "error", err)
		c = s.mergeConfig(nil)
	}
	if on, _ := c["MdblistOfficialListsEnabled"].(bool); !on {
		return "", 0
	}
	return strings.TrimSpace(str(c["MdblistApiKey"])), cmp.Or(wholeNumber(c["MdblistOfficialListsMaxItems"], 250), 250)
}

func (s *Server) handleOfficialLists(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()
	key, _ := s.officialListsKey(ctx)
	if key == "" {
		writeJSON(w, http.StatusOK, M{"success": false, "error": noOfficialLists, "lists": []any{}})
		return
	}
	lists, err := cachedList(&s.ext.cache, "mdblist-official", false, func() ([]officialList, error) {
		var raw []M
		status, err := s.getJSON(ctx, s.ext.mdblistBase+"/lists/official?apikey="+url.QueryEscape(key), "", &raw)
		if err != nil || status/100 != 2 {
			return nil, errors.New(providerError("MDBList", status, err))
		}
		out := []officialList{}
		for _, l := range raw {
			if slug := text(l, "slug"); slug != "" {
				count, _ := num(l["items"])
				out = append(out, officialList{Slug: slug, Name: cmp.Or(text(l, "name"), slug), Mediatype: text(l, "mediatype"), Count: int(count)})
			}
		}
		return out, nil
	})
	if err != nil {
		writeJSON(w, http.StatusOK, M{"success": false, "error": err.Error(), "lists": []any{}})
		return
	}
	writeJSON(w, http.StatusOK, M{"success": true, "lists": lists})
}

func (s *Server) handleOfficialListItems(w http.ResponseWriter, r *http.Request) {
	slug := strings.TrimSpace(r.PathValue("slug"))
	mediaType := strings.ToLower(strings.TrimSpace(r.URL.Query().Get("mediatype")))
	if mediaType != "" && mediaType != "movie" && mediaType != "show" {
		writeError(w, http.StatusBadRequest, "Invalid mediatype. Expected: movie or show")
		return
	}
	if !mdbSlug.MatchString(slug) {
		writeError(w, http.StatusBadRequest, "Invalid list slug")
		return
	}
	ctx := context.WithoutCancel(r.Context())
	key, limit := s.officialListsKey(ctx)
	if key == "" {
		writeJSON(w, http.StatusOK, M{"success": false, "slug": slug, "error": noOfficialLists, "items": []any{}})
		return
	}
	items, err := cachedList(&s.ext.cache, "mdblist-official:"+slug+":"+strconv.Itoa(limit), false, func() ([]rowItem, error) {
		return s.mdbListItems(ctx, key, "/lists/official/"+slug+"/items", "", limit, "")
	})
	if err != nil {
		writeJSON(w, http.StatusOK, M{"success": false, "slug": slug, "error": err.Error(), "items": []any{}})
		return
	}
	out := []officialItem{}
	for _, it := range items {
		kind := "movie"
		if it.Type == "Series" {
			kind = "show"
		}
		if mediaType != "" && kind != mediaType {
			continue
		}
		id, _ := strconv.ParseInt(it.ProviderIDs.Tmdb, 10, 64)
		out = append(out, officialItem{ID: id, Name: it.Name, Type: kind, ProductionYear: it.ProductionYear, Rank: it.Rank,
			Poster: it.PosterURL, ProviderIDs: it.ProviderIDs})
	}
	writeJSON(w, http.StatusOK, M{"success": true, "slug": slug, "items": out})
}

// normalizeLanguage turns an app's language tag into the language or
// language-REGION form MDBList and TMDB take, or "" when it isn't a plain
// tag, so a made-up value can't open new cache entries.
func normalizeLanguage(tag string) string {
	m := languageTag.FindStringSubmatch(strings.ReplaceAll(strings.TrimSpace(tag), "_", "-"))
	if m == nil {
		return ""
	}
	lang, script, region := strings.ToLower(m[1]), m[2], strings.ToUpper(m[3])
	// TMDB has no numeric regions, and its Latin American Spanish is es-MX.
	if region == "419" && lang == "es" {
		region = "MX"
	} else if len(region) == 3 {
		region = ""
	}
	// TMDB tells the Chinese scripts apart by region alone.
	if region == "" && lang == "zh" && script != "" {
		region = "CN"
		if strings.EqualFold(script, "Hant") {
			region = "TW"
		}
	}
	if region == "" {
		return lang
	}
	return lang + "-" + region
}

func langQuery(lang string) url.Values {
	q := url.Values{}
	if lang != "" {
		q.Set("language", lang)
	}
	return q
}

func yearOf(date string) int {
	y, _ := strconv.Atoi(date[:min(4, len(date))])
	return y
}

func rankOrLast(rank int) int {
	if rank <= 0 {
		return math.MaxInt
	}
	return rank
}

func idText(v any) string {
	switch t := v.(type) {
	case string:
		return strings.TrimSpace(t)
	case float64:
		return strconv.FormatInt(int64(t), 10)
	}
	return ""
}

// tmdbPath turns a TMDB image address into the path the apps size
// themselves.
func tmdbPath(u string) string {
	if i := strings.Index(u, "/t/p/"); i >= 0 && strings.HasPrefix(strings.ToLower(u), "http") {
		if j := strings.IndexByte(u[i+5:], '/'); j >= 0 {
			return u[i+5+j:]
		}
	}
	return u
}

// send runs req on the outbound client. Like getJSON, its errors never hold
// the URL.
func (s *Server) send(req *http.Request) (int, []byte, error) {
	resp, err := s.ext.http.Do(req)
	if err != nil {
		var ue *url.Error
		if errors.As(err, &ue) {
			err = ue.Err
		}
		return 0, nil, err
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(io.LimitReader(resp.Body, 8<<20))
	return resp.StatusCode, body, err
}
