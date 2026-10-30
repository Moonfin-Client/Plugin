package api

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/json"
	"fmt"
	"net/http"
	"slices"
	"strconv"
	"strings"
	"time"

	"github.com/Moonfin-Client/Plugin/Go/internal/settings"
	"github.com/Moonfin-Client/Plugin/Go/internal/store"
)

type M = settings.M

// Moonbase's "user" is a profile, keyed by the account and the profile id the
// host verified. Without an id (Plex, or a Silo account with no profile
// selected) the profile's name stands in.
func settingsKey(id Identity) string {
	if id.ProfileID != "" {
		h := sha256.Sum256([]byte(id.ProfileID))
		return fmt.Sprintf("p/%d/%x", id.UserID, h[:8])
	}
	h := sha256.Sum256([]byte(id.ProfileName))
	return fmt.Sprintf("s/%d/%x", id.UserID, h[:8])
}

// PerProfileKey reports whether key is one there's one of per profile: its
// settings (settingsKey) or a collection order (orderKey). A host with a small
// key budget can pack these together. Other keys under "s/" are kept apart;
// themes are stored gzipped, not as JSON.
func PerProfileKey(key string) bool {
	switch {
	case key == keyIndex, key == keyThemes:
		return false
	case strings.HasPrefix(key, "p/"), strings.HasPrefix(key, "s/"), strings.HasPrefix(key, "o/"):
		return true
	}
	return false
}

// Instance state can't list its keys, so the stored profiles are indexed here
// for the admin routes that walk every user. The account's name and role are
// kept as last seen, since Silo gives plugins no list of accounts.
const keyIndex = "s/index"

type indexEntry struct {
	UserID    int    `json:"userId"`
	ProfileID string `json:"profileId,omitempty"`
	Profile   string `json:"profile"`
	UserName  string `json:"userName,omitempty"`
	Admin     bool   `json:"admin,omitempty"`
	Primary   bool   `json:"primary,omitempty"`
}

func (e indexEntry) identity() Identity {
	return Identity{UserID: e.UserID, ProfileID: e.ProfileID, ProfileName: e.Profile}
}

func (e indexEntry) key() string { return settingsKey(e.identity()) }

// profileKey names the profile in an admin target: its id, or its name on
// Silo builds that kept settings by name.
func (e indexEntry) profileKey() string {
	if e.ProfileID != "" {
		return e.ProfileID
	}
	return e.Profile
}

// adminTarget reads an admin route's {userId}: an account id for all of its
// profiles, or "<account id>~<profile key>" for one of them.
func adminTarget(raw string) (userID int, profile string, ok bool) {
	id, profile, _ := strings.Cut(raw, "~")
	userID, err := strconv.Atoi(id)
	return userID, profile, err == nil && userID > 0
}

func (e indexEntry) isTarget(userID int, profile string) bool {
	return e.UserID == userID && (profile == "" || e.profileKey() == profile)
}

func (s *Server) settingsRoutes() {
	m := s.mux
	m.HandleFunc("GET /Moonfin/Settings", s.sync(s.handleGetSettings))
	m.HandleFunc("POST /Moonfin/Settings", s.sync(s.handlePostSettings))
	m.HandleFunc("DELETE /Moonfin/Settings", s.sync(s.handleDeleteSettings))
	m.HandleFunc("GET /Moonfin/Settings/Stream", s.sync(s.handleStream))
	m.HandleFunc("POST /Moonfin/Settings/Profile/{profile}", s.sync(s.handlePostProfile))
	m.HandleFunc("DELETE /Moonfin/Settings/Profile/{profile}", s.sync(s.handleDeleteProfile))
	m.HandleFunc("GET /Moonfin/Settings/Resolved/{profile}", s.sync(s.handleResolved))
	for _, p := range []string{"/Moonfin/Settings/detailsScreenBlur", "/Moonfin/Settings/detailsScreenBlur/{profile}"} {
		m.HandleFunc("GET "+p, s.sync(s.handleGetBlur))
		m.HandleFunc("POST "+p, s.sync(s.handlePostBlur))
	}
	for _, p := range []string{"/Moonfin/Settings/detailsScreenOpacity", "/Moonfin/Settings/detailsScreenOpacity/{profile}"} {
		m.HandleFunc("GET "+p, s.sync(s.handleGetOpacity))
		m.HandleFunc("POST "+p, s.sync(s.handlePostOpacity))
	}
	m.HandleFunc("GET /Moonfin/Defaults", s.user(s.handleGetDefaults))
	m.HandleFunc("POST /Moonfin/Admin/PushDefaults", s.admin(s.handlePushDefaults))
	m.HandleFunc("POST /Moonfin/Admin/PushDefaults/{userId}", s.admin(s.handlePushDefaults))
	m.HandleFunc("GET /Moonfin/Seerr/Config", s.user(s.handleSeerrConfig))
	m.HandleFunc("GET /Moonfin/Jellyseerr/Config", s.user(s.handleSeerrConfig))
}

