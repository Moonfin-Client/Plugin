package api

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
)

const keySeerrSessions = "seerr-sessions"

// A status check trusts Seerr's last answer about a session for this long.
const seerrSessionFreshness = 5 * time.Minute

const (
	maxSeerrRequestBody  = 1 << 20
	maxSeerrResponseBody = 32 << 20
)

// seerrSession is one account's Seerr sign-in. The cookie is that account's Seerr
// credential, so it never leaves the server.
type seerrSession struct {
	CookieName    string `json:"cookieName"`
	Cookie        string `json:"cookie"`
	SeerrUserID   int    `json:"seerrUserId"`
	DisplayName   string `json:"displayName,omitempty"`
	Avatar        string `json:"avatar,omitempty"`
	Permissions   int    `json:"permissions"`
	CreatedAt     int64  `json:"createdAt"`
	LastValidated int64  `json:"lastValidated"`
}

// seerrClient never follows redirects, since a redirect means a reverse proxy
// or a wrong address in front of Seerr, and following it would hide that.
var seerrClient = &http.Client{
	Timeout:       30 * time.Second,
	CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse },
}

func (s *Server) seerrRoutes() {
	m := s.mux
	m.HandleFunc("POST /Moonfin/Seerr/Login", s.user(s.handleSeerrLogin))
	m.HandleFunc("GET /Moonfin/Seerr/Status", s.user(s.handleSeerrStatus))
	m.HandleFunc("GET /Moonfin/Seerr/Validate", s.user(s.handleSeerrValidate))
	m.HandleFunc("DELETE /Moonfin/Seerr/Logout", s.user(s.handleSeerrLogout))
	for _, method := range []string{http.MethodGet, http.MethodPost, http.MethodPut, http.MethodDelete} {
		m.HandleFunc(method+" /Moonfin/Seerr/Api/{path...}", s.user(s.handleSeerrAPI))
	}
}

// seerrOn is Ping's seerrEnabled.
func seerrOn(cfg Config) bool { return cfg.SeerrEnabled && cfg.SeerrURL != "" }

func seerrURLIfOn(cfg Config) any {
	if !seerrOn(cfg) {
		return nil
	}
	return cfg.SeerrURL
}

// normalizeSeerrURL trims what admins paste around an address, adds http://
// when there's no scheme and drops trailing slashes. Empty when it isn't a
// usable http or https address.
func normalizeSeerrURL(raw string) string {
	v := strings.TrimSpace(strings.Trim(strings.TrimSpace(raw), `"'`))
	if v == "" {
		return ""
	}
	if !strings.Contains(v, "://") {
		v = "http://" + v
	}
	v = strings.TrimRight(v, "/")
	u, err := url.Parse(v)
	if err != nil || (u.Scheme != "http" && u.Scheme != "https") || u.Host == "" {
		return ""
	}
	return v
}

func seerrAccount(id Identity) string { return strconv.Itoa(id.UserID) }

func (s *Server) loadSeerrSession(ctx context.Context, id Identity) (*seerrSession, error) {
	s.seerrMu.Lock()
	defer s.seerrMu.Unlock()
	var all map[string]seerrSession
	if _, err := s.loadJSON(ctx, keySeerrSessions, &all); err != nil {
		return nil, err
	}
	session, ok := all[seerrAccount(id)]
	if !ok || session.Cookie == "" {
		return nil, nil
	}
	return &session, nil
}

// putSeerrSession stores session for id, or removes id's when session is nil.
func (s *Server) putSeerrSession(ctx context.Context, id Identity, session *seerrSession) error {
	s.seerrMu.Lock()
	defer s.seerrMu.Unlock()
	var all map[string]seerrSession
	if _, err := s.loadJSON(ctx, keySeerrSessions, &all); err != nil {
		return err
	}
	if all == nil {
		all = map[string]seerrSession{}
	}
	if session == nil {
		delete(all, seerrAccount(id))
	} else {
		all[seerrAccount(id)] = *session
	}
	return s.saveJSON(ctx, keySeerrSessions, all)
}

type seerrLoginBody struct {
	Username  string `json:"username"`
	Password  string `json:"password"`
	AuthType  string `json:"authType"`
	AuthToken string `json:"authToken"`
}

// seerrUser is the part of Seerr's user record Moonbase keeps.
type seerrUser struct {
	ID          int    `json:"id"`
	DisplayName string `json:"displayName"`
	Avatar      string `json:"avatar"`
	Permissions int    `json:"permissions"`
	PlexID      int    `json:"plexId"`
}

