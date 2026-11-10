package api

import (
	"cmp"
	"context"
	"crypto/pbkdf2"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"encoding/json"
	"math"
	"net/http"
	"slices"
	"strconv"
	"strings"
	"time"
)

// Per-account PINs, ported from the PinService the Jellyfin and Emby plugins
// share, so a PIN set on one device holds on every other. A PIN is four
// digits, only ten thousand combinations, so just a salted slow hash is kept
// and wrong guesses past the first few cost a growing wait. They're counted
// here, where clearing an app's data can't reset them. No reply ever carries
// a PIN or its hash.
const (
	keyPins = "pins"

	pinLength         = 4
	pinFreeAttempts   = 5
	pinLockoutStep    = 30
	pinMaxLockout     = 900
	pinHashIterations = 210_000
	pinSaltBytes      = 16
	pinHashBytes      = 32
	// Bounds on a stored count, so a damaged record can't stall every check.
	pinMinIterations = 10_000
	pinMaxIterations = 2_000_000
)

type pinRecord struct {
	Salt       string `json:"salt"`
	Hash       string `json:"hash"`
	Iterations int    `json:"iterations"`
	// Failed counts the wrong guesses since the last right one.
	Failed         int        `json:"failed,omitempty"`
	LockedUntilUtc *time.Time `json:"lockedUntilUtc,omitempty"`
	UpdatedUtc     time.Time  `json:"updatedUtc"`
	// UserName names the account on the admin page when it has never synced
	// settings, so it isn't in the index.
	UserName string `json:"userName,omitempty"`
}

type pinCheck int

const (
	pinValid pinCheck = iota
	pinWrong
	pinLocked
	pinNone
)

type pinRequest struct {
	Pin        string `json:"pin"`
	CurrentPin string `json:"currentPin"`
}

type pinUser struct {
	ID              string `json:"id"`
	Name            string `json:"name"`
	IsAdministrator bool   `json:"isAdministrator"`
	HasPin          bool   `json:"hasPin"`
}

func (s *Server) pinRoutes() {
	m := s.mux
	m.HandleFunc("GET /Moonfin/Pin", s.user(s.handlePinStatus))
	m.HandleFunc("PUT /Moonfin/Pin", s.user(s.handleSetPin))
	m.HandleFunc("DELETE /Moonfin/Pin", s.user(s.handleRemovePin))
	m.HandleFunc("POST /Moonfin/Pin/Verify", s.user(s.handleVerifyPin))
	m.HandleFunc("GET /Moonfin/Admin/Pins", s.admin(s.handleAdminPins))
	m.HandleFunc("PUT /Moonfin/Users/{userId}/Pin", s.admin(s.handleSetPinFor))
	m.HandleFunc("DELETE /Moonfin/Users/{userId}/Pin", s.admin(s.handleRemovePinFor))
}

func pinShaped(pin string) bool {
	return len(pin) == pinLength && strings.Trim(pin, "0123456789") == ""
}

// lockoutSeconds is how long the next guess waits after failed wrong ones.
func lockoutSeconds(failed int) int {
	if failed <= pinFreeAttempts {
		return 0
	}
	return min(pinMaxLockout, (failed-pinFreeAttempts)*pinLockoutStep)
}

// newPinRecord hashes pin as PinService does, PBKDF2 with SHA-256 over a
// fresh salt.
func newPinRecord(pin, userName string) (pinRecord, error) {
	salt := make([]byte, pinSaltBytes)
	_, _ = rand.Read(salt)
	hash, err := pbkdf2.Key(sha256.New, pin, salt, pinHashIterations, pinHashBytes)
	if err != nil {
		return pinRecord{}, err
	}
	return pinRecord{
		Salt:       base64.StdEncoding.EncodeToString(salt),
		Hash:       base64.StdEncoding.EncodeToString(hash),
		Iterations: pinHashIterations,
		UpdatedUtc: time.Now().UTC(),
		UserName:   userName,
	}, nil
}

func (p pinRecord) matches(guess string) bool {
	if p.Iterations < pinMinIterations || p.Iterations > pinMaxIterations {
		return false
	}
	salt, err := base64.StdEncoding.DecodeString(p.Salt)
	if err != nil || len(salt) == 0 {
		return false
	}
	want, err := base64.StdEncoding.DecodeString(p.Hash)
	if err != nil || len(want) != pinHashBytes {
		return false
	}
	got, err := pbkdf2.Key(sha256.New, guess, salt, p.Iterations, pinHashBytes)
	return err == nil && subtle.ConstantTimeCompare(got, want) == 1
}

