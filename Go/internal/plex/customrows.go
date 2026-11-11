package plex

import (
	"context"

	"github.com/Moonfin-Client/Plugin/Go/internal/api"
)

// LibraryGUIDs maps the imdb, tmdb and tvdb guids Plex keeps on the caller's
// movies, or shows, to their ratingKeys. It reads as the caller, so only what
// is shared with them counts.
func (c *Companion) LibraryGUIDs(ctx context.Context, caller api.Identity, series bool) (map[string]string, error) {
	kind, itemType := arrKind(series)
	sections, err := c.sections(ctx, caller.Token)
	if err != nil {
		return nil, err
	}
	out := map[string]string{}
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
				if _, seen := out[g.ID]; !seen {
					out[g.ID] = it.RatingKey
				}
			}
		}
	}
	return out, nil
}
