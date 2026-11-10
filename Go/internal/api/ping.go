package api

import (
	"fmt"
	"html"
	"net/http"

	"github.com/Moonfin-Client/Plugin/Go/internal/settings"
)

func (s *Server) handlePing(w http.ResponseWriter, r *http.Request) {
	cfg := s.config(r.Context())
	writeJSON(w, http.StatusOK, map[string]any{
		"installed":                true,
		"version":                  s.version,
		"settingsSyncEnabled":      cfg.EnableSettingsSync,
		"serverName":               s.name,
		"seerrEnabled":             seerrOn(cfg),
		"seerrUrl":                 seerrURLIfOn(cfg),
		"mdblistAvailable":         cfg.MdblistAPIKey != "",
		"tmdbAvailable":            cfg.TmdbAPIKey != "",
		"tmdbThumbsSupported":      true,
		"tmdbPersonLinksSupported": true,
		"tmdbReviewsSupported":     true,
		"messagesSupported":        true,
		"recommendationsSupported": s.similarOn(cfg),
		"pinSupported":             s.pins,
		"clientLogSupported":       cfg.ClientLogUpload,
		"smartSearchEnabled":       s.smartSearchOn(cfg),
		"calendarAvailable":        s.calendarOn(cfg, callerOf(r)),
		"calendarDaysBack":         cfg.CalendarDaysBack,
		"calendarDaysAhead":        cfg.CalendarDaysAhead,
		"calendarReminders":        s.remindersOn(),
		"arrQueueAvailable":        arrQueueOn(cfg),
		"defaultSettings":          pingDefaults(cfg, r.URL.Query().Get("profile")),
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
%s</body>`, html.EscapeString(s.version), webLink(s.hasWebApp()))
}

// pingDefaults is the admin's defaults for one device profile, laid over the
// shared ones, or the shared ones alone for global or no profile.
func pingDefaults(cfg Config, profile string) M {
	if p, ok := settings.ProfileName(profile); ok && p != "global" {
		return cfg.DefaultsFor(p)
	}
	return cfg.Defaults
}

// webLink links the page to Moonfin Web. The page's own address may or may
// not end in a slash, so the link is worked out in the browser.
func webLink(has bool) string {
	if !has {
		return ""
	}
	return `<p><a id="web" href="Moonfin/Web/">Open Moonfin Web</a></p>
<script>document.getElementById('web').href = location.pathname.replace(/\/?$/, '/') + 'Moonfin/Web/';</script>
`
}
