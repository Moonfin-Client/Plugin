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
// settings (settingsKey) or its collection orders (ordersKey). A host with a
// small key budget can pack these together. Other keys under "s/" are kept
// apart, and themes are stored gzipped, not as JSON.
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
	m.HandleFunc("POST /Moonfin/Admin/ApplyDefaults", s.admin(s.handleApplyDefaults))
	m.HandleFunc("POST /Moonfin/Admin/CopySettings", s.admin(s.handleCopySettings))
	m.HandleFunc("GET /Moonfin/Settings/{userId}", s.admin(s.handleAdminGetSettings))
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

// handleStream can't stream on Silo, which buffers each plugin response and
// gives a plugin call 10 seconds. The non-2xx answer makes Moonfin back off to
// one retry every 30 seconds instead of reconnecting every second.
func (s *Server) handleStream(w http.ResponseWriter, r *http.Request) {
	if s.notify.on {
		s.serveStream(w, r)
		return
	}
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

// The PushDefaults routes apply every profile's defaults, to everyone or to
// one target. The admin page uses ApplyDefaults.
func (s *Server) handlePushDefaults(w http.ResponseWriter, r *http.Request) {
	reset, _ := strconv.ParseBool(r.URL.Query().Get("overwrite"))
	req := applyRequest{Overwrite: reset}
	if raw := r.PathValue("userId"); raw != "" {
		req.UserIDs = []string{raw}
	}
	s.applyDefaults(w, r, req)
}

type applyRequest struct {
	Overwrite bool     `json:"overwrite"`
	UserIDs   []string `json:"userIds"`
	Profiles  []string `json:"profiles"`
	Keys      []string `json:"keys"`
}

func (s *Server) handleApplyDefaults(w http.ResponseWriter, r *http.Request) {
	var req applyRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeJSON(w, http.StatusBadRequest, M{"error": "A request body is required"})
		return
	}
	if req.UserIDs != nil && len(req.UserIDs) == 0 {
		writeJSON(w, http.StatusBadRequest, M{"error": "A valid userId is required"})
		return
	}
	s.applyDefaults(w, r, req)
}

