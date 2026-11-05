package api

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"path"
	"strconv"
	"strings"
	"time"
)

// The Sonarr and Radarr calendars, read straight from the arr servers the
// admin set up, since this Moonbase has no Seerr proxy to get them from.

// ArrCatalog is what a host gives the calendar. Hosts whose catalog can't
// look titles up leave it out, and the calendar stays off there.
type ArrCatalog interface {
	// FindArrTitles looks titles up by provider id in the libraries the
	// caller can see. The answer has one entry for each title.
	FindArrTitles(ctx context.Context, caller Identity, series bool, titles []ArrTitle) ([]ArrItem, error)
	// ArrFolders lists every series or movie library with its folders and
	// whether the caller can see it.
	ArrFolders(ctx context.Context, caller Identity, series bool) ([]ArrFolder, error)
}

// ArrTitle is a series or movie from an arr calendar.
type ArrTitle struct {
	TvdbID, TmdbID, ImdbID string
	// Episodes are the ones the calendar lists for a series.
	Episodes []EpisodeNumber
}

type EpisodeNumber struct{ Season, Episode int }

// ArrItem is a title found in the library. ItemID is empty for one that isn't.
type ArrItem struct {
	ItemID     string
	LibraryIDs []string
	// Episodes holds the ids of the asked-for episodes that are in the library.
	Episodes map[EpisodeNumber]string
}

type ArrFolder struct {
	LibraryID string
	Locations []string
	Visible   bool
}

// arrServer is a Sonarr or Radarr the admin set up.
type arrServer struct{ URL, APIKey string }

func (a arrServer) set() bool { return a.URL != "" && a.APIKey != "" }

func arrServerFrom(c M, prefix string) arrServer {
	raw := strings.TrimRight(strings.TrimSpace(str(c[prefix+"Url"])), "/")
	if u, err := url.Parse(raw); err != nil || (u.Scheme != "http" && u.Scheme != "https") || u.Host == "" {
		raw = ""
	}
	return arrServer{URL: raw, APIKey: strings.TrimSpace(str(c[prefix+"ApiKey"]))}
}

func (s *Server) calendarRoutes() {
	s.mux.HandleFunc("GET /Moonfin/Seerr/Sonarr/Calendar", s.user(func(w http.ResponseWriter, r *http.Request) { s.handleArrCalendar(w, r, true) }))
	s.mux.HandleFunc("GET /Moonfin/Seerr/Radarr/Calendar", s.user(func(w http.ResponseWriter, r *http.Request) { s.handleArrCalendar(w, r, false) }))
}

// calendarOn is Ping's calendarAvailable.
func (s *Server) calendarOn(cfg Config, caller Identity) bool {
	_, ok := s.catalog.(ArrCatalog)
	return ok && (cfg.Sonarr.set() || cfg.Radarr.set()) && (caller.Admin || !cfg.CalendarAdminsOnly)
}

// maxArrCached bounds the arr answers, since every window a client asks for
// is its own entry and one answer can run to megabytes.
const maxArrCached = 64

// setBounded drops the entries older than ttl before it stores v, and starts
// over when limit entries are still left.
func (c *ttlCache) setBounded(key string, v any, ttl time.Duration, limit int) {
	c.mu.Lock()
	defer c.mu.Unlock()
	for k, e := range c.m {
		if time.Since(e.at) >= ttl {
			delete(c.m, k)
		}
	}
	if len(c.m) >= limit {
		clear(c.m)
	}
	c.m[key] = cached{time.Now(), v}
}

