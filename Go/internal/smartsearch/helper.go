// Package smartsearch runs the smart search engine, the same C# code the
// Jellyfin and Emby plugins use, as a helper process. Requests and answers are
// one JSON object per line on the helper's stdin and stdout.
package smartsearch

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"os"
	"os/exec"
	"sync"
)

// Helper starts the helper on first use and again after it exits.
type Helper struct {
	path    string
	dataDir string

	mu      sync.Mutex
	stdin   io.WriteCloser
	pending map[int64]chan reply
	nextID  int64
}

type reply struct {
	OK    json.RawMessage `json:"ok"`
	Error string          `json:"error"`
}

// New returns a helper that runs the program at path with dataDir as its data
// folder. Nothing starts until the first call.
func New(path, dataDir string) *Helper {
	return &Helper{path: path, dataDir: dataDir}
}

// Available says whether the helper program is installed.
func (h *Helper) Available() bool {
	info, err := os.Stat(h.path)
	return err == nil && !info.IsDir()
}

// Call sends one request and decodes its answer into out, which may be nil.
func (h *Helper) Call(ctx context.Context, op string, args map[string]any, out any) error {
	answer := make(chan reply, 1)
	h.mu.Lock()
	if h.stdin == nil {
		if err := h.start(); err != nil {
			h.mu.Unlock()
			return err
		}
	}
	h.nextID++
	id := h.nextID
	h.pending[id] = answer
	request := map[string]any{"id": id, "op": op}
	for k, v := range args {
		request[k] = v
	}
	line, err := json.Marshal(request)
	if err == nil {
		_, err = h.stdin.Write(append(line, '\n'))
	}
	if err != nil {
		delete(h.pending, id)
		h.mu.Unlock()
		return fmt.Errorf("smart search helper: send %s: %w", op, err)
	}
	h.mu.Unlock()

	select {
	case r := <-answer:
		if r.Error != "" {
			return fmt.Errorf("smart search helper: %s: %s", op, r.Error)
		}
		if out == nil {
			return nil
		}
		return json.Unmarshal(r.OK, out)
	case <-ctx.Done():
		h.mu.Lock()
		delete(h.pending, id)
		h.mu.Unlock()
		return ctx.Err()
	}
}

// start runs the helper. The caller holds h.mu.
func (h *Helper) start() error {
	cmd := exec.Command(h.path, h.dataDir)
	stdin, err := cmd.StdinPipe()
	if err != nil {
		return err
	}
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		return err
	}
	stderr, err := cmd.StderrPipe()
	if err != nil {
		return err
	}
	if err := cmd.Start(); err != nil {
		return fmt.Errorf("start smart search helper: %w", err)
	}
	h.stdin = stdin
	h.pending = map[int64]chan reply{}
	go logLines(stderr)
	go h.read(cmd, stdin, stdout, h.pending)
	return nil
}

// read hands each answer to its caller. When the helper exits, every waiting
// call fails and the next call starts it again.
func (h *Helper) read(cmd *exec.Cmd, stdin io.WriteCloser, stdout io.Reader, pending map[int64]chan reply) {
	scanner := bufio.NewScanner(stdout)
	scanner.Buffer(make([]byte, 64<<10), 64<<20)
	for scanner.Scan() {
		var r struct {
			ID int64 `json:"id"`
			reply
		}
		if err := json.Unmarshal(scanner.Bytes(), &r); err != nil {
			slog.Error("smart search helper sent a line that isn't JSON", "error", err)
			continue
		}
		h.mu.Lock()
		answer, ok := pending[r.ID]
		delete(pending, r.ID)
		h.mu.Unlock()
		if ok {
			answer <- r.reply
		}
	}
	err := cmd.Wait()
	if err == nil {
		err = errors.New("exited")
	}
	slog.Error("smart search helper stopped", "error", err)
	h.mu.Lock()
	if h.stdin == stdin {
		h.stdin = nil
	}
	for id, answer := range pending {
		answer <- reply{Error: "the helper stopped: " + err.Error()}
		delete(pending, id)
	}
	h.mu.Unlock()
}

func logLines(r io.Reader) {
	scanner := bufio.NewScanner(r)
	for scanner.Scan() {
		slog.Warn("smart search helper", "line", scanner.Text())
	}
}
