// Package web holds the Moonbase admin page and the files it loads.
//
// configPage.html is an exact copy of Jellyfin/backend/Pages/configPage.html,
// checked in the Build workflow. host.js supplies the Plex/Silo dashboard and
// host-specific guide options without changing that page.
package web

import "embed"

//go:embed configPage.html host.js host.css icon.png fonts/*.woff2
var Files embed.FS

// Moonfin is Moonfin Web, copied in from the Jellyfin plugin's bundle by
// make web-bundle. Without it the folder holds only a placeholder and
// Moonbase serves no web app.
//
//go:embed all:moonfin
var Moonfin embed.FS
