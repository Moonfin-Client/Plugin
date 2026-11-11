package api

import (
	"context"
	"fmt"
	"log/slog"
	"math"
	"net/http"
	"regexp"
	"slices"
	"strconv"
	"strings"
	"time"
	"unicode/utf8"
)

// The calendar's Remind me button. A reminder fires once, when its episode or
// movie lands in the library, through the settings stream and a phone push,
// the same as the Jellyfin plugin's.

const keyCalendarReminders = "calendar-reminders"

const (
	maxRemindersPerUser = 500
	tooManyReminders    = "A user can keep up to 500 reminders"
	reminderMaxAge      = 365 * 24 * time.Hour
	maxReminderText     = 500
	reminderPoll        = 2 * time.Minute
	// An addition still counts for a day, so one added before the server knew
	// what it was, or while Moonbase was down, still fires.
	reminderWindow = 24 * time.Hour
)

// reminderNow is the reminders' clock, which tests move.
var reminderNow = time.Now

// ReminderCatalog is what a host gives calendar reminders. Hosts without it
// keep reminders off.
type ReminderCatalog interface {
	// RecentArrivals lists the movies and episodes added to any library from
	// since on.
	RecentArrivals(ctx context.Context, since time.Time) ([]Arrival, error)
	// CanSee reports whether the account can open the item.
	CanSee(ctx context.Context, userID int, itemID string) (bool, error)
}

// Arrival is a movie or episode in the library. An episode's ids are its
// series' ids.
type Arrival struct {
	ItemID                 string
	IsEpisode              bool
	TvdbID, TmdbID, ImdbID string
	Season, Episode        *int
}

type calendarReminder struct {
	Key  string `json:"key"`
	Kind string `json:"kind"`
	// TvdbID is the series' for an episode.
	TvdbID        *int   `json:"tvdbId,omitempty"`
	TmdbID        *int   `json:"tmdbId,omitempty"`
	ImdbID        string `json:"imdbId,omitempty"`
	SeasonNumber  *int   `json:"seasonNumber,omitempty"`
	EpisodeNumber *int   `json:"episodeNumber,omitempty"`
	// Title is the series or movie name, and Subtitle the episode's.
	Title     string    `json:"title"`
	Subtitle  string    `json:"subtitle,omitempty"`
	CreatedAt time.Time `json:"createdAt"`
}

var imdbPattern = regexp.MustCompile(`^tt\d+$`)

// normalizeReminder turns a body, with the keys readBody lowercased, into the
// reminder as it's stored, or says why it can't be stored.
func normalizeReminder(in M) (calendarReminder, string) {
	if in == nil {
		return calendarReminder{}, "A reminder body is required"
	}
	kind := strings.ToLower(strings.TrimSpace(str(in["kind"])))
	imdb := strings.ToLower(strings.TrimSpace(str(in["imdbid"])))
	title := strings.TrimSpace(str(in["title"]))
	subtitle := strings.TrimSpace(str(in["subtitle"]))
	switch {
	case imdb != "" && !imdbPattern.MatchString(imdb):
		return calendarReminder{}, "imdbId must look like tt1234567"
	case title == "":
		return calendarReminder{}, "title is required"
	case utf8.RuneCountInString(title) > maxReminderText || utf8.RuneCountInString(subtitle) > maxReminderText:
		return calendarReminder{}, "title and subtitle are limited to 500 characters"
	}
	r := calendarReminder{Kind: kind, TmdbID: positiveInt(in["tmdbid"]), ImdbID: imdb, Title: title}
	if kind == "episode" {
		r.TvdbID = positiveInt(in["tvdbid"])
		r.SeasonNumber, r.EpisodeNumber = wholeInt(in["seasonnumber"]), wholeInt(in["episodenumber"])
		r.Subtitle = subtitle
	}
	if r.Key = reminderKey(r); r.Key == "" {
		return calendarReminder{}, "An episode reminder needs tvdbId, seasonNumber and episodeNumber, and a movie reminder needs tmdbId or imdbId"
	}
	return r, ""
}

