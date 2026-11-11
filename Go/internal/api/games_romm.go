package api

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"math"
	"net"
	"net/http"
	"net/url"
	"os"
	"path"
	"path/filepath"
	"regexp"
	"slices"
	"strconv"
	"strings"
	"sync"
	"time"
)

// A RomM library serves the games of a RomM server through the admin's client
// token.

type rommSettings struct{ url, token, libraryID string }

func (s rommSettings) set() bool { return s.url != "" && s.token != "" && s.libraryID != "" }

// normalizeRommURL is an http or https address with no query, credentials or
// trailing slash, or "" when the setting isn't one.
func normalizeRommURL(raw string) string {
	u, err := url.Parse(strings.TrimSpace(raw))
	if err != nil || (u.Scheme != "http" && u.Scheme != "https") || u.Host == "" ||
		u.RawQuery != "" || u.ForceQuery || u.Fragment != "" || u.User != nil {
		return ""
	}
	return strings.TrimRight(u.Scheme+"://"+u.Host+u.EscapedPath(), "/")
}

func normalizeRommToken(raw string) string {
	t := strings.TrimSpace(raw)
	if strings.ContainsFunc(t, func(r rune) bool { return r == ' ' || r == '\t' || r == '\n' || r == '\r' }) {
		return ""
	}
	return t
}

// rommPlatformCores holds the RomM platforms Moonfin has a core for, with the
// name the apps show. A guessed core would list games that never boot, so
// any other platform stays hidden.
var rommPlatformCores = map[string][2]string{
	"nes": {"nes", "NES"}, "famicom": {"nes", "Famicom"}, "fds": {"nes", "Famicom Disk System"},
	"snes": {"snes", "SNES"}, "sfam": {"snes", "Super Famicom"}, "gb": {"gb", "Game Boy"}, "gbc": {"gb", "Game Boy Color"},
	"gba": {"gba", "GBA"}, "genesis": {"segaMD", "Genesis"}, "sms": {"segaMS", "Master System"}, "sg1000": {"segaMS", "SG-1000"},
	"gamegear": {"segaGG", "Game Gear"}, "n64": {"n64", "N64"}, "nds": {"nds", "Nintendo DS"}, "psx": {"psx", "PlayStation"},
	"psp": {"psp", "PSP"}, "arcade": {"arcade", "Arcade"}, "neogeoaes": {"arcade", "Neo Geo AES"}, "neogeomvs": {"arcade", "Neo Geo MVS"},
	"tg16": {"pce", "TurboGrafx-16"}, "atari2600": {"atari2600", "Atari 2600"}, "atari7800": {"atari7800", "Atari 7800"},
	"lynx": {"lynx", "Lynx"}, "wonderswan": {"ws", "WonderSwan"}, "wonderswan-color": {"ws", "WonderSwan Color"},
	"neo-geo-pocket": {"ngp", "Neo Geo Pocket"}, "neo-geo-pocket-color": {"ngp", "Neo Geo Pocket Color"},
	"virtualboy": {"vb", "Virtual Boy"},
}

type rommFile struct {
	ID       int    `json:"id"`
	FileName string `json:"file_name"`
	Size     int64  `json:"file_size_bytes"`
}

type rommPlatform struct {
	ID                   int
	SystemID, Name, Core string
	Firmware             []rommFile
	romCount             int
}

type rommGame struct {
	ID, PlatformID    int
	Title             string
	Cover, Screenshot string
	Rom               rommFile
	// ServedName and ServedSize are what the app gets, the ROM inside a zip
	// that Moonbase unpacks.
	ServedName string
	ServedSize int64
	Unpack     bool
	Meta       M
}

type rommSnapshot struct {
	platforms []rommPlatform
	games     []rommGame
	loaded    time.Time
}

