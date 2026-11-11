package anime

import (
	"bytes"
	"context"
	"fmt"
	"io"
	"net/http"
	"path/filepath"
	"sync"
	"time"
)

const userAgent = "Moonfin/1.0 (+https://github.com/Moonfin-Client/Plugin)"

// Sources reaches the sites the markers come from and keeps what they said
// under one folder: AnimeFillerList for the categories, Fribb's anime-lists
// for mapping provider ids to MyAnimeList, AniList for titles, and a
// MyAnimeList episode mirror for recaps. None needs an account or a key.
type Sources struct {
	dir  string
	http *http.Client

	// The sites' addresses, swapped in tests.
	FillerListBase, MappingURL, AniListURL, EpisodeAPIBase string
	// CrawlDelay is the wait between AnimeFillerList requests its robots.txt
	// asks for. The others space requests to stay inside each site's rate limit.
	CrawlDelay, AniListSpacing, EpisodeSpacing, RetryDelay time.Duration

	Log    *Log
	Tables *Cache

	catalog catalogState
	mapping mappingState
	aliases aliasState

	fillerGate, aniListGate, episodeGate gate
}

// New keeps everything in dir, which must exist.
func New(dir string) *Sources {
	return &Sources{
		dir:            dir,
		http:           &http.Client{Timeout: 2 * time.Minute},
		FillerListBase: "https://www.animefillerlist.com",
		MappingURL:     "https://raw.githubusercontent.com/Fribb/anime-lists/master/anime-list-mini.json",
		AniListURL:     "https://graphql.anilist.co",
		EpisodeAPIBase: "https://api.tenrai.org/v1",
		CrawlDelay:     10 * time.Second,
		// AniList allows 30 requests a minute while it's degraded, which is most of the time.
		AniListSpacing: 2500 * time.Millisecond,
		EpisodeSpacing: 2 * time.Second,
		RetryDelay:     3 * time.Second,
		Log:            NewLog(filepath.Join(dir, "moonfin-anime-markers.log")),
		Tables:         NewCache(filepath.Join(dir, "anime_markers_cache.json")),
	}
}

// gate spaces requests to one site.
type gate struct {
	mu   sync.Mutex
	last time.Time
}

// wait holds the caller until spacing has passed since the last request.
func (g *gate) wait(ctx context.Context, spacing time.Duration) error {
	g.mu.Lock()
	defer g.mu.Unlock()
	if d := spacing - time.Since(g.last); d > 0 {
		t := time.NewTimer(d)
		defer t.Stop()
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-t.C:
		}
	}
	g.last = time.Now()
	return nil
}

// fetch calls a site and reads its answer, failing one longer than limit
// rather than handing back a cut-off copy that would be cached as whole.
func (s *Sources) fetch(ctx context.Context, method, url string, body []byte, timeout time.Duration, limit int64) (int, []byte, error) {
	ctx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	req, err := http.NewRequestWithContext(ctx, method, url, bytes.NewReader(body))
	if err != nil {
		return 0, nil, err
	}
	req.Header.Set("User-Agent", userAgent)
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
		req.Header.Set("Accept", "application/json")
	}
	resp, err := s.http.Do(req)
	if err != nil {
		return 0, nil, err
	}
	defer resp.Body.Close()
	b, err := io.ReadAll(io.LimitReader(resp.Body, limit+1))
	if err == nil && int64(len(b)) > limit {
		err = fmt.Errorf("the answer is over %d bytes", limit)
	}
	return resp.StatusCode, b, err
}
