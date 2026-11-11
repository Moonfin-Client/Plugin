package api

import (
	"archive/zip"
	"bufio"
	"encoding/base64"
	"errors"
	"io"
	"io/fs"
	"os"
	"path"
	"path/filepath"
	"slices"
	"strings"
	"unicode"

	"github.com/bodgit/sevenzip"
)

// These file rules and core choices match the Jellyfin plugin, so a folder
// plays with the same core on both.

const defaultGameCore = "nes"

var extensionCores = map[string]string{
	".nes": "nes", ".fds": "nes", ".sfc": "snes", ".smc": "snes", ".gb": "gb", ".gbc": "gb", ".gba": "gba",
	".md": "segaMD", ".gen": "segaMD", ".smd": "segaMD", ".sms": "segaMS", ".gg": "segaGG", ".sg": "segaMS",
	".n64": "n64", ".z64": "n64", ".v64": "n64", ".nds": "nds", ".vb": "vb", ".a26": "atari2600", ".a78": "atari7800",
	".lnx": "lynx", ".ws": "ws", ".wsc": "ws", ".ngp": "ngp", ".ngc": "ngp", ".pce": "pce", ".chd": "psx", ".pbp": "psx",
	".cso": "psp", ".iso": "psp",
}

var systemCores = map[string]string{
	"nes": "nes", "famicom": "nes", "snes": "snes", "superfamicom": "snes", "supernintendo": "snes",
	"gb": "gb", "gameboy": "gb", "gbc": "gb", "gameboycolor": "gb", "gba": "gba", "gameboyadvance": "gba",
	"genesis": "segaMD", "megadrive": "segaMD", "segagenesis": "segaMD", "mastersystem": "segaMS", "sms": "segaMS",
	"gamegear": "segaGG", "gg": "segaGG", "n64": "n64", "nintendo64": "n64", "nds": "nds", "nintendods": "nds",
	"virtualboy": "vb", "atari2600": "atari2600", "atari7800": "atari7800", "lynx": "lynx", "wonderswan": "ws",
	"neogeopocket": "ngp", "pcengine": "pce", "turbografx16": "pce", "psx": "psx", "ps1": "psx", "psone": "psx",
	"playstation": "psx", "psp": "psp", "playstationportable": "psp", "mame": "mame", "arcade": "arcade",
}

var biosExtensions = []string{".bin", ".bios", ".rom", ".img", ".sys", ".bs"}

func fileExt(p string) string { return strings.ToLower(filepath.Ext(p)) }

func isGameArchive(p string) bool { return fileExt(p) == ".zip" || fileExt(p) == ".7z" }

func isKnownRomExt(p string) bool { _, ok := extensionCores[fileExt(p)]; return ok }

func isRomFile(p string) bool { return isKnownRomExt(p) || isGameArchive(p) }

func isBiosFile(p string) bool { return slices.Contains(biosExtensions, fileExt(p)) }

func isCueSheet(p string) bool { return fileExt(p) == ".cue" }

func isArcadeCore(core string) bool { return core == "mame" || core == "arcade" }

// PlayStation discs are often several files, so the core gets a whole zip.
func isDiscSetCore(core string) bool { return core == "psx" }

func keepArchiveIntact(core, p string) bool {
	return (isArcadeCore(core) || isDiscSetCore(core)) && fileExt(p) == ".zip"
}

// repackForCore says a PlayStation .7z goes out as a zip of everything in it,
// since the players only unpack zips.
func repackForCore(core, p string) bool { return isDiscSetCore(core) && fileExt(p) == ".7z" }

func playableForCore(core, p string) bool {
	return isRomFile(p) && (!isArcadeCore(core) || fileExt(p) == ".zip")
}

func libraryGameFile(core, p string) bool {
	return playableForCore(core, p) || (isDiscSetCore(core) && isCueSheet(p))
}

func normalizeSystemName(name string) string {
	var b strings.Builder
	for _, r := range name {
		if unicode.IsLetter(r) || unicode.IsDigit(r) {
			b.WriteRune(unicode.ToLower(r))
		}
	}
	return b.String()
}

