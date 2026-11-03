package plex

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"sync"

	"github.com/Moonfin-Client/Plugin/Go/internal/api"
)

// The cast in a library listing is cut short, so details come in batches of
// this many items through /library/metadata, which has everyone.
const smartBatch = 50

type plexTag struct {
	ID    int    `json:"id,omitempty"`
	Tag   string `json:"tag"`
	Thumb string `json:"thumb,omitempty"`
}

type plexItem struct {
	RatingKey   string    `json:"ratingKey"`
	Title       string    `json:"title"`
	ParentTitle string    `json:"parentTitle"`
	Year        int       `json:"year"`
	Summary     string    `json:"summary"`
	Genre       []plexTag `json:"Genre"`
	Director    []plexTag `json:"Director"`
	Role        []plexTag `json:"Role"`
	GUID        plexGUIDs `json:"Guid"`
}

// plexGUIDs is Plex's Guid list. Items also carry a lowercase guid string,
// which Go matches to this field without caring about case, so anything that
// isn't a list is skipped.
type plexGUIDs []struct {
	ID string `json:"id"`
}

func (g *plexGUIDs) UnmarshalJSON(b []byte) error {
	if len(b) == 0 || b[0] != '[' {
		return nil
	}
	type list plexGUIDs
	return json.Unmarshal(b, (*list)(g))
}

// SmartLibrary reads every movie, show, album and artist as the server owner,
// since the index covers the whole server and each search is checked against
// the caller afterwards.
func (c *Companion) SmartLibrary(ctx context.Context) (api.SmartLibrary, error) {
	lib := api.SmartLibrary{People: map[string]json.RawMessage{}}
	token := c.ownerToken()
	if token == "" {
		return lib, errors.New("the server owner hasn't signed in on the Moonbase page yet")
	}
	sections, err := c.sections(ctx, token)
	if err != nil {
		return lib, err
	}

	for _, s := range sections {
		switch s.Type {
		case "movie", "show":
			kind := map[string]string{"movie": "1", "show": "2"}[s.Type]
			keys, err := c.sectionKeys(ctx, token, s.Key, kind)
			if err != nil {
				return lib, err
			}
			for start := 0; start < len(keys); start += smartBatch {
				items, err := c.metadata(ctx, token, keys[start:min(start+smartBatch, len(keys))])
				if err != nil {
					return lib, err
				}
				for _, it := range items {
					lib.Titles = append(lib.Titles, smartTitle(it, s.Type == "show", lib.People))
				}
			}
		case "artist":
			albums, err := c.sectionItems(ctx, token, s.Key, "9")
			if err != nil {
				return lib, err
			}
			for _, a := range albums {
				lib.Albums = append(lib.Albums, api.SmartAlbum{ID: a.RatingKey, Title: a.Title, Year: a.Year,
					Artists: []string{a.ParentTitle}, Genres: tags(a.Genre), Overview: a.Summary})
			}
			artists, err := c.sectionItems(ctx, token, s.Key, "8")
			if err != nil {
				return lib, err
			}
			for _, a := range artists {
				lib.Artists = append(lib.Artists, api.SmartArtist{ID: a.RatingKey, Name: a.Title, Genres: tags(a.Genre), Overview: a.Summary})
			}
		}
	}
	return lib, nil
}

func smartTitle(it plexItem, series bool, people map[string]json.RawMessage) api.SmartTitle {
	t := api.SmartTitle{ID: it.RatingKey, Series: series, Title: it.Title, Year: it.Year,
		Genres: tags(it.Genre), Actors: tags(it.Role), Directors: tags(it.Director), Overview: it.Summary}
	for _, g := range it.GUID {
		if id, ok := strings.CutPrefix(g.ID, "tmdb://"); ok {
			t.TmdbID = id
		}
	}
	for _, p := range append(it.Role, it.Director...) {
		if _, known := people[p.Tag]; p.ID != 0 && (!known || p.Thumb != "") {
			people[p.Tag], _ = json.Marshal(map[string]any{"type": "person", "id": p.ID, "tag": p.Tag, "thumb": p.Thumb})
		}
	}
	return t
}

