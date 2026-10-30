package plex

import (
	"encoding/json"
	"fmt"
	"html"
	"log/slog"
	"net/http"
	"net/url"
	"strconv"
)

// The admin page signs in with Plex's own PIN flow: Moonbase makes a PIN,
// sends the browser to Plex to approve it, and reads the token back when
// Plex returns the browser to /admin/callback.

const (
	cookiePin    = "moonbase_pin"
	plexAuthPage = "https://app.plex.tv/auth#?"
	tokenMaxAge  = 30 * 24 * 60 * 60
)

func secure(r *http.Request) bool {
	return r.TLS != nil || r.Header.Get("X-Forwarded-Proto") == "https"
}

func origin(r *http.Request) string {
	if secure(r) {
		return "https://" + publicHost(r)
	}
	return "http://" + publicHost(r)
}

func setCookie(w http.ResponseWriter, r *http.Request, name, value, path string, maxAge int) {
	http.SetCookie(w, &http.Cookie{Name: name, Value: value, Path: path, MaxAge: maxAge,
		HttpOnly: true, Secure: secure(r), SameSite: http.SameSiteLaxMode})
}

func (c *Companion) handleSignIn(w http.ResponseWriter, r *http.Request) {
	status, body, err := c.do(r.Context(), http.MethodPost, c.cfg.PlexTV+"/api/v2/pins?strong=true", "")
	var pin struct {
		ID   int    `json:"id"`
		Code string `json:"code"`
	}
	if err != nil || status/100 != 2 || json.Unmarshal(body, &pin) != nil || pin.ID == 0 || pin.Code == "" {
		slog.Error("start a Plex sign-in", "status", status, "error", err)
		page(w, http.StatusBadGateway, "Plex didn't start the sign-in. Check that this server can reach plex.tv, then try again.", "/admin/signin", "Try again")
		return
	}
	setCookie(w, r, cookiePin, strconv.Itoa(pin.ID), "/admin", 15*60)
	q := url.Values{
		"clientID":                 {c.clientID},
		"code":                     {pin.Code},
		"forwardUrl":               {origin(r) + "/admin/callback"},
		"context[device][product]": {"Moonbase"},
	}
	http.Redirect(w, r, plexAuthPage+q.Encode(), http.StatusFound)
}

func (c *Companion) handleCallback(w http.ResponseWriter, r *http.Request) {
	var id int
	ck, err := r.Cookie(cookiePin)
	if err == nil {
		id, err = strconv.Atoi(ck.Value)
	}
	if err != nil || id <= 0 {
		http.Redirect(w, r, "/admin/signin", http.StatusFound)
		return
	}
	status, body, err := c.get(r.Context(), c.cfg.PlexTV+"/api/v2/pins/"+strconv.Itoa(id), "")
	var pin struct {
		AuthToken string `json:"authToken"`
	}
	if err != nil || status != http.StatusOK || json.Unmarshal(body, &pin) != nil {
		slog.Error("finish a Plex sign-in", "status", status, "error", err)
		page(w, http.StatusBadGateway, "Plex didn't finish the sign-in. Start again.", "/admin/signin", "Sign in")
		return
	}
	if pin.AuthToken == "" {
		page(w, http.StatusOK, "Plex hasn't confirmed the sign-in yet. Approve it on the Plex page, then come back here.", "/admin/callback", "Check again")
		return
	}
	status, _, err = c.get(r.Context(), c.cfg.PMS+"/myplex/account", pin.AuthToken)
	if err != nil {
		slog.Error("check the signed-in account with Plex", "error", err)
		page(w, http.StatusBadGateway, "Moonbase couldn't reach your Plex server to check the account.", "/admin/signin", "Try again")
		return
	}
	if status != http.StatusOK {
		page(w, http.StatusForbidden, "Only the owner of this Plex server can change Moonbase's settings.", "/admin/signin", "Sign in with another account")
		return
	}
	if err := c.saveOwnerToken(pin.AuthToken); err != nil {
		slog.Error("save the owner's token", "error", err)
		page(w, http.StatusInternalServerError, "Moonbase couldn't save the sign-in. Check that its data folder is writable.", "/admin/signin", "Try again")
		return
	}
	setCookie(w, r, cookiePin, "", "/admin", -1)
	setCookie(w, r, cookieToken, pin.AuthToken, "/", tokenMaxAge)
	http.Redirect(w, r, "/admin", http.StatusFound)
}

func page(w http.ResponseWriter, status int, message, href, label string) {
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(status)
	fmt.Fprintf(w, `<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Moonbase</title>
<body style="font-family:system-ui;margin:2rem;max-width:40rem">
<h1>Moonbase for Plex</h1>
<p>%s</p>
<p><a href="%s">%s</a></p>
</body>`, html.EscapeString(message), html.EscapeString(href), html.EscapeString(label))
}