// applyDefaults pushes the admin defaults onto stored settings. Merge writes the
// set defaults of the chosen profiles, optionally only some keys, and keeps
// everything else a user chose. Overwrite resets each target to the defaults
// alone. Without targets it covers every stored profile.
func (s *Server) applyDefaults(w http.ResponseWriter, r *http.Request, req applyRequest) {
	profiles, ok := profilesOrAll(req.Profiles)
	if !ok {
		writeJSON(w, http.StatusBadRequest, M{"error": unknownProfileError})
		return
	}
	if req.Overwrite && req.Keys != nil {
		writeJSON(w, http.StatusBadRequest, M{"error": "A full reset applies every default, so it can't take a list of settings"})
		return
	}
	targets, ok := adminTargets(req.UserIDs)
	if !ok {
		writeJSON(w, http.StatusBadRequest, M{"error": "A valid userId is required"})
		return
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
	if !slices.ContainsFunc(profiles, func(p string) bool { return storedDefaults(cfg, p) != nil }) {
		writeJSON(w, http.StatusBadRequest, M{"error": "No default user settings configured"})
		return
	}
	push := map[string]M{}
	for _, p := range profiles {
		if d := keepOnly(storedDefaults(cfg, p), req.Keys); len(d) > 0 {
			push[p] = d
		}
	}
	by := "admin-default-merge"
	if req.Overwrite {
		by = "admin-reset"
	}
	entries := matchingEntries(index, targets)
	keys := make([]string, len(entries))
	for i, e := range entries {
		keys[i] = e.key()
	}
	// Settings are read and written all together, since Silo packs many
	// profiles' settings into each stored key.
	var current map[string][]byte
	if !req.Overwrite {
		if current, err = store.GetMany(ctx, s.kv, keys); err != nil {
			storageFailed(w, r, err)
			return
		}
	}
	global := push["global"]
	writes := map[string][]byte{}
	failed := 0
	for _, e := range entries {
		var env M
		if req.Overwrite {
			reset := clone(global)
			if reset == nil {
				reset = M{}
			}
			env = M{"schemaVersion": 2, "syncEnabled": true, "global": reset,
				"lastUpdated": time.Now().UnixMilli(), "lastUpdatedBy": by}
			for _, p := range settings.Profiles[1:] {
				if d := push[p]; d != nil {
					env[p] = clone(d)
				}
			}
		} else {
			if raw := current[e.key()]; len(bytes.TrimSpace(raw)) > 0 {
				if err := json.Unmarshal(raw, &env); err != nil {
					logError(r, "apply defaults", fmt.Errorf("decode %s: %w", e.key(), err))
					failed++
					continue
				}
			}
			if env == nil {
				continue
			}
			for _, p := range settings.Profiles {
				var layer M
				if p == "global" {
					layer = global
				} else {
					// Global's defaults reach a device profile the user has, except
					// where that device has its own default. A device's own defaults
					// create its profile, or they'd never reach that device.
					if _, has := env[p].(M); has && global != nil {
						layer = settings.Without(global, storedDefaults(cfg, p))
					}
					if own := push[p]; own != nil {
						layer = settings.LayerDefaults(layer, own)
					}
				}
				if len(layer) > 0 {
					env = settings.SaveProfile(env, p, clone(layer), by, serverKeys(cfg), time.Now())
				}
			}
		}
		raw, err := json.Marshal(env)
		if err != nil {
			logError(r, "apply defaults", err)
			failed++
			continue
		}
		writes[e.key()] = raw
	}
	// Every entry is already in the index, so only its own key changes.
	notWritten, err := store.PutMany(ctx, s.kv, writes)
	if err != nil {
		logError(r, "apply defaults", err)
	}
	failed += len(notWritten)
	affected := len(writes) - len(notWritten)
	if affected == 0 && failed > 0 {
		storageFailed(w, r, fmt.Errorf("apply defaults failed for %d profiles", failed))
		return
	}
	writeJSON(w, http.StatusOK, M{"success": true, "overwrite": req.Overwrite, "usersAffected": affected,
		"orphansDeleted": 0, "liveRefreshDeliveries": 0})
}

type copyRequest struct {
	FromUserID string   `json:"fromUserId"`
	ToUserIDs  []string `json:"toUserIds"`
	Profiles   []string `json:"profiles"`
	Keys       []string `json:"keys"`
	Replace    bool     `json:"replace"`
}

// handleCopySettings copies one profile's saved settings onto other targets.
// Merge keeps each target's values for whatever the copy leaves out, replace
// makes every copied profile an exact copy. API keys and hidden items always
// stay with their owner.
func (s *Server) handleCopySettings(w http.ResponseWriter, r *http.Request) {
	var req copyRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		writeJSON(w, http.StatusBadRequest, M{"error": "A request body is required"})
		return
	}
	from, fromOK := adminTargets([]string{req.FromUserID})
	to, toOK := adminTargets(req.ToUserIDs)
	if !fromOK || !toOK || len(to) == 0 {
		writeJSON(w, http.StatusBadRequest, M{"error": "A user to copy from and at least one other user to copy to are required"})
		return
	}
	profiles, ok := profilesOrAll(req.Profiles)
	if !ok {
		writeJSON(w, http.StatusBadRequest, M{"error": unknownProfileError})
		return
	}
	if req.Replace && req.Keys != nil {
		writeJSON(w, http.StatusBadRequest, M{"error": "Replace copies whole profiles, so it can't take a list of settings"})
		return
	}
	ctx := r.Context()
	s.mu.Lock()
	defer s.mu.Unlock()
	var index []indexEntry
	if _, err := s.loadJSON(ctx, keyIndex, &index); err != nil {
		storageFailed(w, r, err)
		return
	}
	cfg := s.config(ctx)
	if !cfg.EnableSettingsSync {
		writeJSON(w, http.StatusServiceUnavailable, M{"error": "Settings sync is disabled"})
		return
	}
	source, status, message := oneEntry(index, from)
	if status != 0 {
		writeJSON(w, status, M{"error": message})
		return
	}
	var env M
	if _, err := s.loadJSON(ctx, source.key(), &env); err != nil {
		storageFailed(w, r, err)
		return
	}
	if env == nil {
		writeJSON(w, http.StatusNotFound, M{"error": "That user has no saved Moonfin settings"})
		return
	}
	copies := map[string]M{}
	for _, p := range profiles {
		profile, _ := env[p].(M)
		if profile == nil {
			continue
		}
		c := keepOnly(profile, req.Keys)
		for _, k := range settings.PersonalKeys {
			delete(c, k)
		}
		if req.Replace || len(c) > 0 {
			copies[p] = c
		}
	}
	var entries []indexEntry
	for _, e := range matchingEntries(index, to) {
		if e.key() != source.key() {
			entries = append(entries, e)
		}
	}
	if len(copies) == 0 || len(entries) == 0 {
		writeJSON(w, http.StatusOK, M{"success": true, "replace": req.Replace, "usersAffected": 0})
		return
	}
	keys := make([]string, len(entries))
	for i, e := range entries {
		keys[i] = e.key()
	}
	current, err := store.GetMany(ctx, s.kv, keys)
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	writes := map[string][]byte{}
	for _, e := range entries {
		var target M
		if raw := current[e.key()]; len(bytes.TrimSpace(raw)) > 0 {
			if err := json.Unmarshal(raw, &target); err != nil {
				logError(r, "copy settings", fmt.Errorf("decode %s: %w", e.key(), err))
				continue
			}
		}
		for p, c := range copies {
			if req.Replace {
				target = settings.ReplaceProfile(target, p, clone(c), "admin-copy", serverKeys(cfg), time.Now())
			} else {
				target = settings.SaveProfile(target, p, clone(c), "admin-copy", serverKeys(cfg), time.Now())
			}
		}
		raw, err := json.Marshal(target)
		if err != nil {
			logError(r, "copy settings", err)
			continue
		}
		writes[e.key()] = raw
	}
	notWritten, err := store.PutMany(ctx, s.kv, writes)
	if err != nil {
		logError(r, "copy settings", err)
	}
	affected := len(writes) - len(notWritten)
	if affected == 0 && len(writes) > 0 {
		storageFailed(w, r, fmt.Errorf("copy settings failed for %d profiles", len(writes)))
		return
	}
	writeJSON(w, http.StatusOK, M{"success": true, "replace": req.Replace, "usersAffected": affected})
}

