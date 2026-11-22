// Package settings ports Moonbase's settings sync rules from the Jellyfin
// plugin to plain JSON maps.
//
// A stored envelope holds the global profile and the desktop, mobile and tv
// device profiles. Keys are kept as the client sent them, so a key the
// Jellyfin model doesn't declare still round-trips here. A value of null
// means "not sent" and never clears anything.
package settings

import (
	"cmp"
	"encoding/json"
	"maps"
	"slices"
	"strings"
	"time"
)

type M = map[string]any

var Profiles = []string{"global", "desktop", "mobile", "tv"}

// ProfileName lowercases a route's profile segment and reports whether it is
// one of Profiles.
func ProfileName(raw string) (string, bool) {
	p := strings.ToLower(raw)
	return p, slices.Contains(Profiles, p)
}

// ServerKeys are the admin's MDBList and TMDB keys. Resolve hands them to
// users who have none of their own and saves drop copies of them, so a key
// rotation reaches everyone.
type ServerKeys struct {
	Tmdb    string
	Mdblist string
}

var aliases = map[string]string{
	"jellyseerrEnabled":   "seerrEnabled",
	"jellyseerrApiKey":    "seerrApiKey",
	"jellyseerrBlockNsfw": "seerrBlockNsfw",
	"jellyseerrRows":      "seerrRows",
}

// Clean prepares an incoming profile: first letters lowercased (the Jellyfin
// binder is case-insensitive), nulls dropped and legacy jellyseerr* keys
// folded into their seerr* names.
func Clean(in M) M {
	out := M{}
	for k, v := range in {
		if v == nil || k == "" {
			continue
		}
		if c := k[0]; c >= 'A' && c <= 'Z' {
			k = string(c+'a'-'A') + k[1:]
		}
		out[k] = v
	}
	for alias, canon := range aliases {
		if v, ok := out[alias]; ok {
			if _, has := out[canon]; !has {
				out[canon] = v
			}
			delete(out, alias)
		}
	}
	return out
}

// SaveProfile merges incoming into one profile of env (nil for a new user)
// and returns the envelope to store.
func SaveProfile(env M, name string, incoming M, clientID string, keys ServerKeys, now time.Time, changes ...SavedFilterChange) M {
	if env == nil {
		env = M{"schemaVersion": 2, "syncEnabled": true}
	}
	pins := savedGlobalFilters(env)
	stored := storedHidden(env)
	if existing, ok := env[name].(M); ok {
		mergeProfile(existing, incoming)
	} else {
		env[name] = incoming
	}
	finish(env, clientID, keys, now, stored)
	preserveSavedFilters(env, pins)
	if len(changes) != 0 {
		applySavedFilterChange(env, changes[0])
	}
	return env
}

// PersonalKeys are a user's own keys and hidden items rather than
// preferences, so copying settings to someone else never moves them.
var PersonalKeys = []string{"seerrApiKey", "mdblistApiKey", "tmdbApiKey", "hiddenContinueWatchingItems", "hiddenNextUpSeries"}

// ReplaceProfile swaps one profile of env (nil for a new user) for incoming,
// keeping the user's PersonalKeys, and returns the envelope to store.
func ReplaceProfile(env M, name string, incoming M, clientID string, keys ServerKeys, now time.Time) M {
	if env == nil {
		env = M{"schemaVersion": 2, "syncEnabled": true}
	}
	pins := savedGlobalFilters(env)
	stored := storedHidden(env)
	if existing, ok := env[name].(M); ok {
		for _, k := range PersonalKeys {
			if v, ok := existing[k]; ok {
				incoming[k] = v
			}
		}
	}
	env[name] = incoming
	finish(env, clientID, keys, now, stored)
	preserveSavedFilters(env, pins)
	return env
}

