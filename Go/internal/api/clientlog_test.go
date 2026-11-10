package api

import (
	"bytes"
	"log/slog"
	"net/http/httptest"
	"regexp"
	"strings"
	"testing"
)

func postReport(s *Server, c caller, auth, body string) *httptest.ResponseRecorder {
	r := httptest.NewRequest("POST", "/Moonfin/ClientLog/Document", strings.NewReader(body))
	for k, v := range c.headers() {
		r.Header.Set(k, v)
	}
	r.Header.Set("Content-Type", "text/plain; charset=utf-8")
	if auth != "" {
		r.Header.Set("Authorization", auth)
	}
	w := httptest.NewRecorder()
	s.ServeHTTP(w, r)
	return w
}

func TestClientLogGoesToTheLog(t *testing.T) {
	var logged bytes.Buffer
	prev := slog.Default()
	slog.SetDefault(slog.New(slog.NewTextHandler(&logged, nil)))
	t.Cleanup(func() { slog.SetDefault(prev) })

	s := newTestServer()
	if _, ping := call(t, s, userMain, "GET", "/Moonfin/Ping", "", nil); ping["clientLogSupported"] != true {
		t.Fatalf("uploads are on by default: %v", ping)
	}
	if w := postReport(s, anon, "", "report"); w.Code != 401 {
		t.Fatalf("anonymous = %d", w.Code)
	}
	auth := `MediaBrowser Client="Moonfin for Android TV", Device="Shield", DeviceId="dev-1", Version="1.2.3", Token="secret-token"`
	w := postReport(s, userMain, auth, "first line\nCRASH here")
	if w.Code != 200 || !regexp.MustCompile(`^\{"FileName":"upload_Moonfin-for-Android-TV_1\.2\.3_\d{8}T\d{9}\.txt"\}$`).MatchString(strings.TrimSpace(w.Body.String())) {
		t.Fatalf("upload = %d %s", w.Code, w.Body)
	}
	out := logged.String()
	for _, want := range []string{"client log uploaded", "user=pat", "profile=Main", `"Moonfin for Android TV"`, "device=Shield", "deviceId=dev-1", `CRASH here`, "truncated=false"} {
		if !strings.Contains(out, want) {
			t.Errorf("log lacks %q: %s", want, out)
		}
	}
	if strings.Contains(out, "secret-token") {
		t.Fatal("the caller's token must never reach the log")
	}

	if w := postReport(s, userMain, "", ""); w.Code != 400 {
		t.Fatalf("empty report = %d", w.Code)
	}
	if w := postReport(s, userMain, "", strings.Repeat("x", maxClientLogBytes+1)); w.Code != 413 {
		t.Fatalf("oversized report = %d", w.Code)
	}
}

func TestClientLogUploadsCanBeTurnedOff(t *testing.T) {
	s := newTestServer()
	saveConfig(t, s, map[string]any{"EnableClientLogUpload": false})
	if _, ping := call(t, s, userMain, "GET", "/Moonfin/Ping", "", nil); ping["clientLogSupported"] != false {
		t.Fatalf("ping = %v", ping)
	}
	if w := postReport(s, userMain, "", "report"); w.Code != 503 || !strings.Contains(w.Body.String(), `"Error":"Client log upload is disabled"`) {
		t.Fatalf("disabled upload = %d %s", w.Code, w.Body)
	}
}

func TestClientLogUploadsAreCappedPerAccount(t *testing.T) {
	s := newTestServer()
	for i := range maxClientLogsAnHour {
		if w := postReport(s, userMain, "", "report"); w.Code != 200 {
			t.Fatalf("upload %d = %d", i+1, w.Code)
		}
	}
	if w := postReport(s, userKids, "", "report"); w.Code != 429 {
		t.Fatalf("another profile of the same account = %d", w.Code)
	}
	if w := postReport(s, other, "", "report"); w.Code != 200 {
		t.Fatalf("another account = %d", w.Code)
	}
}

func TestLoggedReportKeepsTheNewestWholeLines(t *testing.T) {
	if got, cut := loggedReport("short"); got != "short" || cut {
		t.Fatalf("short report = %q %v", got, cut)
	}
	long := strings.Repeat("old line é\n", maxLoggedReport/5) + "newest"
	got, cut := loggedReport(long)
	if !cut || len(got) > maxLoggedReport || !strings.HasPrefix(got, "old line é\n") || !strings.HasSuffix(got, "newest") {
		t.Fatalf("long report kept %d bytes starting %q", len(got), got[:20])
	}
	if logNamePart("  ", "client") != "client" || logNamePart("Moonfin  for/Android TV", "x") != "Moonfin-forAndroid-TV" ||
		len(logNamePart(strings.Repeat("a", 50), "x")) != 32 {
		t.Fatal("file name parts must match the Emby plugin's")
	}
}
