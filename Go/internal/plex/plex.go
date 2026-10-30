// Package plex runs Moonbase as a companion service next to a Plex Media
// Server. Plex has no plugin system, so the companion serves the /Moonfin
// routes on its own port and checks each caller's Plex token with the server.
package plex

import (
	"cmp"
	"context"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/hex"
	"encoding/json"
	"encoding/xml"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"sync"
	"time"

	"github.com/Moonfin-Client/Plugin/Go/internal/api"
	"github.com/Moonfin-Client/Plugin/Go/internal/store"
)

type Config struct {
	// PMS is the Plex Media Server's address, such as http://192.168.1.10:32400.
	PMS string
	// PlexTV is plex.tv's address. Tests point it at a fake.
	PlexTV  string
	DataDir string
	Version string
}

// How long a checked token is trusted before Plex is asked again.
const identityTTL = 10 * time.Minute

type Companion struct {
	cfg      Config
	http     *http.Client
	clientID string
	api      *api.Server

	mu        sync.Mutex
	known     map[[32]byte]known
	machineID string
	owner     string
}

type known struct {
	id    api.Identity
	until time.Time
}

func New(cfg Config) (*Companion, error) {
	cfg.PMS = strings.TrimRight(cfg.PMS, "/")
	if cfg.PlexTV == "" {
		cfg.PlexTV = "https://plex.tv"
	}
	storeDir := filepath.Join(cfg.DataDir, "store")
	if err := os.MkdirAll(storeDir, 0o700); err != nil {
		return nil, err
	}
	clientID, err := loadOrCreateClientID(filepath.Join(cfg.DataDir, "client-id"))
	if err != nil {
		return nil, err
	}
	c := &Companion{cfg: cfg, http: &http.Client{Timeout: 10 * time.Second}, clientID: clientID, known: map[[32]byte]known{}}
	c.api = api.NewServer(cfg.Version, api.Host{
		Name:     "Plex",
		KV:       store.FileKV{Dir: storeDir},
		Catalog:  c,
		Identify: c.Identify,
	})
	return c, nil
}

// loadOrCreateClientID keeps one X-Plex-Client-Identifier per install, so
// plex.tv lists the companion as a single device.
func loadOrCreateClientID(path string) (string, error) {
	if b, err := os.ReadFile(path); err == nil && len(strings.TrimSpace(string(b))) > 0 {
		return strings.TrimSpace(string(b)), nil
	}
	b := make([]byte, 16)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	id := "moonbase-" + hex.EncodeToString(b)
	return id, os.WriteFile(path, []byte(id), 0o600)
}

func (c *Companion) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	if strings.HasPrefix(r.URL.Path, "/Moonfin/") {
		// The Moonfin web app calls from its own origin. Callers sign with a
		// token header, never a cookie, so any origin may call.
		w.Header().Set("Access-Control-Allow-Origin", "*")
		if r.Method == http.MethodOptions {
			w.Header().Set("Access-Control-Allow-Headers", "Authorization, Content-Type, X-Plex-Token")
			w.Header().Set("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE")
			w.Header().Set("Access-Control-Max-Age", "600")
			w.WriteHeader(http.StatusNoContent)
			return
		}
	}
	switch r.URL.Path {
	case "/":
		http.Redirect(w, r, "/admin", http.StatusFound)
		return
	case "/admin/signin":
		c.handleSignIn(w, r)
		return
	case "/admin/callback":
		c.handleCallback(w, r)
		return
	case "/admin":
		if _, err := c.Identify(r); errors.Is(err, api.ErrSignedOut) {
			http.Redirect(w, r, "/admin/signin", http.StatusFound)
			return
		}
	}
	c.api.ServeHTTP(w, r)
}

const cookieToken = "moonbase_token"

var mediaBrowserToken = regexp.MustCompile(`(?:^|[\s,])Token="([^"]+)"`)

// tokenFrom finds the caller's Plex token. Moonfin sends it as X-Plex-Token,
// in the query for image URLs, or in the MediaBrowser header its plugin calls
// share with Jellyfin. The admin page signs with a cookie.
func tokenFrom(r *http.Request) (token string, cookie bool) {
	if t := r.Header.Get("X-Plex-Token"); t != "" {
		return t, false
	}
	if t := r.URL.Query().Get("X-Plex-Token"); t != "" {
		return t, false
	}
	if m := mediaBrowserToken.FindStringSubmatch(r.Header.Get("Authorization")); m != nil {
		return m[1], false
	}
	if ck, err := r.Cookie(cookieToken); err == nil && ck.Value != "" {
		return ck.Value, true
	}
	return "", false
}

// sameOrigin reports whether a browser request came from the companion's own
// pages. Another site on the same host counts as same-site, so SameSite alone
// wouldn't stop it posting with the admin's cookie.
func sameOrigin(r *http.Request) bool {
	origin := r.Header.Get("Origin")
	if origin == "" {
		return r.Header.Get("Sec-Fetch-Site") == "" || r.Header.Get("Sec-Fetch-Site") == "same-origin"
	}
	u, err := url.Parse(origin)
	return err == nil && u.Host == publicHost(r)
}

