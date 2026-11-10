package api

import (
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"testing/fstest"

	"github.com/Moonfin-Client/Plugin/Go/internal/store"
)

var testBundle = fstest.MapFS{
	"index.html":       {Data: []byte(`<html><head><base href="/Moonfin/Web/"><script src="flutter_bootstrap.js"></script></head></html>`)},
	"main.dart.js":     {Data: []byte("main()")},
	"main.wasm":        {Data: []byte("\x00asm")},
	"version.json":     {Data: []byte(`{"version":"3.0.0","build_number":"1"}`)},
	"assets/NOTICES":   {Data: []byte("licenses")},
	"theme/index.html": {Data: []byte("<title>Theme editor</title>")},
}

func webServer(t *testing.T, h func(*Host)) *Server {
	t.Helper()
	host := testHost(store.NewMemKV())
	host.Web = testBundle
	if h != nil {
		h(&host)
	}
	return NewServer("1.2.3", host)
}

func TestWebAppIsServedWithoutSigningIn(t *testing.T) {
	s := webServer(t, nil)
	if resp := serve(context.Background(), s, anon, "GET", "/Moonfin/Web", "", nil); resp.StatusCode != http.StatusFound || resp.Header.Get("Location") != "Web/" {
		t.Fatalf("the address without a slash should redirect to Web/, got %d %q", resp.StatusCode, resp.Header.Get("Location"))
	}
	for _, path := range []string{"/Moonfin/Web/", "/Moonfin/Web/plex-sign-in"} {
		resp := serve(context.Background(), s, anon, "GET", path, "", nil)
		page := string(readAll(t, resp))
		if resp.StatusCode != http.StatusOK || !strings.Contains(page, "flutter_bootstrap.js") {
			t.Fatalf("%s: status %d, page %q", path, resp.StatusCode, page)
		}
		if strings.Contains(page, `<base href="/Moonfin/Web/">`) || !strings.Contains(page, "indexOf('/Moonfin/Web')") {
			t.Fatalf("%s: the fixed base href wasn't replaced: %q", path, page)
		}
		if resp.Header.Get("Cache-Control") != "no-store" {
			t.Fatalf("%s: the page must not be cached, got %q", path, resp.Header.Get("Cache-Control"))
		}
	}
}

func TestWebAppFilesRevalidateByBundleVersion(t *testing.T) {
	s := webServer(t, nil)
	resp := serve(context.Background(), s, anon, "GET", "/Moonfin/Web/main.wasm", "", nil)
	if resp.StatusCode != http.StatusOK || resp.Header.Get("Content-Type") != "application/wasm" {
		t.Fatalf("status %d, type %q", resp.StatusCode, resp.Header.Get("Content-Type"))
	}
	tag := resp.Header.Get("ETag")
	if tag == "" {
		t.Fatal("no ETag")
	}
	r := httptest.NewRequest("GET", "/Moonfin/Web/main.dart.js", nil)
	r.Header.Set("If-None-Match", tag)
	w := httptest.NewRecorder()
	s.ServeHTTP(w, r)
	if w.Code != http.StatusNotModified {
		t.Fatalf("a matching ETag got %d, want 304", w.Code)
	}
	resp = serve(context.Background(), s, anon, "GET", "/Moonfin/Web/assets/NOTICES", "", nil)
	if body := string(readAll(t, resp)); resp.StatusCode != http.StatusOK || body != "licenses" {
		t.Fatalf("a file without an extension should be served as itself, got %d %q", resp.StatusCode, body)
	}
	if resp := serve(context.Background(), s, anon, "GET", "/Moonfin/Web/missing.js", "", nil); resp.StatusCode != http.StatusNotFound {
		t.Fatalf("missing file: %d", resp.StatusCode)
	}
}

func TestThemeEditorKeepsItsOwnPage(t *testing.T) {
	s := webServer(t, nil)
	if resp := serve(context.Background(), s, anon, "GET", "/Moonfin/Web/theme", "", nil); resp.StatusCode != http.StatusFound || resp.Header.Get("Location") != "theme/" {
		t.Fatalf("got %d %q", resp.StatusCode, resp.Header.Get("Location"))
	}
	resp := serve(context.Background(), s, anon, "GET", "/Moonfin/Web/theme/", "", nil)
	if page := string(readAll(t, resp)); resp.StatusCode != http.StatusOK || !strings.Contains(page, "Theme editor") {
		t.Fatalf("got %d %q", resp.StatusCode, page)
	}
}

func TestWebConfigFollowsTheHost(t *testing.T) {
	silo := webServer(t, func(h *Host) { h.WebPluginMode = true })
	_, got := call(t, silo, anon, "GET", "/Moonfin/Web/config.json", "", nil)
	if got["pluginMode"] != true || got["defaultServerUrl"] != "" || got["forcedServerUrl"] != nil || got["enableWebRtcScan"] != true {
		t.Fatalf("silo config: %v", got)
	}

	plex := webServer(t, func(h *Host) { h.WebServer = "http://192.168.1.10:32400" })
	_, got = call(t, plex, anon, "GET", "/Moonfin/Web/config.json", "", nil)
	if got["pluginMode"] != false || got["defaultServerUrl"] != "http://192.168.1.10:32400" {
		t.Fatalf("plex config: %v", got)
	}

	body := `{"WebDefaultServerUrl":" https://plex.example.com ","WebForcedServerUrl":"https://forced.example.com","WebEnableWebRtcScan":false}`
	if code, _ := call(t, plex, adminMain, "POST", "/Moonfin/Admin/Configuration", body, nil); code != http.StatusNoContent {
		t.Fatalf("save: %d", code)
	}
	_, got = call(t, plex, anon, "GET", "/Moonfin/Web/config.json", "", nil)
	if got["defaultServerUrl"] != "https://plex.example.com" || got["forcedServerUrl"] != "https://forced.example.com" || got["enableWebRtcScan"] != false {
		t.Fatalf("the Web Client tab's settings should win: %v", got)
	}
}

func TestBuildWithoutTheBundle(t *testing.T) {
	s := webServer(t, func(h *Host) { h.Web = fstest.MapFS{".keep": {}} })
	code, got := call(t, s, anon, "GET", "/Moonfin/Web/", "", nil)
	if code != http.StatusNotFound || got["Error"] == nil {
		t.Fatalf("got %d %v", code, got)
	}
	if page := string(readAll(t, serve(context.Background(), s, userMain, "GET", "/", "", nil))); strings.Contains(page, "Moonfin/Web") {
		t.Fatalf("the home page links to a web app that isn't there: %q", page)
	}
	with := webServer(t, nil)
	if page := string(readAll(t, serve(context.Background(), with, userMain, "GET", "/", "", nil))); !strings.Contains(page, "Open Moonfin Web") {
		t.Fatalf("the home page doesn't link to Moonfin Web: %q", page)
	}
}