// verifyPin checks a guess and counts a wrong one. Past the free attempts
// every guess waits out the lockout first, whether or not it's right. It
// says how long to wait when locked, and whether pins changed.
func verifyPin(pins map[string]pinRecord, id, guess string, now time.Time) (check pinCheck, wait int, changed bool) {
	p, ok := pins[id]
	if !ok || p.Hash == "" {
		return pinNone, 0, false
	}
	if p.LockedUntilUtc != nil && p.LockedUntilUtc.After(now) {
		return pinLocked, int(math.Ceil(p.LockedUntilUtc.Sub(now).Seconds())), false
	}
	if pinShaped(guess) && p.matches(guess) {
		if p.Failed == 0 && p.LockedUntilUtc == nil {
			return pinValid, 0, false
		}
		p.Failed, p.LockedUntilUtc = 0, nil
		pins[id] = p
		return pinValid, 0, true
	}
	p.Failed++
	p.LockedUntilUtc = nil
	check, wait = pinWrong, lockoutSeconds(p.Failed)
	if wait > 0 {
		until := now.Add(time.Duration(wait) * time.Second)
		p.LockedUntilUtc = &until
		check = pinLocked
	}
	pins[id] = p
	return check, wait, true
}

func (s *Server) loadPins(ctx context.Context) (map[string]pinRecord, error) {
	pins := map[string]pinRecord{}
	_, err := s.loadJSON(ctx, keyPins, &pins)
	return pins, err
}

func readPinRequest(w http.ResponseWriter, r *http.Request) pinRequest {
	var req pinRequest
	// An unreadable or empty body is a request without a PIN, which the
	// routes refuse or allow the way the Jellyfin plugin does.
	_ = json.NewDecoder(http.MaxBytesReader(w, r.Body, 4096)).Decode(&req)
	return req
}

func pinKey(id Identity) string { return strconv.Itoa(id.UserID) }

func pinError(w http.ResponseWriter, status int, message string) {
	writeJSON(w, status, M{"error": message})
}

func tooManyPinAttempts(w http.ResponseWriter, wait int) {
	w.Header().Set("Retry-After", strconv.Itoa(wait))
	writeJSON(w, http.StatusTooManyRequests, M{"error": "Too many attempts", "retryAfterSeconds": wait})
}

// checkPin runs verifyPin and stores what it counted. False means it already
// answered with a storage failure. The caller holds pinMu.
func (s *Server) checkPin(w http.ResponseWriter, r *http.Request, pins map[string]pinRecord, id, guess string) (pinCheck, int, bool) {
	check, wait, changed := verifyPin(pins, id, guess, time.Now().UTC())
	if changed {
		if err := s.saveJSON(r.Context(), keyPins, pins); err != nil {
			storageFailed(w, r, err)
			return 0, 0, false
		}
	}
	return check, wait, true
}

// passCurrent reports whether current is the account's PIN, or it has none,
// and otherwise refuses the request.
func (s *Server) passCurrent(w http.ResponseWriter, r *http.Request, pins map[string]pinRecord, id, current string) bool {
	check, wait, ok := s.checkPin(w, r, pins, id, current)
	switch {
	case !ok:
		return false
	case check == pinLocked:
		tooManyPinAttempts(w, wait)
		return false
	case check == pinWrong:
		pinError(w, http.StatusForbidden, "The current PIN is wrong")
		return false
	}
	return true
}

func (s *Server) handlePinStatus(w http.ResponseWriter, r *http.Request) {
	pins, err := s.loadPins(r.Context())
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, M{"enabled": pins[pinKey(callerOf(r))].Hash != ""})
}

// handleSetPin sets or changes the caller's PIN. Changing one takes the
// current one, and a wrong one counts as a guess.
func (s *Server) handleSetPin(w http.ResponseWriter, r *http.Request) {
	req := readPinRequest(w, r)
	if !pinShaped(req.Pin) {
		pinError(w, http.StatusBadRequest, "A PIN is exactly four digits")
		return
	}
	caller := callerOf(r)
	s.pinMu.Lock()
	defer s.pinMu.Unlock()
	pins, err := s.loadPins(r.Context())
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	if !s.passCurrent(w, r, pins, pinKey(caller), req.CurrentPin) {
		return
	}
	s.storePin(w, r, pins, pinKey(caller), req.Pin, caller.UserName)
}

