package plex

import (
	"context"
	"log/slog"
	"net/url"
	"slices"
	"sync"

	"github.com/Moonfin-Client/Plugin/Go/internal/api"
)

// sectionPrefix turns a section key into the library id Moonfin's Plex views
// carry.
const sectionPrefix = "section-"

func arrKind(series bool) (kind, itemType string) {
	if series {
		return "show", "2"
	}
	return "movie", "1"
}

// FindArrTitles looks arr titles up in the caller's libraries by the tvdb,
// tmdb and imdb guids Plex keeps on each item, in that order, then reads each
// found series' episodes once.
func (c *Companion) FindArrTitles(ctx context.Context, caller api.Identity, series bool, titles []api.ArrTitle) ([]api.ArrItem, error) {
	kind, itemType := arrKind(series)
	found := make([]api.ArrItem, len(titles))
	wanted := map[string]bool{}
	for _, t := range titles {
		for _, g := range arrGUIDs(t) {
			wanted[g] = true
		}
	}
	if len(wanted) == 0 {
		return found, nil
	}
	sections, err := c.sections(ctx, caller.Token)
	if err != nil {
		return nil, err
	}
	type hit struct{ key, library string }
	hits := map[string][]hit{}
	for _, s := range sections {
		if s.Type != kind {
			continue
		}
		items, err := c.sectionItems(ctx, caller.Token, s.Key, itemType, true)
		if err != nil {
			return nil, err
		}
		for _, it := range items {
			for _, g := range it.GUID {
				if wanted[g.ID] {
					hits[g.ID] = append(hits[g.ID], hit{it.RatingKey, sectionPrefix + s.Key})
				}
			}
		}
	}
	for i, t := range titles {
		for _, g := range arrGUIDs(t) {
			if len(hits[g]) == 0 {
				continue
			}
			found[i].ItemID = hits[g][0].key
			for _, h := range hits[g] {
				if !slices.Contains(found[i].LibraryIDs, h.library) {
					found[i].LibraryIDs = append(found[i].LibraryIDs, h.library)
				}
			}
			break
		}
	}
	if series {
		c.arrEpisodes(ctx, caller.Token, titles, found)
	}
	return found, nil
}

func arrGUIDs(t api.ArrTitle) []string {
	var out []string
	for _, g := range [][2]string{{"tvdb://", t.TvdbID}, {"tmdb://", t.TmdbID}, {"imdb://", t.ImdbID}} {
		if g[1] != "" {
			out = append(out, g[0]+g[1])
		}
	}
	return out
}

// arrEpisodes reads the episodes of each found series, a few series at a
// time. A series whose episodes can't be read is left without episode ids.
func (c *Companion) arrEpisodes(ctx context.Context, token string, titles []api.ArrTitle, found []api.ArrItem) {
	var wg sync.WaitGroup
	gate := make(chan struct{}, 6)
	for i := range found {
		if found[i].ItemID == "" || len(titles[i].Episodes) == 0 {
			continue
		}
		wg.Add(1)
		gate <- struct{}{}
		go func() {
			defer func() { <-gate; wg.Done() }()
			var v struct {
				MediaContainer struct {
					Metadata []struct {
						RatingKey   string `json:"ratingKey"`
						ParentIndex int    `json:"parentIndex"`
						Index       int    `json:"index"`
					} `json:"Metadata"`
				} `json:"MediaContainer"`
			}
			if err := c.getJSON(ctx, c.cfg.PMS+"/library/metadata/"+url.PathEscape(found[i].ItemID)+"/allLeaves", token, &v); err != nil {
				slog.WarnContext(ctx, "read a show's episodes for the calendar", "ratingKey", found[i].ItemID, "error", err)
				return
			}
			episodes := map[api.EpisodeNumber]string{}
			for _, e := range v.MediaContainer.Metadata {
				episodes[api.EpisodeNumber{Season: e.ParentIndex, Episode: e.Index}] = e.RatingKey
			}
			found[i].Episodes = episodes
		}()
	}
	wg.Wait()
}

// ArrFolders lists every series or movie library with its folders. The
// owner's token sees them all, and the caller's says which they can open.
func (c *Companion) ArrFolders(ctx context.Context, caller api.Identity, series bool) ([]api.ArrFolder, error) {
	kind, _ := arrKind(series)
	mine, err := c.sections(ctx, caller.Token)
	if err != nil {
		return nil, err
	}
	all := mine
	// shortcut: without the owner's saved token only the caller's own libraries
	// are known, so a title in one hidden from them stays listed with no
	// library. Members can't be named before the owner signs in anyway.
	if owner := c.ownerToken(); !caller.Admin && owner != "" {
		if all, err = c.sections(ctx, owner); err != nil {
			return nil, err
		}
	}
	visible := map[string]bool{}
	for _, s := range mine {
		visible[s.Key] = true
	}
	var out []api.ArrFolder
	for _, s := range all {
		if s.Type != kind {
			continue
		}
		f := api.ArrFolder{LibraryID: sectionPrefix + s.Key, Visible: visible[s.Key]}
		for _, l := range s.Location {
			f.Locations = append(f.Locations, l.Path)
		}
		out = append(out, f)
	}
	return out, nil
}