// SmartItems reads the items as the caller, so Plex itself leaves out what
// isn't shared with them or is over their parental limit.
func (c *Companion) SmartItems(ctx context.Context, caller api.Identity, keys []string) (map[string]json.RawMessage, error) {
	out := map[string]json.RawMessage{}
	status, body, err := c.get(ctx, c.cfg.PMS+"/library/metadata/"+strings.Join(keys, ","), caller.Token)
	if err != nil {
		return nil, err
	}
	if status == http.StatusOK {
		return out, collect(body, out)
	}

	// One item the caller can't open fails the whole batch, so each is asked for on its own.
	var mu sync.Mutex
	var wg sync.WaitGroup
	gate := make(chan struct{}, 6)
	for _, key := range keys {
		wg.Add(1)
		gate <- struct{}{}
		go func() {
			defer func() { <-gate; wg.Done() }()
			status, body, err := c.get(ctx, c.cfg.PMS+"/library/metadata/"+url.PathEscape(key), caller.Token)
			if err != nil || status != http.StatusOK {
				return
			}
			mu.Lock()
			defer mu.Unlock()
			_ = collect(body, out)
		}()
	}
	wg.Wait()
	return out, nil
}

func collect(body []byte, out map[string]json.RawMessage) error {
	var v struct {
		MediaContainer struct {
			Metadata []json.RawMessage `json:"Metadata"`
		} `json:"MediaContainer"`
	}
	if err := json.Unmarshal(body, &v); err != nil {
		return err
	}
	for _, raw := range v.MediaContainer.Metadata {
		var it struct {
			RatingKey string `json:"ratingKey"`
		}
		if json.Unmarshal(raw, &it) == nil && it.RatingKey != "" {
			out[it.RatingKey] = raw
		}
	}
	return nil
}

type section struct {
	Key  string `json:"key"`
	Type string `json:"type"`
}

func (c *Companion) sections(ctx context.Context, token string) ([]section, error) {
	var v struct {
		MediaContainer struct {
			Directory []section `json:"Directory"`
		} `json:"MediaContainer"`
	}
	return v.MediaContainer.Directory, c.getJSON(ctx, c.cfg.PMS+"/library/sections", token, &v)
}

// sectionItems lists one type of item in a library a page at a time.
func (c *Companion) sectionItems(ctx context.Context, token, sectionKey, itemType string) ([]plexItem, error) {
	const page = 500
	var all []plexItem
	for start := 0; ; start += page {
		var v struct {
			MediaContainer struct {
				Metadata []plexItem `json:"Metadata"`
			} `json:"MediaContainer"`
		}
		q := url.Values{"type": {itemType}, "X-Plex-Container-Start": {strconv.Itoa(start)}, "X-Plex-Container-Size": {strconv.Itoa(page)}}
		if err := c.getJSON(ctx, c.cfg.PMS+"/library/sections/"+url.PathEscape(sectionKey)+"/all?"+q.Encode(), token, &v); err != nil {
			return nil, err
		}
		all = append(all, v.MediaContainer.Metadata...)
		if len(v.MediaContainer.Metadata) < page {
			return all, nil
		}
	}
}

func (c *Companion) sectionKeys(ctx context.Context, token, sectionKey, itemType string) ([]string, error) {
	items, err := c.sectionItems(ctx, token, sectionKey, itemType)
	keys := make([]string, 0, len(items))
	for _, it := range items {
		keys = append(keys, it.RatingKey)
	}
	return keys, err
}

func (c *Companion) metadata(ctx context.Context, token string, keys []string) ([]plexItem, error) {
	var v struct {
		MediaContainer struct {
			Metadata []plexItem `json:"Metadata"`
		} `json:"MediaContainer"`
	}
	return v.MediaContainer.Metadata, c.getJSON(ctx, c.cfg.PMS+"/library/metadata/"+strings.Join(keys, ",")+"?includeGuids=1", token, &v)
}

func (c *Companion) getJSON(ctx context.Context, rawURL, token string, v any) error {
	status, body, err := c.get(ctx, rawURL, token)
	if err != nil {
		return err
	}
	if status != http.StatusOK {
		return fmt.Errorf("%s answered %d", strings.SplitN(strings.TrimPrefix(rawURL, c.cfg.PMS), "?", 2)[0], status)
	}
	return json.Unmarshal(body, v)
}

func tags(list []plexTag) []string {
	out := make([]string, 0, len(list))
	for _, t := range list {
		if t.Tag != "" {
			out = append(out, t.Tag)
		}
	}
	return out
}
