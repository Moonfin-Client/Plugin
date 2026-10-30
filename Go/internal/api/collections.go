package api

import (
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"strings"
	"unicode"
)

// Each user's own order of a collection's items, so a drag-and-drop order
// follows them across devices. Item ids stay opaque strings. A profile's
// orders share one key with a size cap, since on Silo that key sits in
// storage every profile shares.

const (
	maxOrderItems  = 5000
	maxOrderItemID = 64
	// About 2,800 Silo or Plex item ids across all of a profile's orders.
	maxOrdersBytes = 64 << 10
)

func (s *Server) collectionRoutes() {
	s.mux.HandleFunc("GET /Moonfin/Collections/{collectionId}/Order", s.user(s.handleGetCollectionOrder))
	s.mux.HandleFunc("POST /Moonfin/Collections/{collectionId}/Order", s.user(s.handleSaveCollectionOrder))
}

// ordersKey holds a profile's orders, by collection id.
func ordersKey(id Identity) string {
	return "o/" + settingsKey(id)
}

// collectionKey keeps only the ASCII letters and digits of the collection id,
// which covers Plex's numeric ids and Silo's ids.
func collectionKey(collectionID string) (string, bool) {
	safe := strings.Map(func(r rune) rune {
		if r < unicode.MaxASCII+1 && (unicode.IsLetter(r) || unicode.IsDigit(r)) {
			return r
		}
		return -1
	}, collectionID)
	if safe == "" {
		return "", false
	}
	if len(safe) > 128 {
		safe = safe[:128]
	}
	return safe, true
}

func (s *Server) handleGetCollectionOrder(w http.ResponseWriter, r *http.Request) {
	order := []string{}
	if collection, ok := collectionKey(r.PathValue("collectionId")); ok {
		var orders map[string][]string
		if _, err := s.loadJSON(r.Context(), ordersKey(callerOf(r)), &orders); err != nil {
			storageFailed(w, r, err)
			return
		}
		if saved, ok := orders[collection]; ok {
			order = saved
		}
	}
	writeJSON(w, http.StatusOK, order)
}

// handleSaveCollectionOrder stores the order. An empty list clears it.
func (s *Server) handleSaveCollectionOrder(w http.ResponseWriter, r *http.Request) {
	collection, ok := collectionKey(r.PathValue("collectionId"))
	if !ok {
		writeError(w, http.StatusBadRequest, "A collection id is required")
		return
	}
	var ids []string
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, maxOrdersBytes)).Decode(&ids); err != nil && !errors.Is(err, io.EOF) {
		if tooBig := new(http.MaxBytesError); errors.As(err, &tooBig) {
			writeError(w, http.StatusRequestEntityTooLarge, "The order is too long")
			return
		}
		writeError(w, http.StatusBadRequest, "A JSON array of item ids is required")
		return
	}
	order := normalizeOrder(ids)
	ctx := r.Context()
	key := ordersKey(callerOf(r))
	s.mu.Lock()
	defer s.mu.Unlock()
	var orders map[string][]string
	if _, err := s.loadJSON(ctx, key, &orders); err != nil {
		storageFailed(w, r, err)
		return
	}
	if len(order) == 0 {
		if _, saved := orders[collection]; !saved {
			// Clearing an order that was never saved shouldn't use up a key.
			w.WriteHeader(http.StatusNoContent)
			return
		}
		delete(orders, collection)
	} else {
		if orders == nil {
			orders = map[string][]string{}
		}
		orders[collection] = order
	}
	var value any
	if len(orders) > 0 {
		raw, _ := json.Marshal(orders)
		if len(raw) > maxOrdersBytes {
			writeError(w, http.StatusRequestEntityTooLarge, "This profile has too many collection orders saved")
			return
		}
		value = json.RawMessage(raw)
	}
	if err := s.saveJSON(ctx, key, value); err != nil {
		storageFailed(w, r, err)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}

// normalizeOrder drops blanks, repeats and overlong ids, keeping the first of
// each so the dragged order survives, and caps the count.
func normalizeOrder(ids []string) []string {
	out := []string{}
	seen := map[string]bool{}
	for _, id := range ids {
		id = strings.TrimSpace(id)
		if id == "" || len(id) > maxOrderItemID || seen[id] {
			continue
		}
		seen[id] = true
		out = append(out, id)
		if len(out) == maxOrderItems {
			break
		}
	}
	return out
}