// SaveEnvelope builds the envelope to store from a whole incoming one.
// mergeMode "merge" merges it into existing the way SaveProfile does per
// profile. Anything else replaces it.
func SaveEnvelope(existing, incoming M, mergeMode, clientID string, keys ServerKeys, now time.Time) M {
	for _, p := range Profiles {
		if m, ok := incoming[p].(M); ok {
			incoming[p] = Clean(m)
		}
	}
	delete(incoming, "hiddenContentBaselines")
	delete(incoming, "liveTvSavedFilterExclusions") // server-owned
	pins := savedGlobalFilters(existing)
	if _, ok := incoming["syncEnabled"].(bool); !ok {
		incoming["syncEnabled"] = true
	}
	if mergeMode != "merge" || existing == nil {
		finish(incoming, clientID, keys, now, nil)
		preserveSavedFilters(incoming, pins)
		if exclusions, ok := existing["liveTvSavedFilterExclusions"]; ok {
			incoming["liveTvSavedFilterExclusions"] = exclusions
		}
		return incoming
	}
	stored := storedHidden(existing)
	existing["syncEnabled"] = incoming["syncEnabled"]
	for k, v := range incoming {
		if v == nil || k == "syncEnabled" {
			continue
		}
		if in, ok := v.(M); ok && slices.Contains(Profiles, k) {
			if ex, ok := existing[k].(M); ok {
				mergeProfile(ex, in)
				continue
			}
		}
		existing[k] = v
	}
	finish(existing, clientID, keys, now, stored)
	preserveSavedFilters(existing, pins)
	return existing
}

// SavedFilterChange is an intentional shared-filter edit in the profile POST.
type SavedFilterChange struct {
	Action   string
	Filter   string
	Filters  []string
	Defaults []any // Inherited admin pins for a first explicit add/remove.
}

func validSavedFilter(key string) bool {
	return len(key) <= 1024 && ((strings.HasPrefix(key, "group:") && len(key) > 6) ||
		(strings.HasPrefix(key, "category:") && len(key) > 9))
}

func ParseSavedFilterChange(raw any) (SavedFilterChange, bool) {
	obj, ok := raw.(M)
	if !ok {
		return SavedFilterChange{}, false
	}
	action, _ := obj["action"].(string)
	switch action {
	case "add", "remove":
		key, _ := obj["filter"].(string)
		if !validSavedFilter(key) {
			return SavedFilterChange{}, false
		}
		return SavedFilterChange{Action: action, Filter: key}, true
	case "initializeIfAbsent":
		rawFilters, ok := obj["filters"].([]any)
		if !ok || len(rawFilters) > 1000 {
			return SavedFilterChange{}, false
		}
		filters := make([]string, 0, len(rawFilters))
		for _, item := range rawFilters {
			key, ok := item.(string)
			if !ok || !validSavedFilter(key) {
				return SavedFilterChange{}, false
			}
			filters = append(filters, key)
		}
		return SavedFilterChange{Action: action, Filters: filters}, true
	}
	return SavedFilterChange{}, false
}

// Empty arrays mean an intentional clear; missing fields must not be replaced.
func pinnedStrings(value any) ([]any, bool) {
	var items []any
	switch v := value.(type) {
	case []any:
		items = v
	case []string:
		for _, item := range v {
			items = append(items, item)
		}
	default:
		return nil, false
	}
	for _, item := range items {
		if _, ok := item.(string); !ok {
			return nil, false
		}
	}
	return append([]any{}, items...), true
}

func savedGlobalFilters(env M) []any {
	if global, ok := env["global"].(M); ok {
		if pins, valid := pinnedStrings(global["liveTvSavedFilters"]); valid {
			return pins
		}
	}
	return nil
}

func StoredSavedFilters(env M) []any {
	if pins := savedGlobalFilters(env); pins != nil {
		return pins
	}
	return []any{}
}

// An explicit admin Merge restores managed pins without making them personal.
func MergeAdminSavedFilters(env M, raw any) {
	selected, ok := pinnedStrings(raw)
	excluded, exists := pinnedStrings(env["liveTvSavedFilterExclusions"])
	if !ok || !exists {
		return
	}
	env["liveTvSavedFilterExclusions"] = slices.DeleteFunc(excluded, func(pin any) bool {
		return slices.Contains(selected, pin)
	})
}

// AdminSavedFilters includes only global defaults; device pin lists are ignored.
func AdminSavedFilters(global M) []any {
	list, _ := pinnedStrings(global["liveTvSavedFilters"])
	pins := []any{}
	for _, pin := range list {
		if !slices.Contains(pins, pin) {
			pins = append(pins, pin)
		}
	}
	return pins
}

