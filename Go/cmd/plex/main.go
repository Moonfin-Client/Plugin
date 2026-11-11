// Command plex runs Moonbase for Plex, a companion service that serves the
// Moonfin plugin routes next to a Plex Media Server.
package main

import (
	"log/slog"
	"net/http"
	"os"
	"path/filepath"
	"time"

	"github.com/Moonfin-Client/Plugin/Go/internal/plex"
)

// version is set at build time with -ldflags "-X main.version=...".
var version string

func main() {
	pms := os.Getenv("PLEX_URL")
	if pms == "" {
		slog.Error("Set PLEX_URL to your Plex server's address, for example http://192.168.1.10:32400")
		os.Exit(2)
	}
	c, err := plex.New(plex.Config{PMS: pms, DataDir: envOr("MOONBASE_DATA", "/config"), Version: version, SmartSearchHelper: smartSearchHelper(), FlagsDir: besideProgram("MOONBASE_FLAGS", "flags"),
		WatchPartyDir: besideProgram("MOONBASE_WATCHPARTY", "watchparty"), EmulatorJSDir: besideProgram("MOONBASE_EMULATORJS", "emulatorjs")})
	if err != nil {
		slog.Error("start Moonbase", "error", err)
		os.Exit(1)
	}
	addr := envOr("MOONBASE_LISTEN", ":32500")
	srv := &http.Server{Addr: addr, Handler: c, ReadHeaderTimeout: 10 * time.Second, ReadTimeout: 30 * time.Second, IdleTimeout: 2 * time.Minute}
	slog.Info("Moonbase for Plex is listening", "addr", addr, "plex", pms, "version", version)
	if err := srv.ListenAndServe(); err != nil {
		slog.Error("serve", "error", err)
		os.Exit(1)
	}
}

// smartSearchHelper is the helper the image installs next to this program,
// unless MOONBASE_SMARTSEARCH names another.
func smartSearchHelper() string {
	return besideProgram("MOONBASE_SMARTSEARCH", filepath.Join("smartsearch", "moonbase-smartsearch"))
}

// besideProgram is the path the image installs next to this program, unless
// the environment variable names another.
func besideProgram(key, rel string) string {
	if path := os.Getenv(key); path != "" {
		return path
	}
	exe, err := os.Executable()
	if err != nil {
		return ""
	}
	return filepath.Join(filepath.Dir(exe), rel)
}

func envOr(key, fallback string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return fallback
}
