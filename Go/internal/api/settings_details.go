package api

import (
	"net/http"
	"strconv"
	"strings"

	"github.com/Moonfin-Client/Plugin/Go/internal/settings"
)

// The detailsScreenBlur and detailsScreenOpacity routes read and write single
// values for clients that don't sync whole profiles.

func targetProfile(w http.ResponseWriter, r *http.Request, fromBody any) (string, bool) {
	raw := r.PathValue("profile")
	if raw == "" {
		raw, _ = fromBody.(string)
	}
	if raw == "" {
		raw = "global"
	}
	name, ok := settings.ProfileName(raw)
	if !ok {
		writeError(w, http.StatusBadRequest, "Invalid profile: "+name+". Valid profiles: global, desktop, mobile, tv")
	}
	return name, ok
}

// intValue reads a JSON number or numeric string, as the Jellyfin binder does.
func intValue(v any) (int, bool) {
	switch t := v.(type) {
	case float64:
		return int(t), t == float64(int(t))
	case string:
		n, err := strconv.Atoi(strings.TrimSpace(t))
		return n, err == nil
	}
	return 0, false
}

func (s *Server) handleGetBlur(w http.ResponseWriter, r *http.Request) {
	name, ok := targetProfile(w, r, nil)
	if !ok {
		return
	}
	resolved, err := s.resolve(r, name, false)
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	blur, ok := intValue(resolved["detailsBackdropBlur"])
	if !ok {
		blur, _ = intValue(resolved["detailsScreenBlur"])
	}
	writeJSON(w, http.StatusOK, M{"Profile": name, "DetailsScreenBlur": strconv.Itoa(min(max(blur, 0), 40))})
}

func (s *Server) handlePostBlur(w http.ResponseWriter, r *http.Request) {
	body, _ := readBody(r)
	raw := body["detailsscreenblur"]
	if raw == nil || raw == "" {
		writeError(w, http.StatusBadRequest, "detailsScreenBlur is required")
		return
	}
	name, ok := targetProfile(w, r, body["profile"])
	if !ok {
		return
	}
	n, ok := intValue(raw)
	if !ok {
		writeError(w, http.StatusBadRequest, "detailsScreenBlur must be a numeric string")
		return
	}
	n = min(max(n, 0), 40)
	clientID, _ := body["clientid"].(string)
	if clientID == "" {
		clientID = "moonfin-detailsScreenBlur-endpoint"
	}
	text := strconv.Itoa(n)
	s.saveProfile(w, r, name, M{"detailsBackdropBlur": n, "detailsScreenBlur": text}, clientID,
		M{"Profile": name, "DetailsScreenBlur": text})
}

func (s *Server) handleGetOpacity(w http.ResponseWriter, r *http.Request) {
	name, ok := targetProfile(w, r, nil)
	if !ok {
		return
	}
	resolved, err := s.resolve(r, name, false)
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	opacity, ok := intValue(resolved["detailsBackdropOpacity"])
	if !ok {
		opacity = 90
	}
	writeJSON(w, http.StatusOK, M{"Profile": name, "DetailsScreenOpacity": min(max(opacity, 0), 100)})
}

func (s *Server) handlePostOpacity(w http.ResponseWriter, r *http.Request) {
	body, _ := readBody(r)
	n, ok := intValue(body["detailsscreenopacity"])
	if !ok {
		writeError(w, http.StatusBadRequest, "detailsScreenOpacity is required")
		return
	}
	name, ok := targetProfile(w, r, body["profile"])
	if !ok {
		return
	}
	n = min(max(n, 0), 100)
	clientID, _ := body["clientid"].(string)
	if clientID == "" {
		clientID = "moonfin-detailsScreenOpacity-endpoint"
	}
	s.saveProfile(w, r, name, M{"detailsBackdropOpacity": n}, clientID,
		M{"Profile": name, "DetailsScreenOpacity": n})
}
