package api

import (
	"archive/zip"
	"bytes"
	"context"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"slices"
	"strconv"
	"strings"
	"testing"
	"time"
)

// The fixtures follow what RomM 5.4.1 sends, trimmed to the fields Moonbase
// reads.
const rommPlatformsJSON = `[
  { "id": 10, "slug": "snes", "fs_slug": "snes", "display_name": "Super Nintendo Entertainment System", "rom_count": 3, "firmware": [] },
  { "id": 8, "slug": "psx", "fs_slug": "psx", "display_name": "PlayStation", "rom_count": 0,
    "firmware": [ { "id": 3, "file_name": "scph5501.bin", "file_size_bytes": 524288, "missing_from_fs": false },
                  { "id": 4, "file_name": "gone.bin", "file_size_bytes": 1, "missing_from_fs": true } ] },
  { "id": 11, "slug": "saturn", "fs_slug": "saturn", "display_name": "Sega Saturn", "rom_count": 40, "firmware": [] },
  { "id": 12, "slug": "arcade", "fs_slug": "arcade", "display_name": "Arcade", "rom_count": 1, "firmware": [] },
  { "id": 13, "slug": "nes", "fs_slug": "nes", "display_name": "NES", "missing_from_fs": true, "firmware": [] },
  { "id": 14, "slug": "snes", "fs_slug": "snes-hacks", "display_name": "Super Nintendo Entertainment System", "firmware": [] }
]`

const rommRomsJSON = `{ "items": [
  { "id": 12, "platform_id": 10, "name": "Super Mario World", "fs_name_no_ext": "Super Mario World (USA)",
    "path_cover_large": "/assets/romm/resources/roms/10/12/cover/big.png?ts=2026-10-23 17:47:20",
    "merged_screenshots": [ "/assets/romm/resources/roms/10/12/screenshots/0.jpg" ],
    "files": [ { "id": 14, "file_name": "Vimm's Lair.txt", "file_size_bytes": 270, "category": "game" },
               { "id": 15, "file_name": "Super Mario World (USA).sfc", "file_size_bytes": 524288, "category": "game" } ] },
  { "id": 7, "platform_id": 10, "name": "Zipped",
    "files": [ { "id": 9, "file_name": "Zipped (USA).zip", "file_size_bytes": 300000,
      "archive_members": [ { "name": "docs/readme.txt", "size": 900000 }, { "name": "roms/Zipped (USA).sfc", "size": 13 } ] } ] },
  { "id": 9, "platform_id": 10, "name": "Seven",
    "files": [ { "id": 11, "file_name": "Seven (USA).7z", "file_size_bytes": 236, "archive_members": [ { "name": "docs/readme.txt", "size": 3000 }, { "name": "Game.nes", "size": 23 } ] } ] },
  { "id": 8, "platform_id": 12, "name": "Metal Slug",
    "files": [ { "id": 10, "file_name": "mslug.zip", "file_size_bytes": 4000000, "archive_members": [ { "name": "201-p1.p1", "size": 2097152 } ] } ] },
  { "id": 13, "platform_id": 10, "name": "Donkey Kong Country 2: Diddy's Kong Quest", "summary": "Diddy and Dixie set off.",
    "regions": [ "USA", "Europe" ],
    "metadatum": { "genres": [ "Platform", "Adventure" ], "developers": [], "publishers": [ "Nintendo" ],
                   "franchises": [ "Donkey Kong" ], "player_count": "1-2", "first_release_date": 816912000000, "average_rating": 79.51 },
    "files": [ { "id": 17, "file_name": "DKC2 (USA).sfc", "file_size_bytes": 4194304 } ] },
  { "id": 1, "platform_id": 10, "name": "No files", "files": [] },
  { "id": 2, "platform_id": 11, "name": "Saturn game", "files": [ { "id": 5, "file_name": "game.iso", "file_size_bytes": 9 } ] },
  { "id": 3, "platform_id": 10, "name": "Missing", "missing_from_fs": true, "files": [ { "id": 6, "file_name": "a.sfc", "file_size_bytes": 9 } ] },
  { "id": 4, "platform_id": 10, "name": "Manual only", "files": [ { "id": 7, "file_name": "a.sfc", "file_size_bytes": 9, "category": "manual" } ] },
  { "id": 5, "platform_id": 10, "name": "Unlisted zip", "files": [ { "id": 8, "file_name": "a.zip", "file_size_bytes": 9 } ] },
  { "id": 6, "platform_id": 10, "name": "Odd types", "player_count": 2, "files": "none" },
  { "id": 20, "platform_id": 8, "name": "Final Fantasy VII", "fs_name": "Final Fantasy VII (USA)",
    "files": [ { "id": 31, "file_name": "Final Fantasy VII (USA) (Track 2).bin", "file_size_bytes": 1000 },
               { "id": 30, "file_name": "Final Fantasy VII (USA) (Track 1).bin", "file_size_bytes": 7000 },
               { "id": 32, "file_name": "Final Fantasy VII (USA).cue", "file_size_bytes": 200 },
               { "id": 33, "file_name": "manual.pdf", "file_size_bytes": 5, "category": "manual" } ] },
  { "id": 21, "platform_id": 8, "name": "Disc",
    "files": [ { "id": 34, "file_name": "Disc.7z", "file_size_bytes": 300,
      "archive_members": [ { "name": "Disc (Track 1).bin", "size": 2352 }, { "name": "Disc (Track 2).bin", "size": 1176 }, { "name": "Disc.cue", "size": 66 } ] } ] },
  { "id": 22, "platform_id": 8, "name": "Tracks missing", "files": [ { "id": 35, "file_name": "Lonely.cue", "file_size_bytes": 200 } ] }
] }`

