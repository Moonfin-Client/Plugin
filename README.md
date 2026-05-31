<h1 align="center">Moonbase, the server plugin for Moonfin!</h1>
<h3 align="center">Moonbase is a multi-purpose Jellyfin server plugin that provides structural elements for all Moonfin clients.</h3>

---

<p align="center">
   <img width="1920" height="1080" alt="Moonbase" src="https://github.com/user-attachments/assets/5d67e8d0-5972-49f2-89d5-376357c8997b" />
</p>

[![License](https://img.shields.io/github/license/Moonfin-Client/Plugin.svg)](https://github.com/Moonfin-Client/Plugin) [![Release](https://img.shields.io/github/release/Moonfin-Client/Plugin.svg)](https://github.com/Moonfin-Client/Plugin/releases)

## What is Moonbase?

Moonbase is a Jellyfin server plugin that provides infrastructure for **all** Moonfin clients. The plugin handles settings synchronization, runtime web config/discovery pages, media bar and home row data preferences, enhanced Administrative pages, media rating integrations, and handles Seerr proxy/SSO configurations. User settings follow a profiled model (global profile with optional desktop/mobile/tv overrides), and admins can define and push server-wide defaults for many of Moonfin's preferences. 

The plugin also serves a **new Moonfin Flutter web interface** that can be opened directly from `/Moonfin/Web/` and has been optimized for in-browser navigation and viewing. This allows for the Moonfin web interface to run side-by-side with Jellyfin's. If desired, a link to the Moonbase web interface can be included in the stock Jellyfin Web header through the [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) bridge detailed below.

> **Note:** It is **highly recommended** that all users of any Moonfin client install and set-up Moonbase on their server to get the best possible experience!

## Opening the Moonbase Web Interface

The Moonbase Web UI can be accessed and book-marked through it's primary path: `https://your-jellyfin-host/Moonfin/Web/`

Optionally: If you install the [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) plug-in, a Moonfin icon will appear in the header (next to SyncPlay) inside stock Jellyfin Web that can be used as a direct link to the web interface.
<img width="1521" height="164" alt="image" src="https://github.com/user-attachments/assets/bcb69e4b-edbe-4d1f-b9f1-dc81822d55d9" />

> **Tip for Admins:** The Plug-in Settings panel (under *Dashboard → Plugins → Moonfin*) can be used to set default user settings, web runtime options (forced/default server URL, WebRTC scan toggle), and many other server-side settings!

## Features

### The Moonbase Web UI (`frontend/`)
- **Cross-Platform Moonfin UI** - One app surface used across web, TV, mobile, and desktop with adaptive navigation and layout behavior
- **Full Library Browsing** - Home, search, favorites, genres, folder/collection views, and unified multi-server library support
- **Multiple Playback Surfaces** - Video, audio, photo, book, trailer, next-up, and still-watching playback routes
- **Live TV and DVR Screens** - Live TV browse, guide, schedule, recordings, series recordings, and live TV player routes
- **Integrated Admin Screens** - In-app (and themed) admin routes for users, libraries, tasks, settings, plugins, logs, devices, analytics, and more
- **Built-In Seerr Screens** - Discover, requests, browse, media detail, and person detail flows in Moonfin
- **Web Diagnostics Route** - Dedicated diagnostics view for web startup and routing issues
- **Per-User Settings Profiles** - Global plus optional desktop/mobile/tv overrides with server sync

### The Moonbase Server Plugin (`backend/`)
- **Settings Sync API** - Per-user preference storage with resolved profiles, profile-specific writes, and admin-configurable defaults
- **Live Sync Events (SSE)** - Optional real-time settings/theme refresh events through `/Moonfin/Settings/Stream`
- **Admin Defaults Operations** - Push current defaults to all existing users
- **Admin Broadcast Messages** - Admins can send announcements to all connected users at any time for in-client display
- **Runtime Web Config and Discovery** - `/Moonfin/Web/config.json` plus `/Moonfin/Discovery` endpoints for same-origin plugin mode startup
- **Theme Upload and Validation APIs** - Admin upload/delete endpoints with strict schema validation and metadata tracking
- **Media Bar and Genre APIs** - Server-resolved media bar content and genre filters shared across clients
- **MDBList and TMDB Proxies** - Ratings endpoints that keep API keys server-side for users to incorporate additional ratings in their client displays
- **Seerr Proxy and SSO** - Authenticated API proxy endpoints with session handling and variant-aware config
- **Web Asset Hosting** - Serves Flutter web build output under `/Moonfin/Web/` with SPA fallback routing
- **Optional Header Bridge** - Lightweight File Transformation integration for one-click entry from stock Jellyfin Web

---

## Installation

### Plugin Repository (Recommended)

1. Jellyfin Dashboard → Administration → Plugins → Repositories
2. Add repository:
   - **Name:** `Moonfin`
   - **URL:** `https://raw.githubusercontent.com/Moonfin-Client/Plugin/refs/heads/master/manifest.json`
3. Go to Catalog → find **Moonfin** → Install
4. Restart Jellyfin

### Manual Install

1. Download the latest `Moonfin.Server-x.x.x.x.zip` from [Releases](https://github.com/Moonfin-Client/Plugin/releases)
2. Extract to your Jellyfin plugins folder:
   | Platform | Path |
   |----------|------|
   | Linux | `/var/lib/jellyfin/plugins/Moonfin/` |
   | Docker | `/config/plugins/Moonfin/` |
   | Windows | `%ProgramData%\Jellyfin\Server\plugins\Moonfin\` |
3. Restart Jellyfin

### Opening the Web App

Moonfin serves its web app at `/Moonfin/Web/`.

1. Open `/Moonfin/Web/` directly on your Jellyfin server.
2. If assets do not appear after a fresh install, run the **Moonfin Startup** task from Jellyfin Dashboard → Administration → Scheduled Tasks once, then refresh the page.

Optional one-click stock web entry:

1. Add the File Transformation plugin repository to Jellyfin:
   - **URL:** `https://www.iamparadox.dev/jellyfin/plugins/manifest.json`
2. Install the **File Transformation** plugin from the catalog
3. Restart Jellyfin
4. Force refresh your browser (Ctrl+Shift+R)
5. Click the Moonfin icon next to SyncPlay

> **UI not loading?** Go to *Dashboard → Scheduled Tasks* and run the **Moonfin Startup** task once, then refresh your browser.

### **Hide the optional header logo**
If you have the File Transformation plug-in installed but do *not* want the Moonfin icon to appear, add these CSS lines to the Branding → Custom CSS settings on the Jellyfin Administrative Dashboard:

```
.headerMoonfinButton {
display: none !important;
}
```

## Configuration

### Admin Settings

Jellyfin Dashboard → Administration → Plugins → **Moonfin** to configure:
- Seerr URL, display name, and enable/disable toggle
- Shared MDBList and TMDB API keys (so individual users don't need their own)
- **Default user settings** — set server-wide defaults for any user-facing setting; users who haven't customized a value inherit the admin default
- **Push Defaults to Existing Users** button to apply current defaults to already-initialized user profiles
- **Broadcast Message** action to send an announcement to all users for immediate in-client display
- Web startup runtime options (forced/default server URL and WebRTC scan toggle)
- Uploaded custom themes list management (upload/delete)
- Enable/disable settings sync globally

### Theme Editor and Custom Themes

- Custom theming is here! A built-in theme editor for Moonfin clients is available at `/Moonfin/Web/theme/`
- This editor allows for visual token editing, validation, and JSON export.
- Admins can upload exported themes in the plugin config page, and clients can fetch uploaded themes from within the Settings menu on their Moonfin client of choice.
- Uploaded themes are schema-validated server-side and tracked with metadata (display name, checksum, uploader, upload time).

# User Settings

Open the Moonbase Web UI at `/Moonfin/Web/`, then use the in-app settings page to customize navbar/media bar behavior, details screen settings, ratings integrations, and other synced preferences the same way you would on any other Moonfin client.

Settings support **device profiles**: a shared global profile plus optional overrides for desktop, mobile, and TV. Device profiles only store values that differ from global, so changes to global automatically flow to all devices unless explicitly overridden. A sync toggle lets you enable or disable server synchronization per-user.

### Reverse Proxy

If you run Jellyfin behind a reverse proxy (e.g., Nginx, Caddy, Traefik), make sure your proxy forwards all `/Moonfin/` paths to Jellyfin. Seerr API traffic is routed through `/Moonfin/Jellyseerr/Api/`. If your reverse proxy does not pass these paths through, Seerr integration requests can fail.

Moonfin uses a proxy-first Seerr integration: API traffic is routed through Jellyfin for seamless SSO. If Seerr integration does not work, verify your reverse proxy path forwarding and auth headers.

## Building from Source

### Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Flutter web build output synced into `frontend/` from the separate Moonfin-Core repo

If your repos are cloned in different locations, run the sync with an explicit target path:

```bash
cd /path/to/Moonfin-Core
./build-web-plugin.sh /path/to/Plugin/frontend
```

### Linux / macOS / Git Bash
```bash
./build.sh
```

### Windows (PowerShell)
```powershell
.\build.ps1
```

Both scripts accept optional parameters:
```
./build.sh [VERSION] [TARGET_ABI]
.\build.ps1 -Version "1.0.0.0" -TargetAbi "10.10.0"
```

The build will:
1. Compile the .NET server plugin
2. Bundle `frontend/` (if present) next to `Moonfin.Server.dll`
3. Package release files into a ZIP
4. Update `manifest.json` with the new checksum

Output: `Moonfin.Server-{VERSION}.zip` in the repo root.

## Project Structure

```
├── build.sh            # Build script (Linux/macOS/Git Bash)
├── build.ps1           # Build script (Windows PowerShell)
├── backend/            # .NET 8 Jellyfin server plugin
│   ├── Api/            # REST controllers (settings, web host, discovery, Jellyseerr proxy)
│   ├── Helpers/        # File Transformation patch callbacks
│   ├── Models/         # User settings, patch payload models
│   ├── Services/       # Startup task, settings persistence
│   ├── Pages/          # Admin config page HTML
│   └── Web/            # Embedded injection bridge files (loader.js/inject.html)
└── frontend/           # Flutter web build artifacts served at /Moonfin/Web/
```

## API Reference

| Endpoint | Method | Auth | Description |
|----------|--------|------|-------------|
| `/Moonfin/Ping` | GET | Yes | Check plugin status and configuration |
| `/Moonfin/Defaults` | GET | Yes | Get admin-configured default settings profile |
| `/Moonfin/Settings` | GET | Yes | Get current user's settings |
| `/Moonfin/Settings` | POST | Yes | Save settings (merge or replace) |
| `/Moonfin/Settings/Stream` | GET | Yes | SSE stream for live `settingsUpdated`, `themesChanged`, and admin message events |
| `/Moonfin/Settings` | HEAD | Yes | Check if user has saved settings |
| `/Moonfin/Settings` | DELETE | Yes | Delete user's settings |
| `/Moonfin/Settings/{userId}` | GET, POST | Admin | Get/save another user's settings |
| `/Moonfin/Admin/PushDefaults` | POST | Admin | Apply current admin defaults to all existing users |
| `/Moonfin/Broadcast` | POST | Admin | Broadcast an admin announcement to all connected users for in-client display |
| `/Moonfin/Settings/Profile/{profile}` | POST, DELETE | Yes | Save/delete a specific profile override |
| `/Moonfin/Settings/Resolved/{profile}` | GET | Yes | Get resolved profile values |
| `/Moonfin/Settings/detailsScreenBlur` and `/Moonfin/Settings/detailsScreenBlur/{profile}` | GET, POST | Yes | Get/save resolved details blur value |
| `/Moonfin/Settings/detailsScreenOpacity` and `/Moonfin/Settings/detailsScreenOpacity/{profile}` | GET, POST | Yes | Get/save resolved details opacity value |
| `/Moonfin/Themes` and `/Moonfin/Themes/{themeId}` | GET | Yes | List/get uploaded custom theme payloads |
| `/Moonfin/Admin/Themes` | GET, POST | Admin | List uploaded theme metadata and upload/replace theme payload |
| `/Moonfin/Admin/Themes/{themeId}` | DELETE | Admin | Delete uploaded custom theme |
| `/Moonfin/Genres` | GET | Yes | List genres for media bar/settings pickers |
| `/Moonfin/MediaBar` | GET | Yes | Get resolved media bar content for current user |
| `/Moonfin/Jellyseerr/Config` | GET | Yes | Get Jellyseerr/Seerr configuration (auto-detects variant) |
| `/Moonfin/Jellyseerr/Login` | POST | Yes | Authenticate with Jellyseerr/Seerr via Jellyfin credentials |
| `/Moonfin/Jellyseerr/Status` | GET | Yes | Check current user's SSO session status |
| `/Moonfin/Jellyseerr/Validate` | GET | Yes | Validate current SSO session against Seerr |
| `/Moonfin/Jellyseerr/Logout` | DELETE | Yes | Clear SSO session |
| `/Moonfin/Jellyseerr/Api/{**path}` | GET, POST, PUT, DELETE | Session | Authenticated API proxy to Jellyseerr/Seerr |
| `/Moonfin/Assets/{fileName}` | GET | Yes | Serve embedded rating icons |
| `/Moonfin/MdbList/Ratings` | GET | Yes | Fetch MDBList ratings by `type` + `tmdbId` |
| `/Moonfin/Tmdb/EpisodeRating` | GET | Yes | Fetch TMDB rating for one episode |
| `/Moonfin/Tmdb/SeasonRatings` | GET | Yes | Fetch TMDB ratings for all episodes in a season |
| `/Moonfin/Web/loader.js` | GET | No | Header-button loader bridge for stock Jellyfin Web |
| `/Moonfin/Web/config.json` | GET | No | Runtime web config for plugin mode |
| `/Moonfin/Discovery` and `/Moonfin/Discovery/discover` | GET | No | Same-origin discovery response for web mode |
| `/Moonfin/Web/{**path}` | GET | No | Serve Moonfin Flutter web assets and SPA routes |

### Seerr Config Response

```json
{
  "enabled": true,
  "url": "https://seerr.example.com",
  "displayName": "Seerr",
  "variant": "seerr",
  "userEnabled": true
}
```

| Field | Type | Description |
|-------|------|-------------|
| `enabled` | bool | Whether Seerr integration is enabled by admin |
| `url` | string | Server URL (used for API proxying) |
| `displayName` | string | UI display name (admin override or auto-detected) |
| `variant` | string | Auto-detected: `"jellyseerr"` (version < 3.0) or `"seerr"` (version ≥ 3.0) |
| `userEnabled` | bool | Whether enabled in user's personal settings |

## Settings Sync

**Direction:** Bidirectional, local-wins

### Settings Envelope (v2)

User settings are stored in a **profiled envelope** with a schema version, sync metadata, and per-device profiles:

```json
{
  "schemaVersion": 2,
  "lastUpdated": 1740200000000,
  "lastUpdatedBy": "desktop",
  "syncEnabled": true,
   "global": { /* base settings - all devices inherit from here */ },
  "desktop": { /* sparse overrides for desktop only */ },
  "mobile": { /* sparse overrides for mobile only */ },
  "tv": { /* sparse overrides for TV only */ }
}
```

**Resolution chain** (first non-null wins): device profile → global profile → admin defaults → built-in defaults.

Device profiles only contain fields the user has explicitly customized for that device. Everything else falls through to global, then to admin defaults.

> **Note:** Not all settings listed below have been integrated into every client yet. The server model defines the full set of syncable settings. Each client only reads and writes the ones it currently supports. Unsupported fields are preserved on the server and ignored by clients that don't use them.

### Synced Settings

Settings stored on the server per-user and shared across all Moonfin clients. Each setting can be set at the global level and optionally overridden per device profile.

| Setting | Type | Description |
|---------|------|-------------|
| `navbarEnabled` | bool | Enable custom navbar |
| `navbarPosition` | string | Navbar position (`top`, `left`) |
| `visualTheme` | string | Built-in theme selection (`moonfin`, `neon_pulse`, etc.) |
| `customThemeId` | string | Uploaded custom theme ID selected for this profile |
| `homeRowsStyle` | string | Home rows rendering style preset |
| `showShuffleButton` | bool | Show shuffle button in toolbar |
| `showGenresButton` | bool | Show genres button in toolbar |
| `showFavoritesButton` | bool | Show favorites button in toolbar |
| `showCastButton` | bool | Show cast/remote playback button |
| `showSyncPlayButton` | bool | Show SyncPlay button |
| `showLibrariesInToolbar` | bool | Show library buttons in toolbar |
| `shuffleContentType` | string | Shuffle content type (`movies`, `tv`, `both`) |
| `mediaBarEnabled` | bool | Enable featured media bar |
| `mediaBarSourceType` | string | Media bar content source (`library`, `collection`) |
| `mediaBarLibraryIds` | list | Library IDs to pull media bar items from (empty = all libraries) |
| `mediaBarCollectionIds` | list | Collection/playlist IDs for media bar (when source is `collection`) |
| `mediaBarItemCount` | int | Number of items in media bar |
| `mediaBarOpacity` | int | Media bar overlay opacity (0–100) |
| `mediaBarOverlayColor` | string | Media bar overlay color key |
| `seasonalSurprise` | string | Seasonal particle effect (`none`, `winter`, `spring`, `summer`, `fall`, `halloween`) |
| `mdblistEnabled` | bool | Enable MDBList ratings |
| `mdblistApiKey` | string | MDBList API key |
| `mdblistRatingSources` | list | Which rating sources to display |
| `mergeContinueWatchingNextUp` | bool | Merge Continue Watching and Next Up rows |
| `enableMultiServerLibraries` | bool | Enable multi-server library aggregation |
| `homeRowsImageTypeOverride` | bool | Override home rows image type |
| `homeRowsImageType` | string | Home rows image type (`poster`, `thumb`, `banner`) |
| `detailsScreenBlur` | string | Blur intensity for details background |
| `browsingBlur` | string | Blur intensity for browsing backgrounds |
| `themeMusicEnabled` | bool | Enable theme music playback |
| `themeMusicOnHomeRows` | bool | Play theme music on home rows |
| `themeMusicVolume` | int | Theme music volume (0–100) |
| `blockedRatings` | list | Content ratings to block |
| `jellyseerrEnabled` | bool | Enable Jellyseerr integration |
| `jellyseerrApiKey` | string | Jellyseerr API key |
| `jellyseerrRows` | object | Jellyseerr discovery row configuration |
| `mediaBarTrailerPreview` | bool | Enable trailer previews in media bar |
| `tmdbApiKey` | string | TMDB API key for episode ratings |
| `tmdbEpisodeRatingsEnabled` | bool | Enable TMDB episode ratings |
| `homeRowOrder` | list | Ordered list of enabled home screen sections |

### On Startup

- Pings `GET /Moonfin/Ping` to check if the server plugin is installed and sync is enabled
- Fetches server settings via `GET /Moonfin/Settings`
- A **snapshot** of the last-synced settings is stored in localStorage as a common ancestor for three-way merges
- **Sync scenarios:**
  - **Both local & server exist (with snapshot):** Three-way merge using the snapshot as the common ancestor. For each setting: changed locally only → keep local; changed on server only → accept server; both changed → local wins
  - **Both local & server exist (no snapshot):** First sync on this client — local wins (`{ ...server, ...local }`), then pushes the merged result to the server
  - **Server only (fresh install/new browser):** Restores server settings to localStorage. This is how settings carry over to a new client
  - **Local only (no server data yet):** Pushes local settings to the server
- After merging, the result is saved as the new snapshot for the next sync

### On Every Settings Change

- Saves to localStorage immediately
- If server is available, also pushes to server via `POST /Moonfin/Settings`

### Cross-Client Behavior

- When you open Moonfin on a **new device/browser** with no local settings, it pulls from the server and your settings follow you
- If you change settings on **Client A**, they push to server. When **Client B** next loads (page refresh/login), it syncs but Client B's local settings win in the merge, so it won't overwrite unsaved local preferences
- Clients subscribed to `/Moonfin/Settings/Stream` receive live events (`settingsUpdated`, `themesChanged`, admin messages) and can refresh without waiting for restart/login
- Admin broadcasts are delivered to connected clients through the same stream so users can see announcements immediately in-app
- Admin **Push Defaults To Existing Users** updates persisted global values and emits `settingsUpdated` so connected SSE clients can re-resolve immediately

### Limitations

- Three-way merge resolves most conflicts, but when both clients change the **same** setting, local wins. If you change different settings on two clients, the merge picks up both changes correctly
- Live update delivery only applies to clients currently connected to the SSE stream; offline clients catch up on next startup/login sync
- Sensitive data like `mdblistApiKey` is synced to the server (stored per-user)

## Contributing

We welcome contributions to Moonfin for Jellyfin Web!

### Guidelines
1. **Check existing issues** - See if your idea/bug is already reported
2. **Discuss major changes** - Open an issue first for significant features
3. **Follow code style** - Match the existing codebase conventions
4. **Test across clients** - Verify changes work on desktop browsers and mobile
5. **Consider upstream** - Features that benefit all users should go to Jellyfin first!

### Pull Request Process
1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Make your changes with clear commit messages
4. Test thoroughly on desktop and mobile browsers
5. Submit a pull request with a detailed description

## Support & Community

- **Issues** - [GitHub Issues](https://github.com/Moonfin-Client/Plugin/issues) for bugs and feature requests
- **Discussions** - [GitHub Discussions](https://github.com/Moonfin-Client/Plugin/discussions) for questions and ideas
- **Upstream Jellyfin** - [jellyfin.org](https://jellyfin.org) for server-related questions

## Credits

Moonfin is built upon the excellent work of:

- **[Jellyfin Project](https://jellyfin.org)** - The foundation and upstream codebase
- **[Druidblack](https://github.com/Druidblack)** - Original MDBList Ratings plugin
- **Moonfin Contributors** - Everyone who has contributed to this project

## License

This project is licensed under GPL-3.0. See the [LICENSE](LICENSE) file for details.

---

<p align="center">
   <strong>Moonfin </strong> is an independent project and is not affiliated with the Jellyfin project.<br>
   <a href="https://github.com/Moonfin-Client">← Back to main Moonfin project</a>
</p>
