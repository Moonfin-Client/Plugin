package anime

import (
	"slices"
	"testing"
)

var (
	attackOnTitan   = &Show{Slug: "attack-titan", Title: "Attack on Titan (Shingeki no Kyojin)"}
	naruto          = &Show{Slug: "naruto", Title: "Naruto"}
	narutoShippuden = &Show{Slug: "naruto-shippuden", Title: "Naruto Shippuden"}
)

func titleIndex() map[string]*Show {
	return BuildIndex([]*Show{attackOnTitan, naruto, narutoShippuden})
}

func TestMatchFindsTheShowUnderEverySpellingALibraryUses(t *testing.T) {
	for _, name := range []string{"Attack on Titan", "attack on titan", "Shingeki no Kyojin", "Attack on Titan (2013)", "Attack on Titan Season 3", "Attack on Titan Part 2"} {
		if got := Match(titleIndex(), name); got != attackOnTitan {
			t.Errorf("%q matched %+v", name, got)
		}
	}
}

func TestMatchKeepsSeparateEntriesApart(t *testing.T) {
	// The season suffix stripping mustn't fold Shippuden onto Naruto.
	if Match(titleIndex(), "Naruto") != naruto || Match(titleIndex(), "Naruto Shippuden") != narutoShippuden {
		t.Fatal("Naruto and Shippuden were mixed up")
	}
}

func TestMatchReturnsNilForUnrelatedTitles(t *testing.T) {
	for _, name := range []string{"Breaking Bad", "", "   "} {
		if got := Match(titleIndex(), name); got != nil {
			t.Errorf("%q matched %+v", name, got)
		}
	}
}

func TestMatchFallsBackToTheOriginalTitle(t *testing.T) {
	if Match(titleIndex(), "Something Localized", "Shingeki no Kyojin") != attackOnTitan {
		t.Fatal("the original title wasn't tried")
	}
}

func TestNormalizeFoldsPunctuationArticlesAndDiacritics(t *testing.T) {
	for input, want := range map[string]string{
		"The Promised Neverland":                   "promised neverland",
		"Fruits Basket":                            "fruits basket",
		"Re:ZERO -Starting Life in Another World-": "re zero starting life in another world",
		"Kaguya-sama: Love Is War":                 "kaguya sama love is war",
		"JoJo's Bizarre Adventure":                 "jojo s bizarre adventure",
		"Tokyo Ghoul √A":                           "tokyo ghoul a",
		"Fullmetal Alchemist & Brotherhood":        "fullmetal alchemist and brotherhood",
		"Pokémon":                                  "pokemon",
	} {
		if got := Normalize(input); got != want {
			t.Errorf("Normalize(%q) = %q, want %q", input, got, want)
		}
	}
}

func TestVariantsTreatsParenthesesAsAlternateNamesButNotYears(t *testing.T) {
	variants := Variants("Dororo (2019)")
	if !slices.Contains(variants, "dororo") || slices.Contains(variants, "2019") {
		t.Fatalf("variants = %v", variants)
	}
}

func TestBuildIndexLetsAShowsOwnNameBeatAnotherShowsAlternateName(t *testing.T) {
	// Verbatim from the site's index, and in its order: the fan re-edit sorts
	// first and lists the real show as its alternate name.
	onePace := &Show{Slug: "one-pace", Title: "One Pace (One Piece)"}
	onePiece := &Show{Slug: "one-piece", Title: "One Piece"}
	index := BuildIndex([]*Show{onePace, onePiece})
	if Match(index, "One Piece") != onePiece || Match(index, "One Pace") != onePace {
		t.Fatal("One Pace took One Piece's name")
	}
}

func TestMatchTellsTwoAdaptationsApartByTheirYear(t *testing.T) {
	// Also verbatim: both are written with U+00D7, and the 1999 entry has a
	// trailing space. Only the year separates them.
	original := &Show{Slug: "hunter-x-hunter-1999", Title: "Hunter × Hunter "}
	remake := &Show{Slug: "hunter-x-hunter", Title: "Hunter × Hunter (2011)"}
	index := BuildIndex([]*Show{original, remake})
	if Match(index, "Hunter x Hunter (2011)") != remake || Match(index, "Hunter x Hunter") != original {
		t.Fatal("the two adaptations were mixed up")
	}
}

func TestNormalizeReadsTheMultiplicationSignAsAnX(t *testing.T) {
	if got := Normalize("Hunter × Hunter"); got != "hunter x hunter" {
		t.Fatalf("got %q", got)
	}
}

func TestBuildIndexLetsTheFirstShowKeepAContestedKey(t *testing.T) {
	// The site's own index has a row whose slug belongs to an unrelated show,
	// so a later collision mustn't overwrite a good mapping.
	first := &Show{Slug: "real-show", Title: "Same Name"}
	second := &Show{Slug: "wrong-show", Title: "Same Name"}
	if Match(BuildIndex([]*Show{first, second}), "Same Name") != first {
		t.Fatal("the later show took the key")
	}
}