// handleAdminGetSettings returns one profile's stored settings, which the admin
// page reads before copying them.
func (s *Server) handleAdminGetSettings(w http.ResponseWriter, r *http.Request) {
	target, ok := adminTargets([]string{r.PathValue("userId")})
	if !ok {
		writeJSON(w, http.StatusBadRequest, M{"error": "A valid userId is required"})
		return
	}
	ctx := r.Context()
	if !s.config(ctx).EnableSettingsSync {
		writeJSON(w, http.StatusServiceUnavailable, M{"error": "Settings sync is disabled"})
		return
	}
	var index []indexEntry
	if _, err := s.loadJSON(ctx, keyIndex, &index); err != nil {
		storageFailed(w, r, err)
		return
	}
	e, status, message := oneEntry(index, target)
	if status != 0 {
		writeJSON(w, status, M{"error": message})
		return
	}
	var env M
	if _, err := s.loadJSON(ctx, e.key(), &env); err != nil {
		storageFailed(w, r, err)
		return
	}
	if env == nil {
		writeJSON(w, http.StatusNotFound, M{"error": "No settings found"})
		return
	}
	writeJSON(w, http.StatusOK, env)
}

var unknownProfileError = "Unknown profile. Valid profiles: " + strings.Join(settings.Profiles, ", ")

// profilesOrAll lowercases the requested profile names, gives every profile
// when none were asked for, and reports false for one that isn't valid.
func profilesOrAll(requested []string) ([]string, bool) {
	if len(requested) == 0 {
		return settings.Profiles, true
	}
	out := make([]string, 0, len(requested))
	for _, raw := range requested {
		p, ok := settings.ProfileName(raw)
		if !ok {
			return nil, false
		}
		out = append(out, p)
	}
	return out, true
}

type adminTargetRef struct {
	userID  int
	profile string
}

// adminTargets reads targets in adminTarget's form. Nil means every profile.
func adminTargets(raw []string) ([]adminTargetRef, bool) {
	if raw == nil {
		return nil, true
	}
	out := make([]adminTargetRef, 0, len(raw))
	for _, v := range raw {
		id, profile, ok := adminTarget(v)
		if !ok {
			return nil, false
		}
		out = append(out, adminTargetRef{id, profile})
	}
	return out, true
}

// matchingEntries is every entry a target names, or the whole index without
// targets.
func matchingEntries(index []indexEntry, targets []adminTargetRef) []indexEntry {
	if targets == nil {
		return index
	}
	var out []indexEntry
	for _, e := range index {
		if slices.ContainsFunc(targets, func(t adminTargetRef) bool { return e.isTarget(t.userID, t.profile) }) {
			out = append(out, e)
		}
	}
	return out
}

// oneEntry is the single profile a target names, or the status and message to
// answer with when it names none or several.
func oneEntry(index []indexEntry, target []adminTargetRef) (indexEntry, int, string) {
	entries := matchingEntries(index, target)
	switch len(entries) {
	case 0:
		return indexEntry{}, http.StatusNotFound, "That user has no saved Moonfin settings"
	case 1:
		return entries[0], 0, ""
	}
	return indexEntry{}, http.StatusBadRequest, "That account has more than one profile, so pick one of them"
}

// storedDefaults is the admin's own defaults for one profile, without the
// shared ones a device falls back to.
func storedDefaults(cfg Config, profile string) M {
	if profile == "global" {
		return cfg.Defaults
	}
	d, _ := cfg.DeviceDefaults[profile].(M)
	return d
}

// keepOnly drops every key of m outside keys. Nil keys keep everything.
func keepOnly(m M, keys []string) M {
	if keys == nil {
		return m
	}
	out := M{}
	for _, k := range keys {
		if v, ok := m[k]; ok {
			out[k] = v
		}
	}
	return out
}

func clone(m M) M {
	raw, _ := json.Marshal(m)
	var out M
	_ = json.Unmarshal(raw, &out)
	return out
}

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
	writeJSON(w, http.StatusOK, M{"Enabled": seerrOn(cfg), "Url": seerrURLIfOn(cfg), "DisplayName": name, "Variant": "seerr", "UserEnabled": userEnabled})
}