// EffectiveSavedFilters resolves current admin defaults and each user's customizations.
func EffectiveSavedFilters(env M, defaults any) []any {
	managed, _ := pinnedStrings(defaults)
	excluded, _ := pinnedStrings(env["liveTvSavedFilterExclusions"])
	personal := savedGlobalFilters(env)
	pins := make([]any, 0, len(managed)+len(personal))
	for _, pin := range managed {
		if !slices.Contains(excluded, pin) && !slices.Contains(pins, pin) {
			pins = append(pins, pin)
		}
	}
	for _, pin := range personal {
		if !slices.Contains(pins, pin) {
			pins = append(pins, pin)
		}
	}
	return pins
}

// Full-profile updates never change the canonical list.
func preserveSavedFilters(env M, stored []any) {
	global, _ := env["global"].(M)
	if stored != nil {
		if global == nil {
			global = M{}
			env["global"] = global
		}
		global["liveTvSavedFilters"] = append([]any{}, stored...)
	} else if global != nil {
		delete(global, "liveTvSavedFilters")
	}
	for _, name := range Profiles[1:] {
		if profile, ok := env[name].(M); ok {
			delete(profile, "liveTvSavedFilters")
		}
	}
}

// The API invokes this under its per-user settings lock.
func applySavedFilterChange(env M, change SavedFilterChange) {
	personal := savedGlobalFilters(env)
	if change.Action == "initializeIfAbsent" && personal != nil {
		return
	}
	if personal == nil {
		personal = []any{}
	}
	excluded, ok := pinnedStrings(env["liveTvSavedFilterExclusions"])
	if !ok {
		excluded = []any{}
	}
	switch change.Action {
	case "initializeIfAbsent":
		for _, pin := range change.Filters {
			if !slices.Contains(personal, any(pin)) {
				personal = append(personal, pin)
			}
		}
		// Existing local pins win on import, not over future new admin defaults.
		for _, pin := range change.Defaults {
			if !slices.Contains(personal, pin) && !slices.Contains(excluded, pin) {
				excluded = append(excluded, pin)
			}
		}
	case "add":
		if !slices.Contains(personal, any(change.Filter)) {
			personal = append(personal, change.Filter)
		}
		excluded = slices.DeleteFunc(excluded, func(pin any) bool { return pin == change.Filter })
	case "remove":
		personal = slices.DeleteFunc(personal, func(pin any) bool { return pin == change.Filter })
		if !slices.Contains(excluded, any(change.Filter)) {
			excluded = append(excluded, change.Filter)
		}
	}
	global, ok := env["global"].(M)
	if !ok {
		global = M{}
		env["global"] = global
	}
	global["liveTvSavedFilters"] = personal
	env["liveTvSavedFilterExclusions"] = excluded
}

func mergeProfile(existing, incoming M) {
	_, hasSections := incoming["homeSections"]
	if _, hasOrder := incoming["homeRowOrder"]; hasOrder && !hasSections {
		// A client that only knows homeRowOrder makes the order authoritative.
		delete(existing, "homeSections")
	}
	for k, v := range incoming {
		if s, ok := v.(string); ok && strings.Contains(k, "ApiKey") && (strings.TrimSpace(s) == "" || s == "null") {
			continue
		}
		existing[k] = v
	}
}

func finish(env M, clientID string, keys ServerKeys, now time.Time, stored map[string]*string) {
	stripServerKeys(env, keys)
	if clientID == "" {
		clientID = "unknown"
	}
	env["lastUpdated"] = now.UnixMilli()
	env["lastUpdatedBy"] = clientID
	env["schemaVersion"] = 2
	moveHidingToGlobal(env, stored)
}

func stripServerKeys(env M, keys ServerKeys) {
	objs := []M{env}
	for _, p := range Profiles {
		if m, ok := env[p].(M); ok {
			objs = append(objs, m)
		}
	}
	for _, o := range objs {
		if s, ok := o["tmdbApiKey"].(string); ok && keys.Tmdb != "" && s == keys.Tmdb {
			delete(o, "tmdbApiKey")
		}
		if s, ok := o["mdblistApiKey"].(string); ok && keys.Mdblist != "" && s == keys.Mdblist {
			delete(o, "mdblistApiKey")
		}
	}
}

