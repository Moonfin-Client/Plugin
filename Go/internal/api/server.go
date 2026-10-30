// Package api serves Moonbase's /Moonfin routes as a plain net/http handler.
// Each host (Silo, the Plex companion) supplies storage, the catalog and who
// is calling.
package api

import (
	"context"
	"encoding/json"
	"errors"
	"log/slog"
	"net/http"
	"sync"

	"github.com/Moonfin-Client/Plugin/Go/internal/store"
)

// Host is what a server gives Moonbase.
type Host struct {
	// Name is the server's name in replies and on the admin page.
	Name    string
	KV      store.KV
	Catalog Catalog
	// Identify says who is calling, or ErrSignedOut.
	Identify func(*http.Request) (Identity, error)
	// AdminBackURL is where the admin page's back arrow goes. Empty hides it.
	AdminBackURL string
}

var ErrSignedOut = errors.New("caller isn't signed in")

type Server struct {
	version  string
	name     string
	backURL  string
	kv       store.KV
	catalog  Catalog
	identify func(*http.Request) (Identity, error)
	mux      *http.ServeMux
	ext      external

	// mu serializes every read-modify-write of stored settings.
	mu sync.Mutex

	// native holds what Silo kept from its old plugin form (legacy_form.go).
	nativeMu sync.RWMutex
	native   map[string]M
}

func NewServer(version string, h Host) *Server {
	if version == "" {
		version = "0.0.0-dev"
	}
	s := &Server{version: version, name: h.Name, backURL: h.AdminBackURL, kv: h.KV, catalog: h.Catalog,
		identify: h.Identify, mux: http.NewServeMux(), ext: newExternal()}
	s.routes()
	return s
}

func (s *Server) routes() {
	s.mux.HandleFunc("GET /Moonfin/Ping", s.user(s.handlePing))
	s.mux.HandleFunc("GET /Moonfin/Whoami", s.user(s.handleWhoami))
	s.mux.HandleFunc("GET /{$}", s.user(s.handleHome))
	s.settingsRoutes()
	s.externalRoutes()
	s.messageRoutes()
	s.adminRoutes()
	s.themeRoutes()
	s.collectionRoutes()
	s.watchPartyRoutes()
}

func (s *Server) ServeHTTP(w http.ResponseWriter, r *http.Request) { s.mux.ServeHTTP(w, r) }

// Identity is who the host says is calling.
type Identity struct {
	UserID int
	// Admin is the host's acting-admin decision. On Silo that's an admin
	// account through its primary profile, the rule Silo's own admin API
	// applies.
	Admin    bool
	UserName string
	// ProfileID is empty on hosts without profiles, and the profile's name
	// keys its settings instead.
	ProfileID   string
	ProfileName string
	// Primary is the account's own profile, the one named after it. Hosts
	// without profiles leave it false.
	Primary bool
	// Token is the caller's own server token, for a host whose catalog reads
	// as the caller (Plex). Never put it in a reply or a log.
	Token string
}

type identityKey struct{}

func callerOf(r *http.Request) Identity {
	id, _ := r.Context().Value(identityKey{}).(Identity)
	return id
}

// user only lets signed-in callers through. On Silo this repeats the check
// Silo's proxy makes on routes declared "authenticated".
func (s *Server) user(next http.HandlerFunc) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		id, err := s.identify(r)
		if errors.Is(err, ErrSignedOut) {
			writeError(w, http.StatusUnauthorized, "Sign in to "+s.name+" first.")
			return
		}
		if err != nil {
			logError(r, "identify caller", err)
			writeError(w, http.StatusBadGateway, s.name+" didn't say who is signed in. Check that it's running.")
			return
		}
		next(w, r.WithContext(context.WithValue(r.Context(), identityKey{}, id)))
	}
}

func (s *Server) admin(next http.HandlerFunc) http.HandlerFunc {
	return s.user(func(w http.ResponseWriter, r *http.Request) {
		if !callerOf(r).Admin {
			writeError(w, http.StatusForbidden, "Only a "+s.name+" admin can do this.")
			return
		}
		next(w, r)
	})
}

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}

func writeError(w http.ResponseWriter, status int, message string) {
	writeJSON(w, status, map[string]any{"Error": message})
}

func logError(r *http.Request, msg string, err error) {
	slog.ErrorContext(r.Context(), msg, "path", r.URL.Path, "error", err)
}
