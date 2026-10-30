// Package themes checks uploaded custom themes against the schema the
// Moonfin apps load, ported from the Jellyfin plugin's MoonfinThemeValidator
// so a theme either plugin accepts loads the same everywhere.
package themes

import (
	"fmt"
	"math"
	"regexp"
	"strconv"
	"strings"
)

type M = map[string]any

var (
	idPattern    = regexp.MustCompile(`^[a-z0-9_-]{2,40}$`)
	colorPattern = regexp.MustCompile(`^#(?:[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$`)
)

var (
	requiredColors = []string{
		"background", "onBackground", "surface", "onSurface", "surfaceVariant", "scrim",
		"accent", "onAccent", "buttonNormal", "buttonFocused", "buttonDisabled", "buttonActive",
		"onButtonNormal", "onButtonFocused", "onButtonDisabled", "inputBackground", "inputFocused",
		"inputBorder", "inputBorderFocused", "rangeTrack", "rangeProgress", "rangeThumb", "seekbarBuffered",
		"badgeBackground", "onBadge", "badgeUnplayed", "badgeWatched", "recordingActive", "recordingScheduled",
	}
	requiredSemantic = []string{
		"statusAvailable", "statusRequested", "statusPending", "statusDownloading",
		"mediaTypeBadgeMovie", "mediaTypeBadgeShow",
	}
	requiredBookColors = []string{
		"background", "accent", "mutedText", "primaryText", "sectionTitle", "divider",
		"placeholder", "shadow", "gradientTop", "gradientBottom", "inactiveChip",
	}
	radiusCorners = []string{"topLeft", "topRight", "bottomLeft", "bottomRight"}
	// Optional fields the lists above don't cover. A wrong type still breaks
	// theme loading in the apps, so they're checked too.
	optionalColors = []string{"error", "card"}
	optionalFlags  = []string{"transparentNavbarSurface", "isGlass", "isPixel"}
)

type checker struct{ errs []string }

func (c *checker) add(format string, args ...any) {
	c.errs = append(c.errs, fmt.Sprintf(format, args...))
}

// Validate checks a theme and returns its id and display name, or the
// problems found.
func Validate(theme M) (id, displayName string, errs []string) {
	c := &checker{}
	if v, ok := theme["schemaVersion"]; ok {
		n, isNum := v.(float64)
		switch {
		case !isNum || n != math.Trunc(n) || n < math.MinInt32 || n > math.MaxInt32:
			c.add("schemaVersion must be an integer.")
		case n > 1:
			c.add("schemaVersion must be 1 or lower.")
		}
	}

	id = strings.TrimSpace(c.requiredString(theme, "id"))
	if id != "" && !idPattern.MatchString(id) {
		c.add("id must match ^[a-z0-9_-]{2,40}$.")
	}
	displayName = strings.TrimSpace(c.requiredString(theme, "displayName"))
	if hasScriptTag(displayName) {
		c.add("displayName cannot contain script tags.")
	}
	c.optionalString(theme, "description")
	if d, ok := theme["description"].(string); ok && hasScriptTag(d) {
		c.add("description cannot contain script tags.")
	}
	c.optionalString(theme, "fontFamily")
	for _, k := range optionalFlags {
		if v, ok := theme[k]; ok {
			if _, isBool := v.(bool); !isBool {
				c.add("%s must be true or false.", k)
			}
		}
	}

	if colors := c.requiredObject(theme, "colors"); colors != nil {
		for _, k := range requiredColors {
			c.requiredColor(colors, k, "colors."+k)
		}
		for _, k := range optionalColors {
			c.optionalColor(colors, k, "colors."+k)
		}
	}
	if semantic := c.requiredObject(theme, "semantic"); semantic != nil {
		for _, k := range requiredSemantic {
			c.requiredColor(semantic, k, "semantic."+k)
		}
		c.optionalColor(semantic, "statusError", "semantic.statusError")
	}
	if book := c.requiredObject(theme, "book"); book != nil {
		for _, k := range requiredBookColors {
			c.requiredColor(book, k, "book."+k)
		}
		switch palette, ok := book["placeholderPalette"]; {
		case !ok:
			c.add("book.placeholderPalette is required.")
		default:
			list, isList := palette.([]any)
			if !isList {
				c.add("book.placeholderPalette must be an array.")
				break
			}
			if len(list) < 1 || len(list) > 16 {
				c.add("book.placeholderPalette must contain 1 to 16 colors.")
			}
			for i, v := range list {
				c.color(v, fmt.Sprintf("book.placeholderPalette[%d]", i))
			}
		}
	}
	if borders := c.requiredObject(theme, "borders"); borders != nil {
		for _, k := range []string{"cardBorder", "chipBorder", "focusBorder"} {
			c.requiredBorder(borders, k, "borders."+k)
		}
		c.radius(borders, "cardRadius", "borders.cardRadius")
		c.radius(borders, "chipRadius", "borders.chipRadius")
		c.requiredColor(borders, "chipBackground", "borders.chipBackground")
		if nav, ok := borders["navBorder"]; ok && nav != nil {
			if obj, isObj := nav.(M); isObj {
				c.border(obj, "borders.navBorder")
			} else {
				c.add("borders.navBorder must be an object when provided.")
			}
		}
		if glow, ok := borders["focusGlow"]; ok {
			c.shadows(glow, "borders.focusGlow", true)
		} else {
			c.add("borders.focusGlow is required.")
		}
	}
	if glow, ok := theme["textGlow"]; ok {
		c.shadows(glow, "textGlow", false)
	}
	if cycle, ok := theme["navColorCycle"]; ok {
		list, isList := cycle.([]any)
		if !isList {
			c.add("navColorCycle must be an array when provided.")
		} else {
			if len(list) > 16 {
				c.add("navColorCycle must contain at most 16 colors.")
			}
			for i, v := range list {
				c.color(v, fmt.Sprintf("navColorCycle[%d]", i))
			}
		}
	}
	return id, displayName, c.errs
}