// systemCore picks a system folder's core by its name, then a close alias,
// then a ROM's extension, and says when it only fell back to the default.
func systemCore(systemName string, roms []string) (core string, defaulted bool) {
	n := normalizeSystemName(systemName)
	if c, ok := systemCores[n]; ok {
		return c, false
	}
	if c, ok := fuzzySystemCore(n); ok {
		return c, false
	}
	for _, rom := range roms {
		if c, ok := extensionCores[fileExt(rom)]; ok {
			return c, false
		}
	}
	return defaultGameCore, true
}

// fuzzySystemCore takes the alias closest in length that one name contains,
// and gives up when two cores tie for it.
func fuzzySystemCore(n string) (string, bool) {
	if len(n) < 3 {
		return "", false
	}
	best, bestDiff, ambiguous := "", 0, false
	for alias, core := range systemCores {
		if len(alias) < 3 || !(strings.Contains(n, alias) || strings.Contains(alias, n)) {
			continue
		}
		diff := max(len(alias)-len(n), len(n)-len(alias))
		switch {
		case best == "" || diff < bestDiff:
			best, bestDiff, ambiguous = core, diff, false
		case diff == bestDiff && core != best:
			ambiguous = true
		}
	}
	return best, best != "" && !ambiguous
}

// artworkCore is the libretro set a system's art comes from. The gb core
// plays Game Boy Color too, but its art is filed apart.
func artworkCore(core, systemName string) string {
	n := normalizeSystemName(systemName)
	if core == "gb" && (n == "gbc" || strings.Contains(n, "gameboycolor")) {
		return "gbc"
	}
	return core
}

var corePlatforms = map[string]string{
	"nes": "Nintendo - Nintendo Entertainment System", "snes": "Nintendo - Super Nintendo Entertainment System",
	"gb": "Nintendo - Game Boy", "gbc": "Nintendo - Game Boy Color", "gba": "Nintendo - Game Boy Advance",
	"n64": "Nintendo - Nintendo 64", "nds": "Nintendo - Nintendo DS", "vb": "Nintendo - Virtual Boy",
	"segaMD": "Sega - Mega Drive - Genesis", "segaMS": "Sega - Master System - Mark III", "segaGG": "Sega - Game Gear",
	"atari2600": "Atari - 2600", "atari7800": "Atari - 7800", "lynx": "Atari - Lynx", "ws": "Bandai - WonderSwan",
	"ngp": "SNK - Neo Geo Pocket", "pce": "NEC - PC Engine - TurboGrafx 16", "psx": "Sony - PlayStation",
	"psp": "Sony - PlayStation Portable", "mame": "MAME", "arcade": "FBNeo - Arcade Games",
}

// A game's id is its file's path, which the routes check is inside a library
// the caller can see before using it.
func gameToken(p string) string { return base64.RawURLEncoding.EncodeToString([]byte(p)) }

func tokenPath(token string) (string, bool) {
	b, err := base64.RawURLEncoding.DecodeString(strings.TrimRight(token, "="))
	if err != nil || len(b) == 0 {
		return "", false
	}
	p := filepath.Clean(string(b))
	return p, filepath.IsAbs(p)
}

func withinRoot(root, p string) bool {
	root = filepath.Clean(root)
	return p == root || strings.HasPrefix(p, strings.TrimSuffix(root, string(filepath.Separator))+string(filepath.Separator))
}

// dirEntries lists a folder's subfolders and files, following links. A folder
// that can't be read lists nothing.
func dirEntries(dir string) (dirs, files []string) {
	list, err := os.ReadDir(dir)
	if err != nil {
		return nil, nil
	}
	for _, e := range list {
		full := filepath.Join(dir, e.Name())
		isDir := e.IsDir()
		if e.Type()&fs.ModeSymlink != 0 {
			info, err := os.Stat(full)
			if err != nil {
				continue
			}
			isDir = info.IsDir()
		}
		if isDir {
			dirs = append(dirs, full)
		} else {
			files = append(files, full)
		}
	}
	return dirs, files
}

// gamesInSystem lists a system folder's games: the first playable file in
// each game folder, then loose files.
func gamesInSystem(systemDir string) []string {
	core, _ := systemCore(filepath.Base(systemDir), nil)
	dirs, loose := dirEntries(systemDir)
	var roms []string
	for _, d := range dirs {
		_, files := dirEntries(d)
		if i := slices.IndexFunc(files, func(f string) bool { return libraryGameFile(core, f) }); i >= 0 {
			roms = append(roms, files[i])
		}
	}
	for _, f := range loose {
		if libraryGameFile(core, f) {
			roms = append(roms, f)
		}
	}
	return roms
}

