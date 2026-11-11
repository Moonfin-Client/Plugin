package api

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"regexp"
	"slices"
	"strconv"
	"strings"
)

// The Sonarr and Radarr download queue for one title, read straight from the
// arr servers the admin set up, and the admin action that drops a stuck
// download, blocklists its release and lets the arr grab another. Seerr's own
// download status leaves out why a download is stuck.

func (s *Server) arrQueueRoutes() {
	s.mux.HandleFunc("GET /Moonfin/Seerr/Sonarr/Queue", s.user(func(w http.ResponseWriter, r *http.Request) { s.handleArrQueue(w, r, true) }))
	s.mux.HandleFunc("GET /Moonfin/Seerr/Radarr/Queue", s.user(func(w http.ResponseWriter, r *http.Request) { s.handleArrQueue(w, r, false) }))
	s.mux.HandleFunc("DELETE /Moonfin/Seerr/Sonarr/Queue/{id}", s.admin(func(w http.ResponseWriter, r *http.Request) { s.handleArrQueueRemove(w, r, true) }))
	s.mux.HandleFunc("DELETE /Moonfin/Seerr/Radarr/Queue/{id}", s.admin(func(w http.ResponseWriter, r *http.Request) { s.handleArrQueueRemove(w, r, false) }))
}

// arrQueueOn is Ping's arrQueueAvailable.
func arrQueueOn(cfg Config) bool { return cfg.Sonarr.set() || cfg.Radarr.set() }

// arrQueueTitle is a movie or series as the arr has it.
type arrQueueTitle struct {
	ID          int   `json:"id"`
	Monitored   *bool `json:"monitored"`
	HasFile     *bool `json:"hasFile"`
	IsAvailable *bool `json:"isAvailable"`
}

// arrQueueEntry is what removal reads from each entry of the whole queue.
type arrQueueEntry struct {
	ID         int    `json:"id"`
	DownloadID string `json:"downloadId"`
}

// arrQueuePageSize holds a whole queue in one page, since arr queues are small.
const arrQueuePageSize = 1000

func arrFor(cfg Config, sonarr bool) (string, arrServer) {
	if sonarr {
		return "sonarr", cfg.Sonarr
	}
	return "radarr", cfg.Radarr
}

func (s *Server) handleArrQueue(w http.ResponseWriter, r *http.Request, sonarr bool) {
	ctx := r.Context()
	service, arr := arrFor(s.config(ctx), sonarr)
	if !arr.set() {
		writeJSON(w, http.StatusNotFound, M{"error": "No " + service + " server is configured"})
		return
	}
	idName, lookup := "tmdbId", "api/v3/movie?tmdbId="
	if sonarr {
		idName, lookup = "tvdbId", "api/v3/series?tvdbId="
	}
	raw := strings.TrimSpace(r.URL.Query().Get(idName))
	id, err := strconv.ParseInt(raw, 10, 64)
	if err != nil || id <= 0 || raw[0] == '+' {
		writeJSON(w, http.StatusBadRequest, M{"error": "A positive tmdbId (Radarr) or tvdbId (Sonarr) is required"})
		return
	}

	var titles []arrQueueTitle
	if !s.arrJSON(w, r, arr, service, http.MethodGet, lookup+strconv.FormatInt(id, 10), nil, &titles) {
		return
	}
	admin := callerOf(r).Admin
	out := M{"service": service, "inArr": false, "canRemove": admin}
	items := []M{}
	if i := slices.IndexFunc(titles, func(t arrQueueTitle) bool { return t.ID > 0 }); i >= 0 {
		title := titles[i]
		out["inArr"] = true
		out["monitored"] = title.Monitored
		if !sonarr {
			out["hasFile"] = title.HasFile
			out["isAvailable"] = title.IsAvailable
		}
		details := "api/v3/queue/details?movieId=" + strconv.Itoa(title.ID)
		if sonarr {
			details = "api/v3/queue/details?seriesId=" + strconv.Itoa(title.ID) + "&includeEpisode=true"
		}
		var records []json.RawMessage
		if !s.arrJSON(w, r, arr, service, http.MethodGet, details, nil, &records) {
			return
		}
		for _, raw := range records {
			// An entry that isn't an object is skipped, not the whole answer.
			var rec map[string]json.RawMessage
			if json.Unmarshal(raw, &rec) == nil && rec != nil {
				items = append(items, shapeQueueEntry(rec, sonarr, admin))
			}
		}
	}
	out["items"] = items
	writeJSON(w, http.StatusOK, out)
}

