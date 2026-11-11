package api

import (
	"context"
	"encoding/json"
	"errors"
	"io/fs"
	"log/slog"
	"maps"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"slices"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/Moonfin-Client/Plugin/Go/internal/seasonal"
)

// SeasonalCatalog is what a host gives Moonbase's seasonal Home row. Hosts
// that can't list their movies leave it out and the route stays off, so the
// apps use the server's own seasonal row where it has one.
type SeasonalCatalog interface {
	// SeasonalMovies lists every movie on the server that has a TMDB id,
	// read as the server owner.
	SeasonalMovies(ctx context.Context) ([]SeasonalMovie, error)
	// SmartItems is what the caller gets for each of these item keys, read
	// as the caller, so anything they can't see is left out.
	SmartItems(ctx context.Context, caller Identity, keys []string) (map[string]json.RawMessage, error)
}

type SeasonalMovie struct {
	ID     string
	TmdbID string
}

const (
	// A holiday with fewer titles than this gives way to the next one running.
	seasonalMinItems       = 6
	seasonalMaxOwned       = 40
	seasonalMaxSuggestions = 40
	seasonalDiscoverPages  = 5
	// The caller's movies are read this many at a time, until the row is full.
	seasonalBatch          = 50
	seasonalSuggestionsTTL = 30 * 24 * time.Hour
	seasonalKeywordsTTL    = 180 * 24 * time.Hour
	seasonalKeywordsDelay  = 50 * time.Millisecond
)

type seasonalRow struct {
	catalog  SeasonalCatalog
	holidays []seasonal.Holiday
	dir      string
	now      func() time.Time

	// mu guards both maps and the files they're saved to.
	mu          sync.Mutex
	keywords    map[string]seasonalKeywords
	suggestions map[string]seasonalSuggestions
}

// seasonalKeywords are one movie's TMDB keywords, kept by its item id.
type seasonalKeywords struct {
	TmdbID    string    `json:"tmdbId"`
	IDs       []int     `json:"keywordIds"`
	FetchedAt time.Time `json:"fetchedAt"`
}

// seasonalSuggestions are a holiday's popular TMDB movies for one year,
// shared by everyone.
type seasonalSuggestions struct {
	Items    []rowItem `json:"items"`
	CachedAt time.Time `json:"cachedAt"`
}

func newSeasonalRow(dir, rules string, catalog Catalog) *seasonalRow {
	c, ok := catalog.(SeasonalCatalog)
	if dir == "" || rules == "" || !ok {
		return nil
	}
	data, err := os.ReadFile(rules)
	var holidays []seasonal.Holiday
	if err == nil {
		holidays, err = seasonal.Parse(data)
	}
	if err == nil {
		err = os.MkdirAll(dir, 0o700)
	}
	if err != nil {
		slog.Error("seasonal row: read the holidays, so the row is off", "error", err)
		return nil
	}
	return &seasonalRow{catalog: c, holidays: holidays, dir: dir, now: time.Now,
		keywords:    readSeasonalFile[seasonalKeywords](dir, "keywords.json"),
		suggestions: readSeasonalFile[seasonalSuggestions](dir, "suggestions.json")}
}

func readSeasonalFile[T any](dir, name string) map[string]T {
	var out map[string]T
	data, err := os.ReadFile(filepath.Join(dir, name))
	if err == nil {
		err = json.Unmarshal(data, &out)
	}
	if err != nil && !errors.Is(err, fs.ErrNotExist) {
		slog.Error("seasonal row: read "+name+", so it starts over", "error", err)
		out = nil
	}
	if out == nil {
		out = map[string]T{}
	}
	return out
}

// save writes one of the row's files. Callers hold mu.
func (row *seasonalRow) save(name string, v any) {
	data, err := json.Marshal(v)
	if err == nil {
		err = writeFileAtomic(filepath.Join(row.dir, name), data)
	}
	if err != nil {
		slog.Error("seasonal row: save "+name, "error", err)
	}
}

