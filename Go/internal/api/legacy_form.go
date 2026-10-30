package api

import (
	"math"
	"slices"
	"strconv"
	"strings"
)

// Earlier releases also offered their settings on Silo's own plugin form, which
// Silo still keeps and hands over through Configure. The Moonbase page is
// now the only place to set them. Until it is first saved, the values from
// the old form are read under its settings (mergeConfig), and that save
// stores them with the page's settings for good.

type fieldKind int

const (
	kindBool      fieldKind = iota // a switch
	kindTribool                    // "Not set", "true" or "false"
	kindInt                        // a number
	kindIntText                    // a number the profile keeps as text
	kindIntChoice                  // a number picked from a list, stored by the form as text
	kindString                     // text or a choice, empty means not set
	kindSegment                    // one segment type's action in mediaSegmentActions ("intro:skip,...")
	kindSlot                       // one slot of a list such as bottomNavbarTabs
	kindShown                      // the options ticked, stored as the ones not ticked
)

type formField struct {
	Entry    string // the card in Silo
	Field    string // the field's key in Silo's form
	Key      string // the configuration key the value lands in
	Defaults bool   // a DefaultUserSettings key
	Kind     fieldKind
	Part     string   // which part of a combined value (kindSegment, kindSlot)
	Options  []string // every option (kindShown)
}

// SetNative takes what Silo holds for the old form, which it hands over
// through Configure once per start.
func (s *Server) SetNative(native map[string]M) {
	s.nativeMu.Lock()
	s.native = native
	s.nativeMu.Unlock()
}

func (s *Server) nativeConfig() map[string]M {
	s.nativeMu.RLock()
	defer s.nativeMu.RUnlock()
	return s.native
}

// fromNative turns the form's values into PluginConfiguration fields and
// DefaultUserSettings keys. Unset values are left out.
func fromNative(native map[string]M) (top, defaults M) {
	top, defaults = M{}, M{}
	segments, slots := map[string][]string{}, map[string][]any{}
	for _, f := range formFields {
		raw := native[f.Entry][f.Field]
		switch f.Kind {
		case kindSegment:
			if action, _ := raw.(string); action != "" {
				segments[f.Key] = append(segments[f.Key], f.Part+":"+action)
			}
			continue
		case kindSlot:
			if v, _ := raw.(string); v != "" && !slices.Contains(slots[f.Key], any(v)) {
				slots[f.Key] = append(slots[f.Key], v)
			}
			continue
		case kindShown:
			if shown, ok := raw.([]any); ok {
				var hidden []any
				for _, o := range f.Options {
					if !slices.Contains(shown, any(o)) {
						hidden = append(hidden, o)
					}
				}
				if len(hidden) > 0 {
					defaults[f.Key] = hidden
				}
			}
			continue
		}
		v, ok := convertIn(f.Kind, raw)
		if !ok {
			continue
		}
		if f.Defaults {
			defaults[f.Key] = v
		} else {
			top[f.Key] = v
		}
	}
	for k, parts := range segments {
		defaults[k] = strings.Join(parts, ",")
	}
	for k, list := range slots {
		defaults[k] = list
	}
	return top, defaults
}

func convertIn(kind fieldKind, v any) (any, bool) {
	switch kind {
	case kindBool:
		b, ok := v.(bool)
		return b, ok
	case kindTribool:
		switch v {
		case "true", true:
			return true, true
		case "false", false:
			return false, true
		}
	case kindInt, kindIntText, kindIntChoice:
		n, ok := num(v)
		if !ok {
			return nil, false
		}
		if kind == kindIntText {
			return strconv.Itoa(int(math.Round(n))), true
		}
		return int(math.Round(n)), true
	case kindString:
		if s, ok := v.(string); ok && strings.TrimSpace(s) != "" {
			return s, true
		}
	}
	return nil, false
}
