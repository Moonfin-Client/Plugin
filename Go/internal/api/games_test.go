package api

import (
	"archive/zip"
	"bytes"
	"context"
	"encoding/json"
	"io"
	"maps"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"slices"
	"strconv"
	"strings"
	"sync/atomic"
	"testing"
	"testing/fstest"

	"github.com/Moonfin-Client/Plugin/Go/internal/store"
)

type gameHost struct {
	fakeCatalog
	libs     []GameLibrary
	dir, ejs string
}

func (h *gameHost) GameLibraries(context.Context, Identity) ([]GameLibrary, error) {
	return h.libs, nil
}
func (h *gameHost) GamesDir() string      { return h.dir }
func (h *gameHost) EmulatorJSDir() string { return h.ejs }

func newGameServer(t *testing.T, libs ...GameLibrary) (*Server, *gameHost) {
	t.Helper()
	gh := &gameHost{libs: libs, dir: t.TempDir(), ejs: t.TempDir()}
	host := testHost(store.NewMemKV())
	host.Catalog = gh
	return NewServer("1.2.3", host), gh
}

func saveGamesConfig(t *testing.T, s *Server, cfg M) {
	t.Helper()
	b, _ := json.Marshal(cfg)
	if code, _ := call(t, s, adminMain, "POST", "/Moonfin/Admin/Configuration", string(b), nil); code != http.StatusNoContent {
		t.Fatalf("save config: %d", code)
	}
}

func writeFile(t *testing.T, p string, b []byte) {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(p, b, 0o644); err != nil {
		t.Fatal(err)
	}
}

func zipOf(t *testing.T, files ...[2]string) []byte {
	t.Helper()
	var buf bytes.Buffer
	zw := zip.NewWriter(&buf)
	for _, f := range files {
		w, err := zw.Create(f[0])
		if err != nil {
			t.Fatal(err)
		}
		io.WriteString(w, f[1])
	}
	if err := zw.Close(); err != nil {
		t.Fatal(err)
	}
	return buf.Bytes()
}

func getJSON[T any](t *testing.T, s *Server, c caller, path string, query map[string]any) (int, T) {
	t.Helper()
	resp := serve(context.Background(), s, c, "GET", path, "", query)
	var out T
	raw := readAll(t, resp)
	if resp.StatusCode == http.StatusOK {
		if err := json.Unmarshal(raw, &out); err != nil {
			t.Fatalf("GET %s: bad JSON %q", path, raw)
		}
	}
	return resp.StatusCode, out
}

func TestSystemCoreFollowsNameThenAliasThenExtension(t *testing.T) {
	cases := []struct {
		system, rom, core string
		defaulted         bool
	}{
		{"MAME", "", "mame", false},
		{"ARCADE", "", "arcade", false},
		{"Sega Master System", "game.sms", "segaMS", false},
		{"Unknown System", "game.gba", "gba", false},
		{"GbaPsx", "game.zip", "nes", true},
		{"Super Nintendo", "", "snes", false},
		{"Game Boy Color", "", "gb", false},
	}
	for _, c := range cases {
		var roms []string
		if c.rom != "" {
			roms = []string{c.rom}
		}
		if core, defaulted := systemCore(c.system, roms); core != c.core || defaulted != c.defaulted {
			t.Errorf("systemCore(%q, %q) = %s, %v, want %s, %v", c.system, c.rom, core, defaulted, c.core, c.defaulted)
		}
	}
	if artworkCore("gb", "Game Boy Color") != "gbc" || artworkCore("gb", "GBC") != "gbc" || artworkCore("gb", "Game Boy") != "gb" {
		t.Fatal("Game Boy Color art should come from its own set")
	}
}

