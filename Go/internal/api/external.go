package api

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"math"
	"net/http"
	"net/url"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"time"
)

// MDBList and TMDB routes. They take provider ids from the client and never
// read Silo's catalog, so they port from the Jellyfin plugin as they are.

const noMdblistKey = "No MDBList API key configured. Add your key in Moonfin Settings, or ask your server admin to set a server-wide key."
const noTmdbKey = "No TMDB API key configured. Add your key in Moonfin Settings, or ask your server admin to set a server-wide key."

// external holds the outbound client, base URLs (swapped in tests) and the
// in-memory caches. Silo's instance state is too small for rating caches.
type external struct {
	http        *http.Client
	mdblistBase string
	tmdbBase    string
	cache       ttlCache
}

func newExternal() external {
	// Silo gives a plugin call 10 seconds, so give up on the provider first.
	return external{
		http:        &http.Client{Timeout: 8 * time.Second},
		mdblistBase: "https://api.mdblist.com",
		tmdbBase:    "https://api.themoviedb.org",
		cache:       ttlCache{m: map[string]cached{}},
	}
}

// maxCached bounds the cache, since signed-in users choose the ids in its keys.
const maxCached = 20000

type ttlCache struct {
	mu sync.Mutex
	m  map[string]cached
}

type cached struct {
	at time.Time
	v  any
}

func (c *ttlCache) get(key string, ttl time.Duration) (any, bool) {
	c.mu.Lock()
	defer c.mu.Unlock()
	e, ok := c.m[key]
	if ok && time.Since(e.at) < ttl {
		return e.v, true
	}
	delete(c.m, key)
	return nil, false
}

// clearPrefix drops every entry whose key starts with prefix and says how many.
func (c *ttlCache) clearPrefix(prefix string) int {
	c.mu.Lock()
	defer c.mu.Unlock()
	n := 0
	for k := range c.m {
		if strings.HasPrefix(k, prefix) {
			delete(c.m, k)
			n++
		}
	}
	return n
}

func (c *ttlCache) set(key string, v any) {
	c.mu.Lock()
	if len(c.m) >= maxCached {
		// A full cache starts over rather than evicting the oldest entry. An LRU
		// would only pay off if refetching ever showed up in provider quotas.
		clear(c.m)
	}
	c.m[key] = cached{time.Now(), v}
	c.mu.Unlock()
}

func (s *Server) externalRoutes() {
	m := s.mux
	m.HandleFunc("GET /Moonfin/MdbList/Ratings", s.user(s.handleMdbListRatings))
	m.HandleFunc("GET /Moonfin/Tmdb/EpisodeRating", s.user(s.handleTmdbEpisode))
	m.HandleFunc("GET /Moonfin/Tmdb/SeasonRatings", s.user(s.handleTmdbSeason))
	m.HandleFunc("GET /Moonfin/Tmdb/NextEpisode", s.user(s.handleTmdbNextEpisode))
	m.HandleFunc("GET /Moonfin/Tmdb/PersonLinks", s.user(s.handleTmdbPersonLinks))
	m.HandleFunc("GET /Moonfin/Tmdb/Thumb", s.user(s.handleTmdbThumb))
	m.HandleFunc("GET /Moonfin/Tmdb/Details", s.user(s.handleTmdbDetails))
	m.HandleFunc("GET /Moonfin/Tmdb/Reviews", s.user(s.handleTmdbReviews))
	m.HandleFunc("GET /Moonfin/Tmdb/ProductionCompanies", s.user(s.handleTmdbCompanies))
}

// compact drops nil values, as the Jellyfin host's JSON writer does.
func compact(m M) M {
	for k, v := range m {
		if v == nil {
			delete(m, k)
		}
	}
	return m
}

