package api

import (
	"bytes"
	"cmp"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"slices"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/Moonfin-Client/Plugin/Go/internal/store"
)

// The settings stream and phone pushes, on hosts that set Host.Notify. Pushes
// go through the hosted relay, which holds the Firebase account, the same way
// the Jellyfin plugin sends them by default.

const keyPushDevices = "push-devices"

// builtInRelayKey is the relay key a build is stamped with through
// -ldflags "-X github.com/Moonfin-Client/Plugin/Go/internal/api.builtInRelayKey=...".
// Without one, push needs the Push Relay Key setting.
var builtInRelayKey string

const (
	streamHeartbeat   = 30 * time.Second
	maxDevicesPerUser = 10
	maxPushToken      = 1024
)

var relayClient = &http.Client{Timeout: 15 * time.Second}

type notifier struct {
	on bool

	mu      sync.Mutex
	streams map[int]map[chan []byte]struct{}

	// devicesMu serializes changes to the stored push devices.
	devicesMu sync.Mutex
}

type pushDevice struct {
	Token    string `json:"token"`
	Platform string `json:"platform,omitempty"`
	DeviceID string `json:"deviceId,omitempty"`
}

func (s *Server) notificationRoutes() {
	if !s.notify.on {
		return
	}
	s.mux.HandleFunc("POST /Moonfin/Notifications/Register", s.user(s.handleRegisterDevice))
	s.mux.HandleFunc("DELETE /Moonfin/Notifications/Register", s.user(s.handleUnregisterDevice))
}

// serveStream holds the settings stream open the way the Jellyfin plugin
// does: a comment when it connects and every 30 seconds after, and each event
// as a data line.
func (s *Server) serveStream(w http.ResponseWriter, r *http.Request) {
	userID := callerOf(r).UserID
	events := make(chan []byte, 16)
	s.notify.mu.Lock()
	if s.notify.streams == nil {
		s.notify.streams = map[int]map[chan []byte]struct{}{}
	}
	if s.notify.streams[userID] == nil {
		s.notify.streams[userID] = map[chan []byte]struct{}{}
	}
	s.notify.streams[userID][events] = struct{}{}
	s.notify.mu.Unlock()
	defer func() {
		s.notify.mu.Lock()
		if delete(s.notify.streams[userID], events); len(s.notify.streams[userID]) == 0 {
			delete(s.notify.streams, userID)
		}
		s.notify.mu.Unlock()
	}()

	rc := http.NewResponseController(w)
	w.Header().Set("Content-Type", "text/event-stream; charset=utf-8")
	w.Header().Set("Cache-Control", "no-cache")
	w.Header().Set("X-Accel-Buffering", "no")
	heartbeat := time.NewTicker(streamHeartbeat)
	defer heartbeat.Stop()
	line := []byte(":connected\n\n")
	for {
		if _, err := w.Write(line); err != nil {
			return
		}
		if err := rc.Flush(); err != nil {
			return
		}
		select {
		case <-r.Context().Done():
			return
		case <-heartbeat.C:
			line = []byte(":heartbeat\n\n")
		case payload := <-events:
			line = fmt.Appendf(nil, "data: %s\n\n", payload)
		}
	}
}

// notifyUser sends an event to the account's open settings streams. A stream
// that has fallen too far behind misses it.
func (s *Server) notifyUser(userID int, event any) {
	payload, err := json.Marshal(event)
	if err != nil {
		return
	}
	s.notify.mu.Lock()
	defer s.notify.mu.Unlock()
	for events := range s.notify.streams[userID] {
		select {
		case events <- payload:
		default:
		}
	}
}

func (s *Server) handleRegisterDevice(w http.ResponseWriter, r *http.Request) {
	r.Body = http.MaxBytesReader(w, r.Body, 16<<10)
	in, _ := readBody(r)
	device := pushDevice{Token: strings.TrimSpace(str(in["token"])), Platform: str(in["platform"]), DeviceID: str(in["deviceid"])}
	switch {
	case device.Token == "":
		writeJSON(w, http.StatusBadRequest, M{"error": "Token is required"})
		return
	case len(device.Token) > maxPushToken || len(device.Platform) > 64 || len(device.DeviceID) > 256:
		writeJSON(w, http.StatusBadRequest, M{"error": "The registration is too long"})
		return
	}
	fits, err := s.changeDevices(r.Context(), callerOf(r).UserID, func(list []pushDevice) []pushDevice {
		list = slices.DeleteFunc(list, func(d pushDevice) bool { return sameDevice(d, device.Token, device.DeviceID) })
		list = append(list, device)
		return list[max(0, len(list)-maxDevicesPerUser):]
	})
	switch {
	case err != nil:
		storageFailed(w, r, err)
	case !fits:
		writeJSON(w, http.StatusBadRequest, M{"error": "Moonbase can't keep any more devices"})
	default:
		writeJSON(w, http.StatusOK, M{"success": true})
	}
}

