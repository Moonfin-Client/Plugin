package anime

import (
	"context"
	"log/slog"
	"net/http"
	"os"
	"path/filepath"
	"sync"
	"time"
)

const (
	catalogMaxAge = 14 * 24 * time.Hour
	// The index is checked on every marker request, so a failed download
	// backs off, or an unreachable site would cost every request the crawl
	// delay plus the timeout.
	downloadRetry = 6 * time.Hour
)

type catalogState struct {
	mu          sync.Mutex
	shows       []*Show
	index       map[string]*Show
	lastAttempt time.Time
}

func (s *Sources) catalogPath() string { return filepath.Join(s.dir, "anime_filler_catalog.json") }

// EnsureCatalog loads the show index, downloading it only when the copy on
// disk is missing or stale. A failed download keeps what's on disk.
func (s *Sources) EnsureCatalog(ctx context.Context) {
	c := &s.catalog
	c.mu.Lock()
	defer c.mu.Unlock()
	stale := s.catalogStale()
	if c.index != nil && !stale {
		return
	}
	if stale && time.Since(c.lastAttempt) > downloadRetry {
		c.lastAttempt = time.Now()
		if shows := s.downloadCatalog(ctx); shows != nil {
			c.adopt(shows)
			if err := writeJSONFile(s.catalogPath(), shows); err != nil {
				slog.Warn("anime markers: save the show catalog", "error", err)
			}
			slog.Info("anime markers: show catalog refreshed", "shows", len(shows))
			return
		}
	}
	if c.index == nil {
		var shows []*Show
		if err := readJSONFile(s.catalogPath(), &shows); err != nil {
			slog.Warn("anime markers: the show catalog on disk couldn't be read", "error", err)
		} else if shows != nil {
			c.adopt(shows)
		}
	}
}

func (c *catalogState) adopt(shows []*Show) {
	c.shows = shows
	c.index = BuildIndex(shows)
}

func (s *Sources) catalogStale() bool {
	info, err := os.Stat(s.catalogPath())
	return err != nil || time.Since(info.ModTime()) > catalogMaxAge
}

func (s *Sources) downloadCatalog(ctx context.Context) []*Show {
	page, ok := s.fillerPage(ctx, "/shows")
	if !ok {
		return nil
	}
	parsed := ParseShowIndex(page)
	// An index that parses to almost nothing means the markup moved, not that
	// the site lost its catalog.
	if len(parsed) < 50 {
		slog.Warn("anime markers: the show catalog looked wrong, keeping any copy already saved", "shows", len(parsed))
		return nil
	}
	shows := make([]*Show, len(parsed))
	for i := range parsed {
		shows[i] = &parsed[i]
	}
	return shows
}

// Catalog is the title lookup and how many shows it holds. The lookup is nil
// until EnsureCatalog has loaded one.
func (s *Sources) Catalog() (map[string]*Show, int) {
	s.catalog.mu.Lock()
	defer s.catalog.mu.Unlock()
	return s.catalog.index, len(s.catalog.shows)
}

// CatalogSavedAt is when the catalog on disk was written.
func (s *Sources) CatalogSavedAt() (time.Time, bool) {
	info, err := os.Stat(s.catalogPath())
	if err != nil {
		return time.Time{}, false
	}
	return info.ModTime().UTC(), true
}

// FetchEpisodes reads one show's episode table. False when the page can't be
// read. An empty table means the page lists no episodes.
func (s *Sources) FetchEpisodes(ctx context.Context, slug string) ([]Episode, bool) {
	page, ok := s.fillerPage(ctx, "/shows/"+slug)
	if !ok {
		return nil, false
	}
	return ParseEpisodes(page), true
}

func (s *Sources) fillerPage(ctx context.Context, path string) (string, bool) {
	if s.fillerGate.wait(ctx, s.CrawlDelay) != nil {
		return "", false
	}
	status, body, err := s.fetch(ctx, http.MethodGet, s.FillerListBase+path, nil, 30*time.Second, 16<<20)
	if err != nil || status != http.StatusOK {
		slog.Debug("anime markers: AnimeFillerList page unreadable", "path", path, "status", status, "error", err)
		return "", false
	}
	return string(body), true
}
