package api

import (
	"encoding/json"
	"fmt"
	"html"
	"mime"
	"net/http"
	"net/url"
	"path"
	"strconv"
	"strings"
	"sync"

	"github.com/Moonfin-Client/Plugin/Go/internal/settings"
	"github.com/Moonfin-Client/Plugin/Go/web"
)

// The admin page is the Jellyfin plugin's configPage.html, served as is with
// web/host.js standing in for the Jellyfin dashboard around it.

const keyConfig = "config"

func (s *Server) adminRoutes() {
	// Go's built-in table has no woff2, and the plugin may run without /etc/mime.types.
	_ = mime.AddExtensionType(".woff2", "font/woff2")
	m := s.mux
	m.HandleFunc("GET /admin", s.admin(s.handleAdminPage))
	m.HandleFunc("GET /admin-assets/{file...}", s.admin(s.handleAdminAsset))
	m.HandleFunc("GET /Moonfin/Assets/icon.png", s.user(s.handleIcon))
	m.HandleFunc("GET /Moonfin/Admin/Configuration", s.admin(s.handleGetConfiguration))
	m.HandleFunc("POST /Moonfin/Admin/Configuration", s.admin(s.handlePostConfiguration))
	m.HandleFunc("GET /Moonfin/Admin/Users", s.admin(s.handleAdminUsers))
	m.HandleFunc("DELETE /Moonfin/Admin/Users/{userId}", s.admin(s.handleDeleteUser))
	m.HandleFunc("GET /Moonfin/MdbList/KeyInfo", s.admin(s.handleMdbListKeyInfo))
	m.HandleFunc("POST /Moonfin/MdbList/ClearCache", s.admin(s.handleMdbListClearCache))
}

var adminPageBody = sync.OnceValue(func() string {
	raw, err := web.Files.ReadFile("configPage.html")
	if err != nil {
		panic(err)
	}
	page := string(raw)
	start, end := strings.Index(page, "<body>"), strings.LastIndex(page, "</body>")
	if start < 0 || end < start {
		panic("configPage.html has no body")
	}
	return page[start+len("<body>") : end]
})

func (s *Server) handleAdminPage(w http.ResponseWriter, r *http.Request) {
	v := url.QueryEscape(s.version)
	back := ""
	if s.backURL != "" {
		label := html.EscapeString("Back to " + s.name)
		back = fmt.Sprintf(`<a href="%s" title="%s" aria-label="%s"><span class="material-icons" aria-hidden="true">arrow_back</span></a>`,
			html.EscapeString(s.backURL), label, label)
	}
	// host.js reads the host's name from here to word the page for it.
	hostJSON, _ := json.Marshal(M{"name": s.name})
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	fmt.Fprintf(w, `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Moonbase</title>
<link rel="stylesheet" href="admin-assets/host.css?v=%[1]s">
<script>window.moonbaseHost = %[4]s;</script>
<script src="admin-assets/host.js?v=%[1]s"></script>
</head>
<body>
<div class="moonbaseHostBar">%[5]s<span class="moonbaseHostVersion">Moonbase %[2]s for %[6]s</span></div>
<div class="moonbaseHostNotices"></div>
%[3]s
</body>
</html>`, v, html.EscapeString(s.version), adminPageBody(), hostJSON, back, html.EscapeString(s.name))
}

func serveEmbedded(w http.ResponseWriter, r *http.Request, name, cacheControl string) {
	data, err := web.Files.ReadFile(name)
	if err != nil {
		http.NotFound(w, r)
		return
	}
	w.Header().Set("Content-Type", mime.TypeByExtension(path.Ext(name)))
	w.Header().Set("Cache-Control", cacheControl)
	_, _ = w.Write(data)
}

func (s *Server) handleAdminAsset(w http.ResponseWriter, r *http.Request) {
	name := r.PathValue("file")
	if name == "configPage.html" || strings.Contains(name, "..") {
		http.NotFound(w, r)
		return
	}
	serveEmbedded(w, r, name, "public, max-age=86400")
}

func (s *Server) handleIcon(w http.ResponseWriter, r *http.Request) {
	serveEmbedded(w, r, "icon.png", "public, max-age=86400")
}

// Fields the page reads from the configuration that Moonbase keeps elsewhere
// or works out, so a save never stores them.
var derivedConfigKeys = []string{"Messages", "UploadedThemes", "HasServiceAccount", "PushEnabled"}

func (s *Server) handleGetConfiguration(w http.ResponseWriter, r *http.Request) {
	out, err := s.fullConfig(r.Context())
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	messages, err := s.loadMessages(r)
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	themes, err := s.loadThemes(r.Context())
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	out["Messages"] = messages
	out["UploadedThemes"] = sortedThemes(themes.Items)
	out["HasServiceAccount"] = false
	out["PushEnabled"] = false
	writeJSON(w, http.StatusOK, out)
}