func fakeRomm(t *testing.T) *httptest.Server {
	t.Helper()
	zipped := zipOf(t, [2]string{"docs/readme.txt", strings.Repeat("r", 100)}, [2]string{"roms/Zipped (USA).sfc", "ZIPPED SNES!!"})
	seven, err := os.ReadFile("testdata/game.7z")
	if err != nil {
		t.Fatal(err)
	}
	disc, err := os.ReadFile("testdata/disc.7z")
	if err != nil {
		t.Fatal(err)
	}
	png := append([]byte("\x89PNG\r\n\x1a\n"), make([]byte, 16)...)
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/api/heartbeat" {
			io.WriteString(w, `{"SYSTEM":{"VERSION":"5.4.1"}}`)
			return
		}
		if strings.HasPrefix(r.URL.Path, "/assets/") {
			if r.URL.Path == "/assets/romm/resources/roms/10/12/cover/big.png" {
				w.Write(png)
				return
			}
			http.NotFound(w, r)
			return
		}
		if r.Header.Get("Authorization") != "Bearer rmm_good" {
			w.WriteHeader(http.StatusUnauthorized)
			return
		}
		switch r.URL.Path {
		case "/api/platforms":
			io.WriteString(w, rommPlatformsJSON)
		case "/api/roms":
			if r.URL.Query().Get("with_files") != "true" || !slices.Equal(r.URL.Query()["platform_ids"], []string{"10", "8", "12", "14"}) {
				t.Errorf("roms query = %s", r.URL.RawQuery)
			}
			io.WriteString(w, rommRomsJSON)
		case "/api/roms/12/content/Super Mario World (USA).sfc":
			if r.URL.Query().Get("file_ids") != "15" {
				t.Errorf("file_ids = %s", r.URL.Query().Get("file_ids"))
			}
			http.ServeContent(w, r, "", time.Time{}, strings.NewReader("SNES ROM BYTES"))
		case "/api/roms/7/content/Zipped (USA).zip":
			w.Write(zipped)
		case "/api/roms/9/content/Seven (USA).7z":
			w.Write(seven)
		case "/api/roms/20/content/Final Fantasy VII (USA)":
			if got := r.URL.Query().Get("file_ids"); got != "31,30,32" {
				t.Errorf("disc set file_ids = %s", got)
			}
			io.WriteString(w, "ROMM DISC SET ZIP")
		case "/api/roms/21/content/Disc.7z":
			w.Write(disc)
		case "/api/firmware/3/content/scph5501.bin":
			io.WriteString(w, "PSX BIOS")
		default:
			http.NotFound(w, r)
		}
	}))
	t.Cleanup(srv.Close)
	return srv
}