func (s *Server) handleSeerrLogin(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()
	cfg := s.config(ctx)
	if !seerrOn(cfg) {
		writeJSON(w, http.StatusServiceUnavailable, M{"error": "Seerr integration is not enabled"})
		return
	}
	var body seerrLoginBody
	if err := json.NewDecoder(io.LimitReader(r.Body, 64<<10)).Decode(&body); err != nil {
		writeJSON(w, http.StatusBadRequest, M{"error": "Send the sign-in as JSON"})
		return
	}
	caller := callerOf(r)

	var endpoint string
	var payload any
	switch strings.ToLower(body.AuthType) {
	case "plex":
		if !s.seerrPlexLogin {
			writeJSON(w, http.StatusBadRequest, M{"error": "Plex sign-in isn't available on " + s.name, "errorCode": "plex_unsupported"})
			return
		}
		if body.AuthToken == "" {
			writeJSON(w, http.StatusBadRequest, M{"error": "authToken is required"})
			return
		}
		endpoint, payload = "auth/plex", M{"authToken": body.AuthToken}
	case "local":
		if body.Username == "" {
			writeJSON(w, http.StatusBadRequest, M{"error": "Username is required"})
			return
		}
		endpoint, payload = "auth/local", M{"email": body.Username, "password": body.Password}
	default:
		writeJSON(w, http.StatusBadRequest, M{"error": "Sign in to Seerr with authType plex or local", "errorCode": "unsupported_auth_type"})
		return
	}

	session, user, err := seerrSignIn(ctx, cfg.SeerrURL, endpoint, payload)
	if err != nil {
		slog.WarnContext(ctx, "Seerr sign-in failed", "user", caller.UserID, "error", err)
		writeJSON(w, http.StatusUnauthorized, M{"error": err.Error(), "success": false})
		return
	}
	// The token decides which Seerr account signs in, so it has to be the caller's own.
	if endpoint == "auth/plex" && user.PlexID != caller.UserID {
		seerrSignOut(ctx, cfg.SeerrURL, session)
		writeJSON(w, http.StatusForbidden, M{"error": "That Plex account isn't the one signed in to Moonbase.", "success": false})
		return
	}
	if err := s.putSeerrSession(ctx, caller, session); err != nil {
		logError(r, "save the Seerr session", err)
		writeError(w, http.StatusInternalServerError, "Couldn't save the Seerr sign-in.")
		return
	}
	slog.InfoContext(ctx, "Seerr session created", "user", caller.UserID, "seerrUser", user.ID)
	writeJSON(w, http.StatusOK, M{
		"success": true, "seerrUserId": session.SeerrUserID, "displayName": session.DisplayName,
		"avatar": session.Avatar, "permissions": session.Permissions,
	})
}

func (s *Server) handleSeerrStatus(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()
	cfg := s.config(ctx)
	if !seerrOn(cfg) {
		writeJSON(w, http.StatusOK, M{"enabled": false, "authenticated": false, "url": nil})
		return
	}
	// Seerr is asked rather than trusting a stored cookie, since a reinstalled
	// Seerr keeps none of its old sessions.
	session, err := s.seerrSessionFor(ctx, cfg, callerOf(r), seerrSessionFreshness)
	if err != nil {
		logError(r, "read the Seerr session", err)
	}
	body := M{"enabled": true, "authenticated": session != nil, "url": cfg.SeerrURL, "permissions": 0}
	if session != nil {
		body["seerrUserId"] = session.SeerrUserID
		body["displayName"] = session.DisplayName
		body["avatar"] = session.Avatar
		body["permissions"] = session.Permissions
		body["sessionCreated"] = session.CreatedAt
		body["lastValidated"] = session.LastValidated
	}
	writeJSON(w, http.StatusOK, body)
}

func (s *Server) handleSeerrValidate(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()
	cfg := s.config(ctx)
	session, err := s.seerrSessionFor(ctx, cfg, callerOf(r), 0)
	if err != nil {
		logError(r, "read the Seerr session", err)
	}
	body := M{"valid": session != nil}
	if session != nil {
		body["lastValidated"] = session.LastValidated
	}
	writeJSON(w, http.StatusOK, body)
}

func (s *Server) handleSeerrLogout(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()
	cfg := s.config(ctx)
	caller := callerOf(r)
	session, err := s.loadSeerrSession(ctx, caller)
	if err != nil {
		logError(r, "read the Seerr session", err)
	}
	if session != nil && cfg.SeerrURL != "" {
		seerrSignOut(ctx, cfg.SeerrURL, session)
	}
	if err := s.putSeerrSession(ctx, caller, nil); err != nil {
		logError(r, "remove the Seerr session", err)
		writeError(w, http.StatusInternalServerError, "Couldn't sign out of Seerr.")
		return
	}
	writeJSON(w, http.StatusOK, M{"success": true, "message": "Logged out from Seerr"})
}