// getJSON fetches rawURL into out. It returns the provider's status code, or
// an error that never contains the URL, since the URL can hold an API key.
func (s *Server) getJSON(ctx context.Context, rawURL, bearer string, out any) (int, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, rawURL, nil)
	if err != nil {
		return 0, errors.New("bad request URL")
	}
	req.Header.Set("User-Agent", "Moonfin/1.0")
	req.Header.Set("Accept", "application/json")
	if bearer != "" {
		req.Header.Set("Authorization", "Bearer "+bearer)
	}
	resp, err := s.ext.http.Do(req)
	if err != nil {
		var ue *url.Error
		if errors.As(err, &ue) {
			err = ue.Err
		}
		return 0, err
	}
	defer resp.Body.Close()
	if resp.StatusCode/100 != 2 {
		return resp.StatusCode, nil
	}
	if err := json.NewDecoder(io.LimitReader(resp.Body, 8<<20)).Decode(out); err != nil {
		return resp.StatusCode, errors.New("unreadable answer")
	}
	return resp.StatusCode, nil
}

func providerError(provider string, status int, err error) string {
	switch {
	case err != nil:
		return fmt.Sprintf("Failed to fetch from %s: %v", provider, err)
	case status == http.StatusTooManyRequests:
		return provider + " rate limit reached. Try again later."
	default:
		return fmt.Sprintf("%s returned status %d", provider, status)
	}
}

func num(v any) (float64, bool) {
	switch t := v.(type) {
	case float64:
		return t, true
	case string:
		f, err := strconv.ParseFloat(strings.TrimSpace(t), 64)
		return f, err == nil
	}
	return 0, false
}

var defaultRatingSources = []any{"imdb", "tmdb", "tomatoes", "metacritic"}

func (s *Server) handleMdbListRatings(w http.ResponseWriter, r *http.Request) {
	q := r.URL.Query()
	typ, tmdbID := strings.ToLower(strings.TrimSpace(q.Get("type"))), strings.TrimSpace(q.Get("tmdbId"))
	if typ == "" || tmdbID == "" {
		writeError(w, http.StatusBadRequest, "Missing required parameters: type, tmdbId")
		return
	}
	if typ != "movie" && typ != "show" {
		writeError(w, http.StatusBadRequest, "Invalid type. Expected: movie or show")
		return
	}
	if !providerID.MatchString(tmdbID) {
		writeError(w, http.StatusBadRequest, "Invalid tmdbId")
		return
	}
	profile := strings.ToLower(strings.TrimSpace(q.Get("profile")))
	if profile != "desktop" && profile != "mobile" && profile != "tv" {
		profile = "global"
	}
	resolved, err := s.resolve(r, profile, false)
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	key, _ := resolved["mdblistApiKey"].(string)
	if strings.TrimSpace(key) == "" {
		writeJSON(w, http.StatusOK, M{"success": false, "error": noMdblistKey, "ratings": []any{}})
		return
	}

	cacheKey := "mdblist:" + typ + ":" + tmdbID
	ratings, ok := s.ext.cache.get(cacheKey, 7*24*time.Hour)
	if !ok {
		if msg, failed := s.ext.cache.get("mdblist-fail:"+cacheKey, 10*time.Minute); failed {
			writeJSON(w, http.StatusOK, M{"success": false, "error": msg, "ratings": []any{}})
			return
		}
		var body struct {
			Ratings []M `json:"ratings"`
		}
		u := fmt.Sprintf("%s/tmdb/%s/%s/?apikey=%s", s.ext.mdblistBase, url.PathEscape(typ), url.PathEscape(tmdbID), url.QueryEscape(key))
		status, err := s.getJSON(r.Context(), u, "", &body)
		if err != nil || status/100 != 2 {
			msg := providerError("MDBList", status, err)
			// A canceled call says nothing about MDBList.
			if r.Context().Err() == nil {
				s.ext.cache.set("mdblist-fail:"+cacheKey, msg)
			}
			writeJSON(w, http.StatusOK, M{"success": false, "error": msg, "ratings": []any{}})
			return
		}
		ratings = body.Ratings
		s.ext.cache.set(cacheKey, ratings)
	}
	sources, _ := resolved["mdblistRatingSources"].([]any)
	if len(sources) == 0 {
		sources = defaultRatingSources
	}
	writeJSON(w, http.StatusOK, M{"success": true, "ratings": filterRatings(ratings.([]M), sources)})
}