func (s *Server) sync(next http.HandlerFunc) http.HandlerFunc {
	return s.user(func(w http.ResponseWriter, r *http.Request) {
		if !s.config(r.Context()).EnableSettingsSync {
			writeError(w, http.StatusServiceUnavailable, "Settings sync is disabled")
			return
		}
		next(w, r)
	})
}

func serverKeys(cfg Config) settings.ServerKeys {
	return settings.ServerKeys{Tmdb: cfg.TmdbAPIKey, Mdblist: cfg.MdblistAPIKey}
}

// loadJSON reads key into v. An empty value is a deleted one, because
// instance state has no delete.
func (s *Server) loadJSON(ctx context.Context, key string, v any) (bool, error) {
	raw, found, err := s.kv.Get(ctx, key)
	if err != nil {
		return false, fmt.Errorf("read %s: %w", key, err)
	}
	if !found || len(bytes.TrimSpace(raw)) == 0 {
		return false, nil
	}
	if err := json.Unmarshal(raw, v); err != nil {
		return false, fmt.Errorf("decode %s: %w", key, err)
	}
	return true, nil
}

func (s *Server) saveJSON(ctx context.Context, key string, v any) error {
	raw := []byte{}
	if v != nil {
		var err error
		if raw, err = json.Marshal(v); err != nil {
			return err
		}
	}
	if err := s.kv.Put(ctx, key, raw); err != nil {
		return fmt.Errorf("write %s: %w", key, err)
	}
	return nil
}

func (s *Server) loadEnvelope(ctx context.Context, id Identity) (M, error) {
	var env M
	_, err := s.loadJSON(ctx, settingsKey(id), &env)
	return env, err
}

// storeEnvelope writes env (nil deletes it) and keeps the index in step.
func (s *Server) storeEnvelope(ctx context.Context, id Identity, env M) error {
	var index []indexEntry
	if _, err := s.loadJSON(ctx, keyIndex, &index); err != nil {
		return err
	}
	entry := indexEntry{UserID: id.UserID, ProfileID: id.ProfileID, Profile: id.ProfileName, UserName: id.UserName, Admin: id.Admin, Primary: id.Primary}
	at := slices.IndexFunc(index, func(e indexEntry) bool { return e.key() == entry.key() })
	if env == nil && at < 0 {
		// Nothing stored, and writing an empty value would use up a key.
		return nil
	}
	// The settings go first, so a write refused at the key limit never leaves
	// the index naming settings that aren't there.
	if err := s.saveJSON(ctx, settingsKey(id), env); err != nil {
		return err
	}
	switch {
	case env == nil:
		index = slices.Delete(index, at, at+1)
	case at < 0:
		index = append(index, entry)
	case index[at] != entry:
		index[at] = entry
	default:
		return nil
	}
	return s.saveJSON(ctx, keyIndex, index)
}

func storageFailed(w http.ResponseWriter, r *http.Request, err error) {
	logError(r, "settings storage", err)
	writeError(w, http.StatusInternalServerError, "Moonbase couldn't read or save its data. Check the server log.")
}

// readBody decodes a JSON object whose top-level keys match case-insensitively,
// as the Jellyfin plugin's model binding does.
func readBody(r *http.Request) (M, error) {
	var body M
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
		return nil, err
	}
	out := M{}
	for k, v := range body {
		out[strings.ToLower(k)] = v
	}
	return out, nil
}

func (s *Server) handleGetSettings(w http.ResponseWriter, r *http.Request) {
	env, err := s.loadEnvelope(r.Context(), callerOf(r))
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	if env == nil {
		writeError(w, http.StatusNotFound, "No settings found for user")
		return
	}
	writeJSON(w, http.StatusOK, env)
}

func (s *Server) handlePostSettings(w http.ResponseWriter, r *http.Request) {
	body, err := readBody(r)
	incoming, ok := body["settings"].(M)
	if err != nil || !ok {
		writeError(w, http.StatusBadRequest, "Settings are required")
		return
	}
	mode, _ := body["mergemode"].(string)
	if mode == "" {
		mode = "merge"
	}
	clientID, _ := body["clientid"].(string)
	s.save(w, r, func(env M) M {
		return settings.SaveEnvelope(env, incoming, mode, clientID, serverKeys(s.config(r.Context())), time.Now())
	})
}