// layoutKeys are the home layout, which Resolve takes whole from one layer.
var layoutKeys = []string{"homeSections", "homeRowOrder"}

// LayerDefaults lays the admin's defaults for one device over their defaults
// for every device. A device's home layout replaces the shared one whole.
func LayerDefaults(all, device M) M {
	if len(device) == 0 {
		return all
	}
	out := maps.Clone(all)
	if out == nil {
		out = M{}
	}
	if slices.ContainsFunc(layoutKeys, func(k string) bool { return device[k] != nil }) {
		for _, k := range layoutKeys {
			delete(out, k)
		}
	}
	maps.Copy(out, device)
	return out
}

// Without is a copy of m minus every key mask sets, and minus the whole home
// layout when mask has one.
func Without(m, mask M) M {
	out := maps.Clone(m)
	for k := range mask {
		delete(out, k)
	}
	if slices.ContainsFunc(layoutKeys, func(k string) bool { return mask[k] != nil }) {
		for _, k := range layoutKeys {
			delete(out, k)
		}
	}
	return out
}

// Resolve flattens the device profile over global over the admin defaults
// (LayerDefaults for that profile).
func Resolve(env M, name string, defaults M, keys ServerKeys) M {
	var device M
	if name != "global" {
		device, _ = env[name].(M)
	}
	global, _ := env["global"].(M)
	layers := []M{device, global, defaults}

	out := M{}
	for i := len(layers) - 1; i >= 0; i-- {
		for k, v := range layers[i] {
			if v == nil || k == "homeSections" || k == "homeRowOrder" || k == "liveTvSavedFilters" || aliases[k] != "" {
				continue
			}
			out[k] = v
		}
	}

	// Keep the old omitted-field response for profiles with no pins at all.
	if defaults["liveTvSavedFilters"] != nil || savedGlobalFilters(env) != nil || env["liveTvSavedFilterExclusions"] != nil {
		out["liveTvSavedFilters"] = EffectiveSavedFilters(env, defaults["liveTvSavedFilters"])
	}

	// The home layout comes whole from the first layer that has one.
	for _, l := range layers {
		sections, order := l["homeSections"], l["homeRowOrder"]
		if sections == nil && order == nil {
			continue
		}
		if sections != nil {
			out["homeSections"] = sections
		}
		if order == nil {
			order = deriveRowOrder(sections)
		}
		if order != nil {
			out["homeRowOrder"] = order
		}
		break
	}

	if sources, ok := out["mdblistRatingSources"].([]any); ok {
		fixed := slices.Clone(sources)
		for i, s := range fixed {
			if str, ok := s.(string); ok && strings.EqualFold(str, "rtAudience") {
				fixed[i] = "tomatoes_audience"
			}
		}
		out["mdblistRatingSources"] = fixed
	}
	fillKey(out, "tmdbApiKey", keys.Tmdb)
	fillKey(out, "mdblistApiKey", keys.Mdblist)
	for alias, canon := range aliases {
		if v, ok := out[canon]; ok {
			out[alias] = v
		}
	}
	return out
}

func fillKey(out M, key, server string) {
	if s, _ := out[key].(string); strings.TrimSpace(s) == "" && server != "" {
		out[key] = server
	}
}

func deriveRowOrder(sections any) any {
	list, _ := sections.([]any)
	type row struct {
		typ   string
		order float64
	}
	var rows []row
	for _, item := range list {
		s, _ := item.(M)
		typ, _ := s["type"].(string)
		kind, _ := s["kind"].(string)
		if strings.EqualFold(kind, "pluginDynamic") || s["enabled"] == false ||
			strings.TrimSpace(typ) == "" || strings.EqualFold(typ, "none") {
			continue
		}
		order, ok := s["order"].(float64)
		if !ok {
			order = 1 << 31
		}
		rows = append(rows, row{typ, order})
	}
	if len(rows) == 0 {
		return nil
	}
	slices.SortStableFunc(rows, func(a, b row) int { return cmp.Compare(a.order, b.order) })
	out := make([]any, len(rows))
	for i, r := range rows {
		out[i] = r.typ
	}
	return out
}