// filterRatings keeps the caller's chosen sources in their order. MDBList
// calls Rotten Tomatoes audience "popcorn" and rates Letterboxd out of 10.
func filterRatings(all []M, sources []any) []M {
	bySource := map[string]M{}
	for _, r := range all {
		if src, _ := r["source"].(string); src != "" {
			bySource[strings.ToLower(src)] = r
		}
	}
	out := []M{}
	for _, s := range sources {
		source, _ := s.(string)
		lookup := strings.ToLower(source)
		if lookup == "rtaudience" || lookup == "tomatoes_audience" {
			lookup = "popcorn"
		}
		r, ok := bySource[lookup]
		if !ok {
			continue
		}
		item := M{"source": source}
		value, hasValue := num(r["value"])
		score, hasScore := num(r["score"])
		if strings.EqualFold(source, "letterboxd") {
			if hasScore {
				value, hasValue = math.Round(score/20*10)/10, true
			} else if hasValue && value > 5 {
				value = math.Round(value/2*10) / 10
			}
		}
		if hasValue {
			item["value"] = value
		}
		if hasScore {
			item["score"] = score
		}
		if votes, ok := num(r["votes"]); ok {
			item["votes"] = int(votes)
		}
		switch u := r["url"].(type) {
		case string:
			item["url"] = u
		case float64:
			item["url"] = strconv.FormatFloat(u, 'f', -1, 64)
		}
		out = append(out, item)
	}
	return out
}

// tmdbKey is the caller's own TMDB key or, failing that, the server's.
func (s *Server) tmdbKey(r *http.Request) (string, error) {
	resolved, err := s.resolve(r, "global", false)
	if err != nil {
		return "", err
	}
	key, _ := resolved["tmdbApiKey"].(string)
	return strings.TrimSpace(key), nil
}

// tmdb fetches a TMDB v3 path into out and returns a user-facing error, or ""
// on success. Read access tokens (they start with eyJ) go in the header,
// v3 keys in the query.
func (s *Server) tmdb(ctx context.Context, key, path string, query url.Values, out any) string {
	if query == nil {
		query = url.Values{}
	}
	bearer := ""
	if strings.HasPrefix(key, "eyJ") {
		bearer = key
	} else {
		query.Set("api_key", key)
	}
	status, err := s.getJSON(ctx, s.ext.tmdbBase+"/3/"+path+"?"+query.Encode(), bearer, out)
	if err != nil || status/100 != 2 {
		return providerError("TMDB", status, err)
	}
	return ""
}

// tmdbRoute runs one cached TMDB lookup and writes its answer. fetch returns
// the success body, or "" and a user-facing error.
func (s *Server) tmdbRoute(w http.ResponseWriter, r *http.Request, cacheKey string, ttl time.Duration, fetch func(key string) (M, string)) {
	if v, ok := s.ext.cache.get(cacheKey, ttl); ok {
		writeJSON(w, http.StatusOK, v)
		return
	}
	key, err := s.tmdbKey(r)
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	if key == "" {
		writeJSON(w, http.StatusOK, M{"success": false, "error": noTmdbKey})
		return
	}
	body, msg := fetch(key)
	if msg != "" {
		writeJSON(w, http.StatusOK, M{"success": false, "error": msg})
		return
	}
	s.ext.cache.set(cacheKey, body)
	writeJSON(w, http.StatusOK, body)
}

// providerID matches the ids that go into MDBList and TMDB paths, so a value
// like ".." can't change which provider route is called.
var providerID = regexp.MustCompile(`^[A-Za-z0-9_-]{1,40}$`)

func requiredTmdbID(w http.ResponseWriter, r *http.Request) (string, bool) {
	id := strings.TrimSpace(r.URL.Query().Get("tmdbId"))
	if id == "" {
		writeError(w, http.StatusBadRequest, "Missing required parameter: tmdbId")
		return "", false
	}
	if !providerID.MatchString(id) {
		writeError(w, http.StatusBadRequest, "Invalid tmdbId")
		return "", false
	}
	return id, true
}

func queryInt(r *http.Request, name string) int {
	n, _ := strconv.Atoi(r.URL.Query().Get(name))
	return n
}