func TestArchivesAreKeptOrUnpackedByCore(t *testing.T) {
	keep := []struct {
		core, file string
		want       bool
	}{
		{"mame", "atetris.zip", true}, {"mame", "ATETRIS.ZIP", true}, {"mame", "atetris.7z", false},
		{"arcade", "atetris.zip", true}, {"nes", "game.zip", false},
		{"psx", "Crash Bandicoot (USA).zip", true}, {"psx", "Crash Bandicoot (USA).7z", false},
	}
	for _, c := range keep {
		if got := keepArchiveIntact(c.core, c.file); got != c.want {
			t.Errorf("keepArchiveIntact(%s, %s) = %v", c.core, c.file, got)
		}
	}
	playable := []struct {
		core, file string
		want       bool
	}{
		{"mame", "atetris.zip", true}, {"mame", "atetris.nes", false}, {"arcade", "atetris.7z", false},
		{"nes", "game.zip", true}, {"nes", "game.nes", true}, {"nes", "game.7z", true}, {"nes", "notes.txt", false},
	}
	for _, c := range playable {
		if got := playableForCore(c.core, c.file); got != c.want {
			t.Errorf("playableForCore(%s, %s) = %v", c.core, c.file, got)
		}
	}
	for file, want := range map[string]bool{"Crash (USA).7z": true, "Crash (USA).zip": false} {
		if got := repackForCore("psx", file); got != want {
			t.Errorf("repackForCore(psx, %s) = %v", file, got)
		}
	}
	if repackForCore("nes", "game.7z") {
		t.Error("only PlayStation repacks a .7z")
	}
}

func TestCueSheetsListTheirTracks(t *testing.T) {
	names := map[string]string{
		`FILE "Crash Bandicoot (USA).bin" BINARY`: "Crash Bandicoot (USA).bin",
		`  file "Game (Track 2).bin" BINARY`:      "Game (Track 2).bin",
		`FILE game.bin BINARY`:                    "game.bin",
		`FILE game.bin`:                           "game.bin",
		`  TRACK 01 MODE2/2352`:                   "",
		`FILE "" BINARY`:                          "",
		`REM FILE "x.bin" BINARY`:                 "",
	}
	for line, want := range names {
		if got := cueTrackName(line); got != want {
			t.Errorf("cueTrackName(%q) = %q, want %q", line, got, want)
		}
	}

	dir := t.TempDir()
	writeFile(t, filepath.Join(dir, "Game.cue"), []byte("FILE \"game (track 1).BIN\" BINARY\nFILE \"Game (Track 2).bin\" BINARY\nFILE \"Game (Track 1).bin\" BINARY\n"))
	writeFile(t, filepath.Join(dir, "Game (Track 1).bin"), bytes.Repeat([]byte{1}, 2352))
	writeFile(t, filepath.Join(dir, "Game (Track 2).bin"), bytes.Repeat([]byte{2}, 588))
	files := cueSetFiles(filepath.Join(dir, "Game.cue"))
	want := []string{filepath.Join(dir, "Game.cue"), filepath.Join(dir, "Game (Track 1).bin"), filepath.Join(dir, "Game (Track 2).bin")}
	if !slices.Equal(files, want) {
		t.Fatalf("cue set = %v, want %v", files, want)
	}

	for _, sheet := range []string{"FILE \"Missing.bin\" BINARY\n", "FILE \"../Outside.bin\" BINARY\n", "FILE \"sub\\Game.bin\" BINARY\n", "REM nothing listed\n"} {
		writeFile(t, filepath.Join(dir, "Bad.cue"), []byte(sheet))
		if got := cueSetFiles(filepath.Join(dir, "Bad.cue")); got != nil {
			t.Errorf("sheet %q gave %v", sheet, got)
		}
	}

	var buf bytes.Buffer
	if err := writeStoredZip(&buf, discEntries(files)); err != nil {
		t.Fatal(err)
	}
	if int64(buf.Len()) != storedZipLength(discEntries(files)) {
		t.Fatalf("disc zip is %d bytes, promised %d", buf.Len(), storedZipLength(discEntries(files)))
	}
	zr, err := zip.NewReader(bytes.NewReader(buf.Bytes()), int64(buf.Len()))
	if err != nil {
		t.Fatal(err)
	}
	for i, f := range zr.File {
		b, _ := os.ReadFile(files[i])
		rc, err := f.Open()
		if err != nil {
			t.Fatal(err)
		}
		got, err := io.ReadAll(rc)
		if err != nil || f.Name != filepath.Base(files[i]) || !bytes.Equal(got, b) {
			t.Fatalf("entry %s doesn't match %s: %v", f.Name, files[i], err)
		}
	}
}