// seerrSessionFor is the caller's session, checked with Seerr when the last
// check is older than maxAge. Only Seerr refusing it signs the caller out,
// never Seerr being unreachable.
func (s *Server) seerrSessionFor(ctx context.Context, cfg Config, id Identity, maxAge time.Duration) (*seerrSession, error) {
	session, err := s.loadSeerrSession(ctx, id)
	if err != nil || session == nil || cfg.SeerrURL == "" {
		return session, err
	}
	if time.Since(time.UnixMilli(session.LastValidated)) < maxAge {
		return session, nil
	}
	if s.seerrSessionDead(ctx, cfg.SeerrURL, id, session) {
		slog.InfoContext(ctx, "Seerr no longer knows the session, removing it", "user", id.UserID)
		return nil, s.putSeerrSession(ctx, id, nil)
	}
	return session, nil
}

// seerrSessionDead asks Seerr's /auth/me about a session, and keeps what it
// says about a live one. Seerr answers 403 both for a session it can't attach
// a user to and for a user lacking a permission, so a refusal anywhere else is
// checked here before the session is dropped. Seerr being unreachable doesn't
// count as dead.
func (s *Server) seerrSessionDead(ctx context.Context, base string, id Identity, session *seerrSession) bool {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, base+"/api/v1/auth/me", nil)
	if err != nil {
		return false
	}
	req.AddCookie(&http.Cookie{Name: session.CookieName, Value: session.Cookie})
	resp, err := seerrClient.Do(req)
	if err != nil {
		slog.WarnContext(ctx, "Couldn't reach Seerr to check a session", "user", id.UserID, "error", err)
		return false
	}
	defer resp.Body.Close()
	body, _ := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if resp.StatusCode == http.StatusUnauthorized || resp.StatusCode == http.StatusForbidden {
		return true
	}
	if resp.StatusCode/100 != 2 || looksLikeHTML(resp.Header.Get("Content-Type"), body) {
		return false
	}
	var me seerrUser
	if json.Unmarshal(body, &me) == nil && me.ID != 0 {
		session.Permissions = me.Permissions
	}
	rotateSeerrCookie(session, resp)
	session.LastValidated = time.Now().UnixMilli()
	if err := s.putSeerrSession(ctx, id, session); err != nil {
		slog.WarnContext(ctx, "save the checked Seerr session", "user", id.UserID, "error", err)
	}
	return false
}

// rotateSeerrCookie keeps a session cookie Seerr reissued on a response.
func rotateSeerrCookie(session *seerrSession, resp *http.Response) bool {
	for _, c := range resp.Cookies() {
		if c.Name == session.CookieName && c.Value != "" && c.Value != session.Cookie {
			session.Cookie = c.Value
			return true
		}
	}
	return false
}

// addSeerrCSRF gets the CSRF cookies Seerr hands out on any response and puts
// them on a write, with the token in a header, for a Seerr with CSRF
// protection on.
func addSeerrCSRF(ctx context.Context, base string, write *http.Request) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, base+"/api/v1/auth/me", nil)
	if err != nil {
		return
	}
	resp, err := seerrClient.Do(req)
	if err != nil {
		return
	}
	resp.Body.Close()
	for _, c := range resp.Cookies() {
		switch c.Name {
		case "XSRF-TOKEN":
			write.Header.Set("X-XSRF-TOKEN", c.Value)
			write.Header.Set("X-CSRF-Token", c.Value)
			write.AddCookie(&http.Cookie{Name: c.Name, Value: c.Value})
		case "_csrf":
			write.AddCookie(&http.Cookie{Name: c.Name, Value: c.Value})
		}
	}
}