func TestRommLibraryServesTheAppsScreens(t *testing.T) {
	romm := fakeRomm(t)
	s, _ := newGameServer(t,
		GameLibrary{ID: "section-9", ConfigID: "9", Name: "RomM", Folders: []string{"/no/such/folder"}},
		GameLibrary{ID: "section-1", ConfigID: "1", Name: "Movies"},
	)
	saveGamesConfig(t, s, M{"GamesEnabled": true, "GameLibraryIds": []string{}, "GamesRommUrl": romm.URL + "/", "GamesRommToken": " rmm_good ", "GamesRommLibraryId": "9"})

	if _, libs := getJSON[[]M](t, s, userMain, "/Moonfin/Games/Libraries", nil); len(libs) != 1 || libs[0]["id"] != "section-9" {
		t.Fatalf("libraries = %v", libs)
	}
	_, systems := getJSON[[]M](t, s, userMain, "/Moonfin/Games/section-9/Systems", nil)
	var names []string
	for _, sys := range systems {
		names = append(names, sys["id"].(string)+"/"+sys["core"].(string)+"/"+strconv.Itoa(int(sys["gameCount"].(float64))))
	}
	if want := []string{"Arcade/arcade/1", "PlayStation/psx/2", "SNES/snes/4"}; !slices.Equal(names, want) {
		t.Fatalf("systems = %v, want %v", names, want)
	}
	_, list := getJSON[[]M](t, s, userMain, "/Moonfin/Games/section-9/Games", map[string]any{"system": "SNES"})
	var titles []string
	for _, g := range list {
		titles = append(titles, g["id"].(string)+"="+g["fileName"].(string))
	}
	if want := []string{"romm-13=DKC2 (USA).sfc", "romm-9=Game.nes", "romm-12=Super Mario World (USA).sfc", "romm-7=Zipped (USA).sfc"}; !slices.Equal(titles, want) {
		t.Fatalf("games = %v, want %v", titles, want)
	}

	_, dkc := getJSON[M](t, s, userMain, "/Moonfin/Games/section-9/Games/romm-13", nil)
	for k, want := range map[string]any{"overview": "Diddy and Dixie set off.", "genre": "Platform, Adventure", "publisher": "Nintendo",
		"franchise": "Donkey Kong", "region": "USA, Europe", "players": float64(2), "year": float64(1995), "rating": 4.0, "sizeBytes": float64(4194304)} {
		if dkc[k] != want {
			t.Errorf("detail %s = %v, want %v", k, dkc[k], want)
		}
	}
	if _, ok := dkc["developer"]; ok {
		t.Error("an empty developer list should be left out")
	}

	r := httptest.NewRequest("GET", "/Moonfin/Games/section-9/Rom/romm-12", nil)
	for k, v := range userMain.headers() {
		r.Header.Set(k, v)
	}
	r.Header.Set("Range", "bytes=0-3")
	w := httptest.NewRecorder()
	s.ServeHTTP(w, r)
	if w.Code != http.StatusPartialContent || w.Body.String() != "SNES" || w.Header().Get("Content-Range") != "bytes 0-3/14" {
		t.Fatalf("proxied range = %d %q %q", w.Code, w.Body.String(), w.Header().Get("Content-Range"))
	}
	if b := readAll(t, serve(context.Background(), s, userMain, "GET", "/Moonfin/Games/section-9/Rom/romm-7", "", nil)); string(b) != "ZIPPED SNES!!" {
		t.Fatalf("unpacked RomM ROM = %q", b)
	}
	if resp := serve(context.Background(), s, userMain, "HEAD", "/Moonfin/Games/section-9/Rom/romm-7", "", nil); resp.Header.Get("Content-Length") != "13" {
		t.Fatalf("HEAD of a zipped RomM ROM = %q", resp.Header.Get("Content-Length"))
	}
	if b := readAll(t, serve(context.Background(), s, userMain, "GET", "/Moonfin/Games/section-9/Rom/romm-9", "", nil)); string(b) != "NES\x1aSEVEN ZIP ROM BYTES" {
		t.Fatalf("unpacked RomM .7z = %q", b)
	}
	if b := readAll(t, serve(context.Background(), s, userMain, "GET", "/Moonfin/Games/section-9/Rom/romm-8", "", nil)); len(b) != 0 {
		t.Fatalf("missing upstream file sent %q", b)
	}

	_, discs := getJSON[[]M](t, s, userMain, "/Moonfin/Games/section-9/Games", map[string]any{"system": "PlayStation"})
	titles = nil
	for _, g := range discs {
		titles = append(titles, g["id"].(string)+"="+g["fileName"].(string))
	}
	if want := []string{"romm-21=Disc.zip", "romm-20=Final Fantasy VII (USA).zip"}; !slices.Equal(titles, want) {
		t.Fatalf("PlayStation games = %v, want %v", titles, want)
	}
	if b := readAll(t, serve(context.Background(), s, userMain, "GET", "/Moonfin/Games/section-9/Rom/romm-20", "", nil)); string(b) != "ROMM DISC SET ZIP" {
		t.Fatalf("RomM disc set = %q", b)
	}
	b := readAll(t, serve(context.Background(), s, userMain, "GET", "/Moonfin/Games/section-9/Rom/romm-21", "", nil))
	zr, err := zip.NewReader(bytes.NewReader(b), int64(len(b)))
	if err != nil {
		t.Fatalf("repacked RomM .7z isn't a zip: %v", err)
	}
	var inside []string
	for _, f := range zr.File {
		inside = append(inside, f.Name)
	}
	if want := []string{"Disc (Track 1).bin", "Disc (Track 2).bin", "Disc.cue"}; !slices.Equal(inside, want) {
		t.Fatalf("repacked RomM .7z holds %v, want %v", inside, want)
	}

	_, psx := getJSON[M](t, s, userMain, "/Moonfin/Games/section-9/Games/romm-12", nil)
	if psx["fileName"] != "Super Mario World (USA).sfc" {
		t.Fatalf("detail = %v", psx)
	}
	if b := readAll(t, serve(context.Background(), s, userMain, "GET", "/Moonfin/Games/section-9/Bios/romm-fw-3", "", nil)); string(b) != "PSX BIOS" {
		t.Fatalf("firmware = %q", b)
	}
	for _, id := range []string{"romm-fw-3", "romm-x", "12"} {
		if code, _ := getJSON[M](t, s, userMain, "/Moonfin/Games/section-9/Games/"+id, nil); code != 404 {
			t.Errorf("game %s answered %d", id, code)
		}
	}

	resp := serve(context.Background(), s, userMain, "GET", "/Moonfin/Games/section-9/Thumb/romm-12", "", nil)
	if readAll(t, resp); resp.StatusCode != 200 || resp.Header.Get("Content-Type") != "image/png" {
		t.Fatalf("RomM cover = %d", resp.StatusCode)
	}
}

