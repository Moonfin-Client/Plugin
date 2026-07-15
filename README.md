<h1 align="center">Moonbase, the server plugin for Moonfin!</h1>
<h3 align="center">Moonbase is the server plugin that powers the Moonfin experience on both Jellyfin and Emby.</h3>

---

<p align="center">
   <img width="1920" height="1080" alt="Moonbase" src="https://github.com/user-attachments/assets/5d67e8d0-5972-49f2-89d5-376357c8997b" />
</p>

[![License](https://img.shields.io/github/license/Moonfin-Client/Plugin.svg)](https://github.com/Moonfin-Client/Plugin) [![Release](https://img.shields.io/github/release/Moonfin-Client/Plugin.svg)](https://github.com/Moonfin-Client/Plugin/releases)

## What is Moonbase?

Moonbase is a server plugin for **Jellyfin and Emby** that provides the shared backbone for every Moonfin client. It syncs your settings across devices, hosts the Moonfin web app right on your server, serves media bar and home screen data, adds extra rating sources, connects your library to Seerr, and gives admins a set of server-wide defaults and tools. This repo ships two plugins from one place: a Jellyfin plugin under `Jellyfin/` and an Emby plugin under `Emby/`.

> **Recommended:** If you use any Moonfin client, install Moonbase on your server for the best experience.

## Opening the Moonfin Web App

Moonbase serves the Moonfin web app at `/Moonfin/Web/`, so you can open and bookmark it directly:

`https://your-server-host/Moonfin/Web/`

On **Jellyfin**, you can optionally add a one-click Moonfin button to the stock web header (next to SyncPlay) by installing the [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) plugin. On **Emby**, the web app is served the same way at `/Moonfin/Web/`, but there is no header button because Emby has no equivalent injection path.

<img width="1521" height="164" alt="Moonfin header button" src="https://github.com/user-attachments/assets/bcb69e4b-edbe-4d1f-b9f1-dc81822d55d9" />

> **Not loading after a fresh install?** Run the **Moonfin Startup** task once (Dashboard, Scheduled Tasks), then refresh your browser.

## Features

- **Cross-device settings sync** so your preferences follow you between web, TV, mobile, and desktop, with an optional per-device profile for desktop, mobile, and TV.
- **The Moonfin web app** hosted at `/Moonfin/Web/`, running side by side with the stock web interface.
- **Media bar and home screen data** resolved on the server and shared across clients.
- **Extra rating sources** through MDBList and TMDB, with the API keys kept on the server.
- **Seerr integration** with a built-in proxy, single sign-on, and optional request and issue notifications.
- **Admin tools** for setting server-wide defaults, pushing them to existing users, and broadcasting messages to everyone at once.
- **Custom themes** with a built-in editor, plus server-side upload and validation.
- **Retro games** support for browsing and playing game libraries in the browser. See [Retro Games](https://github.com/Moonfin-Client/Plugin/wiki/Retro-Games).
- **Custom rows** built from MDBList and IMDb lists, cached on the server.
- **Active downloads dashboard** with live transcode metrics in the admin panel.

## Installation

### Jellyfin

**Plugin repository (recommended)**

1. Jellyfin Dashboard, Administration, Plugins, Repositories
2. Add a repository:
   - **Name:** `Moonfin`
   - **URL:** `https://raw.githubusercontent.com/Moonfin-Client/Plugin/refs/heads/master/manifest.json`
3. Go to Catalog, find **Moonfin**, and install it
4. Restart Jellyfin

**Manual install**

1. Download the latest `Moonfin.Server-x.x.x.x.zip` from [Releases](https://github.com/Moonfin-Client/Plugin/releases)
2. Extract it into your Jellyfin plugins folder:
   | Platform | Path |
   |----------|------|
   | Linux | `/var/lib/jellyfin/plugins/Moonfin/` |
   | Docker | `/config/plugins/Moonfin/` |
   | Windows | `%ProgramData%\Jellyfin\Server\plugins\Moonfin\` |
3. Restart Jellyfin

Optional one-click header button: add the File Transformation plugin repository (`https://www.iamparadox.dev/jellyfin/plugins/manifest.json`), install **File Transformation** from the catalog, restart Jellyfin, then force refresh your browser. To hide the button while keeping the plugin, add this to Branding, Custom CSS:

```css
.headerMoonfinButton { display: none !important; }
```

### Emby

The Emby plugin is a drop-in zip, not a catalog plugin.

1. Download the latest `Emby.Plugins.Moonfin-x.x.x.x.zip` from [Releases](https://github.com/Moonfin-Client/Plugin/releases)
2. Extract its contents (`Emby.Plugins.Moonfin.dll`, `SharpCompress.dll`, and the `web` folder) into your Emby plugins folder:
   | Platform | Path |
   |----------|------|
   | Linux | `/var/lib/emby/plugins/` |
   | Docker | `/config/plugins/` |
   | Windows | `%AppData%\Emby-Server\programdata\plugins\` |
3. Restart Emby

## Configuration

Open your server dashboard, go to Plugins, and select **Moonbase** to configure things like:

- Your Seerr URL and whether Seerr is enabled
- Shared MDBList and TMDB API keys, so individual users do not need their own
- Default user settings that new users inherit, with a button to push them to existing users
- A broadcast message to announce something to everyone at once
- Web startup options and custom theme uploads

Users change their own preferences from the in-app settings page in any Moonfin client. Settings are stored per user and shared across clients, with an optional per-device override for desktop, mobile, and TV.

## Documentation

The deeper reference material lives in the [Wiki](https://github.com/Moonfin-Client/Plugin/wiki):

| Page | What it covers |
|------|----------------|
| [API Reference](https://github.com/Moonfin-Client/Plugin/wiki/API-Reference) | Every plugin endpoint, with methods, auth, and the Seerr config response |
| [Settings Sync](https://github.com/Moonfin-Client/Plugin/wiki/Settings-Sync) | How sync works, the settings envelope, and the full list of synced settings |
| [Retro Games](https://github.com/Moonfin-Client/Plugin/wiki/Retro-Games) | Game libraries, cores, ROMs, BIOS, saves, and in-browser play |
| [Themes](https://github.com/Moonfin-Client/Plugin/wiki/Themes) | The theme editor and custom theme uploads |
| [Reverse Proxy and Seerr](https://github.com/Moonfin-Client/Plugin/wiki/Reverse-Proxy-and-Seerr) | Path forwarding and Seerr single sign-on behind a proxy |
| [Building from Source](https://github.com/Moonfin-Client/Plugin/wiki/Building-from-Source) | Building the Jellyfin and Emby plugins from this repo |

## Contributing

Contributions are welcome. Check the existing issues first, open an issue before starting a large change, match the existing code style, and test your changes on desktop and mobile. Features that would help all Jellyfin or Emby users are worth proposing upstream first.

To submit a change, fork the repo, create a feature branch, make your changes with clear commit messages, and open a pull request with a clear description.

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