func (s *Server) handleArrCalendar(w http.ResponseWriter, r *http.Request, sonarr bool) {
	ctx := r.Context()
	cfg := s.config(ctx)
	service, arr := "Radarr", cfg.Radarr
	if sonarr {
		service, arr = "Sonarr", cfg.Sonarr
	}
	catalog, ok := s.catalog.(ArrCatalog)
	if !ok || !arr.set() {
		writeJSON(w, http.StatusNotFound, M{"error": "No " + service + " server is configured"})
		return
	}
	caller := callerOf(r)
	if cfg.CalendarAdminsOnly && !caller.Admin {
		writeJSON(w, http.StatusForbidden, M{"error": "The calendar is limited to admins on this server"})
		return
	}
	start, end := calendarWindow(r.URL.Query(), cfg, time.Now().UTC())
	if start.After(end) {
		writeJSON(w, http.StatusOK, []any{})
		return
	}
	q := url.Values{"start": {start.Format(time.DateOnly)}, "end": {end.Format(time.DateOnly)}}
	if sonarr {
		q.Set("includeSeries", "true")
		q.Set("includeEpisodeImages", "true")
	}
	if strings.EqualFold(r.URL.Query().Get("unmonitored"), "true") {
		q.Set("unmonitored", "true")
	}

	cacheKey := service + ":" + q.Encode()
	ttl := time.Duration(cfg.CalendarCacheMinutes) * time.Minute
	cached, hit := s.arrCache.get(cacheKey, ttl)
	body, _ := cached.([]byte)
	if !hit {
		var status int
		var err error
		body, status, err = s.fetchArr(ctx, arr, q)
		switch {
		case err != nil:
			logError(r, "fetch the "+service+" calendar", err)
			writeJSON(w, http.StatusBadGateway, M{"error": "Couldn't reach the " + service + " server"})
			return
		case status/100 != 2:
			writeJSON(w, http.StatusBadGateway, M{"error": fmt.Sprintf("The %s server answered %d", service, status)})
			return
		}
	}
	var elems []map[string]json.RawMessage
	var fields []arrFields
	// A field of an unexpected type only leaves that field unread.
	_ = json.Unmarshal(body, &fields)
	if json.Unmarshal(body, &elems) != nil || len(fields) != len(elems) {
		writeJSON(w, http.StatusBadGateway, M{"error": "The " + service + " server sent an unreadable calendar"})
		return
	}
	if !hit && ttl > 0 {
		s.arrCache.setBounded(cacheKey, body, ttl, maxArrCached)
	}

	out, err := annotateArr(ctx, catalog, caller, sonarr, elems, fields)
	if err != nil {
		logError(r, "match the "+service+" calendar to the library", err)
		writeJSON(w, http.StatusBadGateway, M{"error": s.name + " didn't answer, so the calendar couldn't be matched to your libraries"})
		return
	}
	writeJSON(w, http.StatusOK, out)
}

// calendarWindow clamps the asked-for dates to the admin's window around
// today. A date that's missing or unreadable counts as not given.
func calendarWindow(q url.Values, cfg Config, now time.Time) (start, end time.Time) {
	today := time.Date(now.Year(), now.Month(), now.Day(), 0, 0, 0, 0, time.UTC)
	first, last := today.AddDate(0, 0, -cfg.CalendarDaysBack), today.AddDate(0, 0, cfg.CalendarDaysAhead)
	start, end = dayOr(q.Get("start"), today), dayOr(q.Get("end"), last)
	if start.Before(first) {
		start = first
	}
	if end.After(last) {
		end = last
	}
	return start, end
}

// dayOr reads a YYYY-MM-DD date, or the date part of a timestamp.
func dayOr(v string, def time.Time) time.Time {
	v = strings.TrimSpace(v)
	if len(v) > 10 {
		v = v[:10]
	}
	if d, err := time.Parse(time.DateOnly, v); err == nil {
		return d
	}
	return def
}

// fetchArr reads an arr's calendar. Its errors never hold the URL or the key.
func (s *Server) fetchArr(ctx context.Context, arr arrServer, q url.Values) ([]byte, int, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, arr.URL+"/api/v3/calendar?"+q.Encode(), nil)
	if err != nil {
		return nil, 0, errors.New("bad server URL")
	}
	req.Header.Set("X-Api-Key", arr.APIKey)
	req.Header.Set("Accept", "application/json")
	resp, err := s.ext.http.Do(req)
	if err != nil {
		var ue *url.Error
		if errors.As(err, &ue) {
			err = ue.Err
		}
		return nil, 0, err
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(io.LimitReader(resp.Body, 32<<20))
	return body, resp.StatusCode, err
}

// arrIDs are a calendar title's ids: an episode's series on Sonarr, the
// movie itself on Radarr.
type arrIDs struct {
	TvdbID int    `json:"tvdbId"`
	TmdbID int    `json:"tmdbId"`
	ImdbID string `json:"imdbId"`
	Path   string `json:"path"`
}

// arrFields are what the calendar reads from an element.
type arrFields struct {
	arrIDs
	SeasonNumber  int    `json:"seasonNumber"`
	EpisodeNumber int    `json:"episodeNumber"`
	Series        arrIDs `json:"series"`
}

