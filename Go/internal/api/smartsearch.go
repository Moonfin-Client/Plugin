package api

import (
	"context"
	"encoding/json"
	"log/slog"
	"net/http"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/Moonfin-Client/Plugin/Go/internal/smartsearch"
)

// SmartCatalog is what a host gives smart search. Hosts whose catalog can't
// list library items leave it out, and smart search stays off there.
type SmartCatalog interface {
	// SmartLibrary reads everything smart search indexes, as the server owner.
	SmartLibrary(ctx context.Context) (SmartLibrary, error)
	// SmartItems is what the caller gets for each of these item keys, read as
	// the caller, so anything they can't see is left out.
	SmartItems(ctx context.Context, caller Identity, keys []string) (map[string]json.RawMessage, error)
}

// SmartLibrary is the host's library in the shape the engine's SmartLibrary takes.
type SmartLibrary struct {
	Titles  []SmartTitle  `json:"titles"`
	Albums  []SmartAlbum  `json:"albums"`
	Artists []SmartArtist `json:"artists"`
	// People is what a client gets for each credited name.
	People map[string]json.RawMessage `json:"-"`
}

type SmartTitle struct {
	ID        string   `json:"id"`
	Series    bool     `json:"series"`
	Title     string   `json:"title"`
	Year      int      `json:"year"`
	TmdbID    string   `json:"tmdbId,omitempty"`
	Genres    []string `json:"genres"`
	Actors    []string `json:"actors"`
	Directors []string `json:"directors"`
	Overview  string   `json:"overview,omitempty"`
}

type SmartAlbum struct {
	ID       string   `json:"id"`
	Title    string   `json:"title"`
	Year     int      `json:"year"`
	Artists  []string `json:"artists"`
	Genres   []string `json:"genres"`
	Overview string   `json:"overview,omitempty"`
}

type SmartArtist struct {
	ID       string   `json:"id"`
	Name     string   `json:"name"`
	Genres   []string `json:"genres"`
	Overview string   `json:"overview,omitempty"`
}

const (
	smartMaxLimit    = 50
	smartCandidates  = 120
	smartMaxQueryLen = 200
)

type smartState struct {
	helper   *smartsearch.Helper
	building atomic.Bool
	mu       sync.RWMutex
	people   map[string]json.RawMessage
}

func (s *Server) smartRoutes() {
	s.mux.HandleFunc("GET /Moonfin/SmartSearch", s.user(s.handleSmartSearch))
}

// smartCatalog is the host's catalog when smart search can run on it.
func (s *Server) smartCatalog() (SmartCatalog, bool) {
	c, ok := s.catalog.(SmartCatalog)
	return c, ok && s.smart.helper != nil && s.smart.helper.Available()
}

func (s *Server) smartSearchOn(cfg Config) bool {
	_, ok := s.smartCatalog()
	return ok && cfg.SmartSearchEnabled
}

func (s *Server) handleSmartSearch(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()
	cfg := s.config(ctx)
	catalog, ok := s.smartCatalog()
	if !ok || !cfg.SmartSearchEnabled {
		http.NotFound(w, r)
		return
	}
	text := strings.TrimSpace(r.URL.Query().Get("query"))
	if text == "" {
		writeError(w, http.StatusBadRequest, "query is required")
		return
	}
	if len([]rune(text)) > smartMaxQueryLen {
		text = string([]rune(text)[:smartMaxQueryLen])
	}
	limit, err := strconv.Atoi(r.URL.Query().Get("limit"))
	if err != nil {
		limit = 20
	}
	limit = min(max(limit, 1), smartMaxLimit)
	debug := r.URL.Query().Get("debug") == "true"

	model := map[string]any{"model": cfg.SmartSearchModel}
	var status struct {
		Ready bool `json:"ready"`
	}
	if err := s.smart.helper.Call(ctx, "status", model, &status); err != nil {
		logError(r, "smart search status", err)
		writeError(w, http.StatusBadGateway, "Smart search isn't running.")
		return
	}
	if !status.Ready {
		// Turning the feature on doesn't restart the server, so the first search starts the build.
		go s.rebuildSmartSearch(context.Background())
		writeJSON(w, http.StatusOK, M{"Ready": false, "Items": []any{}, "Seerr": []any{}})
		return
	}

	started := time.Now()
	var found struct {
		Query   json.RawMessage `json:"query"`
		Library []struct {
			Key        string  `json:"key"`
			Kind       string  `json:"kind"`
			Title      string  `json:"title"`
			Year       int     `json:"year"`
			Score      float64 `json:"score"`
			Similarity float64 `json:"similarity"`
		} `json:"library"`
	}
	args := map[string]any{"model": cfg.SmartSearchModel, "text": text, "candidates": smartCandidates}
	if err := s.smart.helper.Call(ctx, "search", args, &found); err != nil {
		logError(r, "smart search", err)
		writeError(w, http.StatusBadGateway, "Smart search failed.")
		return
	}

	keys := []string{}
	for _, hit := range found.Library {
		if hit.Kind != "Person" {
			keys = append(keys, hit.Key)
		}
	}
	visible := map[string]json.RawMessage{}
	if len(keys) > 0 {
		if visible, err = catalog.SmartItems(ctx, callerOf(r), keys); err != nil {
			logError(r, "smart search items", err)
			writeError(w, http.StatusBadGateway, s.name+" didn't answer.")
			return
		}
	}
	s.smart.mu.RLock()
	people := s.smart.people
	s.smart.mu.RUnlock()

	items := []any{}
	shown := map[string]bool{}
	for _, hit := range found.Library {
		item, ok := visible[hit.Key]
		if hit.Kind == "Person" {
			item, ok = people[hit.Title]
		}
		// The same show in two libraries would otherwise fill two cards with one title.
		seen := hit.Kind + "|" + strings.ToLower(hit.Title) + "|" + strconv.Itoa(hit.Year)
		if !ok || shown[seen] {
			continue
		}
		shown[seen] = true
		if debug {
			items = append(items, M{"Card": item, "Score": hit.Score, "Similarity": hit.Similarity})
		} else {
			items = append(items, item)
		}
		if len(items) >= limit {
			break
		}
	}

	// There's no Seerr proxy on these hosts, so titles the library lacks have nowhere to go.
	body := M{"Ready": true, "Items": items, "Seerr": []any{}}
	if debug {
		body["Query"] = found.Query
		body["Milliseconds"] = time.Since(started).Milliseconds()
	}
	writeJSON(w, http.StatusOK, body)
}

