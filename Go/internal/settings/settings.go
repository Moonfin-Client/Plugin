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
func SaveProfile(env M, name string, incoming M, clientID string, keys ServerKeys, now time.Time) M {
	if env == nil {
		env = M{"schemaVersion": 2, "syncEnabled": true}
	}
	stored := storedHidden(env)
	if existing, ok := env[name].(M); ok {
		mergeProfile(existing, incoming)
	} else {
		env[name] = incoming
	}
	finish(env, clientID, keys, now, stored)
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
	if _, ok := incoming["syncEnabled"].(bool); !ok {
		incoming["syncEnabled"] = true
	}
	if mergeMode != "merge" || existing == nil {
		finish(incoming, clientID, keys, now, nil)
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
	return existing
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
			if v == nil || k == "homeSections" || k == "homeRowOrder" || aliases[k] != "" {
				continue
			}
			out[k] = v
		}
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