func (s *Server) seasonalRoutes() {
	if s.seasonal == nil {
		return
	}
	s.mux.HandleFunc("GET /Moonfin/Seasonal/Row", s.user(s.handleSeasonalRow))
}

// handleSeasonalRow is the caller's seasonal Home row: the first running
// holiday with enough titles, their movies for it in an order that changes
// daily, and popular TMDB titles they could request through Seerr. Owned
// movies come as the host's own items. Anything that goes wrong answers an
// empty row, which the apps leave out.
func (s *Server) handleSeasonalRow(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()
	country := seasonal.Country(r.URL.Query().Get("country"))
	allYear, _ := strconv.ParseBool(r.URL.Query().Get("allYear"))
	body := M{"holiday": nil, "country": nil, "items": []json.RawMessage{}, "suggestions": []rowItem{}}
	if country != "" {
		body["country"] = country
	}

	profile, err := s.resolve(r, "global", false)
	if err != nil {
		logError(r, "load the seasonal row's settings", err)
		writeJSON(w, http.StatusOK, body)
		return
	}
	var hidden []string
	list, _ := profile["seasonalRowHiddenHolidays"].([]any)
	for _, id := range list {
		if text, ok := id.(string); ok {
			hidden = append(hidden, text)
		}
	}
	now := s.seasonal.now()
	active := seasonal.ActiveOn(s.seasonal.holidays, now, country, hidden, allYear)
	if len(active) == 0 {
		writeJSON(w, http.StatusOK, body)
		return
	}

	caller := callerOf(r)
	key := strings.TrimSpace(str(profile["tmdbApiKey"]))
	seerrReady := key != "" && s.seerrReadyFor(ctx, caller, profile)
	seed := seasonal.Seed(strconv.Itoa(caller.UserID), now)
	for _, a := range active {
		owned, err := s.seasonalOwned(ctx, caller, a.Holiday, seed)
		if err != nil {
			logError(r, "read the seasonal row's movies", err)
			break
		}
		suggestions := []rowItem{}
		if seerrReady {
			suggestions = s.seasonalSuggestions(ctx, a.Holiday, key, now)
		}
		if len(owned)+len(suggestions) < seasonalMinItems {
			continue
		}
		body["holiday"], body["items"], body["suggestions"] = a.Holiday.ID, owned, suggestions
		break
	}
	writeJSON(w, http.StatusOK, body)
}

// seerrReadyFor says whether the caller can request through Seerr: the admin
// set it up, they haven't switched it off, and they're signed in to it.
func (s *Server) seerrReadyFor(ctx context.Context, id Identity, profile M) bool {
	cfg := s.config(ctx)
	if !seerrOn(cfg) || profile["seerrEnabled"] == false {
		return false
	}
	session, err := s.seerrSessionFor(ctx, cfg, id, seerrSessionFreshness)
	return err == nil && session != nil
}

// seasonalOwned is up to seasonalMaxOwned of the caller's movies for the
// holiday, each title once, in the caller's order for the day. Two copies of
// one movie, like a 4K and a 1080p file in different libraries, share their
// TMDB id.
func (s *Server) seasonalOwned(ctx context.Context, caller Identity, h *seasonal.Holiday, seed uint32) ([]json.RawMessage, error) {
	want := h.KeywordIDs()
	tmdb := map[string]string{}
	s.seasonal.mu.Lock()
	for id, e := range s.seasonal.keywords {
		if slices.ContainsFunc(e.IDs, func(k int) bool { return want[k] }) {
			tmdb[id] = e.TmdbID
		}
	}
	s.seasonal.mu.Unlock()

	type owned struct {
		tmdb string
		raw  json.RawMessage
	}
	var found []owned
	ids := seasonal.Shuffle(slices.Sorted(maps.Keys(tmdb)), seed)
	for start := 0; start < len(ids) && len(found) < seasonalMaxOwned; start += seasonalBatch {
		batch := ids[start:min(start+seasonalBatch, len(ids))]
		items, err := s.seasonal.catalog.SmartItems(ctx, caller, batch)
		if err != nil {
			return nil, err
		}
		for _, id := range batch {
			if raw, ok := items[id]; ok {
				found = append(found, owned{tmdb[id], raw})
			}
		}
		found = seasonal.Distinct(found, func(o owned) []string { return seasonal.IDKeys(o.tmdb, "") })
	}
	out := []json.RawMessage{}
	for _, o := range found[:min(len(found), seasonalMaxOwned)] {
		out = append(out, o.raw)
	}
	return out, nil
}

