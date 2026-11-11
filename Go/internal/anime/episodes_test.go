package anime

import (
	"fmt"
	"slices"
	"testing"
	"time"
)

func TestPerSeasonNumberingIsFlattened(t *testing.T) {
	checkNumbers(t, []SeasonIndex{{1, 1}, {1, 2}, {1, 3}, {2, 1}, {2, 2}}, 1, 2, 3, 4, 5)
}

func TestAbsoluteNumberingIsKeptAsIs(t *testing.T) {
	// How One Piece is usually laid out: seasons are arcs, episode numbers run through.
	checkNumbers(t, []SeasonIndex{{3, 144}, {3, 145}, {4, 207}, {4, 208}}, 144, 145, 207, 208)
}

func TestAGapInsideASeasonDoesNotShiftLaterEpisodes(t *testing.T) {
	// Season 1 is four episodes long whether or not they're all there.
	checkNumbers(t, []SeasonIndex{{1, 1}, {1, 3}, {1, 4}, {2, 1}, {2, 2}}, 1, 3, 4, 5, 6)
}

func TestSingleSeasonSeriesAreUnchanged(t *testing.T) {
	checkNumbers(t, []SeasonIndex{{1, 1}, {1, 2}, {1, 3}}, 1, 2, 3)
}

func TestThreeSeasonsAccumulate(t *testing.T) {
	// Season 1 is 25 long and season 2 is 12, so season 3 episode 1 is 38.
	checkNumbers(t, []SeasonIndex{{1, 25}, {2, 1}, {2, 12}, {3, 1}}, 25, 26, 37, 38)
}

func TestRepeatedSlotsDoNotChangeTheNumberingScheme(t *testing.T) {
	slots := []SeasonIndex{{3, 144}, {3, 145}, {4, 207}}
	if !IsAlreadyAbsolute(slots) {
		t.Fatal("distinct numbers weren't read as absolute")
	}
	checkNumbers(t, slots, 144, 145, 207)
}

func TestEmptyInputIsEmpty(t *testing.T) {
	if len(AssignAbsoluteNumbers(nil)) != 0 {
		t.Fatal("numbers for nothing")
	}
}

func checkNumbers(t *testing.T, slots []SeasonIndex, want ...int) {
	t.Helper()
	if got := AssignAbsoluteNumbers(slots); !slices.Equal(got, want) {
		t.Fatalf("got %v, want %v", got, want)
	}
}

var firstAirDate = date(1997, 4, 1)

// A Pokémon-like show: the library has the original 43 episodes, and the site
// leaves out 18, 35 and 38 the way the dub did.
var skippedBySite = []int{18, 35, 38}

func weekly(n int) time.Time { return firstAirDate.AddDate(0, 0, 7*(n-1)) }

func siteTable(count int, dateOf func(int) time.Time, titleOf func(int) string) []Episode {
	var out []Episode
	for n := 1; n <= count; n++ {
		e := Episode{Number: n, Kind: MangaCanon, AirDate: dateOf(n)}
		if n%5 == 0 {
			e.Kind = Filler
		}
		if titleOf != nil {
			e.Title = titleOf(n)
		}
		out = append(out, e)
	}
	return out
}

func library(count int, dateOf func(int) time.Time, titleOf func(int) string) []Slot {
	var out []Slot
	for n := 1; n <= count; n++ {
		s := Slot{Number: n, AirDate: dateOf(n)}
		if titleOf != nil {
			s.Title = titleOf(n)
		}
		out = append(out, s)
	}
	return out
}

func originals() []int {
	var out []int
	for n := 1; n <= 43; n++ {
		if !slices.Contains(skippedBySite, n) {
			out = append(out, n)
		}
	}
	return out
}

// dubOrderedSite is the site's table for the dub-ordered show, numbered 1..40 over the original 43.
func dubOrderedSite(originalDate func(int) time.Time) []Episode {
	o := originals()
	return siteTable(len(o), func(n int) time.Time { return originalDate(o[n-1]) }, nil)
}

func noDate(int) time.Time { return time.Time{} }

func samePointers(site []Episode, markers []*Episode) bool {
	if len(site) != len(markers) {
		return false
	}
	for i := range site {
		if markers[i] != &site[i] {
			return false
		}
	}
	return true
}

func TestMatchKeepsNumbersWhenTheyAgreeWithTheAirDates(t *testing.T) {
	site := siteTable(30, weekly, nil)
	result := MatchEpisodes(library(30, weekly, nil), site)
	if result.Mode != NumberMode || !samePointers(site, result.Markers) || result.NumberAgreements != 30 {
		t.Fatalf("result = %+v", result)
	}
}

