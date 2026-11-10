package api

import (
	"cmp"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"regexp"
	"strings"
	"sync"
	"time"
	"unicode"
)

// Diagnostic and crash reports from the apps, taken the way the Emby
// plugin's ClientLogService takes them. Neither host gives Moonbase a log
// folder an admin can browse, so a report goes to Moonbase's own log: the
// Plex companion's container log, or the plugin's output on Silo.
const (
	maxClientLogBytes   = 1 << 20
	maxClientLogsAnHour = 10
	// maxLoggedReport keeps a report's record inside the 64 KiB line Silo
	// reads from a plugin's output at a time.
	maxLoggedReport = 32 << 10
)

// uploadBudgets counts each account's reports this hour, so a client stuck
// retrying can't flood the log.
type uploadBudgets struct {
	mu sync.Mutex
	m  map[int]uploadBudget
}

type uploadBudget struct {
	start time.Time
	count int
}

func (b *uploadBudgets) claim(userID int, now time.Time) bool {
	b.mu.Lock()
	defer b.mu.Unlock()
	if b.m == nil {
		b.m = map[int]uploadBudget{}
	}
	e := b.m[userID]
	if now.Sub(e.start) >= time.Hour {
		e = uploadBudget{start: now}
	}
	if e.count >= maxClientLogsAnHour {
		return false
	}
	e.count++
	b.m[userID] = e
	return true
}

func (s *Server) handleClientLog(w http.ResponseWriter, r *http.Request) {
	if !s.config(r.Context()).ClientLogUpload {
		writeError(w, http.StatusServiceUnavailable, "Client log upload is disabled")
		return
	}
	caller := callerOf(r)
	if !s.logUploads.claim(caller.UserID, time.Now()) {
		writeError(w, http.StatusTooManyRequests, "Too many reports uploaded recently. Try again later.")
		return
	}
	body, err := io.ReadAll(http.MaxBytesReader(w, r.Body, maxClientLogBytes))
	var tooBig *http.MaxBytesError
	if errors.As(err, &tooBig) {
		writeError(w, http.StatusRequestEntityTooLarge, fmt.Sprintf("Report is larger than %d bytes", maxClientLogBytes))
		return
	}
	if err != nil || len(body) == 0 {
		writeError(w, http.StatusBadRequest, "No body provided.")
		return
	}
	client, device, deviceID, version := clientInfo(r)
	name := clientLogName(client, version, time.Now().UTC())
	report, cut := loggedReport(string(body))
	slog.InfoContext(r.Context(), "client log uploaded", "fileName", name, "user", caller.UserName, "userId", caller.UserID,
		"profile", caller.ProfileName, "client", client, "device", device, "deviceId", deviceID, "version", version,
		"userAgent", r.UserAgent(), "bytes", len(body), "truncated", cut, "report", report)
	writeJSON(w, http.StatusOK, M{"FileName": name})
}

var authField = regexp.MustCompile(`(\w+)="([^"]*)"`)

// clientInfo reads the sender from the MediaBrowser header the apps sign
// Moonfin calls with, or else from Plex's client headers. The token in that
// header is never read here.
func clientInfo(r *http.Request) (client, device, deviceID, version string) {
	fields := map[string]string{}
	for _, m := range authField.FindAllStringSubmatch(r.Header.Get("Authorization"), -1) {
		fields[strings.ToLower(m[1])] = m[2]
	}
	plex := func(key string) string { return cmp.Or(r.Header.Get(key), r.URL.Query().Get(key)) }
	return cmp.Or(fields["client"], plex("X-Plex-Product")), cmp.Or(fields["device"], plex("X-Plex-Device-Name")),
		cmp.Or(fields["deviceid"], plex("X-Plex-Client-Identifier")), cmp.Or(fields["version"], plex("X-Plex-Version"))
}

// clientLogName is the file name the Emby plugin would give the report, so
// it can be found in the log and matches what the app shows.
func clientLogName(client, version string, now time.Time) string {
	stamp := strings.Replace(now.Format("20060102T150405.000"), ".", "", 1)
	return "upload_" + logNamePart(client, "client") + "_" + logNamePart(version, "0") + "_" + stamp + ".txt"
}

// logNamePart keeps the characters safe in a file name, at most 32, with
// spaces as dashes so "Moonfin for Android TV" stays readable.
func logNamePart(v, fallback string) string {
	var b strings.Builder
	for _, c := range v {
		switch {
		case c >= 'a' && c <= 'z', c >= 'A' && c <= 'Z', c >= '0' && c <= '9', c == '.', c == '-', c == '_':
			b.WriteRune(c)
		case unicode.IsSpace(c) && b.Len() > 0 && !strings.HasSuffix(b.String(), "-"):
			b.WriteByte('-')
		}
		if b.Len() >= 32 {
			break
		}
	}
	return cmp.Or(strings.Trim(b.String(), "-"), fallback)
}

// loggedReport is the end of the report, where the newest lines are, cut at
// a line start when it's too long for one record.
func loggedReport(doc string) (string, bool) {
	if len(doc) <= maxLoggedReport {
		return doc, false
	}
	tail := doc[len(doc)-maxLoggedReport:]
	if i := strings.IndexByte(tail, '\n'); i >= 0 {
		tail = tail[i+1:]
	}
	return strings.ToValidUTF8(tail, ""), true
}