// Hidden Continue Watching and Next Up entries live only on global, as JSON
// strings mapping item id to the time it was hidden. Each device's last pull
// is kept as a baseline so an entry it dropped counts as unhidden.
var hideFields = []struct{ field, baseline string }{
	{"hiddenContinueWatchingItems", "continueWatching"},
	{"hiddenNextUpSeries", "nextUpSeries"},
}

func storedHidden(env M) map[string]*string {
	global, _ := env["global"].(M)
	out := map[string]*string{}
	for _, f := range hideFields {
		if s, ok := global[f.field].(string); ok {
			out[f.field] = &s
		}
	}
	return out
}

func moveHidingToGlobal(env M, stored map[string]*string) {
	global, ok := env["global"].(M)
	if !ok {
		global = M{}
		env["global"] = global
	}
	for _, f := range hideFields {
		st := stored[f.field]
		merged := map[string]string{}
		if st != nil {
			merged = parseHidden(*st)
		}
		pushed := false
		for _, name := range Profiles {
			p, _ := env[name].(M)
			value, ok := p[f.field].(string)
			if !ok || (name == "global" && st != nil && value == *st) {
				continue
			}
			pushed = true
			entries := parseHidden(value)
			if base, ok := baselineOf(env, name, f.baseline); ok {
				for k := range parseHidden(base) {
					if _, still := entries[k]; !still {
						delete(merged, k)
					}
				}
			}
			for k, t := range entries {
				if old, ok := merged[k]; !ok || isLaterHide(t, old) {
					merged[k] = t
				}
			}
			profileBaseline(env, name)[f.baseline] = value
		}
		if pushed {
			b, _ := json.Marshal(merged)
			global[f.field] = string(b)
		}
	}
	for _, name := range Profiles[1:] {
		if p, ok := env[name].(M); ok {
			for _, f := range hideFields {
				delete(p, f.field)
			}
		}
	}
}

// RecordBaseline notes what a device was just given by Resolve. It reports
// whether env changed and needs storing.
func RecordBaseline(env M, name string, resolved M) bool {
	cw, cwOK := resolved["hiddenContinueWatchingItems"].(string)
	nu, nuOK := resolved["hiddenNextUpSeries"].(string)
	if cur, ok := baselines(env, false)[name].(M); ok {
		oldCW, oldCWOK := cur["continueWatching"].(string)
		oldNU, oldNUOK := cur["nextUpSeries"].(string)
		if oldCWOK == cwOK && oldCW == cw && oldNUOK == nuOK && oldNU == nu {
			return false
		}
	}
	b := M{}
	if cwOK {
		b["continueWatching"] = cw
	}
	if nuOK {
		b["nextUpSeries"] = nu
	}
	baselines(env, true)[name] = b
	return true
}

func baselines(env M, create bool) M {
	b, ok := env["hiddenContentBaselines"].(M)
	if !ok && create {
		b = M{}
		env["hiddenContentBaselines"] = b
	}
	return b
}

func baselineOf(env M, name, field string) (string, bool) {
	p, _ := baselines(env, false)[name].(M)
	s, ok := p[field].(string)
	return s, ok
}

func profileBaseline(env M, name string) M {
	all := baselines(env, true)
	p, ok := all[name].(M)
	if !ok {
		p = M{}
		all[name] = p
	}
	return p
}

func parseHidden(s string) map[string]string {
	out := map[string]string{}
	if json.Unmarshal([]byte(s), &out) != nil || out == nil {
		return map[string]string{}
	}
	return out
}

var hideTimeLayouts = []string{time.RFC3339Nano, "2006-01-02T15:04:05.999999999"}

func isLaterHide(candidate, existing string) bool {
	c, cErr := parseTime(candidate)
	e, eErr := parseTime(existing)
	if cErr == nil && eErr == nil {
		return c.After(e)
	}
	return candidate > existing
}

func parseTime(s string) (time.Time, error) {
	var err error
	for _, layout := range hideTimeLayouts {
		var t time.Time
		if t, err = time.Parse(layout, s); err == nil {
			return t, nil
		}
	}
	return time.Time{}, err
}
