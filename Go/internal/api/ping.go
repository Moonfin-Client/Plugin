package api

import (
	"fmt"
	"html"
	"net/http"
)

func (s *Server) handlePing(w http.ResponseWriter, r *http.Request) {
	cfg := s.config(r.Context())
	writeJSON(w, http.StatusOK, map[string]any{
		"installed":                true,
		"version":                  s.version,
		"settingsSyncEnabled":      cfg.EnableSettingsSync,
		"serverName":               s.name,
		"seerrEnabled":             false,
		"mdblistAvailable":         cfg.MdblistAPIKey != "",
		"tmdbAvailable":            cfg.TmdbAPIKey != "",
		"tmdbThumbsSupported":      true,
		"tmdbPersonLinksSupported": true,
		"tmdbReviewsSupported":     true,
		"messagesSupported":        true,
		"recommendationsSupported": false,
		"clientLogSupported":       false,
		"defaultSettings":          cfg.Defaults,
	})
}

// handleWhoami shows what the host said about the caller, for checking
// identity on a live server.
func (s *Server) handleWhoami(w http.ResponseWriter, r *http.Request) {
	id := callerOf(r)
	body := map[string]any{
		"userId":      id.UserID,
		"admin":       id.Admin,
		"userName":    id.UserName,
		"profileId":   id.ProfileID,
		"profileName": id.ProfileName,
	}
	libs, err := s.catalog.Libraries(r.Context(), id)
	if err != nil {
		logError(r, "list libraries", err)
		writeError(w, http.StatusBadGateway, s.name+" didn't return the library list.")
		return
	}
	body["libraries"] = libs
	writeJSON(w, http.StatusOK, body)
}

// handleHome is the page Silo opens from its Apps menu. The route also lets
// Moonfin apps find the Silo installation through GET /api/v2/settings/plugins.
func (s *Server) handleHome(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	fmt.Fprintf(w, `<!doctype html><meta charset="utf-8"><title>Moonbase</title>
<body style="font-family:system-ui;margin:2rem;max-width:40rem">
<h1>Moonbase %s</h1>
<p>Moonbase is installed. Sign in with a Moonfin app and it will sync this profile's settings.</p>
</body>`, html.EscapeString(s.version))
}
