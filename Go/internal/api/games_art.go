package api

import (
	"context"
	"crypto/sha1"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"sync"
	"time"
)

// Game art comes from the libretro thumbnail server, matched by name, or from
// RomM. Each image is fetched once and kept under the games folder.

var libretroThumbs = "https://thumbnails.libretro.com/"

const (
	artMissTTL   = 6 * time.Hour
	maxArtBytes  = 16 << 20
	maxArtMisses = 4096
)

var errNoArt = errors.New("no art at this address")

type artFetcher struct {
	http *http.Client
	// gate keeps a screen full of tiles from opening a connection each.
	gate chan struct{}

	mu     sync.Mutex
	misses map[string]time.Time
}

func newArtFetcher() artFetcher {
	return artFetcher{http: &http.Client{Timeout: 15 * time.Second}, gate: make(chan struct{}, 4), misses: map[string]time.Time{}}
}

func artRole(kind string) string {
	switch strings.ToLower(kind) {
	case "snap", "title":
		return strings.ToLower(kind)
	}
	return "boxart"
}

func libretroFolder(role string) string {
	switch role {
	case "snap":
		return "Named_Snaps"
	case "title":
		return "Named_Titles"
	}
	return "Named_Boxarts"
}

// libretroName swaps the characters libretro can't keep in a file name for
// underscores, as its thumbnail sets do.
func libretroName(name string) string {
	return strings.Map(func(r rune) rune {
		if strings.ContainsRune(`&*/:`+"`"+`<>?\|"`, r) {
			return '_'
		}
		return r
	}, name)
}

// libretroArt lists the addresses a game's art may be at, best first: its
// file name, then its title, and for Game Boy the other Game Boy set too,
// since the two often share a folder.
func libretroArt(core, systemName, romName, title, role string) []string {
	platform, ok := corePlatforms[artworkCore(core, systemName)]
	if !ok {
		return nil
	}
	stem := strings.TrimSuffix(romName, filepath.Ext(romName))
	names := [][2]string{{platform, stem}, {platform, title}}
	if other := map[string]string{"Nintendo - Game Boy": "Nintendo - Game Boy Color", "Nintendo - Game Boy Color": "Nintendo - Game Boy"}[platform]; other != "" {
		names = append(names, [2]string{other, stem})
	}
	var out []string
	for _, n := range names {
		name := libretroName(strings.TrimSpace(n[1]))
		if name == "" {
			continue
		}
		u := libretroThumbs + url.PathEscape(n[0]) + "/" + libretroFolder(role) + "/" + url.PathEscape(name) + ".png"
		if !slices.Contains(out, u) {
			out = append(out, u)
		}
	}
	return out
}

// find returns the kept file for the first address with art, fetching as
// needed. An empty path and no error means none of them has any.
func (a *artFetcher) find(ctx context.Context, dir string, urls []string) (string, error) {
	var transient error
	for _, u := range urls {
		sum := sha1.Sum([]byte(u))
		file := filepath.Join(dir, "thumbs", hex.EncodeToString(sum[:]))
		if _, err := os.Stat(file); err == nil {
			return file, nil
		}
		if a.missed(u) {
			continue
		}
		switch err := a.fetch(ctx, u, file); {
		case err == nil:
			return file, nil
		case errors.Is(err, errNoArt):
			a.miss(u)
		default:
			transient = err
		}
	}
	return "", transient
}

func (a *artFetcher) missed(u string) bool {
	a.mu.Lock()
	defer a.mu.Unlock()
	at, ok := a.misses[u]
	return ok && time.Since(at) < artMissTTL
}

func (a *artFetcher) miss(u string) {
	a.mu.Lock()
	defer a.mu.Unlock()
	if len(a.misses) >= maxArtMisses {
		clear(a.misses)
	}
	a.misses[u] = time.Now()
}

func (a *artFetcher) fetch(ctx context.Context, u, file string) error {
	select {
	case a.gate <- struct{}{}:
		defer func() { <-a.gate }()
	case <-ctx.Done():
		return ctx.Err()
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, u, nil)
	if err != nil {
		return errNoArt
	}
	resp, err := a.http.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	switch {
	case resp.StatusCode == http.StatusNotFound || resp.StatusCode == http.StatusGone:
		return errNoArt
	case resp.StatusCode != http.StatusOK:
		return fmt.Errorf("art server answered %d", resp.StatusCode)
	}
	b, err := io.ReadAll(io.LimitReader(resp.Body, maxArtBytes+1))
	if err != nil {
		return err
	}
	if len(b) > maxArtBytes || !strings.HasPrefix(http.DetectContentType(b), "image/") {
		return errNoArt
	}
	return writeFileAtomic(file, b)
}

func (g *games) handleThumb(w http.ResponseWriter, r *http.Request) {
	cfg, l, ok := g.library(w, r)
	if !ok {
		return
	}
	role := artRole(r.URL.Query().Get("type"))
	var urls []string
	if l.romm {
		snap := g.rommSnapshot(cfg.romm)
		game, platform, ok := snap.find(r.PathValue("gameId"))
		if !ok {
			artNotFound(w, false)
			return
		}
		urls = append(rommArt(cfg.romm, game, role), libretroArt(platform.Core, platform.SystemID, game.ServedName, game.Title, role)...)
	} else {
		f, ok := l.file(r.PathValue("gameId"))
		if !ok {
			artNotFound(w, false)
			return
		}
		urls = libretroArt(f.core(), f.systemName, filepath.Base(f.path), gameTitle(f.systemDir, f.path), role)
	}
	file, err := g.art.find(r.Context(), g.dir, urls)
	if err != nil {
		w.Header().Set("Retry-After", "30")
		w.Header().Set("Cache-Control", "no-store")
		w.WriteHeader(http.StatusServiceUnavailable)
		return
	}
	if file == "" {
		artNotFound(w, true)
		return
	}
	b, err := os.ReadFile(file)
	if err != nil {
		artNotFound(w, false)
		return
	}
	w.Header().Set("Content-Type", http.DetectContentType(b))
	w.Header().Set("Cache-Control", "private,max-age=300,must-revalidate")
	w.Header().Set("X-Content-Type-Options", "nosniff")
	_, _ = w.Write(b)
}

// artNotFound lets an app keep a confirmed miss for a while, and ask again
// at once about anything else.
func artNotFound(w http.ResponseWriter, confirmed bool) {
	if confirmed {
		w.Header().Set("Cache-Control", "private,max-age=300")
	} else {
		w.Header().Set("Cache-Control", "no-store")
	}
	w.WriteHeader(http.StatusNotFound)
}