func parseRommPlatforms(raw []json.RawMessage) []rommPlatform {
	var out []rommPlatform
	used := map[string]bool{}
	for _, item := range raw {
		var p struct {
			ID          int    `json:"id"`
			Slug        string `json:"slug"`
			FsSlug      string `json:"fs_slug"`
			DisplayName string `json:"display_name"`
			CustomName  string `json:"custom_name"`
			Name        string `json:"name"`
			Missing     bool   `json:"missing_from_fs"`
			RomCount    int    `json:"rom_count"`
			Firmware    []struct {
				rommFile
				Missing bool `json:"missing_from_fs"`
			} `json:"firmware"`
		}
		if json.Unmarshal(item, &p) != nil {
			continue
		}
		core, ok := rommPlatformCores[strings.ToLower(strings.TrimSpace(p.Slug))]
		fsSlug := strings.TrimSpace(p.FsSlug)
		if p.ID <= 0 || !ok || fsSlug == "" || p.Missing {
			continue
		}
		// Two RomM folders can map to one system, so the second adds its folder name.
		id := core[1]
		if used[strings.ToLower(id)] {
			id += " (" + fsSlug + ")"
		}
		used[strings.ToLower(id)] = true
		platform := rommPlatform{ID: p.ID, SystemID: id, Core: core[0], romCount: p.RomCount,
			Name: firstText(p.DisplayName, p.CustomName, p.Name, id)}
		for _, f := range p.Firmware {
			if f.ID > 0 && strings.TrimSpace(f.FileName) != "" && !f.Missing {
				platform.Firmware = append(platform.Firmware, rommFile{ID: f.ID, FileName: strings.TrimSpace(f.FileName), Size: f.Size})
			}
		}
		out = append(out, platform)
	}
	return out
}

func firstText(values ...string) string {
	for _, v := range values {
		if v = strings.TrimSpace(v); v != "" {
			return v
		}
	}
	return ""
}