func (s *Server) handleArrQueueRemove(w http.ResponseWriter, r *http.Request, sonarr bool) {
	ctx := r.Context()
	service, arr := arrFor(s.config(ctx), sonarr)
	if !arr.set() {
		writeJSON(w, http.StatusNotFound, M{"error": "No " + service + " server is configured"})
		return
	}
	id, err := strconv.Atoi(r.PathValue("id"))
	if err != nil || id <= 0 {
		writeJSON(w, http.StatusBadRequest, M{"error": "A positive queue id is required"})
		return
	}
	var page struct {
		Records []arrQueueEntry `json:"records"`
	}
	if !s.arrJSON(w, r, arr, service, http.MethodGet, "api/v3/queue?page=1&pageSize="+strconv.Itoa(arrQueuePageSize), nil, &page) {
		return
	}
	if page.Records == nil {
		writeJSON(w, http.StatusBadGateway, M{"error": "The " + service + " server sent an answer that couldn't be read"})
		return
	}
	target := slices.IndexFunc(page.Records, func(e arrQueueEntry) bool { return e.ID == id })
	if target < 0 {
		writeJSON(w, http.StatusNotFound, M{"error": "That download is no longer in the " + service + " queue"})
		return
	}
	// Every entry of the download goes with it, such as the episodes of a season pack.
	ids := []int{id}
	if dl := page.Records[target].DownloadID; dl != "" {
		ids = ids[:0]
		for _, e := range page.Records {
			if e.ID > 0 && strings.EqualFold(e.DownloadID, dl) && !slices.Contains(ids, e.ID) {
				ids = append(ids, e.ID)
			}
		}
		slices.Sort(ids)
	}
	const flags = "removeFromClient=true&blocklist=true&skipRedownload=false"
	path, body := "api/v3/queue/"+strconv.Itoa(ids[0])+"?"+flags, []byte(nil)
	if len(ids) > 1 {
		path = "api/v3/queue/bulk?" + flags
		body, _ = json.Marshal(M{"ids": ids})
	}
	// A 404 means the arr no longer has the entry, which is what was asked for.
	if !s.arrJSON(w, r, arr, service, http.MethodDelete, path, body, nil, http.StatusNotFound) {
		return
	}
	writeJSON(w, http.StatusOK, M{"removed": ids})
}

// arrJSON sends one request to the arr and reads its JSON answer into out,
// which may be nil. On failure it writes the error and returns false. An arr
// error is a 502 naming the arr's status, so a 401 from the arr isn't taken for
// the caller's own sign-in failing. A status in also counts as success.
func (s *Server) arrJSON(w http.ResponseWriter, r *http.Request, arr arrServer, service, method, pathAndQuery string, body []byte, out any, also ...int) bool {
	raw, status, err := s.arrRequest(r.Context(), arr, method, pathAndQuery, body)
	if slices.Contains(also, status) {
		return true
	}
	switch {
	case err != nil:
		logError(r, "reach the "+service+" queue", err)
		writeJSON(w, http.StatusBadGateway, M{"error": "Could not reach the " + service + " server"})
		return false
	case status/100 != 2:
		writeJSON(w, http.StatusBadGateway, M{"error": fmt.Sprintf("The %s server answered %d", service, status), "arrStatus": status})
		return false
	case out != nil && (json.Unmarshal(raw, out) != nil || string(bytes.TrimSpace(raw)) == "null"):
		writeJSON(w, http.StatusBadGateway, M{"error": "The " + service + " server sent an answer that couldn't be read"})
		return false
	}
	return true
}

// arrRequest sends one request to an arr. Its errors never hold the URL or the key.
func (s *Server) arrRequest(ctx context.Context, arr arrServer, method, pathAndQuery string, body []byte) ([]byte, int, error) {
	var reader io.Reader
	if len(body) > 0 {
		reader = bytes.NewReader(body)
	}
	req, err := http.NewRequestWithContext(ctx, method, arr.URL+"/"+pathAndQuery, reader)
	if err != nil {
		return nil, 0, errors.New("bad server URL")
	}
	req.Header.Set("X-Api-Key", arr.APIKey)
	req.Header.Set("Accept", "application/json")
	if reader != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	resp, err := s.ext.http.Do(req)
	if err != nil {
		var ue *url.Error
		if errors.As(err, &ue) {
			err = ue.Err
		}
		return nil, 0, err
	}
	defer resp.Body.Close()
	raw, err := io.ReadAll(io.LimitReader(resp.Body, 32<<20))
	return raw, resp.StatusCode, err
}

