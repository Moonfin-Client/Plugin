# Moonbase for Silo and Plex

Moonbase is the server side of the Moonfin apps. On Jellyfin and Emby it's a plugin. On Silo it's a Silo plugin, and on Plex, which has no plugin system, it's a small service that runs next to Plex Media Server.

## Moonbase for Silo

### What it does

- Keeps each Silo profile's Moonfin settings in sync across devices, with admin defaults for all devices or for desktop, mobile and TV.
- Adds MDBList ratings, and TMDB reviews, person links and details for titles. Details are in English.
- Shows server messages from the admin.
- Offers up to 5 custom themes the admin uploads.
- Keeps each user's own order for a collection.
- Keeps a Watch Party's subtitle choice and each member's avatar, which Silo's rooms don't.

### Requirements

- A Silo server from 2026-10-28 (#3021) or newer. Older builds work, with the limits listed under Older Silo builds below.
- Linux on amd64 or arm64.

### Install

1. In Silo's admin pages, open Plugins, go to the **Catalog** tab and add a repository with this address:
   `https://github.com/Moonfin-Client/Plugin/releases/download/moonbase-silo-catalog/index.json`
2. Install **Moonbase** from the catalog. Silo updates it when a new version comes out.
3. Set it up on the **Moonbase** page, see below.

To install it by hand instead, download `Moonbase.Silo-<version>-linux-amd64` (or `-linux-arm64`) from the release, check it against `SHA256SUMS`, and upload it on the same tab. Uploading a newer file replaces the old one and keeps your data.

Moonfin finds Moonbase on its own once it's installed. Connect Moonfin to Silo's native address, usually port 8090, not the Jellyfin-compatible one.

### Settings

Open the **Moonbase** page from the menu at the top right of Silo's plugin page (Integrations > Plugins > Moonbase), or from Plugin Apps in the admin menu. Use your admin account's primary profile, since Silo only treats that one as an admin here. Users change their own settings in the Moonfin app, which syncs them.

It's the same page as Moonbase on Jellyfin, with Silo's name in place of Jellyfin's:

- Sections for things Silo does itself, like Seerr, Live TV and hosting the web app, are hidden. Their settings are kept.
- Sections Silo can't support yet say so at the top, and do nothing on Silo for now.
- Seerr is called Requests, because Moonfin uses Silo's own requests.
- The Home rows builder has a Silo tab with Silo's own rows, like Recommended for you, Trending on this server and Seasonal picks.

**Defaults.** A setting a user hasn't set comes from your default. The apps save every setting at their first sync, so changing a default later doesn't change it for users who already synced. **Apply Defaults To Users** on the Actions tab copies your defaults into everyone's settings, or resets them to your defaults with overwrite on. The **Defaults For** picker on the User defaults tab sets separate defaults for desktop, mobile and TV, which apply over the ones for all devices.

**Staying signed in.** The page stays signed in for as long as your Silo login lasts. If Save fails anyway, the page says so and keeps your changes. Open Moonbase again in the same browser tab within an hour and they come back, ready to save.

**Themes.** Upload them in the Themes section. Apps pick up a new or changed theme the next time they sign in.

**API keys.** The MDBList and TMDB keys reach every signed-in user, the same as on Jellyfin, since Moonfin calls TMDB directly for some rows. Don't use keys you wouldn't hand to your users.

**Removing a user.** Silo doesn't tell Moonbase when an account or profile is deleted. Pick it on the Actions tab and use **Remove Selected User's Data** to remove what Moonbase kept for it.

### Known limits

These come from what Silo lets plugins do.

- **No live updates.** A settings change on one device reaches the others at their next sign-in or sync.
- **No library items.** Silo doesn't let plugins read the items in a library, so similar items, the seasonal row, the media bar, IMDb charts, anime markers, games and the official MDBList lists aren't available, and the Moonbase page's library pickers stay empty.
- **No plugin icon.** Moonbase shows as "MO" in Silo's plugin pages.
- **A few thousand profiles.** Silo gives each plugin little storage, which holds settings for a few thousand profiles. Each profile's collection orders have a cap of about 2,800 items in total.

### Older Silo builds

Before #3021, Silo didn't tell plugins which profile was calling or check a profile's PIN on plugin routes, and treated every profile of an admin account as an admin. On those builds settings are kept per profile name, and anyone signed in to an account can read and change its PIN-locked profiles' Moonfin settings. After upgrading Silo, each profile starts fresh and Moonfin fills it from the device at the next sign-in.

### Data and backups

Moonbase keeps its data in Silo's database, encrypted. A database backup includes it, and restoring it needs the server's `SECRET_KEY`. Updates keep it, and uninstalling the plugin deletes it.

Moonbase logs to Silo's log. API keys never appear in logs or error messages.

## Moonbase for Plex

Moonbase for Plex runs as its own service next to Plex Media Server. It checks every caller's Plex token with your Plex server, so only people who can use your server can use it.

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

The image runs on amd64 and arm64, so it works on most NAS boxes and on a Raspberry Pi 4 or newer. On Unraid, TrueNAS or Synology, add it as a container from that image with the same port, variable and volume.

| Variable | Default | |
|---|---|---|
| `PLEX_URL` | none, required | Your Plex server's address as seen from the container. |
| `MOONBASE_LISTEN` | `:32500` | Address and port to listen on. |
| `MOONBASE_DATA` | `/config` | Where Moonbase keeps its data. |

A named volume works as is. A folder you mount from the host must be writable by user 65532.

### Set it up

Open `http://<host>:32500/admin` and sign in with the Plex account that owns the server. It's the same Moonbase page as on Jellyfin, and only the owner can open it. Defaults work the same way as on Silo. Users change their own settings in the Moonfin app.

Signing in once also lets Moonbase recognize your shared users and Home members. Until the owner signs in, they can't use Moonbase.

### Connect the apps

Moonfin looks for Moonbase on port 32500 of your Plex server's address. If it runs somewhere else, set its address in Moonfin under Settings > Account > Settings Sync > Moonbase Address.

### Known limits

- Plex's remote access only covers Plex itself. To use Moonbase away from home, open or proxy port 32500 the way you would for any other service.
- The Moonfin web app on HTTPS can't reach Moonbase over plain HTTP. Put Moonbase behind a reverse proxy with HTTPS for that.
- It does what Moonbase for Silo does: settings sync, admin defaults, MDBList and TMDB, messages, uploaded themes and each user's collection order. Similar items, custom rows, curated MDBList rows, the seasonal row and the Seerr proxy aren't available.
- A settings change reaches other devices at their next sync.

### Data

`/config` holds Moonbase's data and `owner-token`, the server owner's Plex token from the admin sign-in. Treat `owner-token` like a password. Removing the volume removes everything. Signing out the "Moonbase" device in your Plex account's authorized devices revokes the token.