// seasonalSuggestions are the holiday's popular movies the server doesn't
// have, each title once. TMDB is asked about a holiday once a month, and
// everyone shares the answer.
func (s *Server) seasonalSuggestions(ctx context.Context, h *seasonal.Holiday, key string, now time.Time) []rowItem {
	row := s.seasonal
	cacheKey := h.ID + ":" + strconv.Itoa(now.Year())
	library := map[string]bool{}
	row.mu.Lock()
	cached := row.suggestions[cacheKey]
	for _, e := range row.keywords {
		library[e.TmdbID] = true
	}
	row.mu.Unlock()
	all := cached.Items
	if time.Since(cached.CachedAt) >= seasonalSuggestionsTTL {
		all = s.discoverSeasonal(ctx, h, key)
		if len(all) > 0 {
			row.mu.Lock()
			maps.DeleteFunc(row.suggestions, func(_ string, e seasonalSuggestions) bool {
				return time.Since(e.CachedAt) >= seasonalSuggestionsTTL
			})
			row.suggestions[cacheKey] = seasonalSuggestions{Items: all, CachedAt: time.Now()}
			row.save("suggestions.json", row.suggestions)
			row.mu.Unlock()
		}
	}

	fresh := []rowItem{}
	for _, it := range all {
		if !library[it.ProviderIDs.Tmdb] {
			fresh = append(fresh, it)
		}
	}
	fresh = seasonal.Distinct(fresh, func(it rowItem) []string { return seasonal.IDKeys(it.ProviderIDs.Tmdb, it.ProviderIDs.Imdb) })
	return fresh[:min(len(fresh), seasonalMaxSuggestions)]
}

// discoverSeasonal is TMDB's most popular movies with the holiday's keywords,
// with what a focused card shows. The backdrop stays the one discover gives,
// since the apps show it behind the page.
func (s *Server) discoverSeasonal(ctx context.Context, h *seasonal.Holiday, key string) []rowItem {
	keywords := make([]string, 0, len(h.Keywords))
	for _, k := range h.Keywords {
		keywords = append(keywords, strconv.Itoa(k.ID))
	}
	var items []rowItem
	seen := map[int64]bool{}
	for page := 1; page <= seasonalDiscoverPages; page++ {
		var res struct {
			Results []struct {
				ID          int64  `json:"id"`
				Title       string `json:"title"`
				ReleaseDate string `json:"release_date"`
				Poster      string `json:"poster_path"`
				Backdrop    string `json:"backdrop_path"`
			} `json:"results"`
		}
		q := url.Values{"with_keywords": {strings.Join(keywords, "|")}, "sort_by": {"popularity.desc"},
			"vote_count.gte": {strconv.Itoa(h.MinVotes)}, "include_adult": {"false"}, "page": {strconv.Itoa(page)}}
		if msg := s.tmdb(ctx, key, "discover/movie", q, &res); msg != "" {
			slog.WarnContext(ctx, "seasonal row: TMDB discover failed", "holiday", h.ID, "error", msg)
			break
		}
		added := 0
		for _, m := range res.Results {
			if m.ID <= 0 || seen[m.ID] {
				continue
			}
			seen[m.ID] = true
			items = append(items, rowItem{Name: m.Title, Type: "Movie", ProductionYear: yearOf(m.ReleaseDate), Rank: len(items) + 1,
				ProviderIDs: rowIDs{Tmdb: strconv.FormatInt(m.ID, 10)}, PosterURL: m.Poster, BackdropURL: m.Backdrop})
			added++
		}
		if added == 0 {
			break
		}
	}
	s.fillFromTmdb(ctx, key, "", items)
	// fillFromTmdb leaves out the rating and IMDb id, so they come from the
	// details it just read.
	for i := range items {
		if info := s.tmdbTitle(ctx, key, "movie", items[i].ProviderIDs.Tmdb, ""); info != nil {
			items[i].Rating = info.vote
			items[i].ProviderIDs.Imdb = info.imdb
		}
	}
	return items
}

