<h1 align="center">Moonbase, the server plugin for Moonfin!</h1>
<h3 align="center">Moonbase is the server plugin that powers the Moonfin experience on Jellyfin, Emby, Silo and Plex.</h3>

---

<p align="center">
   <img width="1920" height="1080" alt="Moonbase" src="https://github.com/user-attachments/assets/5d67e8d0-5972-49f2-89d5-376357c8997b" />
</p>

[![License](https://img.shields.io/github/license/Moonfin-Client/Plugin.svg)](https://github.com/Moonfin-Client/Plugin) [![Release](https://img.shields.io/github/release/Moonfin-Client/Plugin.svg)](https://github.com/Moonfin-Client/Plugin/releases) [![Github Downloads](https://img.shields.io/github/downloads/Moonfin-Client/Plugin/total?label=Downloads)](https://github.com/Moonfin-Client/Plugin/releases) 


## What is Moonbase?

Moonbase is a server plugin for **Jellyfin, Emby, Silo and Plex** that gives every Moonfin client its shared backbone. It syncs your settings across devices, hosts the Moonfin web app right on your server, adds extra rating sources, connects your library to Seerr, and gives admins server-wide defaults and tools.

> **Recommended:** if you use any Moonfin client, install Moonbase on your server for the best experience.

This repo ships Moonbase for all four servers: a Jellyfin plugin under `Jellyfin/`, an Emby plugin under `Emby/`, and Moonbase for Silo and Plex, both built from `Go/`. Plex has no plugin system, so there Moonbase is a small service that runs next to Plex Media Server.

## Installation

Moonbase needs **Jellyfin 10.10 or newer**, **Emby 4.8 or newer**, a **Silo** build from 2026-10-28 or newer on Linux, or any **Plex Media Server** plus Docker. The wiki's [Installation](https://github.com/Moonfin-Client/Plugin/wiki/Installation) page has the steps plus updating and uninstalling.

### Jellyfin

The plugin repository is the recommended route, because it updates through the catalog like any other plugin:

1. Open the Jellyfin Dashboard, then go to Administration > Plugins > Repositories.
2. Add a repository:
   - **Name:** `Moonbase`
   - **URL:** `https://raw.githubusercontent.com/Moonfin-Client/Plugin/refs/heads/master/manifest.json`
3. Go to Catalog, find **Moonbase**, and install it.
4. Restart Jellyfin.

<details>
<summary><b>Advanced:</b> manual install</summary>

1. Download the latest `Moonfin.Server-x.x.x.x.zip` from [Releases](https://github.com/Moonfin-Client/Plugin/releases)
2. Extract it into your Jellyfin plugins folder:
   | Platform | Path |
   |----------|------|
   | Linux | `/var/lib/jellyfin/plugins/Moonfin/` |
   | Docker | `/config/plugins/Moonfin/` |
   | Windows | `%ProgramData%\Jellyfin\Server\plugins\Moonfin\` |
3. Restart Jellyfin


</details>

### Emby

The Emby plugin is a drop-in zip, not a catalog plugin:

1. Download the latest `Moonfin.Emby-x.x.x.x.zip` from [Releases](https://github.com/Moonfin-Client/Plugin/releases).
2. Extract its contents (`Emby.Plugins.Moonfin.dll` and the `web` folder) into your Emby plugins folder:
   | Platform | Path |
   |----------|------|
   | Linux | `/var/lib/emby/plugins/` |
   | Docker | `/config/plugins/` |
   | Windows | `%AppData%\Emby-Server\programdata\plugins\` |
3. Restart Emby.

### Silo

1. In Silo's admin pages, open Plugins, go to the **Catalog** tab and add this repository:
   `https://github.com/Moonfin-Client/Plugin/releases/download/moonbase-silo-catalog/index.json`
2. Install **Moonbase** from the catalog. Silo keeps it updated.
3. Set it up on the **Moonbase** page, from the menu at the top right of Silo's plugin page.

To install by hand, upload the `Moonbase.Silo` file for your server from the [Silo and Plex release](https://github.com/Moonfin-Client/Plugin/releases/tag/go-3.0.0) on the same tab.

### Plex

1. Run `ghcr.io/moonfin-client/moonbase-plex:latest` next to Plex Media Server, with port 32500 published and `PLEX_URL` set to your Plex server's address.
2. Open `http://<host>:32500/admin` and sign in with the account that owns the server.

The [Moonbase for Silo and Plex guide](Go/README.md) has the Docker command, settings and known limits for both.

## Opening the Moonfin Web App

Moonbase serves the Moonfin web app on your own server, so you can open and bookmark it directly:

`https://your-server-host/Moonfin/Web/`

> **Not loading after a fresh install?** Run the **Moonfin Startup** task once (Dashboard > Scheduled Tasks), then refresh your browser.

<details>
<summary><b>Advanced:</b> a Moonfin button in the stock Jellyfin web header</summary>

On Jellyfin, you can add a one-click Moonfin button to the stock web header (next to your user icon) by installing the [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) plugin:

1. Add its plugin repository: `https://www.iamparadox.dev/jellyfin/plugins/manifest.json`
2. Install **File Transformation** from the catalog and restart Jellyfin.
3. Force refresh your browser.

To hide the button while keeping the plugin, add this to Branding > Custom CSS:

```css
.headerMoonfinButton { display: none !important; }
```

On Emby, the web app is served the same way at `/Moonfin/Web/`, but there's no header button because Emby has no equivalent injection path.

On Silo, Moonbase serves it too: open it from Plugin Apps > **Moonfin**. On Plex, it's at `http://<host>:32500/Moonfin/Web/`, on the same port as Moonbase's admin page.

<img width="1521" height="164" alt="Moonfin header button" src="https://github.com/user-attachments/assets/bcb69e4b-edbe-4d1f-b9f1-dc81822d55d9" />

</details>

## Features

The list below is for Jellyfin and Emby. Moonbase for Plex covers most of it too, including Smart Search, Seerr, Watch Parties, the calendar, poster labels, list rows, anime markers, retro games and the seasonal row. Moonbase for Silo covers settings sync, admin defaults, MDBList ratings and TMDB reviews, server messages, uploaded themes, each user's own collection order and the web app, since Silo doesn't let plugins read your library. See the [Silo and Plex guide](Go/README.md).

- **Cross-device settings sync**, so your preferences follow you between web, TV, mobile, and desktop, with an optional per-device profile for desktop, mobile, and TV.
- **The Moonfin web app** hosted at `/Moonfin/Web/`, running side by side with the stock web interface.
- **Media bar and home screen data** resolved on the server and shared across clients.
- **Recommendations scored on the server**, matching similar titles on genres, tags, people, studios, franchise, release year and rating, and only showing what each user is allowed to see. On Jellyfin 12 the same scoring reaches every client, including the stock web interface.
- **Extra rating sources** through MDBList and TMDB, with the API keys kept on the server.
- **TMDB extras for the apps**: user reviews, a person's IMDb page and social accounts, and the overview, logo and trailer for titles outside your library, fetched with the server's TMDB key or the user's own.
- **Next air dates** for series, taken from the Sonarr your Seerr is connected to, or from TMDB through the server when there's no Sonarr.
- **Anime filler and recap markers** that badge episodes as Filler, Mixed, Recap or canon using AnimeFillerList and MyAnimeList, plus Subbed and Dubbed pills read from the audio tracks in your own files, or the shorter Sub and Dub. Shows match even when your library titles aren't in English. No account or API key needed.
- **A seasonal home row** of holiday movies your users own, plus Seerr suggestions, around New Year's, Valentine's Day, Easter, Pride, Halloween, Thanksgiving, Christmas, Lunar New Year and Diwali, following each user's parental limit.
- **Seerr integration** with a built-in proxy that keeps each user to their parental limit, single sign-on, optional request and issue notifications, and a download status that says when a Radarr or Sonarr download is stuck, with a way for admins to grab a different release and an optional notification when one gets stuck. The step-by-step guide is [Seerr Setup](https://github.com/Moonfin-Client/Plugin/wiki/Seerr-Setup).
- **Push notifications** that reach Moonfin apps even when they are closed, covering Seerr activity, stuck downloads, calendar reminders, new media added to your library, and admin broadcasts. See [Seerr Notifications](https://github.com/Moonfin-Client/Plugin/wiki/Seerr-Notifications).
- **Admin tools** for setting server-wide defaults across every client setting, for all devices or only TV, desktop or mobile, hiding libraries or keeping them out of Recently Added and adding custom list rows for new users, pushing defaults to existing users or copying one user's settings to others, and broadcasting a message to everyone at once.
- **Sign-in PINs kept on the server**, so a user's PIN follows them to every device. Wrong guesses are slowed down, and admins can set or reset anyone's PIN.
- **Live TV recording checks** that mark recordings which missed part of their airing as Incomplete, and can record the next full showing for you.
- **Settings backup and restore**: Moonbase keeps a spare copy of your server-wide settings and offers to put it back if they ever come back reset.
- **App reports on Emby**, so the apps' Send report to server button works there too. Jellyfin has this built in.
- **Server messages**: an admin can write a note (title, Markdown body, color, audience, start and end dates, an optional link button) that shows up in people's apps, even if they were offline when it was sent.
- **Custom themes** with a built-in editor, plus server-side upload and validation.
- **Retro games** support for browsing and playing game libraries, from folders or a RomM server, with saves synced per user. Each user can link their own RomM account so their saves sync with RomM too. An optional switch lets PSP games start in the web app. See [Retro Games](https://github.com/Moonfin-Client/Plugin/wiki/Retro-Games).
- **Custom rows** built from Letterboxd, TMDB, MDBList and IMDb lists, cached on the server and filtered by each user's parental limit, plus the Watching on MDBList row.
- **Watch Parties**, rooms where people watch the same thing together on their own devices, with chat, reactions, lobby voting, party alerts and an invite page. See [Watch Parties](https://github.com/Moonfin-Client/Plugin/wiki/Watch-Parties).
- **A Sonarr and Radarr calendar** for the apps, with reminders that tell people when a new episode or movie arrives. See [Calendar](https://github.com/Moonfin-Client/Plugin/wiki/Calendar).
- **Smart Search**, which finds titles from a description in plain words, with new and upcoming titles from Seerr for what the server doesn't have. See [Smart Search](https://github.com/Moonfin-Client/Plugin/wiki/Smart-Search).
- **Poster labels** like 4K, HDR or Atmos that admins design and the apps draw on posters.
- **Studio logos** from TMDB for the Studios home row, with dark logos turned white.
- **An active downloads dashboard** with live transcode metrics in the admin panel, plus a per-client view so a transcoded download can show its own progress and ETA.
- **Personal ratings served back to clients**, so a library can be sorted by your own rating and filtered by liked or disliked, which neither server can do on its own.
- **Audiobook bookmarks and notes** stored on the server and synced between clients.
- **After Hours scoreboards**, the top 3 for each of the apps' hidden games, shared by everyone on the server.

## Configuration

Open your server dashboard, go to Plugins, and select **Moonbase**. The [Admin Guide](https://github.com/Moonfin-Client/Plugin/wiki/Admin-Guide) walks through every tab. The main things to set up:

- Your Seerr URL and whether Seerr is enabled
- Shared MDBList and TMDB API keys, so individual users don't need their own. The TMDB key also feeds the seasonal row and the rating checks on custom rows
- Whether Moonbase Sync is on (required for the Seerr and ratings integrations)
- Poster labels and anime markers on the Labels tab, and whether Moonfin Recommends handles similar items
- Incomplete recording detection and re-recording on the Live TV tab
- Users' sign-in PINs on the Accounts tab
- Default user settings that new users inherit, with a button to push them to existing users
- Server messages and the quick broadcast
- Web startup options and custom theme uploads

Users change their own preferences from the in-app settings page in any Moonfin client. Settings are stored per user and shared across clients, with an optional per-device override for desktop, mobile, and TV.

<details>
<summary><b>Advanced:</b> the User defaults tab</summary>

The User defaults tab covers every client setting, in the same sections the apps use: Account, General, Appearance, Home Screen, Libraries, Details Page, Playback and Integrations. Each control mirrors what the clients actually offer, so a default set here is one a client can honor. A setting left on Not set (user decides) passes the user's own choice through.

</details>

## Documentation

The deeper reference material lives in the [Wiki](https://github.com/Moonfin-Client/Plugin/wiki):

| Page | What it covers |
|------|----------------|
| [Installation](https://github.com/Moonfin-Client/Plugin/wiki/Installation) | The Jellyfin catalog route, the Emby zip, the first things to switch on, updating and uninstalling |
| [Admin Guide](https://github.com/Moonfin-Client/Plugin/wiki/Admin-Guide) | The plugin page tab by tab, the scheduled tasks, and what differs on Emby |
| [Common Problems](https://github.com/Moonfin-Client/Plugin/wiki/Common-Problems) | Plain fixes for catalog, web app, header button, sync, reset settings, Seerr, ratings, rows, the seasonal row, anime markers and games trouble |
| [API Reference](https://github.com/Moonfin-Client/Plugin/wiki/API-Reference) | Every plugin endpoint, with methods, auth, the Seerr config response, and the server messages endpoints |
| [Settings Sync](https://github.com/Moonfin-Client/Plugin/wiki/Settings-Sync) | How sync works, the settings envelope, and the full list of synced settings |
| [Data Locations](https://github.com/Moonfin-Client/Plugin/wiki/Data-Locations) | Where the plugin stores user settings, themes, saves, caches, and the settings backup on the server |
| [Retro Games](https://github.com/Moonfin-Client/Plugin/wiki/Retro-Games) | Game libraries, cores, ROMs, BIOS, saves, and in-browser play |
| [Seerr Setup](https://github.com/Moonfin-Client/Plugin/wiki/Seerr-Setup) | The plain step-by-step guide to connecting Seerr |
| [Calendar](https://github.com/Moonfin-Client/Plugin/wiki/Calendar) | Connecting Sonarr and Radarr for the apps' calendar and reminders |
| [Smart Search](https://github.com/Moonfin-Client/Plugin/wiki/Smart-Search) | Turning on Smart Search, picking a model and what it costs your server |
| [Watch Parties](https://github.com/Moonfin-Client/Plugin/wiki/Watch-Parties) | Hosting watch party rooms, invite links and party alerts |
| [Seerr Notifications](https://github.com/Moonfin-Client/Plugin/wiki/Seerr-Notifications) | Push delivery, webhooks, notification types, and self-hosted relays |
| [Themes](https://github.com/Moonfin-Client/Plugin/wiki/Themes) | The theme editor and custom theme uploads |
| [Reverse Proxy and Seerr](https://github.com/Moonfin-Client/Plugin/wiki/Reverse-Proxy-and-Seerr) | Path forwarding and Seerr single sign-on behind a proxy |
| [Building from Source](https://github.com/Moonfin-Client/Plugin/wiki/Building-from-Source) | Building the Jellyfin and Emby plugins from this repo |

## Contributing

Contributions are welcome. Check the existing issues first, open an issue before starting a large change, match the existing code style, and test your changes on desktop and mobile. Features that would help all Jellyfin or Emby users are worth proposing upstream first.

To submit a change, fork the repo, create a feature branch, make your changes with clear commit messages, and open a pull request with a clear description. [CONTRIBUTING.md](CONTRIBUTING.md) has the full guidelines.

## Support and Community

- **Issues** for bugs and feature requests: [GitHub Issues](https://github.com/Moonfin-Client/Plugin/issues)
- **Discussions** for questions and ideas: [GitHub Discussions](https://github.com/Moonfin-Client/Plugin/discussions)

## Credits

Moonfin is built on the work of others:

- **[Jellyfin Project](https://jellyfin.org)** for the foundation and upstream codebase
- **[Druidblack](https://github.com/Druidblack)** for the original MDBList Ratings plugin
- **Moonfin Contributors** for everything they have added to the project

## License

This project is licensed under GPL-3.0. See the [LICENSE](LICENSE) file for details.

---

<p align="center">
   <strong>Moonfin</strong> is an independent project and is not affiliated with the Jellyfin or Emby projects.<br>
   <a href="https://github.com/Moonfin-Client">Back to the main Moonfin project</a>
</p>