func gameTitle(systemDir, romPath string) string {
	parent := filepath.Dir(romPath)
	if systemDir == "" || strings.EqualFold(parent, systemDir) {
		return strings.TrimSuffix(filepath.Base(romPath), filepath.Ext(romPath))
	}
	return filepath.Base(parent)
}

// systemDirOf is the folder right under root that holds romPath.
func systemDirOf(root, romPath string) string {
	root = filepath.Clean(root)
	for cur := filepath.Dir(romPath); withinRoot(root, cur) && cur != root; cur = filepath.Dir(cur) {
		if filepath.Dir(cur) == root {
			return cur
		}
	}
	return ""
}

// biosFiles lists the loose files at the top of a system folder that aren't
// games. A loose disc's .bin tracks share the extension, so those are left out.
func biosFiles(systemDir string) []M {
	_, files := dirEntries(systemDir)
	tracks := map[string]bool{}
	for _, f := range files {
		if isCueSheet(f) {
			for _, t := range cueSetFiles(f) {
				tracks[t] = true
			}
		}
	}
	out := []M{}
	for _, f := range files {
		if isRomFile(f) || tracks[f] || !isBiosFile(f) {
			continue
		}
		out = append(out, M{"id": gameToken(f), "fileName": filepath.Base(f), "sizeBytes": fileSize(f)})
	}
	return out
}

func fileSize(p string) int64 {
	if info, err := os.Stat(p); err == nil {
		return info.Size()
	}
	return 0
}

const maxCueBytes = 64 << 10

// cueSetFiles is a cue sheet followed by the tracks it lists, all from its
// own folder, or nil when a track is missing or the sheet lists none.
func cueSetFiles(cuePath string) []string {
	info, err := os.Stat(cuePath)
	if err != nil || info.Size() > maxCueBytes {
		return nil
	}
	f, err := os.Open(cuePath)
	if err != nil {
		return nil
	}
	defer f.Close()
	folder := filepath.Dir(cuePath)
	out := []string{cuePath}
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		name := cueTrackName(sc.Text())
		if name == "" {
			continue
		}
		track := findInFolder(folder, name)
		if track == "" {
			return nil
		}
		if !slices.Contains(out, track) {
			out = append(out, track)
		}
	}
	if len(out) < 2 {
		return nil
	}
	return out
}

// cueTrackName reads the file a line names, from FILE "name.bin" BINARY or an
// unquoted name, which can't hold a space.
func cueTrackName(line string) string {
	text := strings.TrimSpace(line)
	if len(text) < 5 || !strings.EqualFold(text[:5], "FILE ") {
		return ""
	}
	text = strings.TrimSpace(text[5:])
	if strings.HasPrefix(text, `"`) {
		if end := strings.Index(text[1:], `"`); end > 0 {
			return text[1 : end+1]
		}
		return ""
	}
	if space := strings.LastIndex(text, " "); space > 0 {
		return strings.TrimSpace(text[:space])
	}
	return text
}

