package api

import (
	"bytes"
	"compress/gzip"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"slices"
	"strconv"
	"strings"
	"time"

	"github.com/Moonfin-Client/Plugin/Go/internal/store"
	"github.com/Moonfin-Client/Plugin/Go/internal/themes"
)

// Custom themes, as the Jellyfin plugin's MoonfinThemesController serves
// them. All of them are kept gzipped under one instance-state key, so they
// take one of the plugin's 256 keys however many there are.
const (
	keyThemes = "s/themes"
	// maxThemeBytes is the Jellyfin plugin's limit for one theme's JSON.
	maxThemeBytes = 256 << 10
	// maxThemes leaves room under the key's limit, and keeps the list the
	// apps download well inside Silo's 4 MiB for a plugin response.
	maxThemes = 5
)

// reservedThemeIDs are the apps' built-in themes.
var reservedThemeIDs = []string{"moonfin", "neon_pulse"}

type themeEntry struct {
	ID               string    `json:"id"`
	DisplayName      string    `json:"displayName"`
	FileName         string    `json:"fileName"`
	SizeBytes        int       `json:"sizeBytes"`
	UploadedAtUtc    time.Time `json:"uploadedAtUtc"`
	UploadedByUserID string    `json:"uploadedByUserId,omitempty"`
	ChecksumSha256   string    `json:"checksumSha256"`
}

type themeSet struct {
	Items  []themeEntry               `json:"items"`
	Themes map[string]json.RawMessage `json:"themes"`
}

func (s *Server) themeRoutes() {
	m := s.mux
	m.HandleFunc("GET /Moonfin/Themes", s.sync(s.handleListThemes))
	m.HandleFunc("GET /Moonfin/Themes/{themeId}", s.sync(s.handleGetTheme))
	m.HandleFunc("GET /Moonfin/Admin/Themes", s.admin(s.handleAdminThemes))
	m.HandleFunc("POST /Moonfin/Admin/Themes", s.admin(s.handleUploadTheme))
	m.HandleFunc("DELETE /Moonfin/Admin/Themes/{themeId}", s.admin(s.handleDeleteTheme))
}

func (s *Server) loadThemes(ctx context.Context) (themeSet, error) {
	set := themeSet{Themes: map[string]json.RawMessage{}}
	raw, found, err := s.kv.Get(ctx, keyThemes)
	if err != nil || !found || len(raw) == 0 {
		return set, err
	}
	zr, err := gzip.NewReader(bytes.NewReader(raw))
	if err != nil {
		return set, fmt.Errorf("read %s: %w", keyThemes, err)
	}
	if err := json.NewDecoder(zr).Decode(&set); err != nil {
		return set, fmt.Errorf("decode %s: %w", keyThemes, err)
	}
	if set.Themes == nil {
		set.Themes = map[string]json.RawMessage{}
	}
	return set, nil
}

// errThemesTooBig is a set of themes that doesn't fit the key even gzipped.
var errThemesTooBig = fmt.Errorf("the themes don't fit in Moonbase's storage")

func (s *Server) saveThemes(ctx context.Context, set themeSet) error {
	var buf bytes.Buffer
	zw, _ := gzip.NewWriterLevel(&buf, gzip.BestCompression)
	if err := json.NewEncoder(zw).Encode(set); err != nil {
		return err
	}
	if err := zw.Close(); err != nil {
		return err
	}
	if buf.Len() > store.MaxValueBytes {
		return errThemesTooBig
	}
	return s.kv.Put(ctx, keyThemes, buf.Bytes())
}

// sortedThemes orders the index by display name, then id, as the Jellyfin
// plugin lists them.
func sortedThemes(items []themeEntry) []themeEntry {
	out := slices.Clone(items)
	slices.SortStableFunc(out, func(a, b themeEntry) int {
		if c := strings.Compare(strings.ToLower(a.DisplayName), strings.ToLower(b.DisplayName)); c != 0 {
			return c
		}
		return strings.Compare(a.ID, b.ID)
	})
	return out
}

