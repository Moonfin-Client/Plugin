package api

import (
	"context"
	"encoding/json"
	"fmt"
	"hash/fnv"
	"net/http"
	"os"
	"path/filepath"
	"slices"
	"strconv"
	"strings"
)

// Poster labels: the admin's rules put labels such as 4K or VFQ on movie and
// episode posters. This is shared/PosterBadges from the Jellyfin and Emby
// plugins in Go, reading the tracks a host hands over under Jellyfin's
// property names, so the same rules and admin page work here.

// BadgeTrack is one track of a file, named the way Jellyfin names it.
type BadgeTrack struct {
	// Type is Video, Audio, Subtitle or Lyric.
	Type                              string
	Language, Title, Codec, Profile   string
	Channels, Width, Height, BitDepth int
	VideoRangeType, VideoRange        string
	IsDefault, IsForced               bool
	// Extra is the host's own description of the track, which only helps the
	// built-in labels spot Atmos and DTS:X.
	Extra string
}

// BadgeFile is what labels are read from.
type BadgeFile struct {
	Name string
	Path string
	// Tracks is nil when the host hasn't read them yet, which isn't the same
	// as a file without tracks.
	Tracks []BadgeTrack
}

// BadgeCatalog is a host that can read its movies' and episodes' files.
type BadgeCatalog interface {
	// BadgeFiles reads the files of the movies and episodes among ids that
	// the caller can see, by id. Anything else is left out.
	BadgeFiles(ctx context.Context, caller Identity, ids []string) (map[string]BadgeFile, error)
	// BadgeSearch finds movies and episodes by name for the admin page's
	// test, shaped like Jellyfin's items.
	BadgeSearch(ctx context.Context, caller Identity, term string, limit int) ([]M, error)
}

// The most ids one request reads, the same cap as the Jellyfin plugin's.
const badgeMaxIDs = 200

func (s *Server) posterBadgeRoutes() {
	s.mux.HandleFunc("GET /Moonfin/PosterBadges/Items", s.user(s.handleBadgeItems))
	s.mux.HandleFunc("POST /Moonfin/PosterBadges/Test", s.admin(s.handleBadgeTest))
	s.mux.HandleFunc("GET /Moonfin/PosterBadges/Presets", s.admin(s.handleBadgePresets))
	s.mux.HandleFunc("GET /Moonfin/PosterBadges/Search", s.admin(s.handleBadgeSearch))
	// Apps load flags as plain images, so they're served without a sign-in, the same as on Jellyfin.
	s.mux.HandleFunc("GET /Moonfin/Assets/{file}", s.handleBadgeFlag)
}

type badgeCondition struct {
	Field, Match, Operator, Value string
}

type badgeRule struct {
	Label, Color, Preset                  string
	Enabled                               *bool
	Conditions                            []badgeCondition
	Style, TextColor, Size, Corner, Shape string
	Icon                                  string
	Opacity                               *float64
	UserIds, Profiles                     []string
}

func (r badgeRule) enabled() bool { return r.Enabled == nil || *r.Enabled }

type badgeDefaults struct {
	Style        string
	Opacity      int
	Size         string
	Corner       string
	Shape        string
	MaxPerCorner int
}

func newBadgeDefaults() badgeDefaults {
	return badgeDefaults{Style: "solid", Opacity: 100, Size: "medium", Corner: "topLeft", Shape: "rounded", MaxPerCorner: 2}
}

// badgeConfig reads the rules and the look the admin page saved.
func (s *Server) badgeConfig(ctx context.Context) ([]badgeRule, badgeDefaults) {
	c, err := s.fullConfig(ctx)
	if err != nil {
		c = s.mergeConfig(nil)
	}
	return parseBadgeConfig(c["PosterBadgeRules"], c["PosterBadgeDefaults"])
}

func parseBadgeConfig(rawRules, rawDefaults any) ([]badgeRule, badgeDefaults) {
	var rules []badgeRule
	if b, err := json.Marshal(rawRules); err == nil {
		_ = json.Unmarshal(b, &rules)
	}
	defaults := newBadgeDefaults()
	if b, err := json.Marshal(rawDefaults); err == nil {
		_ = json.Unmarshal(b, &defaults)
	}
	return rules, defaults
}