func episodeOut(ep M) M {
	return compact(M{"success": true, "voteAverage": ep["vote_average"], "voteCount": ep["vote_count"],
		"name": ep["name"], "airDate": ep["air_date"], "seasonNumber": ep["season_number"],
		"episodeNumber": ep["episode_number"], "stillPath": ep["still_path"]})
}

func (s *Server) handleTmdbEpisode(w http.ResponseWriter, r *http.Request) {
	id, ok := requiredTmdbID(w, r)
	if !ok {
		return
	}
	season, episode := queryInt(r, "season"), queryInt(r, "episode")
	s.tmdbRoute(w, r, fmt.Sprintf("tmdb-ep:%s:%d:%d", id, season, episode), 24*time.Hour, func(key string) (M, string) {
		var ep M
		if msg := s.tmdb(r.Context(), key, fmt.Sprintf("tv/%s/season/%d/episode/%d", url.PathEscape(id), season, episode), nil, &ep); msg != "" {
			return nil, msg
		}
		return episodeOut(ep), ""
	})
}

func (s *Server) handleTmdbSeason(w http.ResponseWriter, r *http.Request) {
	id, ok := requiredTmdbID(w, r)
	if !ok {
		return
	}
	season := queryInt(r, "season")
	s.tmdbRoute(w, r, fmt.Sprintf("tmdb-season:%s:%d", id, season), 24*time.Hour, func(key string) (M, string) {
		var data struct {
			Name     any `json:"name"`
			Episodes []M `json:"episodes"`
		}
		if msg := s.tmdb(r.Context(), key, fmt.Sprintf("tv/%s/season/%d", url.PathEscape(id), season), nil, &data); msg != "" {
			return nil, msg
		}
		episodes := []M{}
		for _, ep := range data.Episodes {
			out := episodeOut(ep)
			episodes = append(episodes, out)
			if n, ok := num(ep["episode_number"]); ok {
				s.ext.cache.set(fmt.Sprintf("tmdb-ep:%s:%d:%d", id, season, int(n)), out)
			}
		}
		return compact(M{"success": true, "seasonName": data.Name, "episodes": episodes}), ""
	})
}

func (s *Server) handleTmdbNextEpisode(w http.ResponseWriter, r *http.Request) {
	id, ok := requiredTmdbID(w, r)
	if !ok {
		return
	}
	s.tmdbRoute(w, r, "tmdb-next:"+id, 6*time.Hour, func(key string) (M, string) {
		var data struct {
			Next M `json:"next_episode_to_air"`
		}
		if msg := s.tmdb(r.Context(), key, "tv/"+url.PathEscape(id), nil, &data); msg != "" {
			return nil, msg
		}
		return compact(M{"success": data.Next != nil, "airDate": data.Next["air_date"],
			"seasonNumber": data.Next["season_number"], "episodeNumber": data.Next["episode_number"],
			"name": data.Next["name"]}), ""
	})
}

var (
	handlePattern = regexp.MustCompile(`^@?[A-Za-z0-9._-]{1,100}$`)
	imdbPerson    = regexp.MustCompile(`^nm\d+$`)
)

func (s *Server) handleTmdbPersonLinks(w http.ResponseWriter, r *http.Request) {
	id, ok := requiredTmdbID(w, r)
	if !ok {
		return
	}
	s.tmdbRoute(w, r, "tmdb-person:"+id, 24*time.Hour, func(key string) (M, string) {
		var p M
		q := url.Values{"append_to_response": {"external_ids"}}
		if msg := s.tmdb(r.Context(), key, "person/"+url.PathEscape(id), q, &p); msg != "" {
			return nil, msg
		}
		ids, _ := p["external_ids"].(M)
		text := func(m M, k string) string { s, _ := m[k].(string); return strings.TrimSpace(s) }
		match := func(v string, re *regexp.Regexp) any {
			if v != "" && re.MatchString(v) {
				return v
			}
			return nil
		}
		imdb := text(p, "imdb_id")
		if imdb == "" {
			imdb = text(ids, "imdb_id")
		}
		var homepage any
		if u, err := url.Parse(text(p, "homepage")); err == nil && (u.Scheme == "http" || u.Scheme == "https") {
			homepage = u.String()
		}
		return compact(M{"success": true, "imdbId": match(imdb, imdbPerson), "homepage": homepage,
			"instagram": match(text(ids, "instagram_id"), handlePattern), "twitter": match(text(ids, "twitter_id"), handlePattern),
			"facebook": match(text(ids, "facebook_id"), handlePattern), "tiktok": match(text(ids, "tiktok_id"), handlePattern),
			"youtube": match(text(ids, "youtube_id"), handlePattern)}), ""
	})
}