func TestRommConnectionTest(t *testing.T) {
	romm := fakeRomm(t)
	s, _ := newGameServer(t)
	test := func(body M) M {
		b, _ := json.Marshal(body)
		code, out := call(t, s, adminMain, "POST", "/Moonfin/Games/Romm/Test", string(b), nil)
		if code != 200 {
			t.Fatalf("test = %d", code)
		}
		return out
	}
	if out := test(M{"url": romm.URL, "token": "rmm_good"}); out["ok"] != true || out["version"] != "5.4.1" || out["platformCount"] != float64(6) ||
		out["supportedPlatformCount"] != float64(4) || out["supportedRomCount"] != float64(4) {
		t.Fatalf("good test = %v", out)
	}
	if out := test(M{"url": romm.URL, "token": "rmm_bad"}); out["error"] != "RomM rejected the token (/api/platforms)." {
		t.Fatalf("bad token = %v", out)
	}
	if out := test(M{"url": "romm.example.com", "token": "x"}); out["ok"] != false || !strings.HasPrefix(out["error"].(string), "Enter the RomM address") {
		t.Fatalf("bad address = %v", out)
	}
	if code, _ := call(t, s, userMain, "POST", "/Moonfin/Games/Romm/Test", "{}", nil); code != 403 {
		t.Fatalf("a user ran the test: %d", code)
	}
}

func TestRommAddressesAndArt(t *testing.T) {
	urls := map[string]string{
		"http://192.168.1.20:8080/":         "http://192.168.1.20:8080",
		" https://romm.example.com ":        "https://romm.example.com",
		"ftp://romm.example.com":            "",
		"http://user:pass@romm.example.com": "",
		"http://romm.example.com/?x=1":      "",
		"romm.example.com":                  "",
		"":                                  "",
	}
	for in, want := range urls {
		if got := normalizeRommURL(in); got != want {
			t.Errorf("normalizeRommURL(%q) = %q, want %q", in, got, want)
		}
	}
	set := rommSettings{url: "http://romm.test"}
	art := map[string]string{
		"/assets/romm/resources/roms/10/12/cover/big.png?ts=2026-10-23 17:47:20": "http://romm.test/assets/romm/resources/roms/10/12/cover/big.png?ts=2026-10-23%2017:47:20",
		"/api/roms/1/content/x":        "",
		"/assets/../api/users":         "",
		"https://elsewhere.test/a.png": "",
		"":                             "",
	}
	for cover, want := range art {
		got := rommArt(set, rommGame{Cover: cover}, "boxart")
		if want == "" && got != nil || want != "" && (len(got) != 1 || got[0] != want) {
			t.Errorf("rommArt(%q) = %v, want %q", cover, got, want)
		}
	}
}
