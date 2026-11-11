package api

import (
	"archive/zip"
	"io"
	"net/http"
	"os"
	"path"
	"path/filepath"
	"strings"
	"time"
)

// The EmulatorJS player page, and the cores an admin can upload so games play
// without the CDN.

const emulatorJSCDN = "https://cdn.emulatorjs.org/stable/data/"

func (g *games) coreDataDir() string { return filepath.Join(g.dir, "emulatorjs", "data") }

// coresInstalled needs the files every core loads first, so a lone loader.js
// falls back to the CDN instead of a mix of local misses.
func coresInstalled(dir string) bool {
	for _, f := range []string{"loader.js", "emulator.min.js", "emulator.min.css"} {
		if _, err := os.Stat(filepath.Join(dir, f)); err != nil {
			return false
		}
	}
	return true
}

func (g *games) handlePlayer(w http.ResponseWriter, r *http.Request) {
	page, err := os.ReadFile(filepath.Join(g.host.EmulatorJSDir(), "player.html"))
	if err != nil {
		writeError(w, http.StatusNotFound, "player.html not found")
		return
	}
	data := emulatorJSCDN
	if coresInstalled(g.coreDataDir()) {
		data = "./data/"
	}
	// PSP's core needs SharedArrayBuffer, which only a cross-origin isolated
	// page gets. Other cores keep loading normally, from the CDN too.
	if r.URL.Query().Get("core") == "psp" {
		w.Header().Set("Cross-Origin-Opener-Policy", "same-origin")
		w.Header().Set("Cross-Origin-Embedder-Policy", "credentialless")
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	_, _ = io.WriteString(w, strings.ReplaceAll(string(page), "__EJS_PATHTODATA__", data))
}

func (g *games) handleBridge(w http.ResponseWriter, r *http.Request) {
	b, err := os.ReadFile(filepath.Join(g.host.EmulatorJSDir(), "moonfin-bridge.js"))
	if err != nil {
		writeError(w, http.StatusNotFound, "moonfin-bridge.js not found")
		return
	}
	w.Header().Set("Content-Type", "application/javascript")
	_, _ = w.Write(b)
}

func (g *games) handleCoreData(w http.ResponseWriter, r *http.Request) {
	root, err := os.OpenRoot(g.coreDataDir())
	if err != nil {
		writeError(w, http.StatusNotFound, "Self-hosted EmulatorJS data folder not installed.")
		return
	}
	defer root.Close()
	name := r.PathValue("path")
	if name == "" {
		name = "loader.js"
	}
	f, err := root.Open(name)
	if err != nil {
		http.NotFound(w, r)
		return
	}
	defer f.Close()
	info, err := f.Stat()
	if err != nil || info.IsDir() {
		http.NotFound(w, r)
		return
	}
	http.ServeContent(w, r, name, info.ModTime(), f)
}

func (g *games) handleCoresStatus(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusOK, g.coresStatus(0))
}

func (g *games) coresStatus(files int) M {
	installed := coresInstalled(g.coreDataDir())
	out := M{"installed": installed, "downloading": false, "state": "idle", "filesInstalled": files}
	if installed {
		out["state"] = "installed"
	}
	if failed := g.coresErr.Load(); failed != nil && *failed != "" {
		out["state"], out["error"] = "failed", *failed
	}
	return out
}

// handleCoresUpload installs an uploaded EmulatorJS data folder zip, about
// 290 MB, before it answers, so the page can show the result.
func (g *games) handleCoresUpload(w http.ResponseWriter, r *http.Request) {
	// The server's read timeout is far too short for an upload this size.
	_ = http.NewResponseController(w).SetReadDeadline(time.Time{})
	g.cores.Lock()
	defer g.cores.Unlock()
	if err := os.MkdirAll(g.dir, 0o700); err != nil {
		storageFailed(w, r, err)
		return
	}
	tmp, err := os.CreateTemp(g.dir, "cores-*.zip")
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	defer os.Remove(tmp.Name())
	n, err := io.Copy(tmp, r.Body)
	if cerr := tmp.Close(); err == nil {
		err = cerr
	}
	if err != nil {
		writeError(w, http.StatusBadRequest, "The upload didn't arrive whole.")
		return
	}
	if n == 0 {
		writeError(w, http.StatusBadRequest, "Empty upload.")
		return
	}
	files, err := installCores(tmp.Name(), g.coreDataDir())
	failed := ""
	switch {
	case err != nil:
		// Anyone signed in can read the status, so the error, which can name server paths, only goes to the log.
		logError(r, "install EmulatorJS cores", err)
		failed = "The cores zip couldn't be installed. The Moonbase log has the reason."
	case files == 0 || !coresInstalled(g.coreDataDir()):
		failed = "The cores zip didn't contain a complete EmulatorJS data folder."
	}
	g.coresErr.Store(&failed)
	writeJSON(w, http.StatusAccepted, g.coresStatus(files))
}

// installCores unpacks the zip's EmulatorJS data folder, found by its
// loader.js wherever the zip nests it, and says how many files it wrote.
func installCores(archive, dataDir string) (int, error) {
	zr, err := zip.OpenReader(archive)
	if err != nil {
		return 0, err
	}
	defer zr.Close()
	prefix, found := "", false
	for _, f := range zr.File {
		name := strings.ReplaceAll(f.Name, `\`, "/")
		if strings.EqualFold(path.Base(name), "loader.js") && !f.FileInfo().IsDir() {
			prefix, found = name[:len(name)-len("loader.js")], true
			break
		}
	}
	if !found {
		return 0, nil
	}
	count := 0
	for _, f := range zr.File {
		name := strings.ReplaceAll(f.Name, `\`, "/")
		if f.FileInfo().IsDir() || len(name) <= len(prefix) || !strings.EqualFold(name[:len(prefix)], prefix) {
			continue
		}
		rel := filepath.FromSlash(name[len(prefix):])
		if !filepath.IsLocal(rel) {
			continue
		}
		if err := extractTo(f, filepath.Join(dataDir, rel)); err != nil {
			return count, err
		}
		count++
	}
	return count, nil
}

func extractTo(f *zip.File, dst string) error {
	if err := os.MkdirAll(filepath.Dir(dst), 0o755); err != nil {
		return err
	}
	src, err := f.Open()
	if err != nil {
		return err
	}
	defer src.Close()
	out, err := os.Create(dst)
	if err != nil {
		return err
	}
	_, err = io.Copy(out, src)
	if cerr := out.Close(); err == nil {
		err = cerr
	}
	return err
}
