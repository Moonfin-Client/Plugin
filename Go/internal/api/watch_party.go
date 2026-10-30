package api

import (
	"encoding/json"
	"net/http"
	"net/url"
	"regexp"
	"strconv"
	"strings"
	"time"

	"github.com/Moonfin-Client/Plugin/Go/internal/store"
)

// Silo's Watch Party rooms keep no subtitle choice and share no member
// avatars, so both are kept here by room id: the host's subtitle pick for
// the whole room, and each member's own avatar link (Silo's preset links
// are public, an upload's is signed for 15 minutes, so members post theirs
// again before it runs out). Plugins can't ask Silo who hosts a room: the
// first profile to set a room's subtitles owns them. Moonfin only lets the
// host set them and room ids are UUIDs only members are told, but a member
// calling the route directly before the host would own them instead.
const keyWatchParty = "watch-party"

const (
	watchPartyTTL      = 24 * time.Hour
	watchPartyMaxRooms = 200
	// An account may have this many rooms stored at once, so no one can
	// fill the store and keep others' rooms out.
	watchPartyRoomsPerAccount = 10
)

type watchPartyRoom struct {
	OwnerUserID    int    `json:"ownerUserId"`
	OwnerProfileID string `json:"ownerProfileId"`
	Subtitle       M      `json:"subtitle,omitempty"`
	// Avatars are members' avatar links by Silo profile id.
	Avatars   map[string]string `json:"avatars,omitempty"`
	UpdatedAt time.Time         `json:"updatedAt"`
	// CreatedBy is the account whose request first stored the room.
	CreatedBy int `json:"createdBy,omitempty"`
}

const maxRoomAvatars = 50

var roomIDPattern = regexp.MustCompile(`^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$`)

// roomID is the request's room id, or false after refusing a malformed one.
func roomID(w http.ResponseWriter, r *http.Request) (string, bool) {
	id := r.PathValue("roomId")
	if !roomIDPattern.MatchString(id) {
		writeError(w, http.StatusBadRequest, "A room id is required")
		return "", false
	}
	return id, true
}

// memberKey names a member in a room: its profile, or its account on a host
// without profiles.
func memberKey(id Identity) string {
	if id.ProfileID != "" {
		return id.ProfileID
	}
	return "u" + strconv.Itoa(id.UserID)
}

func (s *Server) watchPartyRoutes() {
	s.mux.HandleFunc("GET /Moonfin/WatchParty/{roomId}", s.user(s.handleGetWatchParty))
	s.mux.HandleFunc("PUT /Moonfin/WatchParty/{roomId}", s.user(s.handlePutWatchParty))
	s.mux.HandleFunc("PUT /Moonfin/WatchParty/{roomId}/Avatar", s.user(s.handlePutWatchPartyAvatar))
}

func (s *Server) handleGetWatchParty(w http.ResponseWriter, r *http.Request) {
	id, ok := roomID(w, r)
	if !ok {
		return
	}
	var rooms map[string]watchPartyRoom
	if _, err := s.loadJSON(r.Context(), keyWatchParty, &rooms); err != nil {
		storageFailed(w, r, err)
		return
	}
	room, ok := rooms[id]
	if !ok || time.Since(room.UpdatedAt) > watchPartyTTL {
		writeJSON(w, http.StatusOK, M{})
		return
	}
	writeJSON(w, http.StatusOK, M{"subtitle": room.Subtitle, "avatars": room.Avatars})
}

// handlePutWatchParty sets the room's subtitle choice: {"subtitle": {...}}
// for a track or {"off": true}, or {"subtitle": null} to leave subtitles to
// each member.
func (s *Server) handlePutWatchParty(w http.ResponseWriter, r *http.Request) {
	id, ok := roomID(w, r)
	if !ok {
		return
	}
	var body map[string]json.RawMessage
	var subtitle M
	err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4096)).Decode(&body)
	raw, has := body["subtitle"]
	if err == nil && has {
		err = json.Unmarshal(raw, &subtitle)
	}
	if err != nil || !has {
		writeError(w, http.StatusBadRequest, "A JSON object with subtitle is required")
		return
	}
	caller := callerOf(r)
	s.updateWatchParty(w, r, id, func(room *watchPartyRoom) (int, string) {
		if room.OwnerProfileID != "" &&
			(room.OwnerUserID != caller.UserID || room.OwnerProfileID != memberKey(caller)) {
			return http.StatusForbidden, "Only the room's host sets its subtitles"
		}
		room.OwnerUserID, room.OwnerProfileID = caller.UserID, memberKey(caller)
		room.Subtitle = subtitle
		return 0, ""
	}, M{"subtitle": subtitle})
}