// findInFolder matches a bare name without case, since sheets written on
// Windows often disagree with the disk about it.
func findInFolder(folder, name string) string {
	if strings.ContainsAny(name, `/\`) || name == "." || name == ".." {
		return ""
	}
	exact := filepath.Join(folder, name)
	if info, err := os.Stat(exact); err == nil && info.Mode().IsRegular() {
		return exact
	}
	_, files := dirEntries(folder)
	for _, f := range files {
		if strings.EqualFold(filepath.Base(f), name) {
			return f
		}
	}
	return ""
}

// archiveEntry is one file in a .zip, a .7z or a loose disc set.
type archiveEntry struct {
	name string
	size uint64
	open func() (io.ReadCloser, error)
}

// readArchive lists a .zip's or .7z's files, leaving folders out. The
// entries can only be opened until done is called.
func readArchive(archive string) (entries []archiveEntry, done func() error, err error) {
	if fileExt(archive) == ".7z" {
		zr, err := sevenzip.OpenReader(archive)
		if err != nil {
			return nil, nil, err
		}
		for _, f := range zr.File {
			if !f.FileInfo().IsDir() {
				entries = append(entries, archiveEntry{name: f.Name, size: f.UncompressedSize, open: f.Open})
			}
		}
		return entries, zr.Close, nil
	}
	zr, err := zip.OpenReader(archive)
	if err != nil {
		return nil, nil, err
	}
	for _, f := range zr.File {
		if !f.FileInfo().IsDir() {
			entries = append(entries, archiveEntry{name: f.Name, size: f.UncompressedSize64, open: f.Open})
		}
	}
	return entries, zr.Close, nil
}

// discEntries is a loose disc set as archive entries, so it goes out the same
// way a repacked .7z does.
func discEntries(files []string) []archiveEntry {
	out := make([]archiveEntry, 0, len(files))
	for _, f := range files {
		out = append(out, archiveEntry{name: f, size: uint64(fileSize(f)), open: func() (io.ReadCloser, error) { return os.Open(f) }})
	}
	return out
}

// storedZipLength is the exact size of the uncompressed zip writeStoredZip
// makes, or -1 when it would need zip64.
func storedZipLength(entries []archiveEntry) int64 {
	total := int64(22)
	for _, e := range entries {
		n := int64(len(entryName(e.name)))
		total += 30 + n + int64(e.size) + 16 + 46 + n
	}
	if len(entries) == 0 || len(entries) >= 0xFFFF || total >= 0xFFFFFFFF {
		return -1
	}
	return total
}

// writeStoredZip sends a disc as one stored zip, since both players already
// unpack a zip, and writes it as it goes out.
func writeStoredZip(w io.Writer, entries []archiveEntry) error {
	zw := zip.NewWriter(w)
	for _, e := range entries {
		if err := addStored(zw, e); err != nil {
			return err
		}
	}
	return zw.Close()
}

func addStored(zw *zip.Writer, e archiveEntry) error {
	src, err := e.open()
	if err != nil {
		return err
	}
	defer src.Close()
	dst, err := zw.CreateHeader(&zip.FileHeader{Name: entryName(e.name), Method: zip.Store})
	if err != nil {
		return err
	}
	_, err = io.Copy(dst, src)
	return err
}

// maxExtractedRom caps one unpacked ROM, since any signed-in user can ask for
// one and it's held in memory.
const maxExtractedRom = 512 << 20

var errRomTooLarge = errors.New("the ROM in this archive is too large to unpack")

// romEntry is the archive entry a client is sent: the first with a known ROM
// extension, else the largest file.
func romEntry(entries []archiveEntry) *archiveEntry {
	var largest *archiveEntry
	for i, e := range entries {
		if isKnownRomExt(e.name) {
			return &entries[i]
		}
		if largest == nil || e.size > largest.size {
			largest = &entries[i]
		}
	}
	return largest
}

func entryName(name string) string { return path.Base(strings.ReplaceAll(name, `\`, "/")) }

// unpackedRomInfo reads the name and size an archive's ROM is sent with from
// its index, so a HEAD unpacks nothing.
func unpackedRomInfo(archive string) (string, int64, bool) {
	entries, done, err := readArchive(archive)
	if err != nil {
		return "", 0, false
	}
	defer done()
	e := romEntry(entries)
	if e == nil {
		return "", 0, false
	}
	return entryName(e.name), int64(e.size), true
}

func unpackRom(archive string) ([]byte, error) {
	entries, done, err := readArchive(archive)
	if err != nil {
		return nil, err
	}
	defer done()
	e := romEntry(entries)
	if e == nil {
		return nil, fs.ErrNotExist
	}
	if e.size > maxExtractedRom {
		return nil, errRomTooLarge
	}
	rc, err := e.open()
	if err != nil {
		return nil, err
	}
	defer rc.Close()
	// The index can understate the size, so the copy is capped too.
	b, err := io.ReadAll(io.LimitReader(rc, maxExtractedRom+1))
	if err != nil {
		return nil, err
	}
	if len(b) > maxExtractedRom {
		return nil, errRomTooLarge
	}
	if len(b) == 0 {
		return nil, fs.ErrNotExist
	}
	return b, nil
}