func (c *checker) shadows(v any, path string, allowSpread bool) {
	list, ok := v.([]any)
	if !ok {
		c.add("%s must be an array.", path)
		return
	}
	if len(list) > 8 {
		c.add("%s must contain at most 8 entries.", path)
	}
	for i, e := range list {
		p := fmt.Sprintf("%s[%d]", path, i)
		entry, ok := e.(M)
		if !ok {
			c.add("%s must be an object.", p)
			continue
		}
		if col, ok := entry["color"]; ok {
			c.color(col, p+".color")
		} else {
			c.add("%s.color is required.", p)
		}
		c.requiredNumber(entry, "blurRadius", p+".blurRadius", 0, 64)
		c.requiredNumber(entry, "offsetX", p+".offsetX", -500, 500)
		c.requiredNumber(entry, "offsetY", p+".offsetY", -500, 500)
		spread, has := entry["spreadRadius"]
		switch {
		case has && !allowSpread:
			if n, isNum := spread.(float64); !isNum || n != 0 {
				c.add("%s.spreadRadius must be 0 for textGlow.", p)
			}
		case has:
			c.number(spread, p+".spreadRadius", -32, 32)
		case allowSpread:
			c.add("%s.spreadRadius is required.", p)
		}
	}
}

func (c *checker) requiredBorder(owner M, key, path string) {
	v, ok := owner[key]
	if !ok {
		c.add("%s is required.", path)
		return
	}
	obj, ok := v.(M)
	if !ok {
		c.add("%s must be an object.", path)
		return
	}
	c.border(obj, path)
}

func (c *checker) border(obj M, path string) {
	if col, ok := obj["color"]; ok {
		c.color(col, path+".color")
	} else {
		c.add("%s.color is required.", path)
	}
	c.requiredNumber(obj, "width", path+".width", 0, 16)
}

func (c *checker) radius(owner M, key, path string) {
	v, ok := owner[key]
	if !ok {
		c.add("%s is required.", path)
		return
	}
	if _, isNum := v.(float64); isNum {
		c.number(v, path, 0, 9999)
		return
	}
	corners, ok := v.(M)
	if !ok {
		c.add("%s must be a number or corner object.", path)
		return
	}
	for _, k := range radiusCorners {
		c.requiredNumber(corners, k, path+"."+k, 0, 9999)
	}
}

func (c *checker) requiredNumber(owner M, key, path string, lo, hi float64) {
	v, ok := owner[key]
	if !ok {
		c.add("%s is required.", path)
		return
	}
	c.number(v, path, lo, hi)
}

func (c *checker) number(v any, path string, lo, hi float64) {
	n, ok := v.(float64)
	if !ok {
		c.add("%s must be a number.", path)
		return
	}
	if n < lo || n > hi {
		c.add("%s must be between %s and %s.", path, strconv.FormatFloat(lo, 'f', -1, 64), strconv.FormatFloat(hi, 'f', -1, 64))
	}
}

func (c *checker) requiredColor(owner M, key, path string) {
	v, ok := owner[key]
	if !ok {
		c.add("%s is required.", path)
		return
	}
	c.color(v, path)
}

func (c *checker) optionalColor(owner M, key, path string) {
	if v, ok := owner[key]; ok {
		c.color(v, path)
	}
}

func (c *checker) color(v any, path string) {
	s, ok := v.(string)
	if !ok {
		c.add("%s must be a string color (#RRGGBB or #AARRGGBB).", path)
		return
	}
	if !colorPattern.MatchString(s) {
		c.add("%s must be a valid color (#RRGGBB or #AARRGGBB).", path)
	}
}

func (c *checker) optionalString(owner M, key string) {
	if v, ok := owner[key]; ok {
		if _, isStr := v.(string); !isStr {
			c.add("%s must be a string.", key)
		}
	}
}

func (c *checker) requiredString(owner M, key string) string {
	v, ok := owner[key]
	if !ok {
		c.add("%s is required.", key)
		return ""
	}
	s, ok := v.(string)
	if !ok {
		c.add("%s must be a string.", key)
		return ""
	}
	if strings.TrimSpace(s) == "" {
		c.add("%s cannot be empty.", key)
		return ""
	}
	return s
}

func (c *checker) requiredObject(owner M, key string) M {
	v, ok := owner[key]
	if !ok {
		c.add("%s is required.", key)
		return nil
	}
	obj, ok := v.(M)
	if !ok {
		c.add("%s must be an object.", key)
		return nil
	}
	return obj
}

func hasScriptTag(s string) bool {
	l := strings.ToLower(s)
	return strings.Contains(l, "<script") || strings.Contains(l, "</script")
}