// handlePutWatchPartyAvatar sets the caller's own avatar link for the room:
// {"url": "..."}, an absolute http(s) link or a path on this server.
func (s *Server) handlePutWatchPartyAvatar(w http.ResponseWriter, r *http.Request) {
	id, ok := roomID(w, r)
	if !ok {
		return
	}
	var body struct {
		URL string `json:"url"`
	}
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4096)).Decode(&body); err != nil ||
		!validAvatarLink(body.URL) {
		writeError(w, http.StatusBadRequest, "An http(s) or server-relative avatar url is required")
		return
	}
	caller := callerOf(r)
	s.updateWatchParty(w, r, id, func(room *watchPartyRoom) (int, string) {
		if room.Avatars == nil {
			room.Avatars = map[string]string{}
		}
		if _, ok := room.Avatars[memberKey(caller)]; !ok && len(room.Avatars) >= maxRoomAvatars {
			return http.StatusServiceUnavailable, "Too many members in this room"
		}
		room.Avatars[memberKey(caller)] = body.URL
		return 0, ""
	}, M{"url": body.URL})
}

func validAvatarLink(link string) bool {
	if len(link) == 0 || len(link) > 2048 {
		return false
	}
	u, err := url.Parse(link)
	if err != nil {
		return false
	}
	if u.Scheme == "" {
		return u.Host == "" && strings.HasPrefix(u.Path, "/")
	}
	return (u.Scheme == "https" || u.Scheme == "http") && u.Host != ""
}

// updateWatchParty applies change to room id under the lock, after expiring
// old rooms, and answers with reply. change returns a status and message to
// refuse with, or 0.
func (s *Server) updateWatchParty(
	w http.ResponseWriter,
	r *http.Request,
	id string,
	change func(room *watchPartyRoom) (int, string),
	reply M,
) {
	ctx := r.Context()
	caller := callerOf(r)
	s.mu.Lock()
	defer s.mu.Unlock()
	var rooms map[string]watchPartyRoom
	if _, err := s.loadJSON(ctx, keyWatchParty, &rooms); err != nil {
		storageFailed(w, r, err)
		return
	}
	if rooms == nil {
		rooms = map[string]watchPartyRoom{}
	}
	now := time.Now().UTC()
	for key, room := range rooms {
		if now.Sub(room.UpdatedAt) > watchPartyTTL {
			delete(rooms, key)
		}
	}
	room, known := rooms[id]
	if !known {
		// Any signed-in user can name a room, so new rooms are refused once
		// the store or the account's share of it is full. Live rooms are
		// never dropped to make way.
		started := 0
		for _, other := range rooms {
			if other.CreatedBy == caller.UserID {
				started++
			}
		}
		if len(rooms) >= watchPartyMaxRooms || started >= watchPartyRoomsPerAccount {
			writeError(w, http.StatusServiceUnavailable, "Too many Watch Party rooms, try again later")
			return
		}
		room.CreatedBy = caller.UserID
	}
	if status, message := change(&room); status != 0 {
		writeError(w, status, message)
		return
	}
	room.UpdatedAt = now
	rooms[id] = room
	if watchPartyBytes(rooms) > store.MaxValueBytes {
		writeError(w, http.StatusServiceUnavailable, "Watch Party storage is full, try again later")
		return
	}
	if err := s.saveJSON(ctx, keyWatchParty, rooms); err != nil {
		storageFailed(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, reply)
}

func watchPartyBytes(rooms map[string]watchPartyRoom) int {
	raw, _ := json.Marshal(rooms)
	return len(raw)
}
