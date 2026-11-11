package plex

import (
	"context"
	"encoding/json"
	"fmt"
	"maps"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/Moonfin-Client/Plugin/Go/internal/api"
	"github.com/Moonfin-Client/Plugin/Go/internal/similar"
)

// A caller's library listing is reused this long, so titles added since
// become candidates once it runs out.
const similarTTL = 10 * time.Minute

// similarListings bounds the listings kept, one per caller and library.
const similarListings = 64

type similarItem struct {
	RatingKey string `json:"ratingKey"`
	Type      string `json:"type"`
	GUID      string `json:"guid"`
	// Go matches JSON keys without caring about case, so these only keep
	// Plex's Guid and Rating lists from being read into guid and rating.
	GUIDs                 json.RawMessage `json:"Guid"`
	Ratings               json.RawMessage `json:"Rating"`
	Title                 string          `json:"title"`
	Year                  int             `json:"year"`
	Studio                string          `json:"studio"`
	Rating                *float32        `json:"rating"`
	AudienceRating        *float32        `json:"audienceRating"`
	RatingImage           string          `json:"ratingImage"`
	OriginallyAvailableAt string          `json:"originallyAvailableAt"`
	Genre                 []plexTag       `json:"Genre"`
	Collection            []plexTag       `json:"Collection"`
	Label                 []plexTag       `json:"Label"`
	Role                  []plexTag       `json:"Role"`
	Director              []plexTag       `json:"Director"`
	Writer                []plexTag       `json:"Writer"`
}

// facts reads the rating the way the app does, so a Rotten Tomatoes critic
// score never stands in for the community rating.
func (it similarItem) facts() similar.Facts {
	f := similar.Facts{Name: it.Title, Genres: tags(it.Genre), Tags: append(tags(it.Collection), tags(it.Label)...),
		CommunityRating: it.AudienceRating}
	if it.Studio != "" {
		f.Studios = []string{it.Studio}
	}
	if it.Year > 0 {
		f.Year = &it.Year
	}
	if f.CommunityRating == nil && !strings.HasPrefix(it.RatingImage, "rottentomatoes") {
		f.CommunityRating = it.Rating
	}
	return f
}

func (it similarItem) credits() []similar.Credit {
	var out []similar.Credit
	for _, g := range []struct {
		role similar.Role
		list []plexTag
	}{{similar.Actor, it.Role}, {similar.Director, it.Director}, {similar.Writer, it.Writer}} {
		for _, t := range g.list {
			out = append(out, similar.Credit{Name: t.Tag, Role: g.role})
		}
	}
	return out
}

func (it similarItem) premiere() time.Time {
	t, _ := time.Parse(time.DateOnly, it.OriginallyAvailableAt)
	return t
}

type similarCandidate struct {
	key, guid string
	facts     similar.Facts
	premiere  time.Time
}

type similarCache struct {
	mu sync.Mutex
	m  map[string]*similarListing
}

type similarListing struct {
	done  chan struct{}
	at    time.Time
	items []similarCandidate
	err   error
}

func (l *similarListing) fresh() bool {
	select {
	case <-l.done:
		return l.err == nil && time.Since(l.at) < similarTTL
	default:
		return true
	}
}

