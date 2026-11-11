package anime

import (
	"context"
	"encoding/json"
	"fmt"
	"log/slog"
	"net/http"
	"path/filepath"
	"slices"
	"strconv"
	"strings"
	"sync"
	"time"
)

const (
	aliasMaxAge    = 30 * 24 * time.Hour
	aniListBatch   = 50
	aniListMediaQL = "query($ids:[Int]){Page(perPage:50){media(%s:$ids,type:ANIME){id idMal title{romaji english} synonyms}}}"
)

// providers are the ids that can lead to a show, in the order their titles
// are tried. The anime databases come first because they name one entry,
// where a TVDB or TMDB id covers a whole show MyAnimeList splits up.
var providers = []struct {
	name string
	keys []string
}{
	{"mal", []string{"MyAnimeList", "Mal"}},
	{"anilist", []string{"AniList"}},
	{"anidb", []string{"AniDB"}},
	{"kitsu", []string{"Kitsu", "KitsuIo"}},
	{"anisearch", []string{"AniSearch"}},
	{"tvdb", []string{"Tvdb", "TheTVDB"}},
	{"tmdb", []string{"Tmdb", "TheMovieDb"}},
	{"imdb", []string{"Imdb"}},
}

// ProviderKey is one provider id of a series as an alias cache key, such as "anidb:266".
type ProviderKey struct{ Key, Provider, Value string }

// ProviderKeys lists a series' provider ids, given by any capitalization of
// Jellyfin's provider names, in the order providers tries them.
func ProviderKeys(ids map[string]string) []ProviderKey {
	names := make([]string, 0, len(ids))
	for name := range ids {
		names = append(names, name)
	}
	slices.Sort(names)
	var out []ProviderKey
	seen := map[string]bool{}
	for _, p := range providers {
		for _, name := range names {
			value := strings.TrimSpace(ids[name])
			if value == "" || !slices.ContainsFunc(p.keys, func(k string) bool { return strings.EqualFold(k, name) }) {
				continue
			}
			key := p.name + ":" + value
			if !seen[key] {
				seen[key] = true
				out = append(out, ProviderKey{key, p.name, value})
			}
		}
	}
	return out
}

type aliasState struct {
	mu        sync.Mutex
	m         map[string]AliasEntry
	refreshMu sync.Mutex
}

func (s *Sources) aliasPath() string { return filepath.Join(s.dir, "anime_title_aliases.json") }

// loadedAliases must be called with aliases.mu held.
func (s *Sources) loadedAliases() map[string]AliasEntry {
	if s.aliases.m == nil {
		s.aliases.m = map[string]AliasEntry{}
		if err := readJSONFile(s.aliasPath(), &s.aliases.m); err != nil {
			slog.Warn("anime markers: the title aliases couldn't be read, starting fresh", "error", err)
			s.aliases.m = map[string]AliasEntry{}
		}
	}
	return s.aliases.m
}

// Alias is the titles one provider id is known by.
type Alias struct {
	Key   string
	Entry AliasEntry
}

// Aliases are the cached titles of each of the series' provider ids that has
// any, best first. It never goes to the network.
func (s *Sources) Aliases(ids map[string]string) []Alias {
	s.aliases.mu.Lock()
	defer s.aliases.mu.Unlock()
	cache := s.loadedAliases()
	var out []Alias
	for _, k := range ProviderKeys(ids) {
		if e, ok := cache[k.Key]; ok && len(e.Titles) > 0 {
			out = append(out, Alias{k.Key, e})
		}
	}
	return out
}

// RefreshAliases fetches titles for every provider id of these series that
// isn't cached, or was cached over a month ago. An id that can't be tied to an
// anime entry without a request costs nothing, so a whole library can be
// handed over. It says how many ids were stored.
func (s *Sources) RefreshAliases(ctx context.Context, series []map[string]string) (int, error) {
	s.aliases.refreshMu.Lock()
	defer s.aliases.refreshMu.Unlock()
	s.EnsureMapping(ctx)

	// An AniList id is asked about directly. Anything else needs a MyAnimeList
	// id, stated outright or found through the mapping table.
	byAniList := map[int][]string{}
	byMal := map[int][]string{}
	s.aliases.mu.Lock()
	cache := s.loadedAliases()
	for _, ids := range series {
		for _, k := range ProviderKeys(ids) {
			if cached, ok := cache[k.Key]; ok && time.Since(cached.FetchedAt) < aliasMaxAge {
				continue
			}
			if k.Provider == "anilist" {
				if id, err := strconv.Atoi(k.Value); err == nil {
					addKey(byAniList, id, k.Key)
				}
				continue
			}
			mal, err := strconv.Atoi(k.Value)
			ok := err == nil
			if k.Provider != "mal" {
				mal, ok = s.ResolveProvider(k.Provider, k.Value)
			}
			if ok {
				addKey(byMal, mal, k.Key)
			}
		}
	}
	s.aliases.mu.Unlock()

	stored, err := s.fetchAliases(ctx, "id_in", byAniList, func(m AniListMedia) *int { return m.ID })
	if err == nil {
		var more int
		more, err = s.fetchAliases(ctx, "idMal_in", byMal, func(m AniListMedia) *int { return m.MalID })
		stored += more
	}
	if stored > 0 {
		s.aliases.mu.Lock()
		if werr := writeJSONFile(s.aliasPath(), s.loadedAliases()); werr != nil {
			slog.Warn("anime markers: save the title aliases", "error", werr)
		}
		s.aliases.mu.Unlock()
		slog.Info("anime markers: titles looked up", "ids", stored)
	}
	return stored, err
}

