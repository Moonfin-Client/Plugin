package anime

import (
	"context"
	"encoding/json"
	"fmt"
	"log/slog"
	"net/http"
	"strconv"
	"time"
)

const (
	// Guards against a malformed pagination answer spinning forever. Pages hold 100 episodes.
	recapMaxPages    = 30
	recapMaxAttempts = 3
)

// ResolveMalID finds a series' MyAnimeList id from a stated one, then the
// mapping table, then AniList by its AniList id, then an AniList search for
// its name. False when nothing leads to one.
func (s *Sources) ResolveMalID(ctx context.Context, ids map[string]string, name string) (int, bool) {
	keys := ProviderKeys(ids)
	for _, k := range keys {
		if k.Provider == "mal" {
			if mal, err := strconv.Atoi(k.Value); err == nil {
				return mal, true
			}
		}
	}
	s.EnsureMapping(ctx)
	if mal, ok := s.resolveMapped(keys); ok {
		return mal, true
	}
	for _, k := range keys {
		if id, err := strconv.Atoi(k.Value); k.Provider == "anilist" && err == nil {
			if mal, ok := s.aniListMal(ctx, "query($id:Int){Media(id:$id,type:ANIME){idMal title{romaji english native}}}", map[string]any{"id": id}, ""); ok {
				return mal, true
			}
		}
	}
	// Last, and the least reliable, since the search is fuzzy. The answer only
	// counts when one of its titles normalizes to the name asked for.
	if Normalize(name) == "" {
		return 0, false
	}
	return s.aniListMal(ctx, "query($search:String){Media(search:$search,type:ANIME){idMal title{romaji english native}}}", map[string]any{"search": name}, name)
}

// aniListMal asks AniList for one Media's idMal. With a name, the answer must
// carry that title.
func (s *Sources) aniListMal(ctx context.Context, query string, variables map[string]any, name string) (int, bool) {
	if s.aniListGate.wait(ctx, s.AniListSpacing) != nil {
		return 0, false
	}
	body, _ := json.Marshal(map[string]any{"query": query, "variables": variables})
	status, answer, err := s.fetch(ctx, http.MethodPost, s.AniListURL, body, 15*time.Second, 1<<20)
	if err != nil || status != http.StatusOK {
		return 0, false
	}
	var v struct {
		Data struct {
			Media *struct {
				IDMal json.RawMessage `json:"idMal"`
				Title map[string]any  `json:"title"`
			} `json:"Media"`
		} `json:"data"`
	}
	if json.Unmarshal(answer, &v) != nil || v.Data.Media == nil {
		return 0, false
	}
	mal, ok := readInt(v.Data.Media.IDMal)
	if !ok || name == "" {
		return mal, ok
	}
	for _, field := range []string{"romaji", "english", "native"} {
		if title, isString := v.Data.Media.Title[field].(string); isString && Normalize(title) == Normalize(name) {
			return mal, true
		}
	}
	slog.Debug("anime markers: the AniList search came back with a different show", "name", name)
	return 0, false
}

// RecapLookup is the episode numbers a MyAnimeList entry marks as recaps, and
// how many episodes the entry covers.
type RecapLookup struct {
	Recaps   map[int]bool
	Episodes int
}

// FetchRecaps reads which of an entry's episodes are recaps from a mirror of
// MyAnimeList's episode lists. False when the lookup failed, so the caller
// leaves the show alone rather than recording an empty answer.
func (s *Sources) FetchRecaps(ctx context.Context, malID int) (RecapLookup, bool) {
	lookup := RecapLookup{Recaps: map[int]bool{}}
	for page := 1; page <= recapMaxPages; page++ {
		v, ok := s.episodePage(ctx, malID, page)
		if !ok {
			return lookup, false
		}
		for _, e := range v.Data {
			lookup.Episodes++
			if e.Recap {
				lookup.Recaps[e.MalID] = true
			}
		}
		if !v.Pagination.HasNextPage {
			return lookup, true
		}
	}
	// Out of pages with more to come. A partial answer would let the caller
	// write recap=false over every episode it never saw.
	slog.Debug("anime markers: the recap lookup hit the page cap", "mal", malID)
	return lookup, false
}

type episodePage struct {
	Data []struct {
		// On the episodes route this is the episode's number within the entry.
		MalID int  `json:"mal_id"`
		Recap bool `json:"recap"`
	} `json:"data"`
	Pagination struct {
		HasNextPage bool `json:"has_next_page"`
	} `json:"pagination"`
}

func (s *Sources) episodePage(ctx context.Context, malID, page int) (episodePage, bool) {
	var v episodePage
	url := fmt.Sprintf("%s/anime/%d/episodes?page=%d", s.EpisodeAPIBase, malID, page)
	for attempt := 1; attempt <= recapMaxAttempts; attempt++ {
		if s.episodeGate.wait(ctx, s.EpisodeSpacing) != nil {
			return v, false
		}
		status, body, err := s.fetch(ctx, http.MethodGet, url, nil, 20*time.Second, 8<<20)
		switch {
		case err == nil && status == http.StatusOK:
			return v, json.Unmarshal(body, &v) == nil
		case err != nil:
			s.Log.Printf("    episodes MAL %d page %d -> %v", malID, page, err)
		default:
			s.Log.Printf("    episodes MAL %d page %d -> HTTP %d %.200s", malID, page, status, body)
			// 5xx and 429 mean the API is briefly unavailable. A 404 means the
			// entry doesn't exist, so retrying won't help.
			if status < 500 && status != http.StatusTooManyRequests {
				return v, false
			}
		}
		if ctx.Err() != nil {
			return v, false
		}
		if attempt < recapMaxAttempts {
			time.Sleep(s.RetryDelay * time.Duration(attempt))
		}
	}
	return v, false
}