// queueFields are the entry fields passed on as the arr sent them.
var queueFields = []string{
	"id", "title", "status", "trackedDownloadState", "trackedDownloadStatus", "size", "sizeleft",
	"timeleft", "estimatedCompletionTime", "added", "downloadId", "protocol", "downloadClient", "indexer",
}

// shapeQueueEntry keeps what a client needs from a queue entry, under the
// arr's own names. The download folder, and any server path inside a
// message, is kept to admins.
func shapeQueueEntry(rec map[string]json.RawMessage, sonarr, admin bool) M {
	item := M{}
	for _, name := range queueFields {
		if v, ok := rec[name]; ok && string(v) != "null" {
			item[name] = v
		}
	}
	var extra struct {
		Quality struct {
			Quality struct {
				Name string `json:"name"`
			} `json:"quality"`
		} `json:"quality"`
		ErrorMessage   string `json:"errorMessage"`
		OutputPath     string `json:"outputPath"`
		StatusMessages []struct {
			Title    string   `json:"title"`
			Messages []string `json:"messages"`
		} `json:"statusMessages"`
		EpisodeID    *int `json:"episodeId"`
		SeasonNumber *int `json:"seasonNumber"`
		Episode      *struct {
			SeasonNumber  *int `json:"seasonNumber"`
			EpisodeNumber *int `json:"episodeNumber"`
		} `json:"episode"`
	}
	// A field of an unexpected type only leaves that field unread.
	raw, _ := json.Marshal(rec)
	_ = json.Unmarshal(raw, &extra)

	redact := func(s string) string {
		if admin {
			return s
		}
		return redactPaths(s)
	}
	if extra.Quality.Quality.Name != "" {
		item["quality"] = extra.Quality.Quality.Name
	}
	if extra.ErrorMessage != "" {
		item["errorMessage"] = redact(extra.ErrorMessage)
	}
	messages := []M{}
	for _, m := range extra.StatusMessages {
		lines := []string{}
		for _, l := range m.Messages {
			if l != "" {
				lines = append(lines, redact(l))
			}
		}
		messages = append(messages, M{"title": redact(m.Title), "messages": lines})
	}
	item["statusMessages"] = messages
	if sonarr {
		if extra.EpisodeID != nil {
			item["episodeId"] = *extra.EpisodeID
		}
		season := extra.SeasonNumber
		if season == nil && extra.Episode != nil {
			season = extra.Episode.SeasonNumber
		}
		if season != nil {
			item["seasonNumber"] = *season
		}
		if extra.Episode != nil && extra.Episode.EpisodeNumber != nil {
			item["episodeNumber"] = *extra.Episode.EpisodeNumber
		}
	}
	if admin && extra.OutputPath != "" {
		item["outputPath"] = extra.OutputPath
	}
	return item
}

// absolutePath is a path inside a message: /downloads/x, C:\downloads\x or
// \\nas\share\x, at the start or after a space, quote or opening bracket.
// Folder names often hold spaces, so a path runs to the next quote or the end
// of the message, and the words after it are cut with it rather than any of
// the path being left.
var absolutePath = regexp.MustCompile(`(^|[\s'"(\[])((?:/[^\s'"/]|[A-Za-z]:\\|\\\\[^\s'"\\])[^'"]*)`)

// redactPaths cuts each absolute path in a message down to its last folder or file name.
func redactPaths(s string) string {
	return absolutePath.ReplaceAllStringFunc(s, func(m string) string {
		lead := absolutePath.FindStringSubmatch(m)[1]
		p := m[len(lead):]
		trimmed := strings.TrimRight(p, `/\.,;:)] `)
		name := trimmed
		if cut := strings.LastIndexAny(trimmed, `/\`); cut >= 0 {
			name = trimmed[cut+1:]
		}
		return lead + name + p[len(trimmed):]
	})
}