// publicHost is the host the browser used. A reverse proxy that rewrites Host
// passes the original on as X-Forwarded-Host. A cross-site page can't set
// that header, so trusting it doesn't weaken sameOrigin.
func publicHost(r *http.Request) string {
	if h := r.Header.Get("X-Forwarded-Host"); h != "" {
		return strings.TrimSpace(strings.Split(h, ",")[0])
	}
	return r.Host
}

// Identify checks the caller's token with Plex and remembers the answer for
// a while, so a busy app doesn't ask Plex on every call.
func (c *Companion) Identify(r *http.Request) (api.Identity, error) {
	token, cookie := tokenFrom(r)
	if token == "" {
		return api.Identity{}, api.ErrSignedOut
	}
	if cookie && r.Method != http.MethodGet && r.Method != http.MethodHead && !sameOrigin(r) {
		return api.Identity{}, api.ErrSignedOut
	}
	key := sha256.Sum256([]byte(token))
	now := time.Now()
	c.mu.Lock()
	k, ok := c.known[key]
	c.mu.Unlock()
	if ok && now.Before(k.until) {
		return k.id, nil
	}
	id, err := c.lookup(r.Context(), token)
	if err != nil {
		if errors.Is(err, api.ErrSignedOut) && err != api.ErrSignedOut {
			slog.WarnContext(r.Context(), "refused a Plex token", "path", r.URL.Path, "reason", err)
		}
		return api.Identity{}, err
	}
	c.mu.Lock()
	if len(c.known) > 1000 {
		for k, v := range c.known {
			if now.After(v.until) {
				delete(c.known, k)
			}
		}
	}
	c.known[key] = known{id: id, until: now.Add(identityTTL)}
	c.mu.Unlock()
	return id, nil
}

var errUnknownToken = errors.New("plex.tv doesn't know this token")

// lookup asks the server whether the token may use it and whether it's the
// owner's, then asks plex.tv whose it is. A member's server token is often
// unknown to plex.tv, so it's then looked up in the owner's share list.
func (c *Companion) lookup(ctx context.Context, token string) (api.Identity, error) {
	status, _, err := c.get(ctx, c.cfg.PMS+"/myplex/account", token)
	switch {
	case err != nil:
		return api.Identity{}, fmt.Errorf("reach Plex: %w", err)
	case status == http.StatusUnauthorized:
		return api.Identity{}, api.ErrSignedOut
	case status != http.StatusOK && status != http.StatusForbidden:
		return api.Identity{}, fmt.Errorf("plex answered %d to /myplex/account", status)
	}
	userID, name, err := c.plexTVUser(ctx, token)
	if errors.Is(err, errUnknownToken) {
		userID, name, err = c.sharedUser(ctx, token)
	}
	if err != nil {
		return api.Identity{}, err
	}
	return api.Identity{UserID: userID, Admin: status == http.StatusOK, UserName: name, Token: token}, nil
}

func (c *Companion) plexTVUser(ctx context.Context, token string) (int, string, error) {
	status, body, err := c.get(ctx, c.cfg.PlexTV+"/api/v2/user", token)
	if err != nil {
		return 0, "", fmt.Errorf("reach plex.tv: %w", err)
	}
	if status == http.StatusUnauthorized {
		return 0, "", errUnknownToken
	}
	if status != http.StatusOK {
		return 0, "", fmt.Errorf("plex.tv answered %d to /api/v2/user", status)
	}
	var u struct {
		ID       int    `json:"id"`
		Username string `json:"username"`
		Title    string `json:"title"`
	}
	if err := json.Unmarshal(body, &u); err != nil || u.ID <= 0 {
		return 0, "", fmt.Errorf("plex.tv sent an unreadable user: %v", err)
	}
	return u.ID, cmp.Or(u.Username, u.Title), nil
}

func (c *Companion) sharedUser(ctx context.Context, token string) (int, string, error) {
	owner := c.ownerToken()
	if owner == "" {
		return 0, "", fmt.Errorf("%w: the server owner hasn't signed in to the Moonbase admin page, so shared users can't be named yet", api.ErrSignedOut)
	}
	machine, err := c.machineIdentifier(ctx)
	if err != nil {
		return 0, "", err
	}
	status, body, err := c.get(ctx, c.cfg.PlexTV+"/api/servers/"+url.PathEscape(machine)+"/shared_servers", owner)
	if err != nil {
		return 0, "", fmt.Errorf("reach plex.tv: %w", err)
	}
	if status != http.StatusOK {
		return 0, "", fmt.Errorf("plex.tv answered %d to shared_servers", status)
	}
	var list struct {
		Shares []struct {
			UserID      int    `xml:"userID,attr"`
			Username    string `xml:"username,attr"`
			Email       string `xml:"email,attr"`
			AccessToken string `xml:"accessToken,attr"`
		} `xml:"SharedServer"`
	}
	if err := xml.Unmarshal(body, &list); err != nil {
		return 0, "", fmt.Errorf("plex.tv sent an unreadable share list: %w", err)
	}
	for _, s := range list.Shares {
		if s.UserID > 0 && subtle.ConstantTimeCompare([]byte(s.AccessToken), []byte(token)) == 1 {
			name := cmp.Or(s.Username, s.Email)
			if name == "" {
				name = c.homeTitle(ctx, owner, s.UserID)
			}
			return s.UserID, name, nil
		}
	}
	return 0, "", fmt.Errorf("%w: the token isn't the owner's and isn't in the server's share list", api.ErrSignedOut)
}