// rebuildSmartSearch indexes the library, then the TMDB catalog when there's a
// key, then the library again so people's texts name what the fresh catalog
// says they're known for. Unchanged entries keep their vectors, so the second
// pass is quick. Only one rebuild runs at a time.
func (s *Server) rebuildSmartSearch(ctx context.Context) {
	if !s.smart.building.CompareAndSwap(false, true) {
		return
	}
	defer s.smart.building.Store(false)
	cfg := s.config(ctx)
	catalog, ok := s.smartCatalog()
	if !ok || !cfg.SmartSearchEnabled {
		return
	}

	library, err := catalog.SmartLibrary(ctx)
	if err != nil {
		slog.ErrorContext(ctx, "smart search: read the library", "error", err)
		return
	}
	s.smart.mu.Lock()
	s.smart.people = library.People
	s.smart.mu.Unlock()

	helper := s.smart.helper
	model := map[string]any{"model": cfg.SmartSearchModel}
	if err := helper.Call(ctx, "ensureModel", model, nil); err != nil {
		slog.ErrorContext(ctx, "smart search: get the model", "error", err)
		return
	}
	var built struct {
		Count   int `json:"count"`
		Seconds int `json:"seconds"`
	}
	args := map[string]any{"model": cfg.SmartSearchModel, "library": library}
	if err := helper.Call(ctx, "rebuildLibrary", args, &built); err != nil {
		slog.ErrorContext(ctx, "smart search: index the library", "error", err)
		return
	}
	slog.InfoContext(ctx, "smart search: library indexed", "entries", built.Count, "seconds", built.Seconds)
	if cfg.TmdbAPIKey == "" {
		slog.InfoContext(ctx, "smart search: no server-wide TMDB key, so there's no TMDB catalog")
		return
	}

	catalogArgs := map[string]any{"model": cfg.SmartSearchModel, "tmdbApiKey": cfg.TmdbAPIKey}
	if err := helper.Call(ctx, "rebuildCatalog", catalogArgs, &built); err != nil {
		slog.ErrorContext(ctx, "smart search: index the TMDB catalog", "error", err)
		return
	}
	slog.InfoContext(ctx, "smart search: TMDB catalog indexed", "titles", built.Count, "seconds", built.Seconds)
	if err := helper.Call(ctx, "rebuildLibrary", args, nil); err != nil {
		slog.ErrorContext(ctx, "smart search: index the library again", "error", err)
	}
}

// scheduleSmartSearch rebuilds a minute after startup and then every night at
// 3:00, like the plugins' scheduled task. The minute gives a server started
// alongside the host time to answer. Each run checks the setting itself.
func (s *Server) scheduleSmartSearch() {
	go func() {
		time.Sleep(time.Minute)
		for {
			s.rebuildSmartSearch(context.Background())
			now := time.Now()
			next := time.Date(now.Year(), now.Month(), now.Day(), 3, 0, 0, 0, now.Location())
			if !next.After(now) {
				next = next.AddDate(0, 0, 1)
			}
			time.Sleep(time.Until(next))
		}
	}()
}
