// Package web holds the Moonbase admin page and the files it loads.
//
// configPage.html is an unedited copy of Jellyfin/backend/Pages/configPage.html,
// refreshed by hand. host.js stands in for the Jellyfin dashboard pieces the page
// calls, so the copy never needs editing.
package web

import "embed"

//go:embed configPage.html host.js host.css icon.png fonts/*.woff2
var Files embed.FS