// reminderKey is the key the app computes too, or empty when the reminder
// can't have one.
func reminderKey(r calendarReminder) string {
	switch r.Kind {
	case "episode":
		if r.TvdbID != nil && r.SeasonNumber != nil && *r.SeasonNumber >= 0 && r.EpisodeNumber != nil && *r.EpisodeNumber >= 0 {
			return fmt.Sprintf("episode:%d:%d:%d", *r.TvdbID, *r.SeasonNumber, *r.EpisodeNumber)
		}
	case "movie":
		if r.TmdbID != nil {
			return fmt.Sprintf("movie:%d", *r.TmdbID)
		}
		if r.ImdbID != "" {
			return "movie:imdb:" + r.ImdbID
		}
	}
	return ""
}

// wholeInt reads a whole number sent as a number or a string, as the Jellyfin
// plugin does, or nil.
func wholeInt(v any) *int {
	f, ok := num(v)
	if !ok || f != math.Trunc(f) || math.Abs(f) > math.MaxInt32 {
		return nil
	}
	n := int(f)
	return &n
}

func positiveInt(v any) *int {
	if n := wholeInt(v); n != nil && *n > 0 {
		return n
	}
	return nil
}

// reminderMatches reports whether the arrival is what the reminder waits
// for. An episode matches on any of its series' ids.
func reminderMatches(r calendarReminder, a Arrival) bool {
	if (r.Kind == "episode") != a.IsEpisode {
		return false
	}
	same := sameID(r.TvdbID, a.TvdbID) || sameID(r.TmdbID, a.TmdbID) || (r.ImdbID != "" && strings.EqualFold(r.ImdbID, a.ImdbID))
	if !same || !a.IsEpisode {
		return same
	}
	return sameInt(r.SeasonNumber, a.Season) && sameInt(r.EpisodeNumber, a.Episode)
}

func sameID(id *int, other string) bool { return id != nil && *id > 0 && strconv.Itoa(*id) == other }

func sameInt(a, b *int) bool { return a != nil && b != nil && *a == *b }

func reminderMessage(r calendarReminder) (title, body string) {
	title = r.Title + " is in your library"
	if r.Kind != "episode" {
		return title, "Ready to watch"
	}
	body = fmt.Sprintf("S%dE%d", *r.SeasonNumber, *r.EpisodeNumber)
	if r.Subtitle != "" {
		body += " " + r.Subtitle
	}
	return title, body
}

// loadReminders reads every account's reminders, leaving out any older than
// a year.
func (s *Server) loadReminders(ctx context.Context) (map[string][]calendarReminder, error) {
	var all map[string][]calendarReminder
	if _, err := s.loadJSON(ctx, keyCalendarReminders, &all); err != nil {
		return nil, err
	}
	cutoff := reminderNow().Add(-reminderMaxAge)
	for account, list := range all {
		all[account] = slices.DeleteFunc(list, func(r calendarReminder) bool { return r.Key == "" || !r.CreatedAt.After(cutoff) })
	}
	return all, nil
}

// upsertReminder stores r under its key, keeping the first createdAt. A
// refusal says why it wasn't stored.
func (s *Server) upsertReminder(ctx context.Context, userID int, r calendarReminder) (stored calendarReminder, refusal string, err error) {
	s.remindersMu.Lock()
	defer s.remindersMu.Unlock()
	all, err := s.loadReminders(ctx)
	if err != nil {
		return r, "", err
	}
	if all == nil {
		all = map[string][]calendarReminder{}
	}
	account := strconv.Itoa(userID)
	list := all[account]
	r.CreatedAt = reminderNow().UTC()
	switch i := slices.IndexFunc(list, func(o calendarReminder) bool { return o.Key == r.Key }); {
	case i >= 0:
		r.CreatedAt = list[i].CreatedAt
		list[i] = r
	case len(list) >= maxRemindersPerUser:
		return r, tooManyReminders, nil
	default:
		list = append(list, r)
	}
	all[account] = list
	if fits, err := s.saveWithin(ctx, keyCalendarReminders, all); err != nil || !fits {
		return r, "Moonbase can't keep any more reminders", err
	}
	return r, "", nil
}