func (d badgeDefaults) perCorner() int { return max(1, d.MaxPerCorner) }

func (s *Server) handleBadgeItems(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()
	all, defaults := s.badgeConfig(ctx)
	caller := callerOf(r)
	rules := badgesForViewer(all, strconv.Itoa(caller.UserID), r.URL.Query().Get("profile"))
	version := badgeVersion(all, defaults)
	catalog, ok := s.catalog.(BadgeCatalog)
	if len(rules) == 0 || !ok {
		// Lets a client stop asking while this viewer has no rules.
		writeJSON(w, http.StatusOK, M{"enabled": false, "version": version, "items": M{}})
		return
	}
	items := M{}
	answer := M{"enabled": true, "version": version, "maxPerCorner": defaults.perCorner(), "items": items}

	// No ids asks only whether this viewer gets any label, so a client can
	// hide a setting that would do nothing.
	ids := badgeIDs(r.URL.Query().Get("ids"))
	if len(ids) == 0 {
		writeJSON(w, http.StatusOK, answer)
		return
	}
	files, err := catalog.BadgeFiles(ctx, caller, ids)
	if err != nil {
		logError(r, "read poster label files", err)
		writeError(w, http.StatusBadGateway, s.name+" didn't say what's in these files.")
		return
	}
	for id, file := range files {
		// Unread tracks would pass every "no such track" rule, so the item gets no labels rather than wrong ones.
		if file.Tracks == nil {
			continue
		}
		if badges := matchBadges(rules, file, defaults); len(badges) > 0 {
			items[id] = badges
		}
	}
	writeJSON(w, http.StatusOK, answer)
}

func badgeIDs(raw string) []string {
	var ids []string
	for _, id := range strings.Split(raw, ",") {
		if id = strings.TrimSpace(id); id != "" && !slices.Contains(ids, id) {
			ids = append(ids, id)
			if len(ids) == badgeMaxIDs {
				break
			}
		}
	}
	return ids
}

func (s *Server) handleBadgeTest(w http.ResponseWriter, r *http.Request) {
	// Settings the page leaves out keep their defaults, as they do on Jellyfin.
	defaults := newBadgeDefaults()
	req := struct {
		ItemID   string `json:"ItemId"`
		Rules    []badgeRule
		Defaults *badgeDefaults
	}{Defaults: &defaults}
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 1<<20)).Decode(&req); err != nil || strings.TrimSpace(req.ItemID) == "" {
		writeError(w, http.StatusBadRequest, "Pick a movie or an episode.")
		return
	}
	catalog, ok := s.catalog.(BadgeCatalog)
	if !ok {
		writeError(w, http.StatusNotImplemented, s.name+" can't read its files for labels.")
		return
	}
	id := strings.TrimSpace(req.ItemID)
	files, err := catalog.BadgeFiles(r.Context(), callerOf(r), []string{id})
	if err != nil {
		logError(r, "read poster label test file", err)
		writeError(w, http.StatusBadGateway, s.name+" didn't say what's in this file.")
		return
	}
	file, found := files[id]
	if !found {
		writeError(w, http.StatusBadRequest, "Pick a movie or an episode.")
		return
	}
	results := []M{}
	for _, res := range explainBadges(req.Rules, file) {
		results = append(results, M{"index": res.index, "outcome": res.outcome, "conditions": res.conditions})
	}
	writeJSON(w, http.StatusOK, M{
		"name":         file.Name,
		"path":         file.Path,
		"tracks":       describeTracks(file.Tracks),
		"results":      results,
		"badges":       matchBadges(req.Rules, file, defaults),
		"maxPerCorner": defaults.perCorner(),
	})
}

