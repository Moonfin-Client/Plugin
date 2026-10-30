package themes

import (
	"encoding/json"
	"slices"
	"testing"
)

const colour = "#FF112233"

func colours(keys []string) M {
	m := M{}
	for _, k := range keys {
		m[k] = colour
	}
	return m
}

// Valid is a theme the validator accepts, as the Emby plugin's ThemeFixture
// builds it, run through JSON so numbers are float64 as on the wire.
func Valid() M {
	border := func() M { return M{"color": colour, "width": 2} }
	book := colours(requiredBookColors)
	book["placeholderPalette"] = []any{colour}
	theme := M{
		"schemaVersion": 1, "id": "midnight-blue", "displayName": "Midnight Blue",
		"colors": colours(requiredColors), "semantic": colours(requiredSemantic), "book": book,
		"borders": M{
			"cardBorder": border(), "chipBorder": border(), "focusBorder": border(),
			"cardRadius": 8, "chipRadius": 4, "chipBackground": colour,
			"focusGlow": []any{M{"color": colour, "blurRadius": 4, "offsetX": 0, "offsetY": 1, "spreadRadius": 2}},
		},
	}
	raw, _ := json.Marshal(theme)
	var out M
	_ = json.Unmarshal(raw, &out)
	return out
}

func TestValidAccepted(t *testing.T) {
	id, name, errs := Validate(Valid())
	if len(errs) != 0 || id != "midnight-blue" || name != "Midnight Blue" {
		t.Fatalf("%q %q %v", id, name, errs)
	}
}

func TestRejections(t *testing.T) {
	for want, mutate := range map[string]func(M){
		"id must match ^[a-z0-9_-]{2,40}$.":                                    func(m M) { m["id"] = "Bad Id" },
		"displayName cannot contain script tags.":                              func(m M) { m["displayName"] = "<SCRIPT>x" },
		"schemaVersion must be 1 or lower.":                                    func(m M) { m["schemaVersion"] = 2.0 },
		"schemaVersion must be an integer.":                                    func(m M) { m["schemaVersion"] = 1.5 },
		"colors.accent is required.":                                           func(m M) { delete(m["colors"].(M), "accent") },
		"semantic.statusPending must be a valid color (#RRGGBB or #AARRGGBB).": func(m M) { m["semantic"].(M)["statusPending"] = "red" },
		"book.placeholderPalette must contain 1 to 16 colors.":                 func(m M) { m["book"].(M)["placeholderPalette"] = []any{} },
		"borders.cardBorder.width must be between 0 and 16.":                   func(m M) { m["borders"].(M)["cardBorder"].(M)["width"] = 17.0 },
		"borders.focusGlow[0].spreadRadius is required.":                       func(m M) { delete(m["borders"].(M)["focusGlow"].([]any)[0].(M), "spreadRadius") },
		"textGlow[0].spreadRadius must be 0 for textGlow.": func(m M) {
			m["textGlow"] = []any{M{"color": colour, "blurRadius": 1.0, "offsetX": 0.0, "offsetY": 0.0, "spreadRadius": 1.0}}
		},
		"isGlass must be true or false.":                        func(m M) { m["isGlass"] = "yes" },
		"borders.chipRadius must be a number or corner object.": func(m M) { m["borders"].(M)["chipRadius"] = "4" },
	} {
		theme := Valid()
		mutate(theme)
		if _, _, errs := Validate(theme); !slices.Contains(errs, want) {
			t.Errorf("want %q, got %v", want, errs)
		}
	}
}