// homeTitle names a managed Home member, who has no username or email. A
// failed lookup only leaves the name blank.
func (c *Companion) homeTitle(ctx context.Context, owner string, userID int) string {
	status, body, err := c.get(ctx, c.cfg.PlexTV+"/api/v2/home/users", owner)
	if err != nil || status != http.StatusOK {
		return ""
	}
	var v struct {
		Users []struct {
			ID    int    `json:"id"`
			Title string `json:"title"`
		} `json:"users"`
	}
	if json.Unmarshal(body, &v) != nil {
		return ""
	}
	for _, u := range v.Users {
		if u.ID == userID {
			return u.Title
		}
	}
	return ""
}

func (c *Companion) machineIdentifier(ctx context.Context) (string, error) {
	c.mu.Lock()
	id := c.machineID
	c.mu.Unlock()
	if id != "" {
		return id, nil
	}
	status, body, err := c.get(ctx, c.cfg.PMS+"/identity", "")
	if err != nil || status != http.StatusOK {
		return "", fmt.Errorf("read the server's identity: status %d, %v", status, err)
	}
	var v struct {
		MediaContainer struct {
			MachineIdentifier string `json:"machineIdentifier"`
		} `json:"MediaContainer"`
	}
	if err := json.Unmarshal(body, &v); err != nil || v.MediaContainer.MachineIdentifier == "" {
		return "", fmt.Errorf("the server sent no machineIdentifier: %v", err)
	}
	c.mu.Lock()
	c.machineID = v.MediaContainer.MachineIdentifier
	c.mu.Unlock()
	return v.MediaContainer.MachineIdentifier, nil
}

// Libraries lists the caller's libraries, asking Plex as the caller so a
// shared user only sees what's shared with them.
func (c *Companion) Libraries(ctx context.Context, caller api.Identity) ([]api.Library, error) {
	status, body, err := c.get(ctx, c.cfg.PMS+"/library/sections", caller.Token)
	if err != nil || status != http.StatusOK {
		return nil, fmt.Errorf("list libraries: status %d, %v", status, err)
	}
	var v struct {
		MediaContainer struct {
			Directory []struct {
				Key   string `json:"key"`
				Title string `json:"title"`
				Type  string `json:"type"`
			} `json:"Directory"`
		} `json:"MediaContainer"`
	}
	if err := json.Unmarshal(body, &v); err != nil {
		return nil, err
	}
	out := []api.Library{}
	for _, d := range v.MediaContainer.Directory {
		out = append(out, api.Library{ID: d.Key, Name: d.Title, MediaType: plexTypes[d.Type]})
	}
	return out, nil
}

// plexTypes maps Plex's library types to the ones the admin page knows.
var plexTypes = map[string]string{"movie": "movie", "show": "tv", "artist": "music", "photo": "photos"}

func (c *Companion) get(ctx context.Context, rawURL, token string) (int, []byte, error) {
	return c.do(ctx, http.MethodGet, rawURL, token)
}

// do calls Plex or plex.tv with the companion's own client headers. The token
// goes in a header, never the URL, so it stays out of logs.
func (c *Companion) do(ctx context.Context, method, rawURL, token string) (int, []byte, error) {
	req, err := http.NewRequestWithContext(ctx, method, rawURL, nil)
	if err != nil {
		return 0, nil, err
	}
	req.Header.Set("Accept", "application/json")
	req.Header.Set("X-Plex-Client-Identifier", c.clientID)
	req.Header.Set("X-Plex-Product", "Moonbase")
	req.Header.Set("X-Plex-Version", c.cfg.Version)
	if token != "" {
		req.Header.Set("X-Plex-Token", token)
	}
	resp, err := c.http.Do(req)
	if err != nil {
		return 0, nil, err
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(io.LimitReader(resp.Body, 8<<20))
	return resp.StatusCode, body, err
}

func (c *Companion) ownerToken() string {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.owner == "" {
		if b, err := os.ReadFile(filepath.Join(c.cfg.DataDir, "owner-token")); err == nil {
			c.owner = strings.TrimSpace(string(b))
		}
	}
	return c.owner
}

func (c *Companion) saveOwnerToken(token string) error {
	if err := os.WriteFile(filepath.Join(c.cfg.DataDir, "owner-token"), []byte(token), 0o600); err != nil {
		return err
	}
	c.mu.Lock()
	c.owner = token
	c.mu.Unlock()
	return nil
}
