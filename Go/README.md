# Moonbase in Go

The Go backends of Moonbase, the server side of the Moonfin apps. Both serve the same `/Moonfin` routes from `internal/api`:

- **Moonbase for Silo** (`cmd/silo`), a native Silo plugin.
- **Moonbase for Plex** (`cmd/plex`), a small service that runs next to Plex Media Server, since Plex has no plugin system.

Each host supplies its own storage, sign-in check and library access (`internal/silo`, `internal/plex`). Neither uses the Jellyfin or Emby plugin code, apart from a copy of the Jellyfin admin page.

## Moonbase for Silo

This is a native Silo plugin written in Go against the public [silo-plugin-sdk](https://github.com/Silo-Server/silo-plugin-sdk). It doesn't use Silo's Jellyfin-compatible API, and apart from a copy of the Jellyfin admin page it shares no code or build with the Jellyfin and Emby plugins in this repo.

It covers the parts of Moonbase that Silo's plugin API can support:

- Settings sync: each Silo profile keeps its own Moonfin settings across devices, with admin defaults.
- MDBList ratings and the TMDB episode, season, next episode, person link, thumbnail, review, production company and title details routes. Studio logos link to TMDB rather than being served by the plugin, and details are in English.
- Server messages from the admin.
- Custom themes the admin uploads, up to 5 (see Settings).
- Each user's own order for a collection.
- Watch Party extras Silo's rooms don't keep: the host's subtitle choice for the room and each member's avatar (`/Moonfin/WatchParty/{roomId}`). Moonfin runs Watch Together on Silo's own Watch Party rooms and keeps these here, each for 24 hours.
- The Moonbase admin settings, on the Moonbase page.

The rest needs things Silo's plugin API doesn't offer. See Known limits below.

### Supported versions

| | Version |
|---|---|
| Silo server | `9b77105` (#3021, 2026-10-28) or newer. Older builds don't verify the profile or the acting admin on plugin routes, see Known limits. |
| Plugin SDK | `v0.30.0`, the version that server pins |
| Platforms | `linux/amd64` (tested live), `linux/arm64` (builds, not tested on hardware) |

### Install

1. In Silo's admin pages, open Plugins, go to the **Catalog** tab and add a repository with this address:
   `https://github.com/Moonfin-Client/Plugin/releases/download/moonbase-silo-catalog/index.json`
2. Install **Moonbase** from the catalog. Silo updates it when a new version comes out.
3. Set it up on the **Moonbase** page, see Settings below.

To install by hand instead, download `Moonbase.Silo-<version>-linux-amd64` (or `-linux-arm64`) from the release, check it against `SHA256SUMS`, and upload it on the same tab. Silo runs the file once with `manifest` to read it, then starts it. The upload also works from a shell with an admin token:

```sh
curl -X POST "$SILO/api/v2/admin/plugins/uploads" \
  -H "Authorization: Bearer $ADMIN_TOKEN" \
  -F "archive=@Moonbase.Silo-<version>-linux-amd64;type=application/octet-stream"
```

Uploading a newer file replaces the installed one and keeps its installation id and its data.

### Settings

Moonbase is set up on the **Moonbase** page, which opens from the menu at the top right of Silo's plugin page (Integrations > Plugins > Moonbase) or from Plugin Apps in the admin menu. It has every setting, along with Messages, Apply Defaults To Users, Broadcast, the MDBList key test and the layout editors. It's for admins. Users change their own settings in the Moonfin app, which syncs them.

The Moonbase page is the Jellyfin admin page itself, with the same tabs, fields, search and Save button, in Jellyfin's dark style. `web/host.js` stands in for the Jellyfin dashboard it expects, and adapts the page to each host from its `hosts` table. On Silo it says "Silo" where the Jellyfin copy says "Jellyfin", and:

- hides the sections for things Silo does itself or will never use (hosting Moonfin web, Seerr, recommendations, Live TV, web plugin status, active downloads), and a tab left empty. Their settings still load and save unchanged.
- notes at the top of sections Silo's plugin API can't support yet why they do nothing (IMDb charts, anime markers, games, the official MDBList lists, PINs).
- calls Seerr "Requests" in the user defaults and the Home rows builder, since Moonfin uses Silo's own requests there.
- adds a Silo tab to the Home rows builder with Silo's own Home rows (Recommended for you, Trending on this server, Seasonal picks and the rest). A row is saved by its section type as `type:<section_type>`, since Moonbase can't read a server's section ids, and Moonfin shows the profile's first Silo row of that type.

Lists it would fill from the library stay empty, because Silo doesn't let plugins read library items. The user picker on Actions lists accounts that have synced settings: picking an account means its own profile, and hovering it (or tapping its arrow) offers its other profiles or all of them together.

Earlier releases also put most settings on Silo's own plugin form. Silo keeps what was saved there, and Moonbase reads it until the Moonbase page is first saved, which keeps those values with the page's settings from then on.

Defaults work as they do on Jellyfin. A setting a user has no value for comes from the admin's default. The apps store every setting they hold at their first sync, so changing a default later doesn't change it for users who already synced. **Apply Defaults To Users** on the Actions tab copies the defaults into each user's settings, or resets them to the defaults with overwrite on. The **Defaults for** picker on the User defaults tab sets separate defaults for desktop, mobile and TV, which apply over the ones for all devices.

Silo signs a plugin page in for five minutes and renews that while the page keeps calling its routes and the Silo sign-in is still good. The page pings Moonbase each minute to keep it signed in. If Save fails anyway, the page says so and keeps what you tried to save. Open Moonbase again in the same browser tab within an hour and those changes come back, ready to save.

Custom themes are uploaded on the Moonbase page's Themes section and checked the way the Jellyfin plugin checks them. Moonbase on Silo keeps up to 5, gzipped together under one storage key, and refuses an upload that wouldn't fit even compressed. Apps pick up a new or changed theme the next time they sign in.

Both API keys reach every signed-in user through `Settings/Resolved`, the same as on Jellyfin, since Moonfin calls TMDB directly with the key for some rows. Don't use keys here you wouldn't hand to your users.

### Where the routes are

Silo serves plugin routes under the plugin's installation id, not at the server root:

```
https://silo.example/api/v2/plugin-content/plugins/<installation id>/Moonfin/...
```

`/api/v1/plugins/<installation id>/Moonfin/...` is an older alias for the same thing. Clients find the installation id with `GET /api/v2/settings/plugins`, which lists Moonbase as `plugin_id` `moonfin.moonbase`. Requests use Silo's own headers: `Authorization: Bearer <token>`, plus `X-Profile-Id` (and `X-Profile-Token` for a PIN-locked profile). Silo checks the profile, and its PIN proof, before the request reaches Moonbase, and passes the verified profile on as `X-Silo-Profile-Id`. The routes only answer on Silo's native port (8090 by default). Moonbase doesn't use or need the Jellyfin-compatible port.

### Known limits

These come from Silo's plugin API, not from the plugin.

- **No live updates.** Silo can't stream a plugin's response or pass a plugin's events to apps, so a settings change on one device reaches the others at their next sign-in or manual sync. `Settings/Stream` answers 501.
- **Older Silo builds.** Before #3021, Silo didn't pass plugins the profile id, didn't check `X-Profile-Token` on plugin routes and marked every profile of an admin account as admin. On those builds settings are kept per profile name, and anyone signed in to an account can read and change its PIN-locked profiles' Moonfin settings. Settings stored that way aren't carried over: on a newer Silo each profile starts a new record keyed by its id, and Moonfin fills it from the device at the next sign-in.
- **No library items.** Silo lets plugins list the libraries an account may see, but not the items in them. So similar items, the seasonal row and the media bar aren't available, and the Moonbase page's library pickers stay empty.
- **No plugin icon.** Silo's plugin pages don't take icons from plugins, so Moonbase shows as "MO".
- **Admin routes need the primary profile.** Silo marks a request as admin only for an admin account acting through its primary profile, the rule its own admin API uses.
- **Storage is small.** Silo gives each plugin 256 keys of up to 256 KiB, and a key can be emptied but never deleted. So Moonbase packs every profile's settings and collection orders into 32 gzipped keys (`pk/0` to `pk/31`), and keeps its settings, index, messages, themes and Watch Party extras under keys of their own. That holds a few thousand profiles: about 2,500 with settings of 11 KB each, the fullest key setting the limit, and the profile index runs out at about 2,900. Settings saved before packing stay under their own key until the profile next saves, which moves them into its packed key and empties the old one.
- **Deleted users leave their settings behind.** Silo doesn't tell plugins when an account or profile is deleted. Remove an account's settings, or one profile's, with **Remove Selected User's Data** on the Moonbase page's Actions tab.

### Data, backups and removal

Moonbase's settings, synced profiles and messages live in Silo's per-plugin instance state. Silo encrypts it and stores it in its Postgres database, so it’s included in a database backup and needs the server's `SECRET_KEY` to restore. Upgrades keep it. Uninstalling the plugin deletes it.

### Logs

The plugin logs to Silo's log as plugin output. Storage and provider failures are logged with the route path. API keys never appear in logs or error messages.

## Moonbase for Plex

Moonbase for Plex runs as its own service next to Plex Media Server. It checks every caller's Plex token with your Plex server, so only people who can use the server can use it.

### Run it

Run it with Docker on the machine that runs Plex, or any machine that can reach it:

```sh
docker run -d --name moonbase-plex --restart unless-stopped \
  -p 32500:32500 \
  -e PLEX_URL=http://192.168.1.10:32400 \
  -v moonbase-plex:/config \
  ghcr.io/moonfin-client/moonbase-plex:latest
```

Or with Docker Compose:

```yaml
services:
  moonbase-plex:
    image: ghcr.io/moonfin-client/moonbase-plex:latest
    restart: unless-stopped
    ports:
      - "32500:32500"
    environment:
      PLEX_URL: http://192.168.1.10:32400
    volumes:
      - moonbase-plex:/config
volumes:
  moonbase-plex:
```

The image is built for amd64 and arm64, so it runs on most NAS boxes and on a Raspberry Pi 4 or newer. On Unraid, TrueNAS or Synology, add it as a container from that image with the same port, variable and volume.

| Variable | Default | |
|---|---|---|
| `PLEX_URL` | none, required | Your Plex server's address as seen from the container. |
| `MOONBASE_LISTEN` | `:32500` | Address and port to listen on. |
| `MOONBASE_DATA` | `/config` | Where Moonbase keeps its data. |

The image runs as a non-root user. A named volume works as is. A bind-mounted folder must be writable by uid 65532.

### Set it up

Open `http://<host>:32500/admin` and sign in with the Plex account that owns the server. It's the same Moonbase page as on Jellyfin, and only the owner can open it. Defaults work the same way as on Silo, see Settings above. Users change their own settings in the Moonfin app.

Signing in once also lets Moonbase recognize shared users and Home members. plex.tv doesn't always know their server tokens, so Moonbase looks them up in the owner's share list. Until the owner signs in, those users can't use Moonbase.

### How apps call it

Routes are at the root: `http://<host>:32500/Moonfin/...`. Apps send the token they already use with the Plex server, as an `X-Plex-Token` header, an `X-Plex-Token` query parameter for image links, or the `Token` field of a MediaBrowser `Authorization` header. Moonbase never asks for the user's plex.tv account token.

### Known limits

- Plex's remote access and relay only cover Plex itself. To use Moonbase away from home, open or proxy port 32500 the way you would any other service.
- The Moonfin web app on HTTPS can't call Moonbase over plain HTTP. Put Moonbase behind a reverse proxy with HTTPS for that.
- It covers what Moonbase for Silo does: settings sync, admin defaults, MDBList and TMDB, messages, uploaded themes and each user's collection order. The routes that read the library (similar items, custom rows, curated MDBList rows, the seasonal row) and the Seerr proxy aren't available.
- No live updates. `Settings/Stream` answers 501, so a change reaches other devices at their next sync.

### Data

`/config` holds `store/` (one file per synced profile, messages and settings), `client-id` (how plex.tv knows this install) and `owner-token`, the owner's Plex token from the admin sign-in. Treat `owner-token` like a password. Removing the volume removes everything. Signing out of the "Moonbase" device in your Plex account's authorized devices revokes the token.

## Building

Needs Go 1.26 or newer.

```sh
make test         # go vet and go test -race
make dist         # dist/Moonbase.Silo-<version>-linux-{amd64,arm64} and SHA256SUMS
make build        # dist/moonbase and dist/moonbase-plex for this machine
make silo-index BASE_URL=<download address>   # dist/index.json, the catalog Silo reads
make plex-image   # the moonbase-plex:<version> image (DOCKER=podman works too)
```

`cmd/silo/manifest.json` holds the version for local builds. `./dist/Moonbase.Silo-<version>-linux-amd64 manifest` prints the manifest the way Silo reads it at install.

### Releasing

Push a tag named `moonbase-go-v<version>`, for example `moonbase-go-v0.7.0`. The release workflow then:

- builds and tests both backends at that version
- publishes `Moonbase.Silo-<version>-linux-{amd64,arm64}`, `SHA256SUMS` and the catalog on a GitHub release of the same name
- replaces `index.json` on the `moonbase-silo-catalog` release, so Silo servers that added the catalog see the update
- pushes `ghcr.io/moonfin-client/moonbase-plex:<version>` and `:latest` for amd64 and arm64

None of these become the repository's latest release, which stays the Jellyfin plugin's. After the first release, make the `moonbase-plex` package public under the organization's Packages settings, or `docker pull` asks people to sign in.

To pick up a newer Jellyfin admin page, copy `Jellyfin/backend/Pages/configPage.html` over `web/configPage.html` unchanged. If it calls a Jellyfin dashboard function or `ApiClient` method that `web/host.js` doesn't have yet, add it there. `web/host.js` finds sections by their titles and changes the Home rows builder through the page's globals (`HOME_SECTION_DEFINITIONS`, `HOME_LAYOUT_TABS`, `FIELD_DEPENDENCIES` and others), so check those still match.