// handleTmdbThumb redirects to a TMDB backdrop for a title outside the
// library, found by TMDB id or by IMDb id.
func (s *Server) handleTmdbThumb(w http.ResponseWriter, r *http.Request) {
	q := r.URL.Query()
	typ := "movie"
	if strings.EqualFold(q.Get("type"), "tv") {
		typ = "tv"
	}
	id := strings.TrimSpace(q.Get("tmdbId"))
	imdb := strings.TrimSpace(q.Get("imdbId"))
	key, err := s.tmdbKey(r)
	if err != nil || key == "" || (id == "" && imdb == "") ||
		(id != "" && !providerID.MatchString(id)) || (imdb != "" && !providerID.MatchString(imdb)) {
		http.NotFound(w, r)
		return
	}
	if id == "" {
		var asked bool
		if typ, id, asked = s.tmdbFind(r.Context(), key, imdb); !asked {
			http.NotFound(w, r)
			return
		}
	}
	path, ok := s.ext.cache.get("tmdb-thumb:"+typ+":"+id, 7*24*time.Hour)
	if !ok && id != "" {
		// Titled backdrops are picked in English, since Silo doesn't share its
		// metadata language with plugins.
		var details M
		q := url.Values{"append_to_response": {"images"}, "include_image_language": {"en,null"}}
		if msg := s.tmdb(r.Context(), key, typ+"/"+url.PathEscape(id), q, &details); msg != "" && !strings.Contains(msg, "status 404") {
			http.NotFound(w, r)
			return
		}
		path = pickBackdrop(details)
		s.ext.cache.set("tmdb-thumb:"+typ+":"+id, path)
	}
	if p, _ := path.(string); p != "" {
		scheme := "https"
		if strings.EqualFold(q.Get("scheme"), "http") {
			scheme = "http"
		}
		w.Header().Set("Cache-Control", "public,max-age=86400")
		http.Redirect(w, r, scheme+"://image.tmdb.org/t/p/w1280"+p, http.StatusFound)
		return
	}
	http.NotFound(w, r)
}

// tmdbFind looks a title up on TMDB by IMDb id. ok is false when TMDB
// couldn't be asked; an unknown title is ok with an empty id.
func (s *Server) tmdbFind(ctx context.Context, key, imdb string) (typ, id string, ok bool) {
	found, hit := s.ext.cache.get("tmdb-find:"+imdb, 30*24*time.Hour)
	if !hit {
		var data map[string][]M
		if msg := s.tmdb(ctx, key, "find/"+url.PathEscape(imdb), url.Values{"external_source": {"imdb_id"}}, &data); msg != "" {
			return "", "", false
		}
		found = ""
		for _, kind := range [][2]string{{"movie_results", "movie"}, {"tv_results", "tv"}} {
			if hits := data[kind[0]]; len(hits) > 0 {
				if n, ok := num(hits[0]["id"]); ok {
					found = kind[1] + ":" + strconv.FormatInt(int64(n), 10)
					break
				}
			}
		}
		s.ext.cache.set("tmdb-find:"+imdb, found)
	}
	typ, id, _ = strings.Cut(found.(string), ":")
	return typ, id, true
}

func pickBackdrop(details M) string {
	images, _ := details["images"].(M)
	backdrops, _ := images["backdrops"].([]any)
	for _, b := range backdrops {
		bm, _ := b.(M)
		if l, _ := bm["iso_639_1"].(string); strings.EqualFold(l, "en") {
			if p, _ := bm["file_path"].(string); p != "" {
				return p
			}
		}
	}
	p, _ := details["backdrop_path"].(string)
	return p
}
