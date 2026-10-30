package main

import (
	_ "embed"

	"github.com/Silo-Server/silo-plugin-sdk/pkg/pluginsdk/runtime"

	"github.com/Moonfin-Client/Plugin/Go/internal/silo"
)

// version is set at build time with -ldflags "-X main.version=...".
var version string

//go:embed manifest.json
var manifestJSON []byte

func main() {
	plugin := silo.New(version)
	runtime.ServeManifestWithOptions(manifestJSON, version,
		runtime.CapabilityServers{HttpRoutes: plugin}, runtime.WithConfigure(plugin.Configure))
}