func (s *Server) handlePostProfile(w http.ResponseWriter, r *http.Request) {
	name, ok := settings.ProfileName(r.PathValue("profile"))
	if !ok {
		writeError(w, http.StatusBadRequest, "Invalid profile: "+r.PathValue("profile"))
		return
	}
	body, err := readBody(r)
	incoming, ok := body["profile"].(M)
	if err != nil || !ok {
		writeError(w, http.StatusBadRequest, "Profile settings are required")
		return
	}
	clientID, _ := body["clientid"].(string)
	s.saveProfile(w, r, name, settings.Clean(incoming), clientID, nil)
}

// saveProfile merges one profile and answers like the Jellyfin plugin, with
// extra fields added to the success body.
func (s *Server) saveProfile(w http.ResponseWriter, r *http.Request, name string, incoming M, clientID string, extra M) {
	s.save(w, r, func(env M) M {
		return settings.SaveProfile(env, name, incoming, clientID, serverKeys(s.config(r.Context())), time.Now())
	}, extra)
}

func (s *Server) save(w http.ResponseWriter, r *http.Request, apply func(M) M, extra ...M) {
	id := callerOf(r)
	s.mu.Lock()
	defer s.mu.Unlock()
	env, err := s.loadEnvelope(r.Context(), id)
	if err == nil {
		created := env == nil
		if err = s.storeEnvelope(r.Context(), id, apply(env)); err == nil {
			body := M{"Success": true, "Created": created, "UserId": strconv.Itoa(id.UserID)}
			for _, e := range extra {
				for k, v := range e {
					body[k] = v
				}
			}
			writeJSON(w, http.StatusOK, body)
			return
		}
	}
	storageFailed(w, r, err)
}

func (s *Server) handleDeleteSettings(w http.ResponseWriter, r *http.Request) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if err := s.storeEnvelope(r.Context(), callerOf(r), nil); err != nil {
		storageFailed(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, M{"Success": true, "Message": "Settings deleted"})
}

func (s *Server) handleDeleteProfile(w http.ResponseWriter, r *http.Request) {
	raw := r.PathValue("profile")
	name, ok := settings.ProfileName(raw)
	if name == "global" {
		writeError(w, http.StatusBadRequest, "Can't delete the global profile. Use DELETE /Settings to remove all settings.")
		return
	}
	if !ok {
		writeError(w, http.StatusBadRequest, "Invalid profile: "+raw)
		return
	}
	id := callerOf(r)
	s.mu.Lock()
	defer s.mu.Unlock()
	env, err := s.loadEnvelope(r.Context(), id)
	if err == nil && env != nil {
		delete(env, name)
		env["lastUpdated"] = time.Now().UnixMilli()
		err = s.storeEnvelope(r.Context(), id, env)
	}
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, M{"Success": true, "Message": fmt.Sprintf("Profile '%s' deleted", raw)})
}

func (s *Server) resolve(r *http.Request, name string, record bool) (M, error) {
	id := callerOf(r)
	// Only recording a baseline writes, so read-only lookups skip the lock.
	if record {
		s.mu.Lock()
		defer s.mu.Unlock()
	}
	env, err := s.loadEnvelope(r.Context(), id)
	if err != nil {
		return nil, err
	}
	stored, err := s.fullConfig(r.Context())
	if err != nil {
		return nil, err
	}
	cfg := configFrom(stored)
	resolved := settings.Resolve(env, name, cfg.DefaultsFor(name), serverKeys(cfg))
	if record && env != nil && settings.RecordBaseline(env, name, resolved) {
		if err := s.storeEnvelope(r.Context(), id, env); err != nil {
			logError(r, "record hidden content baseline", err)
		}
	}
	return resolved, nil
}

// handleResolved answers 200 even for a profile that has never synced (the
// Jellyfin plugin answers 404 when there are no admin defaults), so Moonfin
// turns sync on by itself at the first sign-in.
func (s *Server) handleResolved(w http.ResponseWriter, r *http.Request) {
	name, ok := settings.ProfileName(r.PathValue("profile"))
	if !ok {
		writeError(w, http.StatusBadRequest, "Invalid profile: "+r.PathValue("profile")+". Valid profiles: global, desktop, mobile, tv")
		return
	}
	resolved, err := s.resolve(r, name, true)
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, resolved)
}

// handleStream can't stream: Silo buffers each plugin response and gives a
// plugin call 10 seconds. The non-2xx answer makes Moonfin back off to one
// retry every 30 seconds instead of reconnecting every second.
func (s *Server) handleStream(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusNotImplemented, M{"supported": false, "transport": "none"})
}

func (s *Server) handleGetDefaults(w http.ResponseWriter, r *http.Request) {
	stored, err := s.fullConfig(r.Context())
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	d := configFrom(stored).Defaults
	if d == nil {
		d = M{}
	}
	writeJSON(w, http.StatusOK, d)
}