// seerrSignIn posts a sign-in to Seerr and returns the session it opened. The
// error text is shown to the user.
func seerrSignIn(ctx context.Context, base, endpoint string, payload any) (*seerrSession, *seerrUser, error) {
	raw, _ := json.Marshal(payload)
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, base+"/api/v1/"+endpoint, bytes.NewReader(raw))
	if err != nil {
		return nil, nil, errors.New("The Seerr address in Moonbase isn't usable.")
	}
	req.Header.Set("Content-Type", "application/json")
	if u, err := url.Parse(base); err == nil {
		req.Header.Set("Origin", u.Scheme+"://"+u.Host)
	}
	req.Header.Set("Referer", base+"/")
	addSeerrCSRF(ctx, base, req)

	resp, err := seerrClient.Do(req)
	if err != nil {
		return nil, nil, fmt.Errorf("Can't reach Seerr: %v", err)
	}
	defer resp.Body.Close()
	body, _ := io.ReadAll(io.LimitReader(resp.Body, 1<<20))
	if resp.StatusCode/100 == 3 {
		return nil, nil, errors.New("Seerr redirected the sign-in. Check that the Seerr address in Moonbase matches its public address, https and any sub-path included.")
	}
	if resp.StatusCode/100 != 2 {
		if resp.StatusCode == http.StatusForbidden {
			return nil, nil, errors.New("Access denied. Make sure you have a Seerr account.")
		}
		if msg := seerrMessage(body); msg != "" {
			return nil, nil, errors.New(msg)
		}
		return nil, nil, fmt.Errorf("Authentication failed: %d", resp.StatusCode)
	}

	name, value := sessionCookieOf(resp)
	if value == "" {
		return nil, nil, errors.New("No session cookie received from Seerr")
	}
	var user seerrUser
	if err := json.Unmarshal(body, &user); err != nil || user.ID == 0 {
		return nil, nil, errors.New("Seerr didn't say who signed in.")
	}
	now := time.Now().UnixMilli()
	return &seerrSession{
		CookieName: name, Cookie: value, SeerrUserID: user.ID, DisplayName: user.DisplayName,
		Avatar: user.Avatar, Permissions: user.Permissions, CreatedAt: now, LastValidated: now,
	}, &user, nil
}

// sessionCookieOf finds the session cookie on a sign-in answer. Stock Seerr
// calls it connect.sid, but a rebranded build can rename it, so any cookie
// that isn't a CSRF one counts.
func sessionCookieOf(resp *http.Response) (string, string) {
	var fallback *http.Cookie
	for _, c := range resp.Cookies() {
		if c.Value == "" {
			continue
		}
		if c.Name == "connect.sid" {
			return c.Name, c.Value
		}
		if c.Name != "XSRF-TOKEN" && c.Name != "_csrf" && fallback == nil {
			fallback = c
		}
	}
	if fallback == nil {
		return "", ""
	}
	return fallback.Name, fallback.Value
}

// seerrSignOut ends a session on Seerr, best effort.
func seerrSignOut(ctx context.Context, base string, session *seerrSession) {
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, base+"/api/v1/auth/logout", nil)
	if err != nil {
		return
	}
	req.AddCookie(&http.Cookie{Name: session.CookieName, Value: session.Cookie})
	addSeerrCSRF(ctx, base, req)
	if resp, err := seerrClient.Do(req); err == nil {
		resp.Body.Close()
	}
}

func seerrMessage(body []byte) string {
	var m struct {
		Message string `json:"message"`
		Error   string `json:"error"`
	}
	if json.Unmarshal(body, &m) != nil {
		return ""
	}
	if m.Message != "" {
		return m.Message
	}
	return m.Error
}

// validSeerrPath keeps the proxy inside Seerr's /api/v1. The path arrives
// still escaped, so it's decoded first or %2e%2e would get past.
func validSeerrPath(path string) bool {
	decoded, err := url.PathUnescape(path)
	if err != nil || decoded == "" || strings.ContainsAny(decoded, "\\\x00") {
		return false
	}
	for _, segment := range strings.Split(decoded, "/") {
		if segment == "." || segment == ".." {
			return false
		}
	}
	return true
}

func (s *Server) handleSeerrAPI(w http.ResponseWriter, r *http.Request) {
	ctx := r.Context()
	cfg := s.config(ctx)
	if !seerrOn(cfg) {
		writeJSON(w, http.StatusServiceUnavailable, M{"error": "Seerr integration is not enabled"})
		return
	}
	path := r.PathValue("path")
	if !validSeerrPath(path) {
		writeJSON(w, http.StatusBadRequest, M{"error": "That isn't a Seerr API path"})
		return
	}
	var body []byte
	if r.Method == http.MethodPost || r.Method == http.MethodPut {
		var err error
		body, err = io.ReadAll(io.LimitReader(r.Body, maxSeerrRequestBody+1))
		if err != nil || len(body) > maxSeerrRequestBody {
			writeJSON(w, http.StatusRequestEntityTooLarge, M{"error": "The request is too large for Seerr"})
			return
		}
	}

	status, contentType, out := s.proxySeerr(ctx, cfg.SeerrURL, callerOf(r), r.Method, path, r.URL.RawQuery, body, r.Header.Get("Content-Type"))
	if contentType == "" {
		contentType = "application/octet-stream"
	}
	w.Header().Set("Content-Type", contentType)
	w.WriteHeader(status)
	_, _ = w.Write(out)
}