// removeReminder reports whether the reminder was there, so only the check
// that removes it fires it.
func (s *Server) removeReminder(ctx context.Context, userID int, key string) (bool, error) {
	s.remindersMu.Lock()
	defer s.remindersMu.Unlock()
	all, err := s.loadReminders(ctx)
	if err != nil {
		return false, err
	}
	account := strconv.Itoa(userID)
	i := slices.IndexFunc(all[account], func(r calendarReminder) bool { return r.Key == key })
	if i < 0 {
		return false, nil
	}
	if all[account] = slices.Delete(all[account], i, i+1); len(all[account]) == 0 {
		delete(all, account)
	}
	return true, s.saveJSON(ctx, keyCalendarReminders, all)
}

func (s *Server) remindersOn() bool {
	_, ok := s.catalog.(ReminderCatalog)
	return ok
}

func (s *Server) reminderRoutes() {
	if !s.remindersOn() {
		return
	}
	s.mux.HandleFunc("GET /Moonfin/Calendar/Reminders", s.user(s.handleListReminders))
	s.mux.HandleFunc("POST /Moonfin/Calendar/Reminders", s.user(s.handleSaveReminder))
	s.mux.HandleFunc("DELETE /Moonfin/Calendar/Reminders/{key}", s.user(s.handleDeleteReminder))
}

func (s *Server) handleListReminders(w http.ResponseWriter, r *http.Request) {
	all, err := s.loadReminders(r.Context())
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	list := all[strconv.Itoa(callerOf(r).UserID)]
	if list == nil {
		list = []calendarReminder{}
	}
	writeJSON(w, http.StatusOK, M{"reminders": list})
}

func (s *Server) handleSaveReminder(w http.ResponseWriter, r *http.Request) {
	r.Body = http.MaxBytesReader(w, r.Body, 16<<10)
	in, _ := readBody(r)
	reminder, refusal := normalizeReminder(in)
	if refusal != "" {
		writeJSON(w, http.StatusBadRequest, M{"error": refusal})
		return
	}
	stored, refusal, err := s.upsertReminder(r.Context(), callerOf(r).UserID, reminder)
	switch {
	case err != nil:
		storageFailed(w, r, err)
	case refusal != "":
		writeJSON(w, http.StatusBadRequest, M{"error": refusal})
	default:
		writeJSON(w, http.StatusOK, stored)
	}
}

func (s *Server) handleDeleteReminder(w http.ResponseWriter, r *http.Request) {
	if _, err := s.removeReminder(r.Context(), callerOf(r).UserID, r.PathValue("key")); err != nil {
		storageFailed(w, r, err)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

func (s *Server) watchReminders(catalog ReminderCatalog) {
	for range time.Tick(reminderPoll) {
		s.fireReminders(context.Background(), catalog)
	}
}

// fireReminders tells each account about the reminders of theirs that arrived
// in a library they can see, and forgets those reminders. A reminder ignores
// the account's other notification choices, since they asked for it.
func (s *Server) fireReminders(ctx context.Context, catalog ReminderCatalog) {
	all, err := s.loadReminders(ctx)
	if err != nil {
		slog.WarnContext(ctx, "calendar: read the reminders", "error", err)
		return
	}
	if len(all) == 0 {
		return
	}
	arrivals, err := catalog.RecentArrivals(ctx, reminderNow().Add(-reminderWindow))
	if err != nil {
		slog.WarnContext(ctx, "calendar: read what arrived in the library", "error", err)
		return
	}
	for _, a := range arrivals {
		for account, list := range all {
			userID, _ := strconv.Atoi(account)
			for _, r := range list {
				if !reminderMatches(r, a) {
					continue
				}
				if ok, err := catalog.CanSee(ctx, userID, a.ItemID); err != nil || !ok {
					if err != nil {
						slog.WarnContext(ctx, "calendar: check who can see an item", "item", a.ItemID, "error", err)
					}
					continue
				}
				if removed, err := s.removeReminder(ctx, userID, r.Key); err != nil || !removed {
					continue
				}
				title, body := reminderMessage(r)
				route := "/item/" + a.ItemID
				s.notifyUser(userID, M{"type": "seerrNotification", "title": title, "body": body, "route": route})
				s.pushToUser(ctx, userID, title, body, route)
				slog.InfoContext(ctx, "calendar: reminder fired", "key", r.Key, "user", userID)
			}
		}
	}
}
