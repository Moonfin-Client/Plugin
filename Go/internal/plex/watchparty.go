package plex

import (
	"cmp"
	"context"
	"crypto/sha256"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/Moonfin-Client/Plugin/Go/internal/api"
	"github.com/Moonfin-Client/Plugin/Go/internal/watchparty"
)

// Watch parties are only served here. Other plugins built from this module
// don't register these routes, since their calls can't long-poll.

// How long an item check per token is trusted, and the admin settings.
const (
	partyItemTTL     = time.Minute
	partySettingsTTL = 5 * time.Second
)

type partyState struct {
	engine *watchparty.Engine

	mu         sync.Mutex
	items      map[[32]byte]partyItem
	settings   watchparty.Settings
	settingsAt time.Time
	name       string
}

type partyItem struct {
	item  *watchparty.Item
	until time.Time
}

func (c *Companion) startWatchParties() {
	c.party = &partyState{items: map[[32]byte]partyItem{}}
	c.party.engine = watchparty.New(c, c.partySettings, nil)
	go c.party.engine.Run(context.Background())
}

// partySettings reads the Watch Parties tab, at most every few seconds, since
// every poll and report asks.
func (c *Companion) partySettings() watchparty.Settings {
	p := c.party
	p.mu.Lock()
	fresh := time.Since(p.settingsAt) < partySettingsTTL
	s := p.settings
	p.mu.Unlock()
	if fresh {
		return s
	}
	s = c.api.WatchPartySettings(context.Background())
	p.mu.Lock()
	p.settings, p.settingsAt = s, time.Now()
	p.mu.Unlock()
	return s
}

var ratingKey = regexp.MustCompile(`^[0-9]+$`)

// FindItem asks Plex for the item as the caller, so a shared user only gets
// what's shared with them and their restrictions apply.
func (c *Companion) FindItem(ctx context.Context, caller watchparty.Caller, id string) *watchparty.Item {
	if !ratingKey.MatchString(id) || caller.Token == "" {
		return nil
	}
	key := sha256.Sum256([]byte(caller.Token + "\x00" + id))
	now := time.Now()
	p := c.party
	p.mu.Lock()
	hit, ok := p.items[key]
	p.mu.Unlock()
	if ok && now.Before(hit.until) {
		return hit.item
	}
	status, body, err := c.get(ctx, c.cfg.PMS+"/library/metadata/"+id, caller.Token)
	if err != nil || (status != http.StatusOK && status != http.StatusUnauthorized && status != http.StatusForbidden && status != http.StatusNotFound) {
		// Only a clear answer is kept, so a hiccup doesn't lock anyone out for a minute.
		return nil
	}
	var item *watchparty.Item
	if status == http.StatusOK {
		item = partyItemFrom(body)
	}
	p.mu.Lock()
	if len(p.items) > 2000 {
		for k, v := range p.items {
			if now.After(v.until) {
				delete(p.items, k)
			}
		}
	}
	p.items[key] = partyItem{item: item, until: now.Add(partyItemTTL)}
	p.mu.Unlock()
	return item
}

// partyItemFrom reads one metadata answer. Shows, seasons and other folders can't
// be watched as one item, so they read as nothing.
func partyItemFrom(body []byte) *watchparty.Item {
	var v struct {
		MediaContainer struct {
			Metadata []struct {
				RatingKey        string `json:"ratingKey"`
				Type             string `json:"type"`
				Title            string `json:"title"`
				GrandparentTitle string `json:"grandparentTitle"`
				ParentIndex      *int   `json:"parentIndex"`
				Index            *int   `json:"index"`
				Year             *int   `json:"year"`
				Duration         int64  `json:"duration"`
			} `json:"Metadata"`
		} `json:"MediaContainer"`
	}
	if json.Unmarshal(body, &v) != nil || len(v.MediaContainer.Metadata) == 0 {
		return nil
	}
	m := v.MediaContainer.Metadata[0]
	switch m.Type {
	case "show", "season", "artist", "album", "collection", "playlist", "photo":
		return nil
	}
	item := &watchparty.Item{ID: m.RatingKey, Title: m.Title, Type: capitalize(m.Type), Year: m.Year}
	if m.Duration > 0 {
		seconds := float64(m.Duration) / 1000
		item.RuntimeSeconds = &seconds
	}
	if m.Type == "episode" {
		series := m.GrandparentTitle
		item.SeriesName, item.Season, item.Episode = &series, m.ParentIndex, m.Index
	}
	return item
}

func capitalize(s string) string {
	if s == "" {
		return s
	}
	return strings.ToUpper(s[:1]) + s[1:]
}

var mediaBrowserDevice = regexp.MustCompile(`(?:^|[\s,])DeviceId="([^"]+)"`)

// deviceOf tells one device of a user from another, so a user can be in a
// party from two of them.
func deviceOf(r *http.Request) string {
	if d := cmp.Or(r.Header.Get("X-Plex-Client-Identifier"), r.URL.Query().Get("X-Plex-Client-Identifier")); d != "" {
		return d
	}
	if m := mediaBrowserDevice.FindStringSubmatch(r.Header.Get("Authorization")); m != nil {
		return m[1]
	}
	return ""
}