// scheduleSeasonalKeywords looks up the movies' keywords a minute after
// startup and then every night at 4:00.
func (s *Server) scheduleSeasonalKeywords() {
	go func() {
		time.Sleep(time.Minute)
		for {
			s.syncSeasonalKeywords(context.Background())
			now := time.Now()
			next := time.Date(now.Year(), now.Month(), now.Day(), 4, 0, 0, 0, now.Location())
			if !next.After(now) {
				next = next.AddDate(0, 0, 1)
			}
			time.Sleep(time.Until(next))
		}
	}()
}

// syncSeasonalKeywords looks up each movie's TMDB keywords once. After the
// first run it only asks about movies that are new, were matched to another
// TMDB title, or were looked up half a year ago.
func (s *Server) syncSeasonalKeywords(ctx context.Context) {
	row := s.seasonal
	key := s.config(ctx).TmdbAPIKey
	if key == "" {
		slog.InfoContext(ctx, "seasonal keywords sync skipped: there's no server-wide TMDB key")
		return
	}
	movies, err := row.catalog.SeasonalMovies(ctx)
	if err != nil {
		slog.ErrorContext(ctx, "seasonal keywords sync: list the movies", "error", err)
		return
	}

	live := make(map[string]bool, len(movies))
	var pending []SeasonalMovie
	row.mu.Lock()
	for _, m := range movies {
		live[m.ID] = true
		if e, ok := row.keywords[m.ID]; !ok || e.TmdbID != m.TmdbID || time.Since(e.FetchedAt) >= seasonalKeywordsTTL {
			pending = append(pending, m)
		}
	}
	gone := len(row.keywords)
	maps.DeleteFunc(row.keywords, func(id string, _ seasonalKeywords) bool { return !live[id] })
	gone -= len(row.keywords)
	row.mu.Unlock()
	slog.InfoContext(ctx, "seasonal keywords sync", "toLookUp", len(pending), "movies", len(movies), "gone", gone)

	stored, failed, lastErr := 0, 0, ""
	for _, m := range pending {
		if !providerID.MatchString(m.TmdbID) {
			continue
		}
		var res struct {
			Keywords []struct {
				ID int `json:"id"`
			} `json:"keywords"`
		}
		switch msg := s.tmdb(ctx, key, "movie/"+m.TmdbID+"/keywords", nil, &res); {
		case strings.Contains(msg, "status 404"):
			// TMDB doesn't know the movie, so it's asked about again next run.
		case msg != "":
			failed, lastErr = failed+1, msg
		default:
			ids := make([]int, 0, len(res.Keywords))
			for _, k := range res.Keywords {
				ids = append(ids, k.ID)
			}
			row.mu.Lock()
			row.keywords[m.ID] = seasonalKeywords{TmdbID: m.TmdbID, IDs: ids, FetchedAt: time.Now()}
			if stored++; stored%200 == 0 {
				row.save("keywords.json", row.keywords)
			}
			row.mu.Unlock()
		}
		time.Sleep(seasonalKeywordsDelay)
	}
	if failed > 0 {
		slog.WarnContext(ctx, "seasonal keywords sync: some movies weren't looked up, so they're asked about next run", "count", failed, "error", lastErr)
	}

	row.mu.Lock()
	row.save("keywords.json", row.keywords)
	count := len(row.keywords)
	row.mu.Unlock()
	slog.InfoContext(ctx, "seasonal keywords sync complete", "movies", count)
}
