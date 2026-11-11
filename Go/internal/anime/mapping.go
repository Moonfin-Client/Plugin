package anime

import (
	"context"
	"encoding/json"
	"log/slog"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"
)

const mappingMaxAge = 14 * 24 * time.Hour

// mappingState is Fribb/anime-lists, a community mirror of AniList's own id
// mapping, which leads a provider id to a MyAnimeList id without a request.
type mappingState struct {
	mu          sync.Mutex
	byProvider  map[string]map[string]int
	count       int
	writtenAt   time.Time
	lastAttempt time.Time
}

func (s *Sources) mappingPath() string { return filepath.Join(s.dir, "anime_id_mapping.json") }

// EnsureMapping loads the table, downloading it only when the copy on disk is
// missing or stale, and reading it again only when the file has changed.
func (s *Sources) EnsureMapping(ctx context.Context) {
	m := &s.mapping
	m.mu.Lock()
	defer m.mu.Unlock()
	info, err := os.Stat(s.mappingPath())
	stale := err != nil || time.Since(info.ModTime()) > mappingMaxAge
	if m.byProvider != nil && !stale {
		return
	}
	if stale && time.Since(m.lastAttempt) > downloadRetry {
		m.lastAttempt = time.Now()
		s.downloadMapping(ctx)
		info, err = os.Stat(s.mappingPath())
	}
	if err != nil || (m.byProvider != nil && info.ModTime().Equal(m.writtenAt)) {
		return
	}
	b, err := os.ReadFile(s.mappingPath())
	if err == nil {
		m.byProvider, m.count, err = parseMapping(b)
	}
	if err != nil {
		slog.Warn("anime markers: the id mapping couldn't be loaded", "error", err)
		return
	}
	m.writtenAt = info.ModTime()
	slog.Info("anime markers: id mapping loaded", "entries", m.count)
}

func (s *Sources) downloadMapping(ctx context.Context) {
	status, body, err := s.fetch(ctx, http.MethodGet, s.MappingURL, nil, 2*time.Minute, 64<<20)
	if err != nil || status != http.StatusOK {
		slog.Warn("anime markers: the id mapping download failed, keeping any copy already saved", "status", status, "error", err)
		return
	}
	if err := writeFile(s.mappingPath(), body); err != nil {
		slog.Warn("anime markers: save the id mapping", "error", err)
	}
}

type mappingEntry struct {
	MalID       json.RawMessage `json:"mal_id"`
	AniListID   json.RawMessage `json:"anilist_id"`
	AniDBID     json.RawMessage `json:"anidb_id"`
	AniSearchID json.RawMessage `json:"anisearch_id"`
	KitsuID     json.RawMessage `json:"kitsu_id"`
	TvdbID      json.RawMessage `json:"tvdb_id"`
	TmdbID      json.RawMessage `json:"themoviedb_id"`
	ImdbID      json.RawMessage `json:"imdb_id"`
}

// parseMapping indexes the table by provider. One TVDB or TMDB id covers a
// whole show that MyAnimeList splits per season, so the first entry wins,
// which is the earliest season.
func parseMapping(b []byte) (map[string]map[string]int, int, error) {
	var entries []mappingEntry
	if err := json.Unmarshal(b, &entries); err != nil {
		return nil, 0, err
	}
	index := map[string]map[string]int{}
	add := func(provider, key string, mal int) {
		if key = strings.TrimSpace(key); key == "" {
			return
		}
		if provider == "imdb" {
			key = strings.ToLower(key)
		}
		if index[provider] == nil {
			index[provider] = map[string]int{}
		}
		if _, taken := index[provider][key]; !taken {
			index[provider][key] = mal
		}
	}
	count := 0
	for _, e := range entries {
		mal, ok := readInt(e.MalID)
		if !ok {
			continue
		}
		count++
		for provider, raw := range map[string]json.RawMessage{"anilist": e.AniListID, "anidb": e.AniDBID, "anisearch": e.AniSearchID, "kitsu": e.KitsuID, "tvdb": e.TvdbID} {
			if id, ok := readInt(raw); ok {
				add(provider, strconv.Itoa(id), mal)
			}
		}
		var tmdb struct {
			TV json.RawMessage `json:"tv"`
		}
		if json.Unmarshal(e.TmdbID, &tmdb) == nil {
			if id, ok := readInt(tmdb.TV); ok {
				add("tmdb", strconv.Itoa(id), mal)
			}
		}
		// The table lists IMDb ids as an array, since one entry can have several.
		var imdb []string
		var one string
		if json.Unmarshal(e.ImdbID, &imdb) != nil && json.Unmarshal(e.ImdbID, &one) == nil {
			imdb = []string{one}
		}
		for _, id := range imdb {
			add("imdb", id, mal)
		}
	}
	return index, count, nil
}

// readInt reads a JSON number that fits an int32, so it accepts the same ids
// the Jellyfin and Emby plugins do.
func readInt(raw json.RawMessage) (int, bool) {
	var n json.Number
	if len(raw) == 0 || raw[0] == '"' || json.Unmarshal(raw, &n) != nil {
		return 0, false
	}
	v, err := strconv.ParseInt(n.String(), 10, 32)
	return int(v), err == nil
}

// ResolveProvider leads one provider id to a MyAnimeList id. Providers use the
// names ProviderKeys gives them: anidb, tvdb and so on.
func (s *Sources) ResolveProvider(provider, value string) (int, bool) {
	s.mapping.mu.Lock()
	defer s.mapping.mu.Unlock()
	value = strings.TrimSpace(value)
	if provider == "imdb" {
		value = strings.ToLower(value)
	} else if _, err := strconv.Atoi(value); err != nil {
		return 0, false
	}
	mal, ok := s.mapping.byProvider[provider][value]
	return mal, ok
}

// resolveMapped tries a series' provider ids against the table, the anime
// databases first.
func (s *Sources) resolveMapped(keys []ProviderKey) (int, bool) {
	for _, provider := range []string{"anilist", "anidb", "anisearch", "kitsu", "tvdb", "tmdb", "imdb"} {
		for _, k := range keys {
			if k.Provider == provider {
				if mal, ok := s.ResolveProvider(provider, k.Value); ok {
					return mal, true
				}
			}
		}
	}
	return 0, false
}