func (s *Server) handleBadgePresets(w http.ResponseWriter, r *http.Request) {
	out := []M{}
	for _, p := range badgePresets {
		out = append(out, M{"id": p.id, "label": p.label, "group": p.group, "color": p.color, "description": p.description})
	}
	writeJSON(w, http.StatusOK, out)
}

func (s *Server) handleBadgeSearch(w http.ResponseWriter, r *http.Request) {
	items := []M{}
	term := strings.TrimSpace(r.URL.Query().Get("term"))
	catalog, ok := s.catalog.(BadgeCatalog)
	if ok && len([]rune(term)) >= 2 {
		found, err := catalog.BadgeSearch(r.Context(), callerOf(r), term, 8)
		if err != nil {
			logError(r, "search for the poster label test", err)
			writeError(w, http.StatusBadGateway, s.name+" didn't answer the search.")
			return
		}
		items = append(items, found...)
	}
	writeJSON(w, http.StatusOK, M{"Items": items, "TotalRecordCount": len(items)})
}

func (s *Server) handleBadgeFlag(w http.ResponseWriter, r *http.Request) {
	name := r.PathValue("file")
	ext := filepath.Ext(name)
	code := strings.TrimSuffix(strings.TrimPrefix(name, "flag-"), ext)
	if s.flagsDir == "" || (ext != ".svg" && ext != ".webp") || !strings.HasPrefix(name, "flag-") || !slices.Contains(badgeFlagCodes, code) {
		http.NotFound(w, r)
		return
	}
	f, err := os.Open(filepath.Join(s.flagsDir, name))
	if err != nil {
		http.NotFound(w, r)
		return
	}
	defer f.Close()
	info, err := f.Stat()
	if err != nil {
		http.NotFound(w, r)
		return
	}
	w.Header().Set("Cache-Control", "public, max-age=86400")
	http.ServeContent(w, r, name, info.ModTime(), f)
}

// badgesForViewer keeps the rules one viewer gets: on, with something to
// draw, and not limited to other users or devices. A client that doesn't say
// its device only gets the rules shown on every device.
func badgesForViewer(rules []badgeRule, userID, profile string) []badgeRule {
	viewer := badgeUserID(userID)
	var kept []badgeRule
	for _, rule := range rules {
		if !rule.enabled() || shownKey(rule) == "" {
			continue
		}
		if len(rule.UserIds) > 0 && !slices.ContainsFunc(rule.UserIds, func(id string) bool { return viewer != "" && badgeUserID(id) == viewer }) {
			continue
		}
		if len(rule.Profiles) > 0 && !slices.ContainsFunc(rule.Profiles, func(p string) bool { return strings.EqualFold(strings.TrimSpace(p), strings.TrimSpace(profile)) }) {
			continue
		}
		kept = append(kept, rule)
	}
	return kept
}

// badgeUserID compares ids the way the admin page writes them, without
// dashes and in lower case.
func badgeUserID(id string) string {
	return strings.ToLower(strings.ReplaceAll(strings.TrimSpace(id), "-", ""))
}

// badgeVersion fingerprints the rules and the look. Clients keep labels for
// the session and drop them when this changes, so an edit shows without a
// restart.
func badgeVersion(rules []badgeRule, d badgeDefaults) string {
	h := fnv.New64a()
	add := func(values ...any) {
		for _, v := range values {
			fmt.Fprint(h, v, "\x1f")
		}
	}
	add(d.Style, d.Opacity, d.Size, d.Corner, d.Shape, d.MaxPerCorner)
	for _, r := range rules {
		opacity := ""
		if r.Opacity != nil {
			opacity = strconv.FormatFloat(*r.Opacity, 'f', -1, 64)
		}
		add(r.Label, r.Color, r.enabled(), r.Preset, r.TextColor, r.Style, opacity, r.Size, r.Corner, r.Shape, r.Icon,
			strings.Join(r.UserIds, ","), strings.Join(r.Profiles, ","))
		for _, c := range r.Conditions {
			add(c.Field, c.Match, c.Operator, c.Value)
		}
		fmt.Fprint(h, "\x1e")
	}
	return fmt.Sprintf("%016x", h.Sum64())
}
