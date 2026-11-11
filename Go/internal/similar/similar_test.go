package similar

import (
	"fmt"
	"math"
	"slices"
	"testing"
	"time"
)

type item struct {
	Facts
	premiere time.Time
	people   []Credit
}

func year(y int) *int           { return &y }
func rating(r float32) *float32 { return &r }

func movie(name string, y int, genres ...string) *item {
	return &item{Facts: Facts{Name: name, Genres: genres, Year: year(y)}}
}

func rank(seed *item, candidates []*item, limit int) []string {
	got := Rank(candidates, NewSeed(seed.Facts, seed.people), EffectiveLimit(limit),
		func(i *item) Facts { return i.Facts },
		func(i *item) time.Time { return i.premiere },
		func([]*item) func(*item) []Credit { return func(i *item) []Credit { return i.people } })
	names := []string{}
	for _, i := range got {
		names = append(names, i.Name)
	}
	return names
}

func expect(t *testing.T, got []string, want ...string) {
	t.Helper()
	if !slices.Equal(got, want) {
		t.Fatalf("ranked %q, want %q", got, want)
	}
}

func TestSharedGenresOutrankUnrelatedItems(t *testing.T) {
	seed := movie("Seed", 2020, "Science Fiction", "Adventure")
	got := rank(seed, []*item{movie("Match", 2020, "Science Fiction", "Adventure"), movie("Partial", 2020, "Adventure"), movie("Unrelated", 2020, "Romance")}, 0)
	expect(t, got, "Match", "Partial", "Unrelated")
}

func TestGenreScoreIsCappedSoOneSpamEntryCannotDominate(t *testing.T) {
	var shared []string
	for i := range 12 {
		shared = append(shared, fmt.Sprintf("Genre%d", i))
	}
	// Seven shared genres already reach the cap, so the newer premiere breaks the tie.
	capped := movie("Capped", 2000, shared[:7]...)
	capped.premiere = time.Date(2000, 6, 1, 0, 0, 0, 0, time.UTC)
	spammer := movie("Spammer", 2000, shared...)
	spammer.premiere = time.Date(2000, 1, 1, 0, 0, 0, 0, time.UTC)
	if got := rank(movie("Seed", 2000, shared...), []*item{spammer, capped}, 0); got[0] != "Capped" {
		t.Fatalf("ranked %q", got)
	}
}

func TestCloserProductionYearsRankHigher(t *testing.T) {
	got := rank(movie("Seed", 2010, "Drama"), []*item{movie("Far", 1989, "Drama"), movie("Near", 2011, "Drama")}, 0)
	expect(t, got, "Near", "Far")
}

func TestSequelTitlesGetABonus(t *testing.T) {
	got := rank(movie("Blade Runner", 1982, "Science Fiction"), []*item{movie("Total Recall", 1982, "Science Fiction"), movie("Blade Runner 2049", 1982, "Science Fiction")}, 0)
	expect(t, got, "Blade Runner 2049", "Total Recall")
}

func TestPluralizedSequelTitlesGetABonus(t *testing.T) {
	got := rank(movie("Alien", 1979, "Science Fiction"), []*item{movie("Total Recall", 1979, "Science Fiction"), movie("Aliens", 1979, "Science Fiction")}, 0)
	expect(t, got, "Aliens", "Total Recall")
	if similarTitle(keywords("Alien"), "The Alienist") {
		t.Fatal("The Alienist isn't a sequel of Alien")
	}
}

func TestStopWordsAndShortWordsDoNotEarnATitleBonus(t *testing.T) {
	got := rank(movie("The Godfather", 1972, "Crime"), []*item{movie("The Notebook", 1972, "Crime"), movie("Godfather Part II", 1972, "Crime")}, 0)
	expect(t, got, "Godfather Part II", "The Notebook")
}

func TestATitlelessSeedDoesNotGiveEveryCandidateTheBonus(t *testing.T) {
	got := rank(movie("The Fly", 1986, "Horror"), []*item{movie("Videodrome", 1976, "Horror"), movie("The Thing", 1986, "Horror")}, 0)
	expect(t, got, "The Thing", "Videodrome")
}

func TestSharedStudiosAndCloseRatingsRankHigher(t *testing.T) {
	with := func(name, studio string, r float32) *item {
		i := movie(name, 2015, "Drama")
		i.Studios, i.CommunityRating = []string{studio}, rating(r)
		return i
	}
	got := rank(with("Seed", "A24", 8.0), []*item{with("FarRating", "Other", 2.0), with("CloseRating", "Other", 8.1), with("SameStudio", "A24", 8.1)}, 0)
	expect(t, got, "SameStudio", "CloseRating", "FarRating")
}

