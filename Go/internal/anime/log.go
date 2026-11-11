package anime

import (
	"bufio"
	"errors"
	"fmt"
	"io/fs"
	"log/slog"
	"os"
	"sync"
	"sync/atomic"
	"time"
)

// The log rotates to a .1 file past this size, keeping only the current file
// and one before it.
const logMaxBytes = 2 << 20

// Log records every marker request and what it was answered with, for working
// out why a badge does or doesn't appear.
type Log struct {
	path string
	mu   sync.Mutex
	// On is the admin's verbose logging setting. Writes do nothing while it's off.
	On atomic.Bool
}

func NewLog(path string) *Log { return &Log{path: path} }

func (l *Log) Path() string { return l.path }

// Size is the bytes on disk, and false when nothing has been written.
func (l *Log) Size() (int64, bool) {
	info, err := os.Stat(l.path)
	if err != nil {
		return 0, false
	}
	return info.Size(), true
}

// Printf appends one timestamped line. It never fails, since a diagnostic
// that breaks the request it describes would be worse than none.
func (l *Log) Printf(format string, args ...any) {
	if !l.On.Load() {
		return
	}
	message := fmt.Sprintf(format, args...)
	// Mirrored to the main log so the two can be lined up by time.
	slog.Info("anime markers: " + message)
	l.mu.Lock()
	defer l.mu.Unlock()
	if info, err := os.Stat(l.path); err == nil && info.Size() > logMaxBytes {
		_ = os.Rename(l.path, l.path+".1")
	}
	f, err := os.OpenFile(l.path, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o600)
	if err != nil {
		return
	}
	defer f.Close()
	fmt.Fprintf(f, "%s  %s\n", time.Now().Format("2006-01-02 15:04:05.000 -07:00"), message)
}

// Clear deletes the log so a fresh reproduction starts empty, and says whether there was one.
func (l *Log) Clear() bool {
	l.mu.Lock()
	defer l.mu.Unlock()
	removed := false
	for _, path := range []string{l.path, l.path + ".1"} {
		err := os.Remove(path)
		removed = removed || err == nil
		if err != nil && !errors.Is(err, fs.ErrNotExist) {
			slog.Warn("anime markers: clear the log", "error", err)
		}
	}
	return removed
}

// Tail is the last n lines, newest last.
func (l *Log) Tail(n int) []string {
	l.mu.Lock()
	defer l.mu.Unlock()
	lines := []string{}
	f, err := os.Open(l.path)
	if err != nil {
		return lines
	}
	defer f.Close()
	scanner := bufio.NewScanner(f)
	scanner.Buffer(make([]byte, 64<<10), 1<<20)
	for scanner.Scan() {
		lines = append(lines, scanner.Text())
		if len(lines) > n {
			lines = lines[1:]
		}
	}
	return lines
}