func TestHostFoldersMapToMoonbasePaths(t *testing.T) {
	cfg := gamesConfig{paths: []pathMapping{{`D:\Media\`, "/media"}, {`D:\Media\Games\Arcade`, "/arcade"}, {`E:\Roms`, "/roms"}}}
	cases := map[string]string{
		`D:\Media\Games`:             "/media/Games",
		`D:\Media\Games\Arcade\mame`: "/arcade/mame",
		`E:\Roms\SNES`:               "/roms/SNES",
		`d:\media`:                   "/media",
		`D:\Media2\Games`:            `D:\Media2\Games`,
		"/data/games":                "/data/games",
		`D:\Media\a\b.txt`:           "/media/a/b.txt",
	}
	for in, want := range cases {
		if got := cfg.localFolder(in); got != want {
			t.Errorf("localFolder(%q) = %q, want %q", in, got, want)
		}
	}
	if got := (gamesConfig{}).localFolder("/data/games"); got != "/data/games" {
		t.Fatalf("no mapping changed the folder: %s", got)
	}
}

func TestSaveNamesStayInsideAndApart(t *testing.T) {
	if got := safeFileName("../../etc/passwd"); strings.ContainsAny(got, "./") {
		t.Fatalf("unsafe name %q", got)
	}
	long1, long2 := strings.Repeat("a", 300)+"1", strings.Repeat("a", 300)+"2"
	if a, b := safeFileName(long1), safeFileName(long2); a == b || len(a) > 200 {
		t.Fatalf("long ids collide or overflow: %q %q", a, b)
	}
	if safeFileName("lr-snes-abc_DEF") != "lr-snes-abc_DEF" {
		t.Fatal("an ordinary id changed")
	}
}

func retroLibrary(t *testing.T) (root string, files map[string]string) {
	t.Helper()
	base := t.TempDir()
	root = filepath.Join(base, "Games")
	files = map[string]string{
		"smw":     filepath.Join(root, "SNES", "Super Mario World", "Super Mario World (USA).sfc"),
		"loose":   filepath.Join(root, "SNES", "loose.smc"),
		"zipped":  filepath.Join(root, "GBA", "Zipped (USA).zip"),
		"cue":     filepath.Join(root, "PlayStation", "Loose Game.cue"),
		"bios":    filepath.Join(root, "PlayStation", "scph5501.bin"),
		"hidden":  filepath.Join(root, ".trash", "x.nes"),
		"outside": filepath.Join(base, "GamesElsewhere", "SNES", "outside.sfc"),
		"notes":   filepath.Join(root, "PlayStation", "notes.txt"),
	}
	writeFile(t, files["smw"], bytes.Repeat([]byte("S"), 4096))
	writeFile(t, files["loose"], []byte("loose rom"))
	writeFile(t, filepath.Join(root, "SNES", "readme.txt"), []byte("not a game"))
	writeFile(t, files["zipped"], zipOf(t, [2]string{"docs/readme.txt", strings.Repeat("r", 9000)}, [2]string{"roms/Zipped (USA).gba", "GBA ROM BYTES"}))
	writeFile(t, files["cue"], []byte("FILE \"Loose Game.bin\" BINARY\n"))
	writeFile(t, filepath.Join(root, "PlayStation", "Loose Game.bin"), bytes.Repeat([]byte{7}, 2352))
	writeFile(t, files["bios"], bytes.Repeat([]byte{9}, 512))
	writeFile(t, files["notes"], []byte("bios notes"))
	writeFile(t, files["hidden"], []byte("x"))
	writeFile(t, files["outside"], []byte("outside"))
	return root, files
}

func TestFolderGameLibraryServesTheAppsScreens(t *testing.T) {
	root, files := retroLibrary(t)
	s, _ := newGameServer(t,
		GameLibrary{ID: "section-7", ConfigID: "7", Name: "Retro", Folders: []string{root}},
		GameLibrary{ID: "section-8", ConfigID: "8", Name: "Movies", Folders: []string{t.TempDir()}},
	)
	if code, libs := getJSON[[]M](t, s, userMain, "/Moonfin/Games/Libraries", nil); code != 200 || len(libs) != 0 {
		t.Fatalf("games off: %d %v", code, libs)
	}
	saveGamesConfig(t, s, M{"GamesEnabled": true, "GameLibraryIds": []string{"7"}})

	if _, libs := getJSON[[]M](t, s, userMain, "/Moonfin/Games/Libraries", nil); len(libs) != 1 || libs[0]["id"] != "section-7" || libs[0]["name"] != "Retro" {
		t.Fatalf("libraries = %v", libs)
	}
	_, systems := getJSON[[]M](t, s, userMain, "/Moonfin/Games/section-7/Systems", nil)
	var got []string
	for _, sys := range systems {
		got = append(got, sys["name"].(string)+"/"+sys["core"].(string)+"/"+strconv.Itoa(int(sys["gameCount"].(float64))))
	}
	if want := []string{"GBA/gba/1", "PlayStation/psx/1", "SNES/snes/2"}; !slices.Equal(got, want) {
		t.Fatalf("systems = %v, want %v", got, want)
	}
	_, snes := getJSON[[]M](t, s, userMain, "/Moonfin/Games/section-7/Games", map[string]any{"system": "snes"})
	if len(snes) != 2 || snes[0]["title"] != "loose" || snes[1]["title"] != "Super Mario World" || snes[1]["fileName"] != "Super Mario World (USA).sfc" {
		t.Fatalf("SNES games = %v", snes)
	}
	if code, _ := getJSON[[]M](t, s, userMain, "/Moonfin/Games/section-8/Systems", nil); code != 404 {
		t.Fatalf("a library not picked for games answered %d", code)
	}

	// A zipped ROM is unpacked, and the detail names what the app gets.
	zipped := gameToken(files["zipped"])
	_, detail := getJSON[M](t, s, userMain, "/Moonfin/Games/section-7/Games/"+zipped, nil)
	if detail["fileName"] != "Zipped (USA).gba" || detail["sizeBytes"] != float64(len("GBA ROM BYTES")) || detail["core"] != "gba" || detail["backendOverrideSupported"] != true {
		t.Fatalf("zipped detail = %v", detail)
	}
	resp := serve(context.Background(), s, userMain, "GET", "/Moonfin/Games/section-7/Rom/"+zipped, "", nil)
	if b := readAll(t, resp); string(b) != "GBA ROM BYTES" {
		t.Fatalf("unpacked ROM = %q", b)
	}
	resp = serve(context.Background(), s, userMain, "HEAD", "/Moonfin/Games/section-7/Rom/"+zipped, "", nil)
	if resp.Header.Get("Content-Length") != strconv.Itoa(len("GBA ROM BYTES")) {
		t.Fatalf("HEAD length = %q", resp.Header.Get("Content-Length"))
	}

	// A plain ROM answers ranges, with or without the file name on the end.
	r := httptest.NewRequest("GET", "/Moonfin/Games/section-7/Rom/"+gameToken(files["smw"])+"/Super%20Mario%20World%20(USA).sfc", nil)
	for k, v := range userMain.headers() {
		r.Header.Set(k, v)
	}
	r.Header.Set("Range", "bytes=0-9")
	w := httptest.NewRecorder()
	s.ServeHTTP(w, r)
	if w.Code != http.StatusPartialContent || w.Body.String() != "SSSSSSSSSS" {
		t.Fatalf("range = %d %q", w.Code, w.Body.String())
	}

	// A loose disc goes out as one zip, and its tracks aren't BIOS files.
	cue := gameToken(files["cue"])
	_, disc := getJSON[M](t, s, userMain, "/Moonfin/Games/section-7/Games/"+cue, nil)
	bios, _ := disc["bios"].([]any)
	if disc["fileName"] != "Loose Game.zip" || disc["sizeBytes"] != float64(storedZipLength(discEntries(cueSetFiles(files["cue"])))) || len(bios) != 1 || bios[0].(map[string]any)["fileName"] != "scph5501.bin" {
		t.Fatalf("disc detail = %v", disc)
	}
	b := readAll(t, serve(context.Background(), s, userMain, "GET", "/Moonfin/Games/section-7/Rom/"+cue, "", nil))
	if zr, err := zip.NewReader(bytes.NewReader(b), int64(len(b))); err != nil || len(zr.File) != 2 {
		t.Fatalf("disc zip: %v", err)
	}
	biosID := bios[0].(map[string]any)["id"].(string)
	if b := readAll(t, serve(context.Background(), s, userMain, "GET", "/Moonfin/Games/section-7/Bios/"+biosID, "", nil)); len(b) != 512 {
		t.Fatalf("BIOS = %d bytes", len(b))
	}

	// Only files inside the library are served, and the ROM route only games.
	for _, token := range []string{"not-a-token", gameToken(files["outside"]), gameToken(files["notes"]), gameToken(filepath.Join(root, "SNES", "..", "..", "GamesElsewhere", "SNES", "outside.sfc"))} {
		if resp := serve(context.Background(), s, userMain, "GET", "/Moonfin/Games/section-7/Rom/"+token, "", nil); resp.StatusCode != 404 {
			t.Errorf("ROM %s answered %d", token, resp.StatusCode)
		}
	}
	if resp := serve(context.Background(), s, userMain, "GET", "/Moonfin/Games/section-7/Bios/"+gameToken(files["notes"]), "", nil); resp.StatusCode != 200 {
		t.Fatalf("the BIOS route should serve any library file, got %d", resp.StatusCode)
	}
	if code, _ := call(t, s, anon, "GET", "/Moonfin/Games/section-7/Systems", "", nil); code != 401 {
		t.Fatalf("anonymous systems = %d", code)
	}
}

func TestGameChoicesAndSavesAreEachAccounts(t *testing.T) {
	root, files := retroLibrary(t)
	s, _ := newGameServer(t, GameLibrary{ID: "section-7", ConfigID: "7", Name: "Retro Games", Folders: []string{root}})
	// With none picked, a library named like a game library is used.
	saveGamesConfig(t, s, M{"GamesEnabled": true})
	game := "/Moonfin/Games/section-7/Games/" + gameToken(files["smw"])

	if code, d := call(t, s, userMain, "PUT", game+"/Backend", `{"backend":"retroarch"}`, nil); code != 400 {
		t.Fatalf("unknown backend: %d %v", code, d)
	}
	if code, d := call(t, s, userMain, "PUT", game+"/Backend", `{"backend":"EmulatorJS"}`, nil); code != 200 || d["userBackendOverride"] != "emulatorjs" {
		t.Fatalf("set backend: %d %v", code, d)
	}
	if _, d := getJSON[M](t, s, userMain, game, nil); d["userBackendOverride"] != "emulatorjs" {
		t.Fatalf("backend not kept: %v", d)
	}
	if _, d := getJSON[M](t, s, other, game, nil); d["userBackendOverride"] != nil {
		t.Fatalf("another account sees the choice: %v", d)
	}
	if _, d := call(t, s, userMain, "PUT", game+"/Backend", `{"backend":null}`, nil); d["userBackendOverride"] != nil {
		t.Fatalf("clear backend: %v", d)
	}

	save := "/Moonfin/Games/Saves/lr-snes-" + gameToken(files["smw"])
	if code, _ := call(t, s, userMain, "GET", save, "", nil); code != 404 {
		t.Fatalf("no save yet = %d", code)
	}
	if code, _ := call(t, s, userMain, "PUT", save, "", nil); code != 400 {
		t.Fatalf("empty save = %d", code)
	}
	if code, _ := call(t, s, userMain, "PUT", save, "STATE", nil); code != 204 {
		t.Fatalf("put save = %d", code)
	}
	if code, _ := call(t, s, userMain, "PUT", save, "SRAM", map[string]any{"kind": "sram"}); code != 204 {
		t.Fatalf("put sram = %d", code)
	}
	if b := readAll(t, serve(context.Background(), s, userMain, "GET", save, "", nil)); string(b) != "STATE" {
		t.Fatalf("state = %q", b)
	}
	if b := readAll(t, serve(context.Background(), s, userMain, "GET", save, "", map[string]any{"kind": "sram"})); string(b) != "SRAM" {
		t.Fatalf("sram = %q", b)
	}
	if code, _ := call(t, s, other, "GET", save, "", nil); code != 404 {
		t.Fatalf("another account reads the save: %d", code)
	}
}

func TestGameArtComesFromLibretroOnce(t *testing.T) {
	root, files := retroLibrary(t)
	var hits atomic.Int32
	png := append([]byte("\x89PNG\r\n\x1a\n"), make([]byte, 32)...)
	thumbs := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		hits.Add(1)
		if r.URL.Path == "/Nintendo - Super Nintendo Entertainment System/Named_Boxarts/Super Mario World (USA).png" {
			w.Write(png)
			return
		}
		http.NotFound(w, r)
	}))
	defer thumbs.Close()
	old := libretroThumbs
	libretroThumbs = thumbs.URL + "/"
	defer func() { libretroThumbs = old }()

	s, _ := newGameServer(t, GameLibrary{ID: "section-7", ConfigID: "7", Name: "Retro", Folders: []string{root}})
	saveGamesConfig(t, s, M{"GamesEnabled": true, "GameLibraryIds": []string{"7"}})
	thumb := "/Moonfin/Games/section-7/Thumb/" + gameToken(files["smw"])
	for range 2 {
		resp := serve(context.Background(), s, userMain, "GET", thumb, "", nil)
		if b := readAll(t, resp); resp.StatusCode != 200 || !bytes.Equal(b, png) || resp.Header.Get("Content-Type") != "image/png" {
			t.Fatalf("thumb = %d %s", resp.StatusCode, resp.Header.Get("Content-Type"))
		}
	}
	if hits.Load() != 1 {
		t.Fatalf("libretro was asked %d times", hits.Load())
	}

	hits.Store(0)
	for range 2 {
		resp := serve(context.Background(), s, userMain, "GET", "/Moonfin/Games/section-7/Thumb/"+gameToken(files["loose"]), "", map[string]any{"type": "snap"})
		if resp.StatusCode != 404 || resp.Header.Get("Cache-Control") != "private,max-age=300" {
			t.Fatalf("missing art = %d %q", resp.StatusCode, resp.Header.Get("Cache-Control"))
		}
	}
	if hits.Load() != 1 {
		t.Fatalf("a miss was asked about %d times", hits.Load())
	}
}

func TestEmulatorPlayerAndUploadedCores(t *testing.T) {
	s, gh := newGameServer(t)
	writeFile(t, filepath.Join(gh.ejs, "player.html"), []byte("<script>window.EJS_pathtodata = '__EJS_PATHTODATA__';</script>"))
	writeFile(t, filepath.Join(gh.ejs, "moonfin-bridge.js"), []byte("// bridge"))

	resp := serve(context.Background(), s, anon, "GET", "/Moonfin/EmulatorJS/player.html", "", map[string]any{"core": "snes"})
	if b := readAll(t, resp); resp.StatusCode != 200 || !strings.Contains(string(b), emulatorJSCDN) || resp.Header.Get("Cross-Origin-Opener-Policy") != "" {
		t.Fatalf("player = %d %s", resp.StatusCode, b)
	}
	resp = serve(context.Background(), s, anon, "GET", "/Moonfin/EmulatorJS/player.html", "", map[string]any{"core": "psp"})
	if resp.Header.Get("Cross-Origin-Embedder-Policy") != "credentialless" {
		t.Fatal("PSP needs an isolated page")
	}
	if b := readAll(t, serve(context.Background(), s, anon, "GET", "/Moonfin/EmulatorJS/moonfin-bridge.js", "", nil)); string(b) != "// bridge" {
		t.Fatalf("bridge = %q", b)
	}

	cores := zipOf(t, [2]string{"EmulatorJS/data/loader.js", "loader"}, [2]string{"EmulatorJS/data/emulator.min.js", "js"},
		[2]string{"EmulatorJS/data/emulator.min.css", "css"}, [2]string{"EmulatorJS/data/cores/snes.wasm", "wasm"},
		[2]string{"EmulatorJS/README.md", "outside the data folder"})
	if code, _ := call(t, s, userMain, "POST", "/Moonfin/Games/Cores/Upload", string(cores), nil); code != 403 {
		t.Fatalf("a user uploaded cores: %d", code)
	}
	if code, st := call(t, s, adminMain, "POST", "/Moonfin/Games/Cores/Upload", string(cores), nil); code != 202 || st["installed"] != true || st["filesInstalled"] != float64(4) {
		t.Fatalf("upload = %d %v", code, st)
	}
	if _, st := call(t, s, userMain, "GET", "/Moonfin/Games/Cores/Status", "", nil); st["state"] != "installed" {
		t.Fatalf("status = %v", st)
	}
	if b := readAll(t, serve(context.Background(), s, anon, "GET", "/Moonfin/EmulatorJS/player.html", "", nil)); !strings.Contains(string(b), "'./data/'") {
		t.Fatalf("installed cores not used: %s", b)
	}
	if b := readAll(t, serve(context.Background(), s, anon, "GET", "/Moonfin/EmulatorJS/data/cores/snes.wasm", "", nil)); string(b) != "wasm" {
		t.Fatalf("core = %q", b)
	}
	for _, p := range []string{"/Moonfin/EmulatorJS/data/cores", "/Moonfin/EmulatorJS/data/..%2F..%2Fsaves"} {
		if resp := serve(context.Background(), s, anon, "GET", p, "", nil); resp.StatusCode != 404 {
			t.Errorf("%s answered %d", p, resp.StatusCode)
		}
	}
	if _, st := call(t, s, adminMain, "POST", "/Moonfin/Games/Cores/Upload", string(zipOf(t, [2]string{"readme.txt", "x"})), nil); st["state"] != "failed" || st["error"] == nil {
		t.Fatalf("a zip without cores = %v", st)
	}
}

func TestPSPIsolationCoversMoonfinWeb(t *testing.T) {
	host := testHost(store.NewMemKV())
	host.Catalog = &gameHost{dir: t.TempDir(), ejs: t.TempDir()}
	host.Web = fstest.MapFS{"index.html": {Data: []byte(`<html><head><base href="/Moonfin/Web/"></head></html>`)}}
	s := NewServer("1.2.3", host)
	if resp := serve(context.Background(), s, anon, "GET", "/Moonfin/Web/", "", nil); resp.Header.Get("Cross-Origin-Embedder-Policy") != "" {
		t.Fatal("Moonfin Web is isolated before the admin asks")
	}
	saveGamesConfig(t, s, M{"GamesIsolateWebApp": true})
	resp := serve(context.Background(), s, anon, "GET", "/Moonfin/Web/", "", nil)
	if resp.Header.Get("Cross-Origin-Opener-Policy") != "same-origin" || resp.Header.Get("Cross-Origin-Embedder-Policy") != "credentialless" {
		t.Fatalf("isolation headers = %v", resp.Header)
	}
}

func TestGamesStayOffOnHostsWithoutGameLibraries(t *testing.T) {
	s := newTestServer()
	for _, p := range []string{"/Moonfin/Games/Libraries", "/Moonfin/EmulatorJS/player.html"} {
		if code, _ := call(t, s, userMain, "GET", p, "", nil); code != 404 {
			t.Errorf("%s answered %d on Silo", p, code)
		}
	}
}

func TestSevenZipGamesAreUnpackedOrRepacked(t *testing.T) {
	root := t.TempDir()
	single := filepath.Join(root, "NES", "Seven (USA).7z")
	disc := filepath.Join(root, "PlayStation", "Disc Seven (USA).7z")
	for src, dst := range map[string]string{"testdata/game.7z": single, "testdata/disc.7z": disc} {
		b, err := os.ReadFile(src)
		if err != nil {
			t.Fatal(err)
		}
		writeFile(t, dst, b)
	}
	s, _ := newGameServer(t, GameLibrary{ID: "section-7", ConfigID: "7", Name: "Retro", Folders: []string{root}})
	saveGamesConfig(t, s, M{"GamesEnabled": true, "GameLibraryIds": []string{"7"}})

	rom := "NES\x1aSEVEN ZIP ROM BYTES"
	id := gameToken(single)
	_, detail := getJSON[M](t, s, userMain, "/Moonfin/Games/section-7/Games/"+id, nil)
	if detail["fileName"] != "Game.nes" || detail["sizeBytes"] != float64(len(rom)) || detail["core"] != "nes" {
		t.Fatalf("single ROM detail = %v", detail)
	}
	if b := readAll(t, serve(context.Background(), s, userMain, "GET", "/Moonfin/Games/section-7/Rom/"+id, "", nil)); string(b) != rom {
		t.Fatalf("unpacked ROM = %q", b)
	}

	// A PlayStation .7z goes out as a stored zip of everything in it, at the size the detail promised.
	id = gameToken(disc)
	_, detail = getJSON[M](t, s, userMain, "/Moonfin/Games/section-7/Games/"+id, nil)
	if detail["fileName"] != "Disc Seven (USA).zip" || detail["core"] != "psx" {
		t.Fatalf("disc detail = %v", detail)
	}
	resp := serve(context.Background(), s, userMain, "HEAD", "/Moonfin/Games/section-7/Rom/"+id, "", nil)
	if resp.Header.Get("Content-Length") != strconv.Itoa(int(detail["sizeBytes"].(float64))) {
		t.Fatalf("HEAD length %q, detail %v", resp.Header.Get("Content-Length"), detail["sizeBytes"])
	}
	b := readAll(t, serve(context.Background(), s, userMain, "GET", "/Moonfin/Games/section-7/Rom/"+id, "", nil))
	if float64(len(b)) != detail["sizeBytes"] {
		t.Fatalf("disc zip is %d bytes, promised %v", len(b), detail["sizeBytes"])
	}
	zr, err := zip.NewReader(bytes.NewReader(b), int64(len(b)))
	if err != nil {
		t.Fatal(err)
	}
	sizes := map[string]uint64{}
	for _, f := range zr.File {
		if f.Method != zip.Store {
			t.Errorf("%s isn't stored", f.Name)
		}
		sizes[f.Name] = f.UncompressedSize64
	}
	if want := map[string]uint64{"Disc.cue": 66, "Disc (Track 1).bin": 2352, "Disc (Track 2).bin": 1176}; !maps.Equal(sizes, want) {
		t.Fatalf("disc zip holds %v, want %v", sizes, want)
	}
}