func TestSharedCastAndCrewRaiseTheRanking(t *testing.T) {
	seed := movie("Seed", 2015, "Drama")
	seed.people = []Credit{{"Denis Villeneuve", Director}}
	with := movie("WithDirector", 2015, "Drama")
	with.people = []Credit{{"Denis Villeneuve", Director}}
	without := movie("WithoutDirector", 2015, "Drama")
	without.people = []Credit{{"Someone Else", Director}}
	if got := rank(seed, []*item{without, with}, 0); got[0] != "WithDirector" {
		t.Fatalf("ranked %q", got)
	}
}

func TestSharedCastAndCrewUseDiminishingReturns(t *testing.T) {
	seed := movie("Seed", 2015, "Drama")
	seed.people = []Credit{{"Director A", Director}, {"Director B", Director}}
	one := movie("OneMatch", 2015, "Drama")
	one.people = []Credit{{"Director A", Director}}
	two := movie("TwoMatches", 2015, "Drama")
	two.people = []Credit{{"Director A", Director}, {"Director B", Director}}
	expect(t, rank(seed, []*item{one, two}, 0), "TwoMatches", "OneMatch")
}

func TestLimitIsHonoredAndClamped(t *testing.T) {
	var catalog []*item
	for i := range 50 {
		catalog = append(catalog, movie(fmt.Sprintf("Item%d", i), 2015, "Drama"))
	}
	seed := movie("Seed", 2015, "Drama")
	for limit, want := range map[int]int{5: 5, 0: 20, -3: 20, 10_000: 50} {
		if got := len(rank(seed, catalog, limit)); got != want {
			t.Errorf("limit %d returned %d, want %d", limit, got, want)
		}
	}
	if EffectiveLimit(10_000) != MaxLimit {
		t.Fatalf("limit isn't capped at %d", MaxLimit)
	}
}

// The points behind the rankings above, worked out by hand.
func TestScoresMatchTheCSharpPoints(t *testing.T) {
	seed := NewSeed(Facts{Name: "Blade Runner", Genres: []string{"Drama", "Science Fiction", "Thriller"}, Tags: []string{"noir"},
		Studios: []string{"Warner", "Shaw"}, Year: year(1982), CommunityRating: rating(8.0)},
		[]Credit{{"A", Actor}, {"B", Actor}, {"C", Actor}, {"D", Actor}, {"R", Director}, {"W1", Writer}, {"W2", Writer}, {"X", Other}})
	cases := []struct {
		name string
		got  float64
		want float64
	}{
		// 2 genres 14, a tag 4, both studios 20, three years apart 8, half a
		// point of rating apart 9.5, a sequel 25.
		{"metadata", scoreMetadata(Facts{Name: "Blade Runner 2049", Genres: []string{"drama", "SCIENCE FICTION", "Western"}, Tags: []string{"Noir"},
			Studios: []string{"Warner", "Shaw"}, Year: year(1985), CommunityRating: rating(7.5)}, seed), 80.5},
		// A repeated genre counts twice, and without a seed rating the candidate's own counts.
		{"repeats", scoreMetadata(Facts{Genres: []string{"Drama", "Drama"}, Year: year(2000), CommunityRating: rating(7.5)},
			NewSeed(Facts{Genres: []string{"Drama"}}, nil)), 14 + 7.5},
		{"genre cap", scoreMetadata(Facts{Genres: []string{"Drama", "Drama", "Drama", "Drama", "Drama", "Drama"}}, seed), 35},
		{"years apart", scoreMetadata(Facts{Year: year(1997)}, seed), 0},
		// Three actors 20 (a fourth adds nothing), a director 15, two writers
		// 25, and Other never counts.
		{"people", scorePeople([]Credit{{"a", Actor}, {"B", Actor}, {"C", Actor}, {"D", Actor}, {"R", Director}, {"W1", Writer}, {"W2", Writer}, {"X", Other}}, seed), 60},
	}
	for _, tc := range cases {
		if math.Abs(tc.got-tc.want) > 1e-9 {
			t.Errorf("%s = %v, want %v", tc.name, tc.got, tc.want)
		}
	}
}

func TestKeywordsKeepLettersOfAnyScript(t *testing.T) {
	got := keywords("Amélie: Part_2 of the Spider-Man saga")
	want := map[string]bool{"amélie": true, "part_2": true, "spider": true, "saga": true}
	if len(got) != len(want) {
		t.Fatalf("keywords = %v", got)
	}
	for k := range want {
		if !got[k] {
			t.Fatalf("keywords = %v, missing %q", got, k)
		}
	}
}
