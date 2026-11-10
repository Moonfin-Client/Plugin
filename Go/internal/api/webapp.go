package api

import (
	"bytes"
	"cmp"
	"crypto/sha256"
	"encoding/hex"
	"io/fs"
	"net/http"
	"path"
	"strings"

	"github.com/Moonfin-Client/Plugin/Go/web"
)

// webBase replaces the bundle's fixed base href. Silo serves plugins under its
// own path and a proxy may add another, so the page works its base out from
// where it was opened.
const webBase = `<script>(function () {
  var p = location.pathname, i = p.indexOf('/Moonfin/Web');
  var b = document.createElement('base');
  b.href = (i < 0 ? '' : p.slice(0, i)) + '/Moonfin/Web/';
  document.head.appendChild(b);
})();</script>`

func bundledWeb() fs.FS {
	sub, _ := fs.Sub(web.Moonfin, "moonfin")
	return sub
}

func (s *Server) webRoutes() {
	s.mux.HandleFunc("GET /Moonfin/Web", s.handleWebApp)
	s.mux.HandleFunc("GET /Moonfin/Web/{path...}", s.handleWebApp)
}

func (s *Server) handleWebApp(w http.ResponseWriter, r *http.Request) {
	if r.URL.Path == "/Moonfin/Web" {
		addSlash(w, "Web/")
		return
	}
	rel := r.PathValue("path")
	dir := strings.TrimSuffix(rel, "/")
	switch {
	case rel == "config.json":
		s.handleWebConfig(w, r)
	case isFile(s.web, rel):
		if s.webTag != "" {
			w.Header().Set("ETag", s.webTag)
			w.Header().Set("Cache-Control", "no-cache")
		}
		http.ServeFileFS(w, r, s.web, rel)
	case dir != "" && isFile(s.web, path.Join(dir, "index.html")):
		// A folder with its own page, like the theme editor, is served as is.
		if rel == dir {
			addSlash(w, path.Base(dir)+"/")
			return
		}
		http.ServeFileFS(w, r, s.web, path.Join(dir, "index.html"))
	case path.Ext(rel) != "":
		http.NotFound(w, r)
	default:
		s.serveWebIndex(w, r)
	}
}

// addSlash redirects to the same folder with a trailing slash. The address is
// relative, since the one the browser used may start with a host's own path.
// Without the slash, the browser fetches files one folder too high.
func addSlash(w http.ResponseWriter, location string) {
	w.Header().Set("Location", location)
	w.WriteHeader(http.StatusFound)
}

func (s *Server) serveWebIndex(w http.ResponseWriter, r *http.Request) {
	page, err := fs.ReadFile(s.web, "index.html")
	if err != nil {
		writeError(w, http.StatusNotFound, "This build of Moonbase doesn't include Moonfin Web.")
		return
	}
	page = bytes.Replace(page, []byte(`<base href="/Moonfin/Web/">`), []byte(webBase), 1)
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	_, _ = w.Write(page)
}

// handleWebConfig tells the app where its server is. On Silo the app is served
// by the server it signs in to, as on Jellyfin and Emby. On Plex it's served
// next to the server, so it offers the Plex address instead.
func (s *Server) handleWebConfig(w http.ResponseWriter, r *http.Request) {
	cfg := s.config(r.Context())
	var forced any
	if cfg.WebForcedServerURL != "" {
		forced = cfg.WebForcedServerURL
	}
	w.Header().Set("Cache-Control", "no-store")
	writeJSON(w, http.StatusOK, M{
		"schemaVersion":    1,
		"defaultServerUrl": cmp.Or(cfg.WebDefaultServerURL, s.webServer),
		"forcedServerUrl":  forced,
		"enableWebRtcScan": cfg.WebRtcScan,
		"brandingName":     "Moonfin",
		"pluginMode":       s.webPluginMode,
	})
}

// bundleTag names the bundle's version, so a browser revalidates the large
// files rather than downloading them on every visit.
func bundleTag(fsys fs.FS) string {
	v, err := fs.ReadFile(fsys, "version.json")
	if err != nil {
		return ""
	}
	sum := sha256.Sum256(v)
	return `"` + hex.EncodeToString(sum[:8]) + `"`
}

// hasWebApp reports whether this build carries Moonfin Web.
func (s *Server) hasWebApp() bool { return isFile(s.web, "index.html") }

func isFile(fsys fs.FS, name string) bool {
	info, err := fs.Stat(fsys, name)
	return err == nil && !info.IsDir()
}