func addKey(m map[int][]string, id int, key string) {
	if !slices.Contains(m[id], key) {
		m[id] = append(m[id], key)
	}
}

func (s *Sources) fetchAliases(ctx context.Context, field string, keysByID map[int][]string, idOf func(AniListMedia) *int) (int, error) {
	ids := make([]int, 0, len(keysByID))
	for id := range keysByID {
		ids = append(ids, id)
	}
	slices.Sort(ids)
	stored := 0
	for batch := range slices.Chunk(ids, aniListBatch) {
		if err := ctx.Err(); err != nil {
			return stored, err
		}
		media, ok := s.queryAniList(ctx, field, batch)
		if !ok {
			// A failed request stores nothing, so the next run asks again.
			continue
		}
		found := map[int]AniListMedia{}
		for _, m := range media {
			if id := idOf(m); id != nil {
				if _, taken := found[*id]; !taken {
					found[*id] = m
				}
			}
		}
		s.aliases.mu.Lock()
		cache := s.loadedAliases()
		for _, id := range batch {
			// AniList answered without an entry: stored empty, so it isn't
			// asked again until it goes stale.
			m := found[id]
			entry := AliasEntry{MalID: m.MalID, Titles: m.Titles, FetchedAt: time.Now().UTC()}
			if entry.Titles == nil {
				entry.Titles = []string{}
			}
			for _, key := range keysByID[id] {
				cache[key] = entry
				stored++
				if len(entry.Titles) == 0 {
					s.Log.Printf("alias %s: AniList has no entry for it", key)
				} else {
					mal := "?"
					if entry.MalID != nil {
						mal = strconv.Itoa(*entry.MalID)
					}
					s.Log.Printf("alias %s: MAL %s known as %s", key, mal, strings.Join(entry.Titles[:min(5, len(entry.Titles))], " | "))
				}
			}
		}
		s.aliases.mu.Unlock()
	}
	return stored, nil
}

func (s *Sources) queryAniList(ctx context.Context, field string, ids []int) ([]AniListMedia, bool) {
	if s.aniListGate.wait(ctx, s.AniListSpacing) != nil {
		return nil, false
	}
	body, _ := json.Marshal(map[string]any{"query": fmt.Sprintf(aniListMediaQL, field), "variables": map[string]any{"ids": ids}})
	status, answer, err := s.fetch(ctx, http.MethodPost, s.AniListURL, body, 20*time.Second, 8<<20)
	if err != nil || status != http.StatusOK {
		s.Log.Printf("alias AniList %s lookup of %d ids -> HTTP %d %v", field, len(ids), status, err)
		return nil, false
	}
	media, ok := ParseMedia(answer)
	return media, ok
}

// AniListMedia is one AniList entry: its ids and its usable titles, English first.
type AniListMedia struct {
	ID, MalID *int
	Titles    []string
}

// ParseMedia reads a Page.media answer. False when it isn't one, so a
// malformed answer isn't taken for AniList having no entries.
func ParseMedia(b []byte) ([]AniListMedia, bool) {
	var v struct {
		Data *struct {
			Page *struct {
				Media *[]json.RawMessage `json:"media"`
			} `json:"Page"`
		} `json:"data"`
	}
	if json.Unmarshal(b, &v) != nil || v.Data == nil || v.Data.Page == nil || v.Data.Page.Media == nil {
		return nil, false
	}
	out := []AniListMedia{}
	for _, raw := range *v.Data.Page.Media {
		var m struct {
			ID    json.RawMessage `json:"id"`
			IDMal json.RawMessage `json:"idMal"`
			Title struct {
				English, Romaji any
			} `json:"title"`
			Synonyms []any `json:"synonyms"`
		}
		if len(raw) == 0 || raw[0] != '{' || json.Unmarshal(raw, &m) != nil {
			continue
		}
		var titles []string
		for _, t := range append([]any{m.Title.English, m.Title.Romaji}, m.Synonyms...) {
			if title, ok := t.(string); ok {
				titles = addTitle(titles, title)
			}
		}
		out = append(out, AniListMedia{ID: intPtr(m.ID), MalID: intPtr(m.IDMal), Titles: titles})
	}
	return out, true
}

func intPtr(raw json.RawMessage) *int {
	if n, ok := readInt(raw); ok {
		return &n
	}
	return nil
}

// addTitle keeps a title only when it folds to a usable key. A title in a
// non-Latin script folds to nothing and could never match.
func addTitle(titles []string, title string) []string {
	title = strings.TrimSpace(title)
	if Normalize(title) == "" || slices.ContainsFunc(titles, func(t string) bool { return strings.EqualFold(t, title) }) {
		return titles
	}
	return append(titles, title)
}