func TestMatchKeepsNumbersWhenTheTableHasNoDates(t *testing.T) {
	// A table cached before air dates were read behaves as it always has.
	site := siteTable(30, noDate, nil)
	result := MatchEpisodes(library(30, weekly, nil), site)
	if result.Mode != NumberMode || !samePointers(site, result.Markers) {
		t.Fatalf("result = %+v", result)
	}
}

func TestMatchKeepsNumbersWhenTheLibraryHasNoDates(t *testing.T) {
	site := dubOrderedSite(weekly)
	result := MatchEpisodes(library(43, noDate, nil), site)
	if result.Mode != NumberMode || result.Markers[17] != &site[17] {
		t.Fatalf("result = %+v", result)
	}
}

func TestMatchKeepsNumbersWhenTooFewEpisodesAreDated(t *testing.T) {
	lib := library(43, func(n int) time.Time {
		if n <= 5 {
			return weekly(n)
		}
		return time.Time{}
	}, nil)
	if got := MatchEpisodes(lib, dubOrderedSite(weekly)).Mode; got != NumberMode {
		t.Fatalf("mode = %s", got)
	}
}

func TestMatchKeepsNumbersWhenTheLibraryDatesComeFromAnotherBroadcast(t *testing.T) {
	// Dates from a dub that aired a year later agree with neither the numbers
	// nor the site's dates, so the library keeps what it had.
	site := siteTable(30, weekly, nil)
	result := MatchEpisodes(library(30, func(n int) time.Time { return weekly(n).AddDate(0, 0, 365) }, nil), site)
	if result.Mode != NumberMode || !samePointers(site, result.Markers) {
		t.Fatalf("result = %+v", result)
	}
}

func TestMatchPairsByAirDateWhenTheSiteSkipsEpisodes(t *testing.T) {
	result := MatchEpisodes(library(43, weekly, nil), dubOrderedSite(weekly))
	if result.Mode != AirDateMode {
		t.Fatalf("mode = %s", result.Mode)
	}
	// Before the first skipped episode the two numberings agree.
	if result.Markers[16].Number != 17 {
		t.Fatalf("original 17 got %d", result.Markers[16].Number)
	}
	// The episodes the site leaves out get no marker rather than a neighbor's.
	for _, i := range []int{17, 34, 37} {
		if result.Markers[i] != nil {
			t.Fatalf("original %d got site %d", i+1, result.Markers[i].Number)
		}
	}
	// Original 19 is the site's 18, and original 43 is its 40.
	if result.Markers[18].Number != 18 || result.Markers[42].Number != 40 {
		t.Fatalf("got %d and %d", result.Markers[18].Number, result.Markers[42].Number)
	}
}

func TestMatchPairsDatesThatLandADayApart(t *testing.T) {
	// One source stores the Japanese date and the other converts it across midnight.
	result := MatchEpisodes(library(43, func(n int) time.Time { return weekly(n).AddDate(0, 0, 1) }, nil), dubOrderedSite(weekly))
	if result.Mode != AirDateMode || result.Markers[18].Number != 18 || result.Markers[17] != nil {
		t.Fatalf("result = %+v", result)
	}
}

// doubleBroadcast airs originals 39 and 40 on the same day.
func doubleBroadcast(n int) time.Time {
	if n >= 40 {
		return weekly(n - 1)
	}
	return weekly(n)
}

func TestMatchPairsADoubleBroadcastInOrder(t *testing.T) {
	result := MatchEpisodes(library(43, doubleBroadcast, nil), dubOrderedSite(doubleBroadcast))
	if result.Mode != AirDateMode || result.Markers[38].Number != 36 || result.Markers[39].Number != 37 {
		t.Fatalf("result = %+v", result)
	}
}

func TestMatchUsesTitlesToSplitADoubleBroadcast(t *testing.T) {
	// The library lists the two episodes of one day the other way round. Their
	// titles match the site's, which beats air order.
	o := originals()
	site := siteTable(len(o), func(n int) time.Time { return doubleBroadcast(o[n-1]) }, func(n int) string { return fmt.Sprint("Episode ", o[n-1]) })
	lib := library(43, doubleBroadcast, func(n int) string {
		switch n {
		case 39:
			return "Episode 40"
		case 40:
			return "Episode 39"
		}
		return fmt.Sprint("Folge ", n)
	})
	result := MatchEpisodes(lib, site)
	if result.Mode != AirDateMode || result.Markers[38].Title != "Episode 40" || result.Markers[39].Title != "Episode 39" {
		t.Fatalf("result = %+v", result)
	}
}
