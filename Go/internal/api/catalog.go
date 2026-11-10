package api

import (
	"context"
	"net/http"
	"slices"
	"strings"
)

type Library struct {
	ID        string `json:"id"`
	Name      string `json:"name"`
	MediaType string `json:"mediaType"`
}

// Genre is one of the host's genres. ID is the id the app itself gives the
// genre on that host, which its genre rows query by.
type Genre struct {
	ID   string `json:"Id"`
	Name string `json:"Name"`
}

// Catalog is the slice of the host's library Moonbase reads.
type Catalog interface {
	// Libraries lists the libraries the caller may see.
	Libraries(ctx context.Context, caller Identity) ([]Library, error)
	// Genres lists the movie and show genres in the libraries the caller may
	// see. It may repeat a genre.
	Genres(ctx context.Context, caller Identity) ([]Genre, error)
}

// handleGenres feeds the admin page's genre pickers, as the Jellyfin
// plugin's Genres route does.
func (s *Server) handleGenres(w http.ResponseWriter, r *http.Request) {
	genres, err := s.catalog.Genres(r.Context(), callerOf(r))
	if err != nil {
		logError(r, "list genres", err)
		writeError(w, http.StatusBadGateway, s.name+" didn't return the genre list.")
		return
	}
	items := []Genre{}
	seen := map[string]bool{}
	for _, g := range genres {
		if strings.TrimSpace(g.Name) != "" && !seen[g.ID] {
			seen[g.ID] = true
			items = append(items, g)
		}
	}
	slices.SortStableFunc(items, func(a, b Genre) int {
		return strings.Compare(strings.ToLower(a.Name), strings.ToLower(b.Name))
	})
	writeJSON(w, http.StatusOK, M{"Items": items})
}