// handlePostConfiguration replaces the whole configuration, as Jellyfin's
// plugin configuration endpoint does. The page sends back everything it
// read, the old form's values included, so they're kept from here on.
func (s *Server) handlePostConfiguration(w http.ResponseWriter, r *http.Request) {
	var body M
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil || body == nil {
		writeError(w, http.StatusBadRequest, "A JSON object of plugin settings is required")
		return
	}
	for _, k := range derivedConfigKeys {
		delete(body, k)
	}
	if d, ok := body["DefaultUserSettings"].(M); ok {
		body["DefaultUserSettings"] = settings.Clean(d)
	}
	if d, ok := body["DefaultDeviceSettings"].(M); ok {
		devices := M{}
		for _, p := range settings.Profiles[1:] {
			if m, ok := d[p].(M); ok {
				devices[p] = settings.Clean(m)
			}
		}
		body["DefaultDeviceSettings"] = devices
	}
	ctx := r.Context()
	s.mu.Lock()
	defer s.mu.Unlock()
	body[keyFormMoved] = true
	if err := s.saveJSON(ctx, keyConfig, body); err != nil {
		storageFailed(w, r, err)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

// handleDeleteUser removes every profile's settings that an account has
// stored, or one profile's, for an account deleted from the server or one
// that wants a fresh start. Neither Silo nor Plex tells Moonbase when an
// account goes, so the admin does this.
func (s *Server) handleDeleteUser(w http.ResponseWriter, r *http.Request) {
	userID, profile, ok := adminTarget(r.PathValue("userId"))
	if !ok {
		writeJSON(w, http.StatusBadRequest, M{"error": "A valid userId is required"})
		return
	}
	ctx := r.Context()
	s.mu.Lock()
	defer s.mu.Unlock()
	var index []indexEntry
	if _, err := s.loadJSON(ctx, keyIndex, &index); err != nil {
		storageFailed(w, r, err)
		return
	}
	kept := index[:0:0]
	removed := 0
	for _, e := range index {
		if !e.isTarget(userID, profile) {
			kept = append(kept, e)
			continue
		}
		if err := s.saveJSON(ctx, e.key(), nil); err != nil {
			storageFailed(w, r, err)
			return
		}
		removed++
	}
	if removed > 0 {
		if err := s.saveJSON(ctx, keyIndex, kept); err != nil {
			storageFailed(w, r, err)
			return
		}
	}
	writeJSON(w, http.StatusOK, M{"success": true, "profilesRemoved": removed})
}

// handleAdminUsers lists, in the shape Jellyfin's /Users has, the accounts
// that have synced settings, since neither host lets Moonbase list accounts.
// Profiles, which Jellyfin doesn't have, lists each account's profiles with
// the ids the admin routes take for one of them, the account's own profile
// marked Primary. Entries stored before Moonbase kept that flag count the
// profile named after the account until it syncs again.
func (s *Server) handleAdminUsers(w http.ResponseWriter, r *http.Request) {
	var index []indexEntry
	if _, err := s.loadJSON(r.Context(), keyIndex, &index); err != nil {
		storageFailed(w, r, err)
		return
	}
	// The name only stands in for an account none of whose entries says
	// which profile is its own.
	knowsPrimary := map[int]bool{}
	for _, e := range index {
		knowsPrimary[e.UserID] = knowsPrimary[e.UserID] || e.Primary
	}
	users := []M{}
	byID := map[int]M{}
	for _, e := range index {
		id := strconv.Itoa(e.UserID)
		user := byID[e.UserID]
		if user == nil {
			name := e.UserName
			if name == "" {
				name = "Account " + id
			}
			user = M{"Id": id, "Name": name, "Policy": M{"IsAdministrator": e.Admin}, "Profiles": []M{}}
			byID[e.UserID] = user
			users = append(users, user)
		}
		if e.profileKey() == "" {
			// A host without profiles (Plex) has just the account.
			continue
		}
		primary := e.Primary || (!knowsPrimary[e.UserID] && strings.EqualFold(e.Profile, e.UserName))
		user["Profiles"] = append(user["Profiles"].([]M), M{"Id": id + "~" + e.profileKey(), "Name": e.Profile, "Primary": primary})
	}
	writeJSON(w, http.StatusOK, users)
}

func (s *Server) handleMdbListKeyInfo(w http.ResponseWriter, r *http.Request) {
	key := strings.TrimSpace(r.URL.Query().Get("key"))
	if key == "" {
		key = s.config(r.Context()).MdblistAPIKey
	}
	if key == "" {
		writeJSON(w, http.StatusOK, M{"success": false, "error": "No MDBList API key configured.", "isSupporter": false})
		return
	}
	var info M
	status, err := s.getJSON(r.Context(), s.ext.mdblistBase+"/user?apikey="+url.QueryEscape(key), "", &info)
	switch {
	case err != nil:
		writeJSON(w, http.StatusOK, M{"success": false, "error": fmt.Sprintf("Failed to reach MDBList: %v", err), "isSupporter": false})
	case status == http.StatusTooManyRequests:
		writeJSON(w, http.StatusOK, M{"success": false, "error": "MDBList rate limit reached. Try again later.", "isSupporter": false})
	case status/100 != 2:
		writeJSON(w, http.StatusOK, M{"success": false, "error": fmt.Sprintf("MDBList rejected the key (status %d).", status), "isSupporter": false})
	case str(info["username"]) == "":
		writeJSON(w, http.StatusOK, M{"success": false, "error": "Unexpected response from MDBList.", "isSupporter": false})
	default:
		supporter, _ := info["is_supporter"].(bool)
		writeJSON(w, http.StatusOK, compact(M{"success": true, "username": info["username"], "plan": info["plan"],
			"isSupporter": supporter, "apiRequests": info["api_requests"], "apiRequestsCount": info["api_requests_count"],
			"rateLimitRemaining": info["rate_limit_remaining"]}))
	}
}

func (s *Server) handleMdbListClearCache(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusOK, M{"success": true, "removed": s.ext.cache.clearPrefix("mdblist")})
}
