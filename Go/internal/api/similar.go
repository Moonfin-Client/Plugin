package api

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"strconv"
	"strings"

	"github.com/Moonfin-Client/Plugin/Go/internal/similar"
)

// SimilarCatalog is a host that ranks similar items for Moonfin Recommends
// itself. Hosts without it report recommendations as unsupported.
type SimilarCatalog interface {
	// SimilarItems ranks the caller's movies or shows against the item and
	// returns what the caller gets for the best limit of them, best first.
	// It answers ErrItemNotFound when the caller can't open the item.
	SimilarItems(ctx context.Context, caller Identity, id string, limit int, exclude []string) ([]json.RawMessage, error)
}

var ErrItemNotFound = errors.New("item not found")

func (s *Server) similarRoutes() {
	s.mux.HandleFunc("GET /Moonfin/Items/{id}/Similar", s.user(s.handleSimilar))
}

func (s *Server) similarOn(cfg Config) bool {
	_, ok := s.catalog.(SimilarCatalog)
	return ok && cfg.RecommendationsProviderEnabled
}

func (s *Server) handleSimilar(w http.ResponseWriter, r *http.Request) {
	catalog, ok := s.catalog.(SimilarCatalog)
	if !ok {
		http.NotFound(w, r)
		return
	}
	// Empty rather than 404, as on Jellyfin and Emby, since the apps fall back
	// to the server's own similar items on that.
	if !s.config(r.Context()).RecommendationsProviderEnabled {
		writeJSON(w, http.StatusOK, M{"Items": []any{}, "TotalRecordCount": 0})
		return
	}
	q := r.URL.Query()
	limit, _ := strconv.Atoi(q.Get("limit"))
	var exclude []string
	for _, v := range q["excludeItemIds"] {
		for id := range strings.SplitSeq(v, ",") {
			if id = strings.TrimSpace(id); id != "" {
				exclude = append(exclude, id)
			}
		}
	}
	items, err := catalog.SimilarItems(r.Context(), callerOf(r), r.PathValue("id"), similar.EffectiveLimit(limit), exclude)
	if errors.Is(err, ErrItemNotFound) {
		writeError(w, http.StatusNotFound, "Item not found")
		return
	}
	if err != nil {
		logError(r, "similar items", err)
		writeError(w, http.StatusBadGateway, s.name+" didn't return the library.")
		return
	}
	if items == nil {
		items = []json.RawMessage{}
	}
	writeJSON(w, http.StatusOK, M{"Items": items, "TotalRecordCount": len(items)})
}
