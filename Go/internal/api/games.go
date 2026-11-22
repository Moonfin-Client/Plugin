package api

import (
	"bytes"
	"context"
	"crypto/sha1"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"slices"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

// GameLibrary is one of the caller's libraries that can hold games.
type GameLibrary struct {
	// ID is the library's id in the apps. ConfigID is the id the admin page
	// lists it under and saves.
	ID, ConfigID, Name string
	// Folders are the library's folders as the host names them.
	Folders []string
}

// GameHost is a host whose libraries can hold games for Moonbase to serve.
type GameHost interface {
	// GameLibraries lists the libraries the caller can open.
	GameLibraries(ctx context.Context, caller Identity) ([]GameLibrary, error)
	// GamesDir is where Moonbase keeps saves, artwork and uploaded cores.
	GamesDir() string
	// EmulatorJSDir holds player.html and moonfin-bridge.js.
	EmulatorJSDir() string
}

type gamesConfig struct {
	enabled    bool
	libraryIDs []string
	// paths map folders as the host names them to where Moonbase reads them,
	// for when the two see them at different paths.
	paths []pathMapping
	romm  rommSettings
}

type pathMapping struct{ host, local string }

func (s *Server) gamesConfig(ctx context.Context) gamesConfig {
	c, err := s.fullConfig(ctx)
	if err != nil {
		slog.ErrorContext(ctx, "load plugin configuration", "error", err)
		c = s.mergeConfig(nil)
	}
	cfg := gamesConfig{
		romm: rommSettings{
			url:       normalizeRommURL(str(c["GamesRommUrl"])),
			token:     normalizeRommToken(str(c["GamesRommToken"])),
			libraryID: strings.TrimSpace(str(c["GamesRommLibraryId"])),
		},
	}
	cfg.enabled, _ = c["GamesEnabled"].(bool)
	if list, ok := c["GamesPathMappings"].([]any); ok {
		for _, v := range list {
			m, _ := v.(M)
			host, local := strings.TrimSpace(str(m["PlexPath"])), strings.TrimSpace(str(m["MoonbasePath"]))
			if host != "" && local != "" {
				cfg.paths = append(cfg.paths, pathMapping{host, local})
			}
		}
	}
	if ids, ok := c["GameLibraryIds"].([]any); ok {
		for _, id := range ids {
			if s := strings.TrimSpace(str(id)); s != "" {
				cfg.libraryIDs = append(cfg.libraryIDs, s)
			}
		}
	}
	return cfg
}

// isolateWebApp says whether Moonfin Web's page asks to be cross-origin
// isolated. The player runs in an iframe there, and PSP's core only starts
// when the top page is isolated too.
func (s *Server) isolateWebApp(ctx context.Context) bool {
	if _, ok := s.catalog.(GameHost); !ok {
		return false
	}
	c, err := s.fullConfig(ctx)
	isolate, _ := c["GamesIsolateWebApp"].(bool)
	return err == nil && isolate
}

// localFolder is where Moonbase reads a host folder. The mapping with the
// longest matching host path wins, so a nested folder can map elsewhere.
func (c gamesConfig) localFolder(folder string) string {
	f := strings.ReplaceAll(folder, `\`, "/")
	local, rest, matched := "", "", -1
	for _, m := range c.paths {
		from := strings.TrimRight(strings.ReplaceAll(m.host, `\`, "/"), "/")
		if len(from) <= matched || len(f) < len(from) || !strings.EqualFold(f[:len(from)], from) || (len(f) > len(from) && f[len(from)] != '/') {
			continue
		}
		local, rest, matched = m.local, f[len(from):], len(from)
	}
	if matched < 0 {
		return folder
	}
	return filepath.Join(local, filepath.FromSlash(rest))
}

type games struct {
	s    *Server
	host GameHost
	dir  string
	libs ttlCache
	art  artFetcher
	romm rommState

	// files serializes writes to saves and preferences.
	files sync.Mutex
	// cores serializes core uploads, and coresErr is why the last one failed.
	cores    sync.Mutex
	coresErr atomic.Pointer[string]
}

func (s *Server) gameRoutes() {
	host, ok := s.catalog.(GameHost)
	if !ok {
		return
	}
	g := &games{s: s, host: host, dir: host.GamesDir(), libs: ttlCache{m: map[string]cached{}}, art: newArtFetcher()}
	m := s.mux
	m.HandleFunc("GET /Moonfin/Games/Libraries", s.user(g.handleLibraries))
	m.HandleFunc("GET /Moonfin/Games/Debug", s.admin(g.handleDebug))
	// Go's router can't tell Saves/{gameId} from {libraryId}/Systems, so one
	// route takes both.
	m.HandleFunc("GET /Moonfin/Games/{a}/{b}", s.user(g.handleListOrSave))
	m.HandleFunc("PUT /Moonfin/Games/Saves/{gameId}", s.user(g.handlePutSave))
	m.HandleFunc("GET /Moonfin/Games/{libraryId}/Games/{gameId}", s.user(g.handleGame))
	m.HandleFunc("PUT /Moonfin/Games/{libraryId}/Games/{gameId}/Backend", s.user(g.handleBackend))
	m.HandleFunc("GET /Moonfin/Games/{libraryId}/Rom/{gameId}", s.user(g.handleRom))
	m.HandleFunc("GET /Moonfin/Games/{libraryId}/Rom/{gameId}/{fileName}", s.user(g.handleRom))
	m.HandleFunc("GET /Moonfin/Games/{libraryId}/Bios/{biosId}", s.user(g.handleBios))
	m.HandleFunc("GET /Moonfin/Games/{libraryId}/Thumb/{gameId}", s.user(g.handleThumb))
	m.HandleFunc("GET /Moonfin/Games/Cores/Status", s.user(g.handleCoresStatus))
	m.HandleFunc("POST /Moonfin/Games/Cores/Upload", s.admin(g.handleCoresUpload))
	m.HandleFunc("POST /Moonfin/Games/Romm/Test", s.admin(g.handleRommTest))
	// The player page loads without a token. What it fetches carries one.
	m.HandleFunc("GET /Moonfin/EmulatorJS/player.html", g.handlePlayer)
	m.HandleFunc("GET /Moonfin/EmulatorJS/moonfin-bridge.js", g.handleBridge)
	m.HandleFunc("GET /Moonfin/EmulatorJS/data/{path...}", g.handleCoreData)
}

// gameLib is a library that serves games, from RomM or from its folders.
type gameLib struct {
	GameLibrary
	romm bool
	// roots are its folders as Moonbase reads them, the ones that exist.
	roots []string
}

var autoGameLibrary = regexp.MustCompile(`(?i)game|rom|emulat`)

func sameLibraryID(a, b string) bool {
	return strings.TrimSpace(a) != "" && strings.EqualFold(strings.TrimSpace(a), strings.TrimSpace(b))
}

// hostLibraries asks the host once a minute per caller, since every
// thumbnail on a screen needs them.
func (g *games) hostLibraries(ctx context.Context, caller Identity) ([]GameLibrary, error) {
	key := fmt.Sprint(caller.UserID, "/", caller.ProfileID)
	if v, ok := g.libs.get(key, time.Minute); ok {
		return v.([]GameLibrary), nil
	}
	libs, err := g.host.GameLibraries(ctx, caller)
	if err != nil {
		return nil, err
	}
	g.libs.set(key, libs)
	return libs, nil
}

// libraries picks the game libraries the way the Jellyfin plugin does: the
// ones the admin chose, or else those named like game libraries, with the
// RomM library taking over its entry.
func (g *games) libraries(ctx context.Context, caller Identity, cfg gamesConfig) ([]gameLib, error) {
	if !cfg.enabled {
		return nil, nil
	}
	all, err := g.hostLibraries(ctx, caller)
	if err != nil {
		return nil, err
	}
	var out []gameLib
	for _, l := range all {
		if cfg.romm.set() && sameLibraryID(cfg.romm.libraryID, l.ConfigID) {
			out = append(out, gameLib{GameLibrary: l, romm: true})
			continue
		}
		chosen := slices.ContainsFunc(cfg.libraryIDs, func(id string) bool {
			return sameLibraryID(id, l.ConfigID) || sameLibraryID(id, l.ID)
		})
		if len(cfg.libraryIDs) > 0 && !chosen || len(cfg.libraryIDs) == 0 && !autoGameLibrary.MatchString(l.Name) {
			continue
		}
		var roots []string
		for _, f := range l.Folders {
			local := cfg.localFolder(f)
			if info, err := os.Stat(local); err == nil && info.IsDir() {
				roots = append(roots, filepath.Clean(local))
			}
		}
		if len(roots) > 0 {
			out = append(out, gameLib{GameLibrary: l, roots: roots})
		}
	}
	return out, nil
}

// library finds the route's library. It answers the request itself and
// returns false when there's none.
func (g *games) library(w http.ResponseWriter, r *http.Request) (gamesConfig, gameLib, bool) {
	cfg := g.s.gamesConfig(r.Context())
	libs, err := g.libraries(r.Context(), callerOf(r), cfg)
	if err != nil {
		logError(r, "list game libraries", err)
		writeError(w, http.StatusBadGateway, g.s.name+" didn't return the library list.")
		return cfg, gameLib{}, false
	}
	id := r.PathValue("libraryId")
	if id == "" {
		id = r.PathValue("a")
	}
	for _, l := range libs {
		if sameLibraryID(l.ID, id) {
			return cfg, l, true
		}
	}
	http.NotFound(w, r)
	return cfg, gameLib{}, false
}

func (g *games) handleLibraries(w http.ResponseWriter, r *http.Request) {
	libs, err := g.libraries(r.Context(), callerOf(r), g.s.gamesConfig(r.Context()))
	if err != nil {
		logError(r, "list game libraries", err)
		writeError(w, http.StatusBadGateway, g.s.name+" didn't return the library list.")
		return
	}
	out := []M{}
	for _, l := range libs {
		out = append(out, M{"id": l.ID, "name": l.Name})
	}
	writeJSON(w, http.StatusOK, out)
}

func (g *games) handleListOrSave(w http.ResponseWriter, r *http.Request) {
	switch {
	case r.PathValue("a") == "Saves":
		g.handleGetSave(w, r)
	case r.PathValue("b") == "Systems":
		g.handleSystems(w, r)
	case r.PathValue("b") == "Games":
		g.handleGames(w, r)
	case r.PathValue("b") == "Recent":
		g.handleRecent(w, r)
	default:
		http.NotFound(w, r)
	}
}

func (g *games) handleSystems(w http.ResponseWriter, r *http.Request) {
	cfg, l, ok := g.library(w, r)
	if !ok {
		return
	}
	out := []M{}
	if l.romm {
		if snap := g.rommSnapshot(cfg.romm); snap != nil {
			out = snap.systems()
		}
		writeJSON(w, http.StatusOK, out)
		return
	}
	for _, root := range l.roots {
		dirs, _ := dirEntries(root)
		for _, dir := range dirs {
			name := filepath.Base(dir)
			if strings.HasPrefix(name, ".") {
				continue
			}
			roms := gamesInSystem(dir)
			if len(roms) == 0 {
				continue
			}
			core, _ := systemCore(name, roms)
			out = append(out, M{"id": name, "name": name, "core": core, "gameCount": len(roms)})
		}
	}
	sortByFold(out, "name")
	writeJSON(w, http.StatusOK, out)
}

func (g *games) handleGames(w http.ResponseWriter, r *http.Request) {
	cfg, l, ok := g.library(w, r)
	if !ok {
		return
	}
	system := r.URL.Query().Get("system")
	out := []M{}
	if l.romm {
		if snap := g.rommSnapshot(cfg.romm); snap != nil {
			out = snap.gameList(system)
		}
		writeJSON(w, http.StatusOK, out)
		return
	}
	l.eachFolderGame(system, func(game M, _ string) { out = append(out, game) })
	sortByFold(out, "title")
	writeJSON(w, http.StatusOK, out)
}

// eachFolderGame walks a folder library's games, in every system when system
// is empty, handing over each game with its ROM's path.
func (l gameLib) eachFolderGame(system string, fn func(game M, rom string)) {
	for _, root := range l.roots {
		dirs, _ := dirEntries(root)
		for _, dir := range dirs {
			name := filepath.Base(dir)
			if strings.HasPrefix(name, ".") || system != "" && !strings.EqualFold(name, system) {
				continue
			}
			roms := gamesInSystem(dir)
			core, _ := systemCore(name, roms)
			for _, rom := range roms {
				fn(M{"id": gameToken(rom), "title": gameTitle(dir, rom), "system": name, "core": core, "fileName": filepath.Base(rom)}, rom)
			}
		}
	}
}

// handleRecent is a library's newest games for the home screen, by when they
// were added or, with sort=released, by release year with undated games left out.
func (g *games) handleRecent(w http.ResponseWriter, r *http.Request) {
	cfg, l, ok := g.library(w, r)
	if !ok {
		return
	}
	limit := queryInt(r, "limit")
	if limit <= 0 {
		limit = 16
	}
	released := strings.EqualFold(r.URL.Query().Get("sort"), "released")
	out := []M{}
	if l.romm {
		if snap := g.rommSnapshot(cfg.romm); snap != nil {
			out = snap.recentList()
		}
	} else {
		l.eachFolderGame("", func(game M, rom string) {
			// Go has no portable creation time, so a copy that kept its file dates sorts as old.
			var added time.Time
			if info, err := os.Stat(rom); err == nil {
				added = info.ModTime().UTC()
			}
			game["dateAdded"] = added
			out = append(out, game)
		})
	}
	if released {
		out = slices.DeleteFunc(out, func(m M) bool { _, dated := m["year"].(int); return !dated })
	}
	slices.SortStableFunc(out, func(a, b M) int {
		if released {
			if c := b["year"].(int) - a["year"].(int); c != 0 {
				return c
			}
		}
		return b["dateAdded"].(time.Time).Compare(a["dateAdded"].(time.Time))
	})
	writeJSON(w, http.StatusOK, out[:min(limit, 100, len(out))])
}

func sortByFold(items []M, key string) {
	slices.SortStableFunc(items, func(a, b M) int {
		return strings.Compare(strings.ToLower(str(a[key])), strings.ToLower(str(b[key])))
	})
}

// libraryFile is a game's file found by its id, inside one of the library's
// folders.
type libraryFile struct {
	path, root, systemDir, systemName string
}

func (l gameLib) file(token string) (libraryFile, bool) {
	p, ok := tokenPath(token)
	if !ok {
		return libraryFile{}, false
	}
	i := slices.IndexFunc(l.roots, func(root string) bool { return withinRoot(root, p) })
	if i < 0 {
		return libraryFile{}, false
	}
	if info, err := os.Stat(p); err != nil || !info.Mode().IsRegular() {
		return libraryFile{}, false
	}
	f := libraryFile{path: p, root: l.roots[i], systemDir: systemDirOf(l.roots[i], p)}
	if f.systemDir != "" {
		f.systemName = filepath.Base(f.systemDir)
	}
	return f, true
}

func (f libraryFile) core() string {
	core, _ := systemCore(f.systemName, []string{f.path})
	return core
}

func (g *games) handleGame(w http.ResponseWriter, r *http.Request) {
	cfg, l, ok := g.library(w, r)
	if !ok {
		return
	}
	detail, key, ok := g.gameDetail(cfg, l, r.PathValue("gameId"))
	if !ok {
		http.NotFound(w, r)
		return
	}
	writeJSON(w, http.StatusOK, withBackend(detail, g.preference(callerOf(r), key)))
}

// gameDetail is a game's detail before the caller's own choices, and the key
// those are kept under. A folder game's key is its path in the library, since
// its id changes when the library moves.
func (g *games) gameDetail(cfg gamesConfig, l gameLib, gameID string) (M, string, bool) {
	if l.romm {
		snap := g.rommSnapshot(cfg.romm)
		game, platform, ok := snap.find(gameID)
		if !ok {
			return nil, "", false
		}
		return snap.detail(gameID, game, platform), l.ID + ":romm/" + strconv.Itoa(game.ID), true
	}
	f, ok := l.file(gameID)
	if !ok {
		return nil, "", false
	}
	rel, err := filepath.Rel(f.root, f.path)
	if err != nil {
		return nil, "", false
	}
	return folderDetail(gameID, f), l.ID + ":" + filepath.ToSlash(rel), true
}

func withBackend(detail M, backend string) M {
	detail["backendOverrideSupported"] = true
	if backend == emulatorJSBackend {
		detail["userBackendOverride"] = backend
	}
	return detail
}

func folderDetail(gameID string, f libraryFile) M {
	core := f.core()
	name, size := filepath.Base(f.path), fileSize(f.path)
	// The ROM route sends a loose disc or a PlayStation .7z as one zip and
	// unpacks other archives, so the detail names what the app will get.
	if isCueSheet(f.path) {
		if n := storedZipLength(discEntries(cueSetFiles(f.path))); n >= 0 {
			name, size = strings.TrimSuffix(name, filepath.Ext(name))+".zip", n
		}
	} else if repackForCore(core, f.path) {
		if entries, done, err := readArchive(f.path); err == nil {
			if n := storedZipLength(entries); n >= 0 {
				name, size = strings.TrimSuffix(name, filepath.Ext(name))+".zip", n
			}
			done()
		}
	} else if isGameArchive(f.path) && !keepArchiveIntact(core, f.path) {
		if inner, n, ok := unpackedRomInfo(f.path); ok {
			name, size = inner, n
		}
	}
	bios := []M{}
	if f.systemDir != "" {
		bios = biosFiles(f.systemDir)
	}
	return M{"id": gameID, "title": gameTitle(f.systemDir, f.path), "system": f.systemName, "core": core,
		"fileName": name, "sizeBytes": size, "bios": bios}
}

const emulatorJSBackend = "emulatorjs"

func (g *games) handleBackend(w http.ResponseWriter, r *http.Request) {
	cfg, l, ok := g.library(w, r)
	if !ok {
		return
	}
	var body struct{ Backend string }
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4096)).Decode(&body); err != nil && !errors.Is(err, io.EOF) {
		writeJSON(w, http.StatusBadRequest, M{"error": "The request body isn't valid JSON."})
		return
	}
	backend := strings.TrimSpace(body.Backend)
	if backend != "" && !strings.EqualFold(backend, emulatorJSBackend) {
		writeJSON(w, http.StatusBadRequest, M{"error": "The requested game backend is not available."})
		return
	}
	if backend != "" {
		backend = emulatorJSBackend
	}
	detail, key, ok := g.gameDetail(cfg, l, r.PathValue("gameId"))
	if !ok {
		http.NotFound(w, r)
		return
	}
	if err := g.setPreference(callerOf(r), key, backend); err != nil {
		storageFailed(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, withBackend(detail, backend))
}

func (g *games) handleRom(w http.ResponseWriter, r *http.Request) {
	cfg, l, ok := g.library(w, r)
	if !ok {
		return
	}
	if l.romm {
		g.serveRommRom(w, r, cfg.romm, r.PathValue("gameId"))
		return
	}
	// The file name in the path is only there for emulators that read it,
	// and is never used to find the file.
	f, ok := l.file(r.PathValue("gameId"))
	if !ok || !isRomFile(f.path) && !isBiosFile(f.path) && !isCueSheet(f.path) {
		http.NotFound(w, r)
		return
	}
	core := f.core()
	switch {
	case isCueSheet(f.path):
		serveStoredZip(w, r, discEntries(cueSetFiles(f.path)))
	case repackForCore(core, f.path):
		entries, done, err := readArchive(f.path)
		if err != nil {
			http.NotFound(w, r)
			return
		}
		defer done()
		serveStoredZip(w, r, entries)
	case isGameArchive(f.path) && !keepArchiveIntact(core, f.path):
		serveUnpacked(w, r, f.path)
	default:
		serveFile(w, r, f.path)
	}
}

func (g *games) handleBios(w http.ResponseWriter, r *http.Request) {
	cfg, l, ok := g.library(w, r)
	if !ok {
		return
	}
	if l.romm {
		g.serveRommFirmware(w, r, cfg.romm, r.PathValue("biosId"))
		return
	}
	f, ok := l.file(r.PathValue("biosId"))
	if !ok {
		http.NotFound(w, r)
		return
	}
	serveFile(w, r, f.path)
}

// serveFile answers ranges and HEAD, since EmulatorJS checks a cached ROM's
// size before using it.
func serveFile(w http.ResponseWriter, r *http.Request, p string) {
	file, err := os.Open(p)
	if err != nil {
		http.NotFound(w, r)
		return
	}
	defer file.Close()
	info, err := file.Stat()
	if err != nil {
		http.NotFound(w, r)
		return
	}
	w.Header().Set("Content-Type", "application/octet-stream")
	http.ServeContent(w, r, "", info.ModTime(), file)
}

func serveStoredZip(w http.ResponseWriter, r *http.Request, entries []archiveEntry) {
	n := storedZipLength(entries)
	if n < 0 {
		http.NotFound(w, r)
		return
	}
	w.Header().Set("Content-Type", "application/zip")
	w.Header().Set("Content-Length", strconv.FormatInt(n, 10))
	if r.Method == http.MethodHead {
		return
	}
	if err := writeStoredZip(w, entries); err != nil && r.Context().Err() == nil {
		logError(r, "send a disc set", err)
	}
}

func serveUnpacked(w http.ResponseWriter, r *http.Request, archive string) {
	if r.Method == http.MethodHead {
		_, n, ok := unpackedRomInfo(archive)
		if !ok {
			http.NotFound(w, r)
			return
		}
		w.Header().Set("Content-Type", "application/octet-stream")
		w.Header().Set("Content-Length", strconv.FormatInt(n, 10))
		w.Header().Set("Accept-Ranges", "bytes")
		return
	}
	rom, err := unpackRom(archive)
	if errors.Is(err, errRomTooLarge) {
		writeJSON(w, http.StatusRequestEntityTooLarge, M{"error": fmt.Sprintf("The archive entry exceeds the %d MB limit for extracted ROMs.", maxExtractedRom>>20)})
		return
	}
	if err != nil {
		http.NotFound(w, r)
		return
	}
	w.Header().Set("Content-Type", "application/octet-stream")
	http.ServeContent(w, r, "", time.Time{}, bytes.NewReader(rom))
}

func (g *games) handleDebug(w http.ResponseWriter, r *http.Request) {
	cfg := g.s.gamesConfig(r.Context())
	all, err := g.host.GameLibraries(r.Context(), callerOf(r))
	if err != nil {
		logError(r, "list libraries", err)
		writeError(w, http.StatusBadGateway, g.s.name+" didn't return the library list.")
		return
	}
	libraries := []M{}
	for _, l := range all {
		folders := []M{}
		for _, f := range l.Folders {
			local := cfg.localFolder(f)
			info, err := os.Stat(local)
			folders = append(folders, M{"folder": f, "moonbasePath": local, "found": err == nil && info.IsDir()})
		}
		libraries = append(libraries, M{"id": l.ID, "configId": l.ConfigID, "name": l.Name, "folders": folders})
	}
	chosen, _ := g.libraries(r.Context(), callerOf(r), cfg)
	ids := []string{}
	for _, l := range chosen {
		ids = append(ids, l.ID)
	}
	out := M{"gamesEnabled": cfg.enabled, "configuredIds": append([]string{}, cfg.libraryIDs...), "libraries": libraries, "gameLibraries": ids}
	if cfg.romm.set() {
		out["romm"] = g.rommDiagnostics(cfg.romm)
	}
	writeJSON(w, http.StatusOK, out)
}

const maxSaveBytes = 32 << 20

func (g *games) savePath(caller Identity, gameID, kind string) string {
	switch kind = strings.ToLower(strings.TrimSpace(kind)); kind {
	case "sram", "settings":
	default:
		kind = "state"
	}
	name := safeFileName(gameID)
	if name == "" {
		return ""
	}
	return filepath.Join(g.dir, "saves", strconv.Itoa(caller.UserID), name+"."+kind)
}

// safeFileName keeps an id to characters any file system takes. A long one
// ends in its hash, so two ids that share a start never share a file.
func safeFileName(id string) string {
	name := strings.Map(func(r rune) rune {
		if r < 0x20 || strings.ContainsRune(`./\:*?"<>|`, r) {
			return -1
		}
		return r
	}, id)
	if len(name) > 200 {
		sum := sha1.Sum([]byte(id))
		name = name[:150] + "-" + hex.EncodeToString(sum[:])
	}
	return name
}

