package anime

import (
	"regexp"
	"slices"
	"strings"
	"unicode"

	"golang.org/x/text/unicode/norm"
)

var (
	nonAlphanumeric = regexp.MustCompile(`[^a-z0-9]+`)
	parenthetical   = regexp.MustCompile(`\(([^)]*)\)`)
	yearOnly        = regexp.MustCompile(`^(19|20)\d{2}$`)
	// The site sometimes ends a title with a season marker that isn't part of the name.
	seasonSuffix = regexp.MustCompile(`\s+(season\s*\d+|\d+(st|nd|rd|th)\s+season|part\s*\d+|cour\s*\d+|s\d+)$`)
)

// Normalize folds diacritics, lowercases and drops punctuation, leaving the
// key titles are compared on.
func Normalize(title string) string {
	if strings.TrimSpace(title) == "" {
		return ""
	}
	folded := foldDiacritics(title)
	// The site writes "Hunter × Hunter" with U+00D7 while libraries use a
	// letter x, and dropping it as punctuation would leave "hunter hunter".
	folded = strings.ReplaceAll(folded, "×", "x")
	folded = strings.ReplaceAll(folded, "&", " and ")
	folded = strings.ReplaceAll(folded, "＆", " and ")
	cleaned := strings.TrimSpace(nonAlphanumeric.ReplaceAllString(strings.ToLower(folded), " "))
	// "The Promised Neverland" and "Promised Neverland" are the same show, and
	// the two sites disagree about the article often enough to matter.
	for _, article := range []string{"the ", "a ", "an "} {
		if rest, ok := strings.CutPrefix(cleaned, article); ok {
			return rest
		}
	}
	return cleaned
}

func foldDiacritics(s string) string {
	var b strings.Builder
	for _, r := range norm.NFD.String(s) {
		if !unicode.Is(unicode.Mn, r) {
			b.WriteRune(r)
		}
	}
	return norm.NFC.String(b.String())
}

// PrimaryVariants are the keys built from the title itself. A year in
// parentheses is kept as part of the key rather than read as another name.
func PrimaryVariants(title string) []string {
	bare := Normalize(parenthetical.ReplaceAllString(title, " "))
	if bare == "" {
		return nil
	}
	var keys []string
	// "Hunter x Hunter (2011)" and "Hunter x Hunter" are different shows with
	// different filler lists, so the year is tried before the bare name.
	for _, m := range parenthetical.FindAllStringSubmatch(title, -1) {
		if inner := strings.TrimSpace(m[1]); yearOnly.MatchString(inner) {
			keys = append(keys, bare+" "+inner)
		}
	}
	return uniqueKeys(append(keys, bare, strings.TrimSpace(seasonSuffix.ReplaceAllString(bare, ""))))
}

// AlternateVariants are the keys built from the names in a title's parentheses.
func AlternateVariants(title string) []string {
	var keys []string
	for _, m := range parenthetical.FindAllStringSubmatch(title, -1) {
		if inner := strings.TrimSpace(m[1]); inner != "" && !yearOnly.MatchString(inner) {
			normalized := Normalize(inner)
			keys = append(keys, normalized, strings.TrimSpace(seasonSuffix.ReplaceAllString(normalized, "")))
		}
	}
	return uniqueKeys(keys)
}

// Variants is every key a title should be found under, most specific first.
func Variants(title string) []string {
	return uniqueKeys(append(PrimaryVariants(title), AlternateVariants(title)...))
}

// uniqueKeys drops empty and repeated keys, keeping the first of each.
func uniqueKeys(keys []string) []string {
	var out []string
	for _, key := range keys {
		if key != "" && !slices.Contains(out, key) {
			out = append(out, key)
		}
	}
	return out
}

// BuildIndex maps every key to the show that owns it. Every show's own name
// claims its keys before any show's alternate names do, and a key two shows
// share stays with the first.
func BuildIndex(shows []*Show) map[string]*Show {
	index := map[string]*Show{}
	claim := func(variants func(string) []string) {
		for _, show := range shows {
			for _, key := range variants(show.Title) {
				if _, taken := index[key]; !taken {
					index[key] = show
				}
			}
		}
	}
	claim(PrimaryVariants)
	claim(AlternateVariants)
	return index
}

// Match is the first show any of the titles leads to, or nil.
func Match(index map[string]*Show, titles ...string) *Show {
	return match(index, Variants, titles)
}

// MatchPrimary is Match without the names inside parentheses. It's for
// AniList's titles, where "Konpeki no Hitsugi (Jolly Roger)" is a subtitle
// rather than another name for the show.
func MatchPrimary(index map[string]*Show, titles []string) *Show {
	return match(index, PrimaryVariants, titles)
}

func match(index map[string]*Show, variants func(string) []string, titles []string) *Show {
	for _, title := range titles {
		for _, key := range variants(title) {
			if show, ok := index[key]; ok {
				return show
			}
		}
	}
	return nil
}