// SimilarItems ranks the caller's movies or shows against the item with
// Moonfin Recommends. Everything is read as the caller, so Plex leaves out
// what isn't shared with them or is over their parental limit.
func (c *Companion) SimilarItems(ctx context.Context, caller api.Identity, id string, limit int, exclude []string) ([]json.RawMessage, error) {
	status, body, err := c.get(ctx, c.cfg.PMS+"/library/metadata/"+url.PathEscape(id), caller.Token)
	switch {
	case err != nil:
		return nil, err
	case status == http.StatusBadRequest || status == http.StatusUnauthorized || status == http.StatusForbidden || status == http.StatusNotFound:
		return nil, api.ErrItemNotFound
	case status != http.StatusOK:
		return nil, fmt.Errorf("/library/metadata answered %d", status)
	}
	var v struct {
		MediaContainer struct {
			Metadata []similarItem `json:"Metadata"`
		} `json:"MediaContainer"`
	}
	if err := json.Unmarshal(body, &v); err != nil {
		return nil, err
	}
	if len(v.MediaContainer.Metadata) == 0 {
		return nil, api.ErrItemNotFound
	}
	item := v.MediaContainer.Metadata[0]
	if item.Type != "movie" && item.Type != "show" {
		return nil, nil
	}

	kind, itemType := arrKind(item.Type == "show")
	sections, err := c.sections(ctx, caller.Token)
	if err != nil {
		return nil, err
	}
	skip := map[string]bool{item.RatingKey: true}
	for _, key := range exclude {
		skip[key] = true
	}
	// Plex keeps a title once per library, so a copy in another library,
	// the seed's own included, is left out by its guid.
	seen := map[string]bool{item.GUID: true}
	var candidates []similarCandidate
	for _, s := range sections {
		if s.Type != kind {
			continue
		}
		listing, err := c.similarListing(ctx, caller, s.Key, itemType)
		if err != nil {
			return nil, err
		}
		for _, it := range listing {
			if skip[it.key] || it.guid != "" && seen[it.guid] {
				continue
			}
			seen[it.guid] = true
			candidates = append(candidates, it)
		}
	}

	// A library listing cuts genres and cast short, so it only picks the
	// shortlist, whose full metadata is then ranked as Jellyfin ranks.
	seed := similar.NewSeed(item.facts(), item.credits())
	shortlist := similar.Rank(candidates, seed, max(limit, similar.PeopleScoringPoolSize),
		func(it similarCandidate) similar.Facts { return it.facts },
		func(it similarCandidate) time.Time { return it.premiere },
		func([]similarCandidate) func(similarCandidate) []similar.Credit {
			return func(similarCandidate) []similar.Credit { return nil }
		})
	raw := map[string]json.RawMessage{}
	for start := 0; start < len(shortlist); start += smartBatch {
		keys := []string{}
		for _, it := range shortlist[start:min(start+smartBatch, len(shortlist))] {
			keys = append(keys, it.key)
		}
		got, err := c.SmartItems(ctx, caller, keys)
		if err != nil {
			return nil, err
		}
		maps.Copy(raw, got)
	}
	var full []similarItem
	for _, it := range shortlist {
		var details similarItem
		if r, ok := raw[it.key]; ok && json.Unmarshal(r, &details) == nil {
			full = append(full, details)
		}
	}
	ranked := similar.Rank(full, seed, limit, similarItem.facts, similarItem.premiere,
		func([]similarItem) func(similarItem) []similar.Credit { return similarItem.credits })
	out := make([]json.RawMessage, 0, len(ranked))
	for _, it := range ranked {
		out = append(out, raw[it.RatingKey])
	}
	return out, nil
}

// similarListing is the caller's movies or shows in one library. One read
// serves everyone asking for the same listing while it runs.
func (c *Companion) similarListing(ctx context.Context, caller api.Identity, section, itemType string) ([]similarCandidate, error) {
	key := fmt.Sprint(caller.UserID, "/", section, "/", itemType)
	sc := &c.similar
	sc.mu.Lock()
	l := sc.m[key]
	if l == nil || !l.fresh() {
		for k, old := range sc.m {
			if !old.fresh() {
				delete(sc.m, k)
			}
		}
		if sc.m == nil || len(sc.m) >= similarListings {
			sc.m = map[string]*similarListing{}
		}
		l = &similarListing{done: make(chan struct{})}
		sc.m[key] = l
		go func() {
			defer close(l.done)
			// It outlives a caller that gives up, since others may be waiting.
			l.items, l.err = c.readListing(context.WithoutCancel(ctx), caller.Token, section, itemType)
			l.at = time.Now()
		}()
	}
	sc.mu.Unlock()
	select {
	case <-l.done:
		return l.items, l.err
	case <-ctx.Done():
		return nil, ctx.Err()
	}
}

func (c *Companion) readListing(ctx context.Context, token, section, itemType string) ([]similarCandidate, error) {
	const page = 500
	var out []similarCandidate
	for start := 0; ; start += page {
		var v struct {
			MediaContainer struct {
				Metadata []similarItem `json:"Metadata"`
			} `json:"MediaContainer"`
		}
		q := url.Values{"type": {itemType}, "X-Plex-Container-Start": {strconv.Itoa(start)}, "X-Plex-Container-Size": {strconv.Itoa(page)}}
		if err := c.getJSON(ctx, c.cfg.PMS+"/library/sections/"+url.PathEscape(section)+"/all?"+q.Encode(), token, &v); err != nil {
			return nil, err
		}
		for _, it := range v.MediaContainer.Metadata {
			out = append(out, similarCandidate{key: it.RatingKey, guid: it.GUID, facts: it.facts(), premiere: it.premiere()})
		}
		if len(v.MediaContainer.Metadata) < page {
			return out, nil
		}
	}
}
