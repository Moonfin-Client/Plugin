package api

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"net/http"
	"net/url"
	"slices"
	"strconv"
	"strings"
	"time"
)

// Server messages from the admin, ported from the Jellyfin plugin's
// ServerMessage. Field names stay PascalCase as that plugin sends them.
// TargetUserIds hold Silo account ids.
type serverMessage struct {
	ID              string     `json:"Id"`
	Title           string     `json:"Title"`
	Body            string     `json:"Body"`
	Color           string     `json:"Color"`
	Delivery        string     `json:"Delivery"`
	ActionLabel     string     `json:"ActionLabel,omitempty"`
	ActionURL       string     `json:"ActionUrl,omitempty"`
	StartUtc        *time.Time `json:"StartUtc,omitempty"`
	EndUtc          *time.Time `json:"EndUtc,omitempty"`
	Audience        string     `json:"Audience"`
	TargetUserIds   []string   `json:"TargetUserIds"`
	CreatedUtc      time.Time  `json:"CreatedUtc"`
	CreatedByUserID string     `json:"CreatedByUserId,omitempty"`
}

const (
	keyMessages     = "messages"
	maxMessages     = 50
	maxMessageTitle = 120
	maxMessageBody  = 2000
)

func (s *Server) messageRoutes() {
	m := s.mux
	m.HandleFunc("GET /Moonfin/Messages", s.sync(s.handleGetMessages))
	m.HandleFunc("GET /Moonfin/Admin/Messages", s.admin(s.handleAdminMessages))
	m.HandleFunc("POST /Moonfin/Admin/Messages", s.admin(s.handlePostMessage))
	m.HandleFunc("POST /Moonfin/Admin/Messages/Order", s.admin(s.handleOrderMessages))
	m.HandleFunc("DELETE /Moonfin/Admin/Messages/{messageId}", s.admin(s.handleDeleteMessage))
	m.HandleFunc("POST /Moonfin/Broadcast", s.admin(s.handleBroadcast))
}

func truncate(s string, n int) string {
	if r := []rune(s); len(r) > n {
		return string(r[:n])
	}
	return s
}

func (m *serverMessage) sanitize() {
	m.Title = truncate(strings.TrimSpace(m.Title), maxMessageTitle)
	m.Body = truncate(strings.TrimSpace(m.Body), maxMessageBody)
	if !slices.Contains([]string{"green", "red", "yellow", "blue"}, m.Color) {
		m.Color = "white"
	}
	if m.Delivery != "popup" {
		m.Delivery = "inbox"
	}
	if m.Audience != "users" && m.Audience != "admins" {
		m.Audience = "all"
	}
	targets := []string{}
	if m.Audience == "users" {
		for _, raw := range m.TargetUserIds {
			n, err := strconv.Atoi(strings.TrimSpace(raw))
			if id := strconv.Itoa(n); err == nil && n > 0 && !slices.Contains(targets, id) {
				targets = append(targets, id)
			}
		}
	}
	m.TargetUserIds = targets
	m.ActionLabel = strings.TrimSpace(m.ActionLabel)
	link := strings.Trim(strings.TrimSpace(m.ActionURL), `"'`)
	if u, err := url.Parse(strings.TrimSpace(link)); err != nil || (u.Scheme != "http" && u.Scheme != "https") || u.Host == "" {
		m.ActionURL = ""
	} else {
		m.ActionURL = u.String()
	}
	// A link needs a label and a label needs a link.
	if m.ActionURL == "" || m.ActionLabel == "" {
		m.ActionURL, m.ActionLabel = "", ""
	}
	// An end before the start would hide the message forever.
	if m.StartUtc != nil && m.EndUtc != nil && !m.EndUtc.After(*m.StartUtc) {
		m.EndUtc = nil
	}
}

func (m serverMessage) visibleTo(id Identity, now time.Time) bool {
	if (m.StartUtc != nil && now.Before(*m.StartUtc)) || (m.EndUtc != nil && !now.Before(*m.EndUtc)) {
		return false
	}
	switch m.Audience {
	case "admins":
		return id.Admin
	case "users":
		return slices.Contains(m.TargetUserIds, strconv.Itoa(id.UserID))
	}
	return true
}

// pruneMessages drops expired messages, then the oldest beyond the cap.
func pruneMessages(list []serverMessage) []serverMessage {
	now := time.Now()
	list = slices.DeleteFunc(list, func(m serverMessage) bool { return m.EndUtc != nil && m.EndUtc.Before(now) })
	for len(list) > maxMessages {
		oldest := 0
		for i, m := range list {
			if m.CreatedUtc.Before(list[oldest].CreatedUtc) {
				oldest = i
			}
		}
		list = slices.Delete(list, oldest, oldest+1)
	}
	return list
}

func (s *Server) loadMessages(r *http.Request) ([]serverMessage, error) {
	list := []serverMessage{}
	_, err := s.loadJSON(r.Context(), keyMessages, &list)
	return list, err
}

