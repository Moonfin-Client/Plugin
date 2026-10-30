// Command plex runs Moonbase for Plex, a companion service that serves the
// Moonfin plugin routes next to a Plex Media Server.
package main

import (
	"log/slog"
	"net/http"
	"os"
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
	c, err := plex.New(plex.Config{PMS: pms, DataDir: envOr("MOONBASE_DATA", "/config"), Version: version})
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

func envOr(key, fallback string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return fallback
}
