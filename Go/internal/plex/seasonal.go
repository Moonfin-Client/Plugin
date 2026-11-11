package plex

import (
	"context"
	"errors"
	"strings"

	"github.com/Moonfin-Client/Plugin/Go/internal/api"
)

// SeasonalMovies lists the server's movies that Plex matched to a TMDB title,
// read as the server owner, since the keyword store covers the whole server
// and each row is read as its caller afterwards.
func (c *Companion) SeasonalMovies(ctx context.Context) ([]api.SeasonalMovie, error) {
	token := c.ownerToken()
	if token == "" {
		return nil, errors.New("the server owner hasn't signed in on the Moonbase page yet")
	}
	sections, err := c.sections(ctx, token)
	if err != nil {
		return nil, err
	}
	var out []api.SeasonalMovie
	for _, s := range sections {
		if s.Type != "movie" {
			continue
		}
		items, err := c.sectionItems(ctx, token, s.Key, "1", true)
		if err != nil {
			return nil, err
		}
		for _, it := range items {
			for _, g := range it.GUID {
				if id, ok := strings.CutPrefix(g.ID, "tmdb://"); ok {
					out = append(out, api.SeasonalMovie{ID: it.RatingKey, TmdbID: id})
					break
				}
			}
		}
	}
	return out, nil
}