func (s *Server) handleListThemes(w http.ResponseWriter, r *http.Request) {
	set, err := s.loadThemes(r.Context())
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	out := []json.RawMessage{}
	for _, e := range sortedThemes(set.Items) {
		if t, ok := set.Themes[e.ID]; ok {
			out = append(out, t)
		}
	}
	writeJSON(w, http.StatusOK, out)
}

func (s *Server) handleGetTheme(w http.ResponseWriter, r *http.Request) {
	set, err := s.loadThemes(r.Context())
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	t, ok := set.Themes[strings.ToLower(strings.TrimSpace(r.PathValue("themeId")))]
	if !ok {
		writeJSON(w, http.StatusNotFound, M{"error": "Theme not found"})
		return
	}
	writeJSON(w, http.StatusOK, t)
}

func (s *Server) handleAdminThemes(w http.ResponseWriter, r *http.Request) {
	set, err := s.loadThemes(r.Context())
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, M{"items": sortedThemes(set.Items)})
}

func themeRejected(w http.ResponseWriter, errs ...string) {
	writeJSON(w, http.StatusBadRequest, M{"error": "Theme validation failed.", "errors": errs})
}

func (s *Server) handleUploadTheme(w http.ResponseWriter, r *http.Request) {
	raw, err := io.ReadAll(io.LimitReader(r.Body, maxThemeBytes+1))
	if err != nil {
		writeError(w, http.StatusBadRequest, "The theme couldn't be read")
		return
	}
	raw = bytes.TrimSpace(raw)
	if len(raw) > maxThemeBytes {
		themeRejected(w, fmt.Sprintf("Theme JSON exceeds max size (%d bytes).", maxThemeBytes))
		return
	}
	var theme M
	if json.Unmarshal(raw, &theme) != nil || theme == nil {
		writeJSON(w, http.StatusBadRequest, M{"error": "Theme payload must be a JSON object.",
			"errors": []string{"Theme payload must be a JSON object."}})
		return
	}
	id, name, errs := themes.Validate(theme)
	switch {
	case len(errs) > 0:
		themeRejected(w, errs...)
		return
	case slices.Contains(reservedThemeIDs, id):
		themeRejected(w, "id conflicts with a protected built-in theme.")
		return
	}

	ctx := r.Context()
	s.mu.Lock()
	defer s.mu.Unlock()
	set, err := s.loadThemes(ctx)
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	at := slices.IndexFunc(set.Items, func(e themeEntry) bool { return e.ID == id })
	if at < 0 && len(set.Items) >= maxThemes {
		themeRejected(w, fmt.Sprintf("Moonbase on %s keeps up to %d themes. Delete one first.", s.name, maxThemes))
		return
	}
	sum := sha256.Sum256(raw)
	entry := themeEntry{ID: id, DisplayName: name, FileName: id + ".json", SizeBytes: len(raw),
		UploadedAtUtc: time.Now().UTC(), ChecksumSha256: hex.EncodeToString(sum[:])}
	if uid := callerOf(r).UserID; uid > 0 {
		entry.UploadedByUserID = strconv.Itoa(uid)
	}
	if at < 0 {
		set.Items = append(set.Items, entry)
	} else {
		set.Items[at] = entry
	}
	set.Themes[id] = json.RawMessage(raw)
	switch err := s.saveThemes(ctx, set); {
	case err == errThemesTooBig:
		themeRejected(w, "The themes together are too big for Moonbase's storage on Silo, even compressed. Delete a theme or make this one smaller.")
		return
	case err != nil:
		storageFailed(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, M{"success": true, "item": entry})
}

func (s *Server) handleDeleteTheme(w http.ResponseWriter, r *http.Request) {
	id := strings.ToLower(strings.TrimSpace(r.PathValue("themeId")))
	ctx := r.Context()
	s.mu.Lock()
	defer s.mu.Unlock()
	set, err := s.loadThemes(ctx)
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	at := slices.IndexFunc(set.Items, func(e themeEntry) bool { return e.ID == id })
	if at < 0 {
		writeJSON(w, http.StatusNotFound, M{"error": "Theme not found"})
		return
	}
	set.Items = slices.Delete(set.Items, at, at+1)
	delete(set.Themes, id)
	if err := s.saveThemes(ctx, set); err != nil {
		storageFailed(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, M{"success": true})
}