// serveWatchParty answers the watch party routes, or reports false for the
// rest. The older per-room Moonfin/WatchParty/{roomId} routes stay with api.
func (c *Companion) serveWatchParty(w http.ResponseWriter, r *http.Request) bool {
	path := r.URL.Path
	if code, ok := strings.CutPrefix(path, "/Moonfin/Party/"); ok {
		c.handleInvite(w, r, code)
		return true
	}
	rest, admin := strings.CutPrefix(path, "/Moonfin/Admin/WatchParty/")
	if !admin {
		var ok bool
		if rest, ok = strings.CutPrefix(path, "/Moonfin/WatchParty/"); !ok || !watchparty.Routed(rest) {
			return false
		}
	}
	id, err := c.Identify(r)
	switch {
	case errors.Is(err, api.ErrSignedOut):
		writeParty(w, watchparty.Result{Status: http.StatusUnauthorized, Body: api.M{"Error": "Sign in to Plex first."}})
		return true
	case err != nil:
		writeParty(w, watchparty.Result{Status: http.StatusBadGateway, Body: api.M{"Error": "Plex didn't say who is signed in. Check that it's running."}})
		return true
	case admin && !id.Admin:
		writeParty(w, watchparty.Result{Status: http.StatusForbidden, Body: api.M{"Error": "Only a Plex admin can do this."}})
		return true
	}
	var body []byte
	if r.Method != http.MethodGet {
		if body, err = io.ReadAll(http.MaxBytesReader(w, r.Body, 64<<10)); err != nil {
			writeParty(w, watchparty.Result{Status: http.StatusBadRequest, Body: api.M{"code": "bad_request", "message": "The body is too big."}})
			return true
		}
	}
	if admin {
		writeParty(w, c.party.engine.HandleAdmin(r.Method, rest, body))
		return true
	}
	caller := watchparty.Caller{UserID: strconv.Itoa(id.UserID), DeviceID: deviceOf(r), Name: cmp.Or(id.UserName, "Someone"), Token: id.Token,
		PublicBase: requestBase(r)}
	writeParty(w, c.party.engine.Handle(r.Context(), r.Method, rest, caller, r.Header.Get("X-Moonfin-Room-Token"), body, r.URL.Query()))
	return true
}

func writeParty(w http.ResponseWriter, res watchparty.Result) {
	if res.RetryAfter > 0 {
		w.Header().Set("Retry-After", strconv.Itoa(res.RetryAfter))
	}
	if res.Body == nil {
		w.WriteHeader(res.Status)
		return
	}
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(res.Status)
	_ = json.NewEncoder(w).Encode(res.Body)
}

// handleInvite serves the invite page with no sign-in. It never looks the
// code up. The page and preview.png come from shared/WatchParty, which the
// image puts next to the program.
func (c *Companion) handleInvite(w http.ResponseWriter, r *http.Request, code string) {
	if r.Method != http.MethodGet && r.Method != http.MethodHead {
		w.WriteHeader(http.StatusMethodNotAllowed)
		return
	}
	if code == "preview.png" {
		png, err := os.ReadFile(filepath.Join(c.cfg.WatchPartyDir, "preview.png"))
		if err != nil {
			http.NotFound(w, r)
			return
		}
		w.Header().Set("Content-Type", "image/png")
		w.Header().Set("Cache-Control", "public, max-age=86400")
		_, _ = w.Write(png)
		return
	}
	normalized := watchparty.NormalizeCode(code)
	template, err := os.ReadFile(filepath.Join(c.cfg.WatchPartyDir, "invite.html"))
	if normalized == "" || err != nil {
		http.NotFound(w, r)
		return
	}
	machine, _ := c.machineIdentifier(r.Context())
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	_, _ = io.WriteString(w, watchparty.InvitePage(string(template), c.serverName(r.Context()), machine, requestBase(r), normalized))
}

// requestBase is the companion's address as the caller reached it, which is
// where invite links point on Plex.
func requestBase(r *http.Request) string {
	scheme := "http"
	if r.TLS != nil {
		scheme = "https"
	}
	return watchparty.PublicBase(scheme, r.Host, r.Header.Get("X-Forwarded-Proto"), r.Header.Get("X-Forwarded-Host"))
}

// serverName is the Plex server's own name, read with the owner's token once
// it's known. The invite page says "this server" until then.
func (c *Companion) serverName(ctx context.Context) string {
	p := c.party
	p.mu.Lock()
	name := p.name
	p.mu.Unlock()
	owner := c.ownerToken()
	if name != "" || owner == "" {
		return name
	}
	status, body, err := c.get(ctx, c.cfg.PMS+"/", owner)
	var v struct {
		MediaContainer struct {
			FriendlyName string `json:"friendlyName"`
		} `json:"MediaContainer"`
	}
	if err != nil || status != http.StatusOK || json.Unmarshal(body, &v) != nil {
		return ""
	}
	p.mu.Lock()
	p.name = v.MediaContainer.FriendlyName
	p.mu.Unlock()
	return v.MediaContainer.FriendlyName
}
