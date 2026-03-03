<h1 align="center">Moonfin for Jellyfin Web and Mobile</h1>
<h3 align="center">A Jellyfin server plugin that adds a custom UI layer to Jellyfin Web and the Mobile App and cross-client settings synchronization. Includes an optional Jellyseerr/Seerr integration with seamless authenticated proxy support.</h3>

---

<p align="center">
   <img width="4305" height="2659" alt="splash-background" src="https://github.com/user-attachments/assets/8618363e-d982-4828-8274-a2c3c7623ddb" />
</p>

[![License](https://img.shields.io/github/license/Moonfin-Client/Plugin.svg)](https://github.com/Moonfin-Client/Plugin) [![Release](https://img.shields.io/github/release/Moonfin-Client/Pluginsvg)](https://github.com/Moonfin-Client/Plugin/releases)

## What is this?

This is a server plugin that redesigns the Jellyfin Web and Mobile UI to match the UI of the Moonfin clients without modifying Jellyfin itself. It injects a custom frontend on top of the stock interface, adding a featured media bar, a redesigned details screen, a quick-access navigation bar, embedded Jellyseerr/Seerr media requests, MDBList and TMDB ratings, and more. Every feature is optional and toggled per-user from a built-in settings panel.

All user preferences sync across devices through the plugin's server-side API, so your settings follow you from desktop to phone to TV. Admins can set server-wide defaults and provide shared API keys for MDBList and TMDB so individual users don't need their own.

Moonfin requires the [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) plugin to inject its web UI into Jellyfin's pages.

## Features

### Web UI (`frontend/`)
- **Custom Details Screen** - Full-screen overlay with backdrop, logos, metadata, and a permission-aware context menu matching jellyfin-web's behavior
- **Navigation Bar** - Pill-shaped toolbar with Home, Search, Shuffle, Genres, Favorites, Library buttons, and user avatar
- **Featured Media Bar** - Hero slideshow with Ken Burns animation, content logos, and metadata overlay
- **Jellyseerr/Seerr Panel** - Embedded Jellyseerr or Seerr iframe with automatic session-based authentication via the server proxy
- **Settings Panel** - Per-user settings for all features, with device profiles and cross-client sync
- **Device Profiles** - Separate setting overrides for desktop, mobile, and TV; each profile inherits from a shared global base so you only configure what differs per device
- **TV Support** - Spatial navigation and remote-friendly focus management for webOS/Tizen

### Server Plugin (`backend/`)
- **Settings Sync API** - Per-user preference storage with device profiles, three-way merge, and admin-configurable defaults
- **Device Profile Architecture** - v2 settings envelope with a global base profile and sparse desktop/mobile/TV overrides; device profiles only store values that differ from global
- **Admin Default Settings** - Admins can set server-wide default values for every user setting; users who haven't customized a setting inherit the admin default
- **Jellyseerr/Seerr Proxy** - Authenticated reverse proxy that creates browser sessions automatically, so the iframe loads without a separate login (supports both Jellyseerr and Seerr v3)
- **Admin Configuration** - Dashboard page for Jellyseerr/Seerr URL, display name, enable/disable toggles, shared API keys, and default user settings
- **Web Injection** - Serves the frontend JS/CSS as embedded resources, automatically injected via the [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) plugin
----
# Screenshots

## Web UI
<img width="48%" alt="Home" src="https://github.com/user-attachments/assets/e71a5447-31c2-47e9-bfa8-3bd902ca7a50" />
<img width="48%" alt="Media Bar" src="https://github.com/user-attachments/assets/3dffe616-829c-4b2e-9275-d24506b6481d" />
<img width="48%" alt="Details" src="https://github.com/user-attachments/assets/bf9fd6df-d0b5-4eff-9557-5a9ec2acc0ad" />
<img width="48%" alt="Jellyseerr" src="https://github.com/user-attachments/assets/cf3f371b-0ad0-43c0-ba98-4ddce67950d3" />
<img width="48%" alt="Navbar" src="https://github.com/user-attachments/assets/bad74e17-e5f6-4654-b0bb-fed10d3b46ae" />
<img width="48%" alt="Settings" src="https://github.com/user-attachments/assets/e31f1f15-b754-415c-a1fd-46f729964b79" />
<img width="48%" alt="Genres" src="https://github.com/user-attachments/assets/8683d2e8-a096-4f5a-be74-9c0eea922e4e" />

## Mobile UI
<img width="23%" alt="Mobile Home" src="https://github.com/user-attachments/assets/ffdc52ea-b153-4518-9c3b-22870b463a83" />
<img width="23%" alt="Mobile Details" src="https://github.com/user-attachments/assets/e0da8bc2-13ea-4c3c-86fc-7dadfa7be529" />
<img width="23%" alt="Mobile Browse" src="https://github.com/user-attachments/assets/e33b196f-7ba5-469e-bc09-da7612b22f96" />
<img width="23%" alt="Mobile Player" src="https://github.com/user-attachments/assets/4ff4292f-c4b3-409f-8dfd-0d97d9eff45e" />
<img width="23%" alt="Mobile Settings" src="https://github.com/user-attachments/assets/3da56213-3c8b-4b9a-b736-4055acb10714" />
<img width="23%" alt="Mobile Jellyseerr" src="https://github.com/user-attachments/assets/3cc8f260-e1f9-4cb9-bc7a-8e2359f473cf" />
<img width="23%" alt="Mobile Navbar" src="https://github.com/user-attachments/assets/df6408d7-3883-4838-8228-f97d989f15d6" />

---

**Disclaimer:** Screenshots shown in this documentation feature media content, artwork, and actor likenesses for demonstration purposes only. None of the media, studios, actors, or other content depicted are affiliated with, sponsored by, or endorsing the Moonfin client or the Jellyfin project. All rights to the portrayed content belong to their respective copyright holders. These screenshots are used solely to demonstrate the functionality and interface of the application.

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

### Loading the Web UI

Moonfin uses the [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) plugin to automatically inject its web UI.

1. Add the File Transformation plugin repository to Jellyfin:
   - **URL:** `https://www.iamparadox.dev/jellyfin/plugins/manifest.json`
2. Install the **File Transformation** plugin from the catalog
3. Restart Jellyfin
4. Force refresh your browser (Ctrl+Shift+R)

> **UI not loading?** Go to *Dashboard → Scheduled Tasks* and run the **Moonfin Startup** task once, then refresh your browser.

## Configuration

### Admin Settings

Jellyfin Dashboard → Administration → Plugins → **Moonfin** to configure:
- Jellyseerr/Seerr URL, display name, and direct iframe URL
- Shared MDBList and TMDB API keys (so individual users don't need their own)
- **Default user settings** — set server-wide defaults for any user-facing setting; users who haven't customized a value inherit the admin default
- Enable/disable settings sync globally

# User Settings

Once the web UI is loaded, click your **user avatar** in the top right to open the Settings panel and click Moonfin. From there you can customize the navbar, media bar, details screen, seasonal effects, ratings, and more.

Settings support **device profiles**: a shared global profile plus optional overrides for desktop, mobile, and TV. Device profiles only store values that differ from global, so changes to global automatically flow to all devices unless explicitly overridden. A sync toggle lets you enable or disable server synchronization per-user.

### Reverse Proxy

If you run Jellyfin behind a reverse proxy (e.g., Nginx, Caddy, Traefik), make sure your proxy is configured to forward all `/Moonfin/` paths to Jellyfin. Jellyseerr loads inside Jellyfin through a special path (`/Moonfin/Jellyseerr/Web/`). If your reverse proxy isn't set up to pass those paths through, the page can't load and you'll just see a black screen. Some proxies also add security headers that block embedded content from showing up.

#### Seerr v3 (Next.js) Users

Seerr v3 is built on Next.js, which can have issues when proxied through subpaths due to hardcoded asset paths and hydration mismatches. If you're experiencing problems with Seerr v3 loading through the proxy (blank screen, 404 errors on chunks, navigation issues), you can configure a **Direct Iframe URL** in the admin settings:

1. Go to *Jellyfin Dashboard → Administration → Plugins → Moonfin*
2. Set the **Jellyseerr URL** to your Seerr instance (used for API proxying)
3. Set the **Direct Iframe URL** to your public Seerr URL (e.g., `https://seerr.yourdomain.com`)

When the Direct Iframe URL is set, the iframe loads directly from that URL instead of through the Moonfin proxy. SSO API calls still go through the Jellyfin server, but the web UI comes directly from Seerr.

## Building from Source

### Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Node.js](https://nodejs.org/) (LTS)

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
1. Bundle the frontend JS and CSS
2. Compile the .NET server plugin
3. Package `Moonfin.Server.dll` and `meta.json` into a ZIP
4. Update `manifest.json` with the new checksum

Output: `Moonfin.Server-{VERSION}.zip` in the repo root.

## Project Structure

```
├── build.sh            # Build script (Linux/macOS/Git Bash)
├── build.ps1           # Build script (Windows PowerShell)
├── backend/            # .NET 8 Jellyfin server plugin
│   ├── Api/            # REST controllers (settings, Jellyseerr proxy)
│   ├── Helpers/        # File Transformation patch callbacks
│   ├── Models/         # User settings, patch payload models
│   ├── Services/       # Startup task, settings persistence
│   ├── Pages/          # Admin config page HTML
│   └── Web/            # Embedded JS/CSS/HTML served to clients
└── frontend/           # Web UI plugin source
    ├── build.js        # JS/CSS bundler
    └── src/
        ├── plugin.js   # Entry point
        ├── components/ # Details, Navbar, MediaBar, Jellyseerr, Settings
        ├── styles/     # Component CSS
        └── utils/      # API helpers, storage, device detection, TV nav
```

## API Reference

| Endpoint | Method | Auth | Description |
|----------|--------|------|-------------|
| `/Moonfin/Ping` | GET | Yes | Check plugin status and configuration |
| `/Moonfin/Settings` | GET | Yes | Get current user's settings |
| `/Moonfin/Settings` | POST | Yes | Save settings (merge or replace) |
| `/Moonfin/Settings` | HEAD | Yes | Check if user has saved settings |
| `/Moonfin/Settings` | DELETE | Yes | Delete user's settings |
| `/Moonfin/Jellyseerr/Config` | GET | Yes | Get Jellyseerr/Seerr configuration (auto-detects variant) |
| `/Moonfin/Jellyseerr/Login` | POST | Yes | Authenticate with Jellyseerr/Seerr via Jellyfin credentials |
| `/Moonfin/Jellyseerr/Status` | GET | Yes | Check current user's SSO session status |
| `/Moonfin/Jellyseerr/Logout` | DELETE | Yes | Clear SSO session |
| `/Moonfin/Jellyseerr/Api/*` | * | Session | Authenticated API proxy to Jellyseerr/Seerr |
| `/Moonfin/Jellyseerr/Web/*` | GET | Yes | Proxied web UI with injected session |
| `/Moonfin/Assets/{fileName}` | GET | Yes | Serve embedded rating icons |
| `/Moonfin/MDBList/Batch` | POST | Yes | Batch fetch ratings for multiple items |
| `/Moonfin/MDBList/{imdbId}` | GET | Yes | Get MDBList ratings for a single item |
| `/Moonfin/TMDB/Episode/{seriesId}/{seasonNumber}/{episodeNumber}` | GET | Yes | Get TMDB episode rating |

### Jellyseerr/Seerr Config Response

```json
{
  "enabled": true,
  "url": "https://seerr.example.com",
  "directUrl": null,
  "displayName": "Seerr",
  "variant": "seerr",
  "userEnabled": true
}
```

| Field | Type | Description |
|-------|------|-------------|
| `enabled` | bool | Whether Jellyseerr/Seerr is enabled by admin |
| `url` | string | Server URL (used for API proxying) |
| `directUrl` | string? | Optional direct iframe URL for Seerr v3 subpath issues |
| `displayName` | string | UI display name (admin override or auto: "Jellyseerr"/"Seerr") |
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
  "global": { /* base settings — all devices inherit from here */ },
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
| `showClock` | bool | Show clock in navbar |
| `use24HourClock` | bool | Use 24-hour time format |
| `showShuffleButton` | bool | Show shuffle button in toolbar |
| `showGenresButton` | bool | Show genres button in toolbar |
| `showFavoritesButton` | bool | Show favorites button in toolbar |
| `showCastButton` | bool | Show cast/remote playback button |
| `showSyncPlayButton` | bool | Show SyncPlay button |
| `showLibrariesInToolbar` | bool | Show library buttons in toolbar |
| `shuffleContentType` | string | Shuffle content type (`movies`, `tv`, `both`) |
| `mediaBarEnabled` | bool | Enable featured media bar |
| `mediaBarContentType` | string | Media bar content type (`movies`, `tv`, `both`) |
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

### Web-Only Settings (Not Synced)

These settings are stored in localStorage only and do not sync across clients:

| Setting | Description |
|---------|-------------|
| `detailsPageEnabled` | Enable custom details screen |
| `mediaBarAutoAdvance` | Auto-advance media bar slides |
| `mediaBarIntervalMs` | Auto-advance interval in milliseconds |
| `backdropEnabled` | Enable backdrop images |

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

- When you open Jellyfin on a **new device/browser** with no local settings, it pulls from the server and your settings follow you
- If you change settings on **Client A**, they push to server. When **Client B** next loads (page refresh/login), it syncs but Client B's local settings win in the merge, so it won't overwrite unsaved local preferences
- Sync only runs **once on initial page load**, not continuously, so if two clients are open simultaneously, they won't live-sync between each other

### Limitations

- Three-way merge resolves most conflicts, but when both clients change the **same** setting, local wins. If you change different settings on two clients, the merge picks up both changes correctly
- No real-time push between clients (no WebSocket/polling)
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

Moonfin for Jellyfin Web is built upon the excellent work of:

- **[Jellyfin Project](https://jellyfin.org)** - The foundation and upstream codebase
- **[MakD](https://github.com/MakD)** - Original Jellyfin-Media-Bar concept that inspired our featured media bar
- **[Druidblack](https://github.com/Druidblack)** - Original MDBList Ratings plugin
- **Moonfin Contributors** - Everyone who has contributed to this project

## License

This project is licensed under GPL-3.0. See the [LICENSE](LICENSE) file for details.

---

<p align="center">
   <strong>Moonfin for Jellyfin Web</strong> is an independent project and is not affiliated with the Jellyfin project.<br>
   <a href="https://github.com/Moonfin-Client">← Back to main Moonfin project</a>
</p>