func (s *Server) handleUnregisterDevice(w http.ResponseWriter, r *http.Request) {
	r.Body = http.MaxBytesReader(w, r.Body, 16<<10)
	in, _ := readBody(r)
	token, deviceID := strings.TrimSpace(str(in["token"])), str(in["deviceid"])
	if _, err := s.changeDevices(r.Context(), callerOf(r).UserID, func(list []pushDevice) []pushDevice {
		return slices.DeleteFunc(list, func(d pushDevice) bool { return sameDevice(d, token, deviceID) })
	}); err != nil {
		storageFailed(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, M{"success": true})
}

func sameDevice(d pushDevice, token, deviceID string) bool {
	return (token != "" && d.Token == token) || (deviceID != "" && d.DeviceID == deviceID)
}

func (s *Server) loadDevices(ctx context.Context) (map[string][]pushDevice, error) {
	var all map[string][]pushDevice
	_, err := s.loadJSON(ctx, keyPushDevices, &all)
	return all, err
}

// changeDevices replaces the account's devices with what change makes of
// them. False means they no longer fit.
func (s *Server) changeDevices(ctx context.Context, userID int, change func([]pushDevice) []pushDevice) (bool, error) {
	s.notify.devicesMu.Lock()
	defer s.notify.devicesMu.Unlock()
	all, err := s.loadDevices(ctx)
	if err != nil {
		return false, err
	}
	if all == nil {
		all = map[string][]pushDevice{}
	}
	account := strconv.Itoa(userID)
	if all[account] = change(all[account]); len(all[account]) == 0 {
		delete(all, account)
	}
	return s.saveWithin(ctx, keyPushDevices, all)
}

// saveWithin saves v unless it's grown past what Silo's instance state can
// hold, which the other hosts keep to as well. False means it didn't fit.
func (s *Server) saveWithin(ctx context.Context, key string, v any) (bool, error) {
	raw, err := json.Marshal(v)
	if err != nil || len(raw) > store.MaxValueBytes {
		return false, err
	}
	if err := s.kv.Put(ctx, key, raw); err != nil {
		return false, fmt.Errorf("write %s: %w", key, err)
	}
	return true, nil
}

// pushToUser sends a push to each of the account's phones through the relay,
// and forgets the devices the relay says are gone. Push is off without a
// relay address and key.
func (s *Server) pushToUser(ctx context.Context, userID int, title, body, route string) {
	if !s.notify.on {
		return
	}
	cfg := s.config(ctx)
	key := cmp.Or(cfg.PushRelayAppKey, builtInRelayKey)
	if cfg.PushRelayURL == "" || key == "" {
		return
	}
	all, err := s.loadDevices(ctx)
	if err != nil {
		slog.WarnContext(ctx, "push: read the devices", "error", err)
		return
	}
	var tokens []string
	for _, d := range all[strconv.Itoa(userID)] {
		tokens = append(tokens, d.Token)
	}
	if len(tokens) == 0 {
		return
	}
	dead, err := sendRelay(ctx, cfg.PushRelayURL, key, M{"tokens": tokens, "title": title, "body": body, "route": route})
	if err != nil {
		slog.WarnContext(ctx, "push: the relay didn't take it", "user", userID, "error", err)
		return
	}
	if len(dead) > 0 {
		if _, err := s.changeDevices(ctx, userID, func(list []pushDevice) []pushDevice {
			return slices.DeleteFunc(list, func(d pushDevice) bool { return slices.Contains(dead, d.Token) })
		}); err != nil {
			slog.WarnContext(ctx, "push: forget gone devices", "user", userID, "error", err)
		}
	}
}

// sendRelay posts one push to the relay and returns the tokens it says are
// gone. An answer it can't read prunes nothing.
func sendRelay(ctx context.Context, relayURL, key string, payload M) ([]string, error) {
	body, err := json.Marshal(payload)
	if err != nil {
		return nil, err
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, relayURL, bytes.NewReader(body))
	if err != nil {
		return nil, err
	}
	req.Header.Set("Authorization", "Bearer "+key)
	req.Header.Set("Content-Type", "application/json")
	resp, err := relayClient.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode/100 != 2 {
		return nil, fmt.Errorf("the relay answered %d", resp.StatusCode)
	}
	var v struct {
		Results []struct {
			Token string `json:"token"`
			Dead  bool   `json:"dead"`
		} `json:"results"`
	}
	if json.NewDecoder(io.LimitReader(resp.Body, 1<<20)).Decode(&v) != nil {
		return nil, nil
	}
	var dead []string
	for _, r := range v.Results {
		if r.Dead && r.Token != "" {
			dead = append(dead, r.Token)
		}
	}
	return dead, nil
}
