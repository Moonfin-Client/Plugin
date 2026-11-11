package anime

import (
	"encoding/json"
	"errors"
	"io/fs"
	"log/slog"
	"os"
	"path/filepath"
	"sync"
	"time"
)

// Cache keeps the AnimeFillerList episode tables by show slug in one JSON
// file. Entries are replaced, never changed in place, so a reader can keep
// using one it was handed.
type Cache struct {
	path string
	mu   sync.Mutex
	m    map[string]*CacheEntry
}

func NewCache(path string) *Cache { return &Cache{path: path} }

// loaded must be called with mu held.
func (c *Cache) loaded() map[string]*CacheEntry {
	if c.m == nil {
		c.m = map[string]*CacheEntry{}
		if err := readJSONFile(c.path, &c.m); err != nil {
			slog.Warn("anime markers: the marker cache couldn't be read, starting fresh", "error", err)
			c.m = map[string]*CacheEntry{}
		}
	}
	return c.m
}

// Get is the show's table, or nil when there's none younger than maxAge.
func (c *Cache) Get(slug string, maxAge time.Duration) *CacheEntry {
	c.mu.Lock()
	defer c.mu.Unlock()
	if e := c.loaded()[slug]; e != nil && time.Since(e.CachedAt) < maxAge {
		return e
	}
	return nil
}

// GetAny is the show's table whatever its age, for the recap pass, which only
// adds to a table the filler pass stored.
func (c *Cache) GetAny(slug string) *CacheEntry {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.loaded()[slug]
}

// Set stores a freshly fetched table.
func (c *Cache) Set(e *CacheEntry) {
	e.CachedAt = time.Now().UTC()
	e.Format = CurrentFormat
	c.Update(e)
}

// Update stores a table without moving its fetch time, so adding recap flags
// doesn't buy an airing show another full max age before it's read again.
func (c *Cache) Update(e *CacheEntry) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.loaded()[e.Slug] = e
}

// FreshSlugs are the shows whose table is current and younger than maxAge,
// which the next sync can skip.
func (c *Cache) FreshSlugs(maxAge time.Duration) map[string]bool {
	c.mu.Lock()
	defer c.mu.Unlock()
	fresh := map[string]bool{}
	for slug, e := range c.loaded() {
		if e.Format >= CurrentFormat && time.Since(e.CachedAt) < maxAge {
			fresh[slug] = true
		}
	}
	return fresh
}

// Counts is how many shows are cached, their episodes, and how many of those
// are filler or mixed, for the diagnostics readout.
func (c *Cache) Counts() (shows, episodes, flagged int) {
	c.mu.Lock()
	defer c.mu.Unlock()
	for _, e := range c.loaded() {
		shows++
		episodes += len(e.Episodes)
		for _, ep := range e.Episodes {
			if ep.Kind == Filler || ep.Kind == Mixed {
				flagged++
			}
		}
	}
	return shows, episodes, flagged
}

func (c *Cache) Flush() error {
	c.mu.Lock()
	defer c.mu.Unlock()
	return writeJSONFile(c.path, c.loaded())
}

// Clear drops every table, on disk too, and says how many there were.
func (c *Cache) Clear() (int, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	n := len(c.loaded())
	c.m = map[string]*CacheEntry{}
	return n, writeJSONFile(c.path, c.m)
}

// readJSONFile leaves v alone when the file isn't there.
func readJSONFile(path string, v any) error {
	b, err := os.ReadFile(path)
	if errors.Is(err, fs.ErrNotExist) {
		return nil
	}
	if err != nil {
		return err
	}
	return json.Unmarshal(b, v)
}

func writeJSONFile(path string, v any) error {
	b, err := json.Marshal(v)
	if err != nil {
		return err
	}
	return writeFile(path, b)
}

// writeFile replaces the file in one step and syncs it first, so neither a
// crash nor a power cut can leave half a cache behind.
func writeFile(path string, b []byte) error {
	tmp, err := os.CreateTemp(filepath.Dir(path), filepath.Base(path)+".*.tmp")
	if err != nil {
		return err
	}
	defer os.Remove(tmp.Name())
	_, err = tmp.Write(b)
	if err == nil {
		err = tmp.Sync()
	}
	if cerr := tmp.Close(); err == nil {
		err = cerr
	}
	if err != nil {
		return err
	}
	return os.Rename(tmp.Name(), path)
}