func (s *Server) storePin(w http.ResponseWriter, r *http.Request, pins map[string]pinRecord, id, pin, userName string) {
	p, err := newPinRecord(pin, userName)
	if err != nil {
		logError(r, "hash a PIN", err)
		pinError(w, http.StatusInternalServerError, "Couldn't save the PIN")
		return
	}
	pins[id] = p
	if err := s.saveJSON(r.Context(), keyPins, pins); err != nil {
		storageFailed(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, M{"enabled": true})
}

func (s *Server) removePin(w http.ResponseWriter, r *http.Request, pins map[string]pinRecord, id string) {
	if _, ok := pins[id]; ok {
		delete(pins, id)
		if err := s.saveJSON(r.Context(), keyPins, pins); err != nil {
			storageFailed(w, r, err)
			return
		}
	}
	writeJSON(w, http.StatusOK, M{"enabled": false})
}

func (s *Server) handleVerifyPin(w http.ResponseWriter, r *http.Request) {
	req := readPinRequest(w, r)
	s.pinMu.Lock()
	defer s.pinMu.Unlock()
	pins, err := s.loadPins(r.Context())
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	check, wait, ok := s.checkPin(w, r, pins, pinKey(callerOf(r)), req.Pin)
	switch {
	case !ok:
	case check == pinNone:
		pinError(w, http.StatusNotFound, "No PIN is set")
	case check == pinLocked:
		tooManyPinAttempts(w, wait)
	default:
		writeJSON(w, http.StatusOK, M{"valid": check == pinValid})
	}
}

// handleRemovePin removes the caller's PIN, which takes the current one.
func (s *Server) handleRemovePin(w http.ResponseWriter, r *http.Request) {
	req := readPinRequest(w, r)
	id := pinKey(callerOf(r))
	s.pinMu.Lock()
	defer s.pinMu.Unlock()
	pins, err := s.loadPins(r.Context())
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	if !s.passCurrent(w, r, pins, id, cmp.Or(req.CurrentPin, req.Pin)) {
		return
	}
	s.removePin(w, r, pins, id)
}

// pinUsers is every account the admin can manage a PIN for, admins first:
// the accounts that synced settings, and any other that set a PIN.
func (s *Server) pinUsers(ctx context.Context, pins map[string]pinRecord) ([]pinUser, error) {
	var index []indexEntry
	if _, err := s.loadJSON(ctx, keyIndex, &index); err != nil {
		return nil, err
	}
	users := []pinUser{}
	at := map[string]int{}
	add := func(id, name string, admin bool) {
		if i, ok := at[id]; ok {
			users[i].IsAdministrator = users[i].IsAdministrator || admin
			return
		}
		at[id] = len(users)
		users = append(users, pinUser{ID: id, Name: cmp.Or(name, pins[id].UserName, "Account "+id),
			IsAdministrator: admin, HasPin: pins[id].Hash != ""})
	}
	for _, e := range index {
		add(strconv.Itoa(e.UserID), e.UserName, e.Admin)
	}
	for id, p := range pins {
		if p.Hash != "" {
			add(id, "", false)
		}
	}
	adminFirst := func(u pinUser) int {
		if u.IsAdministrator {
			return 0
		}
		return 1
	}
	slices.SortFunc(users, func(a, b pinUser) int {
		return cmp.Or(cmp.Compare(adminFirst(a), adminFirst(b)),
			strings.Compare(strings.ToLower(a.Name), strings.ToLower(b.Name)), strings.Compare(a.ID, b.ID))
	})
	return users, nil
}

func (s *Server) handleAdminPins(w http.ResponseWriter, r *http.Request) {
	pins, err := s.loadPins(r.Context())
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	users, err := s.pinUsers(r.Context(), pins)
	if err != nil {
		storageFailed(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, users)
}

// pinTarget loads the PINs for an admin route and finds its {userId}. False
// means it already answered.
func (s *Server) pinTarget(w http.ResponseWriter, r *http.Request) (map[string]pinRecord, string, bool) {
	n, err := strconv.Atoi(r.PathValue("userId"))
	if err != nil || n <= 0 {
		pinError(w, http.StatusBadRequest, "A valid userId is required")
		return nil, "", false
	}
	id := strconv.Itoa(n)
	pins, err := s.loadPins(r.Context())
	if err != nil {
		storageFailed(w, r, err)
		return nil, "", false
	}
	users, err := s.pinUsers(r.Context(), pins)
	if err != nil {
		storageFailed(w, r, err)
		return nil, "", false
	}
	if !slices.ContainsFunc(users, func(u pinUser) bool { return u.ID == id }) {
		pinError(w, http.StatusNotFound, "That user does not exist")
		return nil, "", false
	}
	return pins, id, true
}

// handleSetPinFor sets a user's PIN without the current one, for an admin.
func (s *Server) handleSetPinFor(w http.ResponseWriter, r *http.Request) {
	req := readPinRequest(w, r)
	s.pinMu.Lock()
	defer s.pinMu.Unlock()
	pins, id, ok := s.pinTarget(w, r)
	if !ok {
		return
	}
	if !pinShaped(req.Pin) {
		pinError(w, http.StatusBadRequest, "A PIN is exactly four digits")
		return
	}
	s.storePin(w, r, pins, id, req.Pin, pins[id].UserName)
}

// handleRemovePinFor removes a user's PIN, for an admin resetting a forgotten one.
func (s *Server) handleRemovePinFor(w http.ResponseWriter, r *http.Request) {
	s.pinMu.Lock()
	defer s.pinMu.Unlock()
	pins, id, ok := s.pinTarget(w, r)
	if !ok {
		return
	}
	s.removePin(w, r, pins, id)
}