// annotateArr adds the caller's library ids to each calendar element and
// drops the ones only in libraries the caller can't see. Episodes are
// grouped by series so each title is looked up once.
func annotateArr(ctx context.Context, catalog ArrCatalog, caller Identity, sonarr bool, elems []map[string]json.RawMessage, fields []arrFields) ([]map[string]json.RawMessage, error) {
	byIDs := map[arrIDs]int{}
	var titles []ArrTitle
	var paths []string
	titleOf := make([]int, len(fields))
	for i, f := range fields {
		ids := f.arrIDs
		if sonarr {
			ids = f.Series
		}
		n, seen := byIDs[ids]
		if !seen {
			n = len(titles)
			byIDs[ids] = n
			titles = append(titles, ArrTitle{TvdbID: positive(ids.TvdbID), TmdbID: positive(ids.TmdbID), ImdbID: strings.TrimSpace(ids.ImdbID)})
			paths = append(paths, ids.Path)
		}
		titleOf[i] = n
		if sonarr {
			titles[n].Episodes = append(titles[n].Episodes, EpisodeNumber{f.SeasonNumber, f.EpisodeNumber})
		}
	}

	found, err := catalog.FindArrTitles(ctx, caller, sonarr, titles)
	if err != nil {
		return nil, err
	}
	if len(found) != len(titles) {
		return nil, fmt.Errorf("the host matched %d of %d titles", len(found), len(titles))
	}
	// A title found by id is in a library the caller sees. Any other is
	// placed by its folder, which may be in a library they can't see.
	hidden := make([]bool, len(titles))
	var folders []ArrFolder
	listed := false
	for n := range titles {
		if found[n].ItemID != "" {
			continue
		}
		if !listed {
			if folders, err = catalog.ArrFolders(ctx, caller, sonarr); err != nil {
				return nil, err
			}
			listed = true
		}
		var visible []string
		matched := libraryFolders(paths[n], folders)
		for _, f := range matched {
			if f.Visible {
				visible = append(visible, f.LibraryID)
			}
		}
		found[n].LibraryIDs = visible
		hidden[n] = len(matched) > 0 && len(visible) == 0
	}

	out := make([]map[string]json.RawMessage, 0, len(elems))
	for i, e := range elems {
		n := titleOf[i]
		if hidden[n] {
			continue
		}
		item := found[n]
		e["moonfinItemId"] = jsonStringOrNull(item.ItemID)
		if sonarr {
			e["moonfinEpisodeId"] = jsonStringOrNull(item.Episodes[EpisodeNumber{fields[i].SeasonNumber, fields[i].EpisodeNumber}])
		}
		libs, _ := json.Marshal(append([]string{}, item.LibraryIDs...))
		e["moonfinLibraryIds"] = libs
		out = append(out, e)
	}
	return out, nil
}

// libraryFolders finds the libraries an arr folder lands in: those with a
// folder it's inside, or failing that those with a folder named like its
// parent, so /tv/Show matches a library at /media/tv.
func libraryFolders(arrPath string, folders []ArrFolder) []ArrFolder {
	p := slashPath(arrPath)
	if p == "" {
		return nil
	}
	var out []ArrFolder
	for _, f := range folders {
		for _, loc := range f.Locations {
			if l := slashPath(loc); l != "" && strings.HasPrefix(strings.ToLower(p+"/"), strings.ToLower(l+"/")) {
				out = append(out, f)
				break
			}
		}
	}
	if len(out) > 0 {
		return out
	}
	parent := path.Base(path.Dir(p))
	if parent == "." || parent == "/" {
		return nil
	}
	for _, f := range folders {
		for _, loc := range f.Locations {
			if strings.EqualFold(path.Base(slashPath(loc)), parent) {
				out = append(out, f)
				break
			}
		}
	}
	return out
}

// slashPath writes a Windows path with forward slashes and drops trailing ones.
func slashPath(p string) string {
	return strings.TrimRight(strings.ReplaceAll(strings.TrimSpace(p), `\`, "/"), "/")
}

func positive(n int) string {
	if n <= 0 {
		return ""
	}
	return strconv.Itoa(n)
}

func jsonStringOrNull(s string) json.RawMessage {
	if s == "" {
		return json.RawMessage("null")
	}
	b, _ := json.Marshal(s)
	return b
}