func (s *Server) handlePushDefaults(w http.ResponseWriter, r *http.Request) {
	reset, _ := strconv.ParseBool(r.URL.Query().Get("overwrite"))
	only, onlyProfile := 0, ""
	if raw := r.PathValue("userId"); raw != "" {
		var ok bool
		if only, onlyProfile, ok = adminTarget(raw); !ok {
			writeJSON(w, http.StatusBadRequest, M{"error": "A valid userId is required"})
			return
		}
	}
	ctx := r.Context()
	s.mu.Lock()
	defer s.mu.Unlock()
	stored, err := s.fullConfig(ctx)
	var index []indexEntry
	if err == nil {
		_, err = s.loadJSON(ctx, keyIndex, &index)
	}
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	cfg := configFrom(stored)
	if !cfg.EnableSettingsSync {
		writeJSON(w, http.StatusServiceUnavailable, M{"error": "Settings sync is disabled"})
		return
	}
	if cfg.Defaults == nil && len(cfg.DeviceDefaults) == 0 {
		writeJSON(w, http.StatusBadRequest, M{"error": "No default user settings configured"})
		return
	}
	by := "admin-reset-all"
	if only != 0 {
		by = "admin-reset-user"
	}
	var targets []indexEntry
	var keys []string
	for _, e := range index {
		if only == 0 || e.isTarget(only, onlyProfile) {
			targets = append(targets, e)
			keys = append(keys, e.key())
		}
	}
	// Settings are read and written all together, since Silo packs many
	// profiles' settings into each stored key.
	var current map[string][]byte
	if !reset {
		if current, err = store.GetMany(ctx, s.kv, keys); err != nil {
			storageFailed(w, r, err)
			return
		}
	}
	writes := map[string][]byte{}
	failed := 0
	for _, e := range targets {
		var env M
		if reset {
			global := clone(cfg.Defaults)
			if global == nil {
				global = M{}
			}
			env = M{"schemaVersion": 2, "syncEnabled": true, "global": global,
				"lastUpdated": time.Now().UnixMilli(), "lastUpdatedBy": by}
			// A device's own defaults go in its profile, or global would win
			// over them.
			for _, p := range settings.Profiles[1:] {
				if d, _ := cfg.DeviceDefaults[p].(M); len(d) > 0 {
					env[p] = clone(d)
				}
			}
		} else {
			var existing M
			if raw := current[e.key()]; len(bytes.TrimSpace(raw)) > 0 {
				if err := json.Unmarshal(raw, &existing); err != nil {
					logError(r, "push defaults", fmt.Errorf("decode %s: %w", e.key(), err))
					failed++
					continue
				}
			}
			if existing == nil {
				continue
			}
			// As on Jellyfin, the defaults go into global and every device
			// profile the user has, since a device profile wins over global.
			env = existing
			for _, p := range settings.Profiles {
				if _, has := env[p].(M); p != "global" && !has {
					continue
				}
				if d := cfg.DefaultsFor(p); len(d) > 0 {
					env = settings.SaveProfile(env, p, clone(d), "admin-default-merge", serverKeys(cfg), time.Now())
				}
			}
		}
		raw, err := json.Marshal(env)
		if err != nil {
			logError(r, "push defaults", err)
			failed++
			continue
		}
		writes[e.key()] = raw
	}
	// Every entry is already in the index, so only its own key changes.
	notWritten, err := store.PutMany(ctx, s.kv, writes)
	if err != nil {
		logError(r, "push defaults", err)
	}
	failed += len(notWritten)
	affected := len(writes) - len(notWritten)
	if affected == 0 && failed > 0 {
		storageFailed(w, r, fmt.Errorf("push defaults failed for %d profiles", failed))
		return
	}
	if only != 0 {
		writeJSON(w, http.StatusOK, M{"success": true, "overwrite": reset})
		return
	}
	writeJSON(w, http.StatusOK, M{"success": true, "overwrite": reset, "usersAffected": affected,
		"orphansDeleted": 0, "liveRefreshDeliveries": 0})
}

func clone(m M) M {
	raw, _ := json.Marshal(m)
	var out M
	_ = json.Unmarshal(raw, &out)
	return out
}

// handleSeerrConfig always reports Seerr off, since this Moonbase has no Seerr
// proxy.
func (s *Server) handleSeerrConfig(w http.ResponseWriter, r *http.Request) {
	cfg := s.config(r.Context())
	userEnabled := true
	if env, err := s.loadEnvelope(r.Context(), callerOf(r)); err == nil {
		if global, ok := env["global"].(M); ok {
			if v, ok := global["seerrEnabled"].(bool); ok {
				userEnabled = v
			}
		}
	}
	name := cfg.SeerrDisplayName
	if name == "" {
		name = "Seerr"
	}
	writeJSON(w, http.StatusOK, M{"Enabled": false, "DisplayName": name, "Variant": "seerr", "UserEnabled": userEnabled})
}