func parseRommRom(raw json.RawMessage, platforms map[int]rommPlatform) (rommGame, bool) {
	var r struct {
		ID          int      `json:"id"`
		PlatformID  int      `json:"platform_id"`
		Name        string   `json:"name"`
		FsNameNoExt string   `json:"fs_name_no_ext"`
		FsName      string   `json:"fs_name"`
		Cover       string   `json:"path_cover_large"`
		Screenshots []string `json:"merged_screenshots"`
		Summary     string   `json:"summary"`
		Regions     []string `json:"regions"`
		Missing     bool     `json:"missing_from_fs"`
		Files       []struct {
			rommFile
			Category string `json:"category"`
			Members  []struct {
				Name string `json:"name"`
				Size int64  `json:"size"`
			} `json:"archive_members"`
		} `json:"files"`
		Meta struct {
			Genres        []string `json:"genres"`
			Developers    []string `json:"developers"`
			Publishers    []string `json:"publishers"`
			Franchises    []string `json:"franchises"`
			PlayerCount   any      `json:"player_count"`
			FirstRelease  float64  `json:"first_release_date"`
			AverageRating float64  `json:"average_rating"`
		} `json:"metadatum"`
	}
	if json.Unmarshal(raw, &r) != nil || r.ID <= 0 || r.Missing {
		return rommGame{}, false
	}
	platform, ok := platforms[r.PlatformID]
	if !ok {
		return rommGame{}, false
	}
	game := rommGame{ID: r.ID, PlatformID: r.PlatformID, Title: firstText(r.Name, r.FsNameNoExt, r.FsName, strconv.Itoa(r.ID)),
		Cover: strings.TrimSpace(r.Cover)}
	if shots := nonEmpty(r.Screenshots); len(shots) > 0 {
		game.Screenshot = shots[0]
	}

	// A RomM game folder often holds extras, so the pick is a file the core
	// plays, a plain ROM ahead of an archive, then the largest.
	chosen := -1
	chosenArchive := false
	for i, f := range r.Files {
		name := strings.TrimSpace(f.FileName)
		if name == "" || f.ID <= 0 || f.Category != "" && !strings.EqualFold(f.Category, "game") || !playableForCore(platform.Core, name) {
			continue
		}
		archive := isGameArchive(name)
		if chosen < 0 || chosenArchive && !archive || chosenArchive == archive && f.Size > r.Files[chosen].Size {
			chosen, chosenArchive = i, archive
		}
	}
	if chosen < 0 {
		return rommGame{}, false
	}
	picked := r.Files[chosen]
	game.Rom = rommFile{ID: picked.ID, FileName: strings.TrimSpace(picked.FileName), Size: picked.Size}
	game.ServedName, game.ServedSize = game.Rom.FileName, game.Rom.Size
	if chosenArchive && !keepArchiveIntact(platform.Core, game.Rom.FileName) {
		// Moonbase unpacks it, so the app is told the inner file's name. An
		// archive RomM hasn't listed is skipped rather than misnamed.
		member, size := "", int64(-1)
		for _, m := range picked.Members {
			name := path.Base(strings.TrimRight(strings.ReplaceAll(strings.TrimSpace(m.Name), `\`, "/"), "/"))
			if name == "" || name == "." || name == "/" {
				continue
			}
			if isKnownRomExt(name) {
				member, size = name, m.Size
				break
			}
			if m.Size > size {
				member, size = name, m.Size
			}
		}
		if member == "" {
			return rommGame{}, false
		}
		game.ServedName, game.ServedSize, game.Unpack = member, size, true
	}

	meta := M{}
	putText(meta, "overview", r.Summary)
	putText(meta, "region", strings.Join(nonEmpty(r.Regions), ", "))
	putText(meta, "genre", strings.Join(nonEmpty(r.Meta.Genres), ", "))
	putText(meta, "developer", strings.Join(nonEmpty(r.Meta.Developers), ", "))
	putText(meta, "publisher", strings.Join(nonEmpty(r.Meta.Publishers), ", "))
	if f := nonEmpty(r.Meta.Franchises); len(f) > 0 {
		meta["franchise"] = f[0]
	}
	if s, ok := r.Meta.PlayerCount.(string); ok {
		if n := mostPlayers(s); n > 0 {
			meta["players"] = n
		}
	}
	if d := int64(r.Meta.FirstRelease); d > 0 {
		// RomM stores milliseconds. A seconds value is far smaller, so both read right.
		if d < 100_000_000_000 {
			d *= 1000
		}
		meta["year"] = time.UnixMilli(d).UTC().Year()
	}
	if r.Meta.AverageRating > 0 {
		meta["rating"] = math.Round(min(r.Meta.AverageRating, 100)/20*10) / 10
	}
	game.Meta = meta
	return game, true
}

func nonEmpty(values []string) []string {
	var out []string
	for _, v := range values {
		if v = strings.TrimSpace(v); v != "" {
			out = append(out, v)
		}
	}
	return out
}

func putText(m M, key, value string) {
	if value = strings.TrimSpace(value); value != "" {
		m[key] = value
	}
}

// mostPlayers reads the highest number in a player count like "1-4".
func mostPlayers(s string) int {
	most, cur, in := 0, 0, false
	for _, r := range s + " " {
		if r >= '0' && r <= '9' {
			cur, in = cur*10+int(r-'0'), true
		} else if in {
			most, cur, in = max(most, cur), 0, false
		}
	}
	return most
}

const (
	rommGamePrefix     = "romm-"
	rommFirmwarePrefix = "romm-fw-"
)

func rommID(value, prefix string) (int, bool) {
	digits, ok := strings.CutPrefix(value, prefix)
	if !ok || digits == "" || strings.Trim(digits, "0123456789") != "" {
		return 0, false
	}
	id, err := strconv.Atoi(digits)
	return id, err == nil && id > 0
}

func (s *rommSnapshot) platform(id int) (rommPlatform, bool) {
	i := slices.IndexFunc(s.platforms, func(p rommPlatform) bool { return p.ID == id })
	if i < 0 {
		return rommPlatform{}, false
	}
	return s.platforms[i], true
}

func (s *rommSnapshot) find(gameID string) (rommGame, rommPlatform, bool) {
	id, ok := rommID(gameID, rommGamePrefix)
	if s == nil || !ok {
		return rommGame{}, rommPlatform{}, false
	}
	i := slices.IndexFunc(s.games, func(g rommGame) bool { return g.ID == id })
	if i < 0 {
		return rommGame{}, rommPlatform{}, false
	}
	p, ok := s.platform(s.games[i].PlatformID)
	return s.games[i], p, ok
}

func (s *rommSnapshot) firmware(firmwareID string) (rommFile, bool) {
	id, ok := rommID(firmwareID, rommFirmwarePrefix)
	if s == nil || !ok {
		return rommFile{}, false
	}
	for _, p := range s.platforms {
		for _, f := range p.Firmware {
			if f.ID == id {
				return f, true
			}
		}
	}
	return rommFile{}, false
}

func (s *rommSnapshot) systems() []M {
	counts := map[int]int{}
	for _, g := range s.games {
		counts[g.PlatformID]++
	}
	out := []M{}
	for _, p := range s.platforms {
		if counts[p.ID] > 0 {
			out = append(out, M{"id": p.SystemID, "name": p.Name, "core": p.Core, "gameCount": counts[p.ID]})
		}
	}
	sortByFold(out, "name")
	return out
}

func (s *rommSnapshot) gameList(system string) []M {
	out := []M{}
	for _, g := range s.games {
		p, ok := s.platform(g.PlatformID)
		if !ok || system != "" && !strings.EqualFold(p.SystemID, system) {
			continue
		}
		out = append(out, M{"id": rommGamePrefix + strconv.Itoa(g.ID), "title": g.Title, "system": p.SystemID, "core": p.Core, "fileName": g.ServedName})
	}
	sortByFold(out, "title")
	return out
}

func (s *rommSnapshot) detail(gameID string, g rommGame, p rommPlatform) M {
	bios := []M{}
	for _, f := range p.Firmware {
		bios = append(bios, M{"id": rommFirmwarePrefix + strconv.Itoa(f.ID), "fileName": f.FileName, "sizeBytes": f.Size})
	}
	detail := M{"id": gameID, "title": g.Title, "system": p.SystemID, "core": p.Core, "fileName": g.ServedName, "sizeBytes": g.ServedSize, "bios": bios}
	for k, v := range g.Meta {
		detail[k] = v
	}
	return detail
}

// rommState keeps the last good listing. Requests read it without waiting on
// RomM, a stale one is refreshed in the background, and a failed refresh
// keeps the old one, so RomM going offline never empties the library.
type rommState struct {
	mu       sync.Mutex
	key      string
	snap     *rommSnapshot
	loading  chan struct{}
	failedAt time.Time
	lastErr  string
}

const (
	rommMaxAge     = 5 * time.Minute
	rommRetryAfter = 30 * time.Second
	rommFirstWait  = 10 * time.Second
	rommLoadLimit  = 2 * time.Minute
)

// gamesHTTP has no overall timeout, since it streams whole ROMs. Each JSON
// call sets its own.
var gamesHTTP = &http.Client{Transport: &http.Transport{Proxy: http.ProxyFromEnvironment, ResponseHeaderTimeout: 30 * time.Second}}

// rommSnapshot is the current listing, waiting briefly for the first one.
func (g *games) rommSnapshot(set rommSettings) *rommSnapshot {
	st := &g.romm
	key := set.url + "\n" + set.token
	st.mu.Lock()
	if st.key != key {
		st.key, st.snap, st.loading, st.failedAt, st.lastErr = key, nil, nil, time.Time{}, ""
	}
	snap := st.snap
	var wait chan struct{}
	if snap == nil || time.Since(snap.loaded) > rommMaxAge {
		wait = g.startRommLoad(set, key)
	}
	st.mu.Unlock()
	if snap != nil || wait == nil {
		return snap
	}
	select {
	case <-wait:
	case <-time.After(rommFirstWait):
	}
	st.mu.Lock()
	defer st.mu.Unlock()
	if st.key != key {
		return nil
	}
	return st.snap
}

// startRommLoad runs with g.romm.mu held and returns the running load, or nil
// when one failed moments ago.
func (g *games) startRommLoad(set rommSettings, key string) chan struct{} {
	st := &g.romm
	if st.loading != nil {
		return st.loading
	}
	if time.Since(st.failedAt) < rommRetryAfter {
		return nil
	}
	done := make(chan struct{})
	st.loading = done
	go func() {
		ctx, cancel := context.WithTimeout(context.Background(), rommLoadLimit)
		snap, err := loadRomm(ctx, set)
		cancel()
		st.mu.Lock()
		if st.key == key {
			if err != nil {
				st.failedAt, st.lastErr = time.Now(), err.Error()
			} else {
				st.snap, st.lastErr = snap, ""
			}
		}
		if st.loading == done {
			st.loading = nil
		}
		st.mu.Unlock()
		close(done)
	}()
	return done
}

func (g *games) rommDiagnostics(set rommSettings) M {
	snap := g.rommSnapshot(set)
	g.romm.mu.Lock()
	defer g.romm.mu.Unlock()
	out := M{"url": set.url, "libraryId": set.libraryID, "loaded": snap != nil, "lastError": g.romm.lastErr}
	if snap != nil {
		out["loadedUtc"] = snap.loaded.UTC()
		out["platforms"] = len(snap.platforms)
		out["games"] = len(snap.games)
	}
	return out
}

const rommPageSize = 500

func loadRomm(ctx context.Context, set rommSettings) (*rommSnapshot, error) {
	var raw []json.RawMessage
	if err := rommGet(ctx, set, "/api/platforms", true, &raw); err != nil {
		return nil, err
	}
	snap := &rommSnapshot{platforms: parseRommPlatforms(raw), loaded: time.Now()}
	if len(snap.platforms) == 0 {
		return snap, nil
	}
	byID := map[int]rommPlatform{}
	q := url.Values{"with_files": {"true"}, "order_by": {"id"}, "order_dir": {"asc"}, "limit": {strconv.Itoa(rommPageSize)}}
	for _, p := range snap.platforms {
		byID[p.ID] = p
		q.Add("platform_ids", strconv.Itoa(p.ID))
	}
	// The page cap only guards a runaway listing.
	for page := 0; page < 2000; page++ {
		q.Set("offset", strconv.Itoa(page*rommPageSize))
		var body struct {
			Items []json.RawMessage `json:"items"`
		}
		if err := rommGet(ctx, set, "/api/roms?"+q.Encode(), true, &body); err != nil {
			return nil, err
		}
		for _, item := range body.Items {
			if game, ok := parseRommRom(item, byID); ok {
				snap.games = append(snap.games, game)
			}
		}
		if len(body.Items) < rommPageSize {
			break
		}
	}
	return snap, nil
}

type rommStatusError struct {
	status int
	path   string
}

func (e *rommStatusError) Error() string {
	p, _, _ := strings.Cut(e.path, "?")
	switch e.status {
	case http.StatusUnauthorized:
		return "RomM rejected the token (" + p + ")"
	case http.StatusForbidden:
		return "The RomM token is missing a scope this needs (" + p + ")"
	}
	return fmt.Sprintf("RomM answered %d (%s)", e.status, p)
}

var errNotRomm = errors.New("that address answered, but not like a RomM server")

// rommGet reads one JSON answer. RomM takes its token only as a bearer
// header, never in the URL.
func rommGet(ctx context.Context, set rommSettings, pathAndQuery string, authorize bool, out any) error {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, set.url+pathAndQuery, nil)
	if err != nil {
		return err
	}
	req.Header.Set("Accept", "application/json")
	if authorize {
		req.Header.Set("Authorization", "Bearer "+set.token)
	}
	resp, err := gamesHTTP.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return &rommStatusError{resp.StatusCode, pathAndQuery}
	}
	if json.NewDecoder(resp.Body).Decode(out) != nil {
		return errNotRomm
	}
	return nil
}

// handleRommTest checks the address and token in the admin page form, so they
// can be tried before saving.
func (g *games) handleRommTest(w http.ResponseWriter, r *http.Request) {
	var body struct{ URL, Token string }
	_ = json.NewDecoder(http.MaxBytesReader(w, r.Body, 4096)).Decode(&body)
	set := rommSettings{url: normalizeRommURL(body.URL), token: normalizeRommToken(body.Token)}
	fail := func(msg string) { writeJSON(w, http.StatusOK, M{"ok": false, "error": msg}) }
	switch {
	case set.url == "":
		fail("Enter the RomM address, like http://192.168.1.20:8080.")
		return
	case set.token == "":
		fail("Enter a RomM client token.")
		return
	}
	ctx, cancel := context.WithTimeout(r.Context(), 30*time.Second)
	defer cancel()
	var heartbeat struct {
		System struct {
			Version string `json:"VERSION"`
		} `json:"SYSTEM"`
	}
	var raw []json.RawMessage
	err := rommGet(ctx, set, "/api/heartbeat", false, &heartbeat)
	if err == nil {
		err = rommGet(ctx, set, "/api/platforms", true, &raw)
	}
	var status *rommStatusError
	var netErr net.Error
	switch {
	case errors.Is(err, errNotRomm) || errors.As(err, &status) && status.status == http.StatusNotFound:
		fail("That address answered, but not like a RomM server.")
		return
	case errors.As(err, &status):
		fail(status.Error() + ".")
		return
	case errors.As(err, &netErr) && netErr.Timeout():
		fail("RomM didn't answer in time.")
		return
	case err != nil:
		fail("Couldn't reach RomM at that address.")
		return
	}
	supported := parseRommPlatforms(raw)
	roms := 0
	for _, p := range supported {
		roms += p.romCount
	}
	writeJSON(w, http.StatusOK, M{"ok": true, "version": heartbeat.System.Version, "platformCount": len(raw),
		"supportedPlatformCount": len(supported), "supportedRomCount": roms})
}

func rommRomPath(game rommGame) string {
	return "/api/roms/" + strconv.Itoa(game.ID) + "/content/" + url.PathEscape(game.Rom.FileName) +
		"?file_ids=" + strconv.Itoa(game.Rom.ID) + "&purpose=play"
}

func (g *games) serveRommRom(w http.ResponseWriter, r *http.Request, set rommSettings, gameID string) {
	game, _, ok := g.rommSnapshot(set).find(gameID)
	if !ok {
		http.NotFound(w, r)
		return
	}
	if !game.Unpack {
		rommProxy(w, r, set, rommRomPath(game))
		return
	}
	// A zipped ROM is unpacked like one in a folder library. RomM listed what
	// it holds, so a HEAD needs no download.
	if r.Method == http.MethodHead {
		w.Header().Set("Content-Type", "application/octet-stream")
		w.Header().Set("Content-Length", strconv.FormatInt(game.ServedSize, 10))
		w.Header().Set("Accept-Ranges", "bytes")
		return
	}
	dir := filepath.Join(g.dir, "tmp")
	if err := os.MkdirAll(dir, 0o700); err != nil {
		storageFailed(w, r, err)
		return
	}
	// The archive reader goes by the extension, so the copy keeps RomM's.
	tmp, err := os.CreateTemp(dir, "romm-*"+fileExt(game.Rom.FileName))
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	defer os.Remove(tmp.Name())
	status, err := downloadRomm(r.Context(), set, rommRomPath(game), tmp)
	if cerr := tmp.Close(); err == nil {
		err = cerr
	}
	switch {
	case status == http.StatusRequestEntityTooLarge:
		writeJSON(w, status, M{"error": fmt.Sprintf("The archive exceeds the %d MB limit for extracted ROMs.", maxExtractedRom>>20)})
	case err != nil:
		w.WriteHeader(http.StatusBadGateway)
	case status != http.StatusOK:
		rommFailure(w, status)
	default:
		serveUnpacked(w, r, tmp.Name())
	}
}

func downloadRomm(ctx context.Context, set rommSettings, pathAndQuery string, dst io.Writer) (int, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, set.url+pathAndQuery, nil)
	if err != nil {
		return 0, err
	}
	req.Header.Set("Authorization", "Bearer "+set.token)
	resp, err := gamesHTTP.Do(req)
	if err != nil {
		return 0, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return resp.StatusCode, nil
	}
	n, err := io.Copy(dst, io.LimitReader(resp.Body, maxExtractedRom+1))
	if n > maxExtractedRom {
		return http.StatusRequestEntityTooLarge, nil
	}
	return http.StatusOK, err
}

func (g *games) serveRommFirmware(w http.ResponseWriter, r *http.Request, set rommSettings, biosID string) {
	f, ok := g.rommSnapshot(set).firmware(biosID)
	if !ok {
		http.NotFound(w, r)
		return
	}
	rommProxy(w, r, set, "/api/firmware/"+strconv.Itoa(f.ID)+"/content/"+url.PathEscape(f.FileName))
}

var byteRange = regexp.MustCompile(`^bytes=[0-9]*-[0-9]*(,\s*[0-9]*-[0-9]*)*$`)

// rommProxy streams a RomM file, passing a byte range through so an app can
// resume or check the size the same way it does with a file on disk.
func rommProxy(w http.ResponseWriter, r *http.Request, set rommSettings, pathAndQuery string) {
	method := http.MethodGet
	if r.Method == http.MethodHead {
		method = http.MethodHead
	}
	req, err := http.NewRequestWithContext(r.Context(), method, set.url+pathAndQuery, nil)
	if err != nil {
		http.NotFound(w, r)
		return
	}
	req.Header.Set("Authorization", "Bearer "+set.token)
	if rng := strings.TrimSpace(r.Header.Get("Range")); byteRange.MatchString(rng) {
		req.Header.Set("Range", rng)
	}
	resp, err := gamesHTTP.Do(req)
	var netErr net.Error
	switch {
	case errors.As(err, &netErr) && netErr.Timeout():
		w.WriteHeader(http.StatusGatewayTimeout)
		return
	case err != nil:
		w.WriteHeader(http.StatusBadGateway)
		return
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK && resp.StatusCode != http.StatusPartialContent {
		rommFailure(w, resp.StatusCode)
		return
	}
	h := w.Header()
	h.Set("Content-Type", "application/octet-stream")
	h.Set("Accept-Ranges", "bytes")
	if resp.ContentLength >= 0 {
		h.Set("Content-Length", strconv.FormatInt(resp.ContentLength, 10))
	}
	if cr := resp.Header.Get("Content-Range"); cr != "" {
		h.Set("Content-Range", cr)
	}
	w.WriteHeader(resp.StatusCode)
	if method == http.MethodGet {
		_, _ = io.Copy(w, resp.Body)
	}
}

func rommFailure(w http.ResponseWriter, status int) {
	switch status {
	case http.StatusNotFound, http.StatusRequestedRangeNotSatisfiable:
		w.WriteHeader(status)
	default:
		w.WriteHeader(http.StatusBadGateway)
	}
}

// rommArt is where RomM keeps a game's art. Only RomM's asset folder is
// taken, so a path from RomM can't point Moonbase anywhere else.
func rommArt(set rommSettings, game rommGame, role string) []string {
	p := map[string]string{"boxart": game.Cover, "snap": game.Screenshot}[role]
	if !strings.HasPrefix(p, "/assets/") || strings.Contains(p, "..") {
		return nil
	}
	return []string{set.url + strings.ReplaceAll(p, " ", "%20")}
}