func (g *games) handleGetSave(w http.ResponseWriter, r *http.Request) {
	p := g.savePath(callerOf(r), r.PathValue("b"), r.URL.Query().Get("kind"))
	if p == "" {
		http.NotFound(w, r)
		return
	}
	b, err := os.ReadFile(p)
	if err != nil {
		http.NotFound(w, r)
		return
	}
	w.Header().Set("Content-Type", "application/octet-stream")
	_, _ = w.Write(b)
}

func (g *games) handlePutSave(w http.ResponseWriter, r *http.Request) {
	p := g.savePath(callerOf(r), r.PathValue("gameId"), r.URL.Query().Get("kind"))
	if p == "" {
		writeError(w, http.StatusBadRequest, "Invalid game id.")
		return
	}
	b, err := io.ReadAll(http.MaxBytesReader(w, r.Body, maxSaveBytes))
	if err != nil {
		writeError(w, http.StatusBadRequest, "Save payload too large.")
		return
	}
	if len(b) == 0 {
		writeError(w, http.StatusBadRequest, "Empty save payload.")
		return
	}
	g.files.Lock()
	defer g.files.Unlock()
	if err := writeFileAtomic(p, b); err != nil {
		storageFailed(w, r, err)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

// writeFileAtomic replaces a file whole, so a crash never leaves half a save.
func writeFileAtomic(p string, b []byte) error {
	if err := os.MkdirAll(filepath.Dir(p), 0o700); err != nil {
		return err
	}
	tmp, err := os.CreateTemp(filepath.Dir(p), ".tmp-*")
	if err != nil {
		return err
	}
	_, err = tmp.Write(b)
	if cerr := tmp.Close(); err == nil {
		err = cerr
	}
	if err == nil {
		err = os.Rename(tmp.Name(), p)
	}
	if err != nil {
		_ = os.Remove(tmp.Name())
	}
	return err
}

// Each account's player choices are one small file, since the store's key
// count is shared with everything else.

func (g *games) preferencePath(caller Identity) string {
	return filepath.Join(g.dir, "preferences", strconv.Itoa(caller.UserID)+".json")
}

func (g *games) preferences(caller Identity) map[string]string {
	prefs := map[string]string{}
	if b, err := os.ReadFile(g.preferencePath(caller)); err == nil {
		_ = json.Unmarshal(b, &prefs)
	}
	return prefs
}

func (g *games) preference(caller Identity, key string) string {
	return g.preferences(caller)[key]
}

func (g *games) setPreference(caller Identity, key, value string) error {
	g.files.Lock()
	defer g.files.Unlock()
	prefs := g.preferences(caller)
	if value == "" {
		delete(prefs, key)
	} else {
		prefs[key] = value
	}
	b, err := json.Marshal(prefs)
	if err != nil {
		return err
	}
	return writeFileAtomic(g.preferencePath(caller), b)
}