func seerrJSON(v any) []byte {
	raw, _ := json.Marshal(v)
	return raw
}

// proxySeerr sends one call to Seerr's /api/v1 as the caller, with their stored
// session, and returns Seerr's answer.
func (s *Server) proxySeerr(ctx context.Context, base string, id Identity, method, path, rawQuery string, body []byte, contentType string) (int, string, []byte) {
	const jsonType = "application/json"
	session, err := s.loadSeerrSession(ctx, id)
	if err != nil {
		slog.ErrorContext(ctx, "read the Seerr session", "user", id.UserID, "error", err)
	}
	if session == nil {
		return http.StatusUnauthorized, jsonType, seerrJSON(M{"error": "Not authenticated with Seerr", "code": "NO_SESSION"})
	}

	target := base + "/api/v1/" + strings.TrimLeft(path, "/")
	if rawQuery != "" {
		target += "?" + rawQuery
	}
	var reader io.Reader
	if len(body) > 0 {
		reader = bytes.NewReader(body)
	}
	req, err := http.NewRequestWithContext(ctx, method, target, reader)
	if err != nil {
		return http.StatusBadRequest, jsonType, seerrJSON(M{"error": "That isn't a Seerr API path"})
	}
	req.AddCookie(&http.Cookie{Name: session.CookieName, Value: session.Cookie})
	if len(body) > 0 {
		if contentType == "" {
			contentType = jsonType
		}
		req.Header.Set("Content-Type", contentType)
	}
	if method != http.MethodGet && method != http.MethodHead {
		addSeerrCSRF(ctx, base, req)
	}

	resp, err := seerrClient.Do(req)
	if err != nil {
		slog.ErrorContext(ctx, "proxy a call to Seerr", "path", path, "error", err)
		return http.StatusBadGateway, jsonType, seerrJSON(M{"error": "Can't reach Seerr: " + err.Error()})
	}
	defer resp.Body.Close()
	out, _ := io.ReadAll(io.LimitReader(resp.Body, maxSeerrResponseBody))
	respType := resp.Header.Get("Content-Type")

	if resp.StatusCode/100 == 3 {
		slog.WarnContext(ctx, "Seerr answered a proxied call with a redirect", "status", resp.StatusCode, "location", resp.Header.Get("Location"))
		return http.StatusBadGateway, jsonType, seerrJSON(M{
			"error": "Seerr redirected the request. Check that the Seerr address in Moonbase matches its public address, or let the media server past any reverse proxy sign-in.",
			"code":  "UPSTREAM_REDIRECT", "location": resp.Header.Get("Location"),
		})
	}
	if looksLikeHTML(respType, out) {
		return http.StatusBadGateway, jsonType, seerrJSON(M{
			"error": "Seerr answered with a web page instead of API data. A reverse proxy in front of Seerr is likely catching the request, so let the media server past its sign-in.",
			"code":  "UPSTREAM_HTML",
		})
	}

	// A refusal can be a permission Seerr withholds or a session it no longer
	// knows, and only the second signs the caller out.
	if resp.StatusCode == http.StatusUnauthorized || resp.StatusCode == http.StatusForbidden {
		if !s.seerrSessionDead(ctx, base, id, session) {
			return resp.StatusCode, respType, out
		}
		slog.InfoContext(ctx, "Seerr session expired", "user", id.UserID)
		if err := s.putSeerrSession(ctx, id, nil); err != nil {
			slog.ErrorContext(ctx, "remove the expired Seerr session", "user", id.UserID, "error", err)
		}
		return http.StatusUnauthorized, jsonType, seerrJSON(M{"error": "Seerr session expired", "code": "SESSION_EXPIRED"})
	}

	if resp.StatusCode/100 == 2 && rotateSeerrCookie(session, resp) {
		if err := s.putSeerrSession(ctx, id, session); err != nil {
			slog.WarnContext(ctx, "save the rotated Seerr cookie", "user", id.UserID, "error", err)
		}
	}
	return resp.StatusCode, respType, out
}

// looksLikeHTML goes by the content type and the first bytes, since some
// proxies leave the content type out.
func looksLikeHTML(contentType string, body []byte) bool {
	if strings.Contains(strings.ToLower(contentType), "text/html") {
		return true
	}
	head := strings.ToLower(strings.TrimSpace(string(body[:min(len(body), 64)])))
	return strings.HasPrefix(head, "<!doctype html") || strings.HasPrefix(head, "<html")
}