func (s *Server) handleGetMessages(w http.ResponseWriter, r *http.Request) {
	list, err := s.loadMessages(r)
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	id, now := callerOf(r), time.Now()
	visible := []serverMessage{}
	for _, m := range list {
		if m.visibleTo(id, now) {
			visible = append(visible, m)
		}
	}
	writeJSON(w, http.StatusOK, M{"items": visible})
}

func (s *Server) handleAdminMessages(w http.ResponseWriter, r *http.Request) {
	list, err := s.loadMessages(r)
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, M{"items": list})
}

// editMessages runs change on the stored list under the lock and stores the
// result. change answers an HTTP status and body, and only a 200 is stored.
func (s *Server) editMessages(w http.ResponseWriter, r *http.Request, change func([]serverMessage) ([]serverMessage, int, M)) {
	s.mu.Lock()
	defer s.mu.Unlock()
	list, err := s.loadMessages(r)
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	list, status, body := change(list)
	if status == http.StatusOK {
		if err := s.saveJSON(r.Context(), keyMessages, pruneMessages(list)); err != nil {
			storageFailed(w, r, err)
			return
		}
	}
	writeJSON(w, status, body)
}

func newMessageID() string {
	b := make([]byte, 16)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}

func (s *Server) handlePostMessage(w http.ResponseWriter, r *http.Request) {
	var msg serverMessage
	if err := json.NewDecoder(r.Body).Decode(&msg); err != nil {
		writeJSON(w, http.StatusBadRequest, M{"error": "A message is required"})
		return
	}
	msg.sanitize()
	if msg.Title == "" && msg.Body == "" {
		writeJSON(w, http.StatusBadRequest, M{"error": "A message needs a title or a body"})
		return
	}
	s.editMessages(w, r, func(list []serverMessage) ([]serverMessage, int, M) {
		if i := slices.IndexFunc(list, func(m serverMessage) bool { return msg.ID != "" && strings.EqualFold(m.ID, msg.ID) }); i >= 0 {
			msg.CreatedUtc, msg.CreatedByUserID = list[i].CreatedUtc, list[i].CreatedByUserID
			list[i] = msg
		} else {
			msg.ID, msg.CreatedUtc = newMessageID(), time.Now().UTC()
			msg.CreatedByUserID = strconv.Itoa(callerOf(r).UserID)
			list = append([]serverMessage{msg}, list...)
		}
		return list, http.StatusOK, M{"success": true, "item": msg}
	})
}

func (s *Server) handleOrderMessages(w http.ResponseWriter, r *http.Request) {
	body, err := readBody(r)
	raw, _ := body["ids"].([]any)
	if err != nil || len(raw) == 0 {
		writeJSON(w, http.StatusBadRequest, M{"error": "Ids are required"})
		return
	}
	s.editMessages(w, r, func(list []serverMessage) ([]serverMessage, int, M) {
		ordered := make([]serverMessage, 0, len(list))
		for _, v := range raw {
			id, _ := v.(string)
			if i := slices.IndexFunc(list, func(m serverMessage) bool { return id != "" && strings.EqualFold(m.ID, id) }); i >= 0 {
				ordered = append(ordered, list[i])
				list = slices.Delete(list, i, i+1)
			}
		}
		// Messages the list left out keep their order, so a stale list can't delete.
		return append(ordered, list...), http.StatusOK, M{"success": true}
	})
}

func (s *Server) handleDeleteMessage(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("messageId")
	s.editMessages(w, r, func(list []serverMessage) ([]serverMessage, int, M) {
		before := len(list)
		list = slices.DeleteFunc(list, func(m serverMessage) bool { return strings.EqualFold(m.ID, id) })
		if len(list) == before {
			return list, http.StatusNotFound, M{"error": "Message not found"}
		}
		return list, http.StatusOK, M{"success": true}
	})
}

// handleBroadcast stores a popup message for everyone. The Jellyfin plugin
// also pushes it live over the settings stream, which Silo can't carry, so
// apps see it the next time they load messages.
func (s *Server) handleBroadcast(w http.ResponseWriter, r *http.Request) {
	if !s.config(r.Context()).EnableSettingsSync {
		writeError(w, http.StatusServiceUnavailable, "Settings sync is disabled")
		return
	}
	body, _ := readBody(r)
	text, _ := body["message"].(string)
	if text = strings.TrimSpace(text); text == "" {
		writeError(w, http.StatusBadRequest, "message is required")
		return
	}
	msg := serverMessage{Body: text, Delivery: "popup"}
	msg.sanitize()
	s.editMessages(w, r, func(list []serverMessage) ([]serverMessage, int, M) {
		msg.ID, msg.CreatedUtc = newMessageID(), time.Now().UTC()
		msg.CreatedByUserID = strconv.Itoa(callerOf(r).UserID)
		return append([]serverMessage{msg}, list...), http.StatusOK, M{"Success": true, "Deliveries": 0, "PushTargets": 0}
	})
}
