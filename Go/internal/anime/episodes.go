package anime

import (
	"slices"
	"sort"
	"time"
)

// SeasonIndex is a library episode's season and episode number.
type SeasonIndex struct{ Season, Index int }

// IsAlreadyAbsolute reports whether the library already numbers the series
// absolutely (S04E207), which is when no episode number repeats.
func IsAlreadyAbsolute(slots []SeasonIndex) bool {
	seen := map[int]bool{}
	for _, s := range slots {
		seen[s.Index] = true
	}
	return len(slots) > 0 && len(seen) == len(slots)
}

// AssignAbsoluteNumbers is the absolute number of each slot, given the slots
// sorted by season and then episode.
func AssignAbsoluteNumbers(slots []SeasonIndex) []int {
	numbers := make([]int, len(slots))
	if IsAlreadyAbsolute(slots) {
		for i, s := range slots {
			numbers[i] = s.Index
		}
		return numbers
	}
	// Each season restarts at 1, so the earlier seasons are added back on. A
	// season's length is its highest episode rather than its count, so a
	// missing episode doesn't shift every later one onto the wrong marker.
	lengths := map[int]int{}
	for _, s := range slots {
		lengths[s.Season] = max(lengths[s.Season], s.Index)
	}
	seasons := make([]int, 0, len(lengths))
	for season := range lengths {
		seasons = append(seasons, season)
	}
	sort.Ints(seasons)
	offsets := map[int]int{}
	running := 0
	for _, season := range seasons {
		offsets[season] = running
		running += lengths[season]
	}
	for i, s := range slots {
		numbers[i] = offsets[s.Season] + s.Index
	}
	return numbers
}

// Slot is a library episode: its absolute number and, when the library has
// them, its air date and title.
type Slot struct {
	Number  int
	AirDate time.Time
	Title   string
}

const (
	NumberMode  = "number"
	AirDateMode = "airdate"
)

const (
	// Fewer dated episodes than this can't tell a numbering problem from a few
	// bad dates, so the number is kept.
	minimumEvidence = 10
	// When this share of dated episodes agree with their number, the numbering
	// is right and the rest is metadata noise.
	trustedNumberAgreement = 0.9
	// Air dates only win when they pair this share of the dated episodes, so a
	// library dated from another country's broadcast keeps its numbers.
	requiredAirDateCoverage = 0.5
	// Air dates from two sources can sit either side of midnight.
	dayTolerance = 1
)

// MatchResult is the site's episode for each slot, in slot order, and how the
// pairing was decided so the admin page can show it.
type MatchResult struct {
	Mode string
	// Markers is nil where nothing matched.
	Markers []*Episode
	// DatedEpisodes is how many slots land on a site episode by number with a date on both sides.
	DatedEpisodes int
	// NumberAgreements is how many of those aired within a day of that site episode.
	NumberAgreements int
}

// MatchEpisodes pairs library episodes with the site's table.
//
// The number is used whenever it can be trusted. Air dates only take over
// when they prove the numbers wrong: the site numbers some shows in a dub
// order that skips episodes (Pokémon leaves out the banned and undubbed
// ones), so every episode after the first gap lands on the wrong marker,
// while the original air dates still line up.
func MatchEpisodes(slots []Slot, site []Episode) MatchResult {
	byNumber := map[int]*Episode{}
	for i := range site {
		byNumber[site[i].Number] = &site[i]
	}
	numbered := make([]*Episode, len(slots))
	dated, agreements := 0, 0
	for i, slot := range slots {
		numbered[i] = byNumber[slot.Number]
		if slot.AirDate.IsZero() || numbered[i] == nil || numbered[i].AirDate.IsZero() {
			continue
		}
		dated++
		if abs(dayOf(slot.AirDate)-dayOf(numbered[i].AirDate)) <= dayTolerance {
			agreements++
		}
	}
	result := MatchResult{Mode: NumberMode, Markers: numbered, DatedEpisodes: dated, NumberAgreements: agreements}
	if dated < minimumEvidence || float64(agreements) >= float64(dated)*trustedNumberAgreement {
		return result
	}

	byDate := matchByAirDate(slots, site)
	dateMatches, datedSlots := 0, 0
	for i, slot := range slots {
		if byDate[i] != nil {
			dateMatches++
		}
		if !slot.AirDate.IsZero() {
			datedSlots++
		}
	}
	if dateMatches >= minimumEvidence && dateMatches > agreements && float64(dateMatches) >= float64(datedSlots)*requiredAirDateCoverage {
		result.Mode, result.Markers = AirDateMode, byDate
	}
	return result
}

// matchByAirDate pairs each slot with the site episode that aired the same
// day, then with one a day either side. A slot with no date, or no clear
// partner, stays unmatched rather than borrowing a neighbor's marker.
func matchByAirDate(slots []Slot, site []Episode) []*Episode {
	result := make([]*Episode, len(slots))
	used := map[int]bool{}

	siteByDay := map[int64][]*Episode{}
	for i := range site {
		if !site[i].AirDate.IsZero() {
			d := dayOf(site[i].AirDate)
			siteByDay[d] = append(siteByDay[d], &site[i])
		}
	}
	for _, list := range siteByDay {
		slices.SortStableFunc(list, func(a, b *Episode) int { return a.Number - b.Number })
	}

	// Same day first. A day can carry two episodes (a double broadcast), so
	// slots and site episodes are paired per day rather than one by one.
	slotsByDay := map[int64][]int{}
	var days []int64
	for i, slot := range slots {
		if slot.AirDate.IsZero() {
			continue
		}
		d := dayOf(slot.AirDate)
		if _, ok := slotsByDay[d]; !ok {
			days = append(days, d)
		}
		slotsByDay[d] = append(slotsByDay[d], i)
	}
	slices.Sort(days)
	for _, d := range days {
		candidates, ok := siteByDay[d]
		if !ok {
			continue
		}
		indexes := slotsByDay[d]
		slices.SortStableFunc(indexes, func(a, b int) int { return slots[a].Number - slots[b].Number })
		pairDay(slots, indexes, candidates, result, used)
	}

	// Then a day either side, for dates that crossed midnight between the two
	// sources. Only a clear candidate is taken.
	for i, slot := range slots {
		if result[i] != nil || slot.AirDate.IsZero() {
			continue
		}
		var nearby []*Episode
		for offset := int64(-dayTolerance); offset <= dayTolerance; offset++ {
			if offset == 0 {
				continue
			}
			for _, e := range siteByDay[dayOf(slot.AirDate)+offset] {
				if !used[e.Number] {
					nearby = append(nearby, e)
				}
			}
		}
		pick := uniqueTitleMatch(slot, nearby)
		if len(nearby) == 1 {
			pick = nearby[0]
		}
		if pick != nil {
			result[i] = pick
			used[pick.Number] = true
		}
	}
	return result
}

// pairDay pairs the slots and site episodes of one day. Matching titles pair
// first, and the rest pair in order, but only when both sides have the same
// number left.
func pairDay(slots []Slot, indexes []int, candidates []*Episode, result []*Episode, used map[int]bool) {
	var open []*Episode
	for _, e := range candidates {
		if !used[e.Number] {
			open = append(open, e)
		}
	}
	var unpaired []int
	for _, i := range indexes {
		var pick *Episode
		if len(open) == 1 && len(indexes) == 1 {
			pick = open[0]
		} else {
			pick = uniqueTitleMatch(slots[i], open)
		}
		if pick == nil {
			unpaired = append(unpaired, i)
			continue
		}
		result[i] = pick
		used[pick.Number] = true
		open = slices.DeleteFunc(open, func(e *Episode) bool { return e == pick })
	}
	if len(unpaired) == 0 || len(unpaired) != len(open) {
		return
	}
	for n, i := range unpaired {
		result[i] = open[n]
		used[open[n].Number] = true
	}
}

// uniqueTitleMatch is the one candidate whose title matches the slot's once
// both are normalized, or nil. The site lists English dub titles, so this only
// helps English libraries, and anything else falls through to air order.
func uniqueTitleMatch(slot Slot, candidates []*Episode) *Episode {
	title := Normalize(slot.Title)
	if title == "" {
		return nil
	}
	var found *Episode
	for _, e := range candidates {
		if Normalize(e.Title) == title {
			if found != nil {
				return nil
			}
			found = e
		}
	}
	return found
}

// dayOf counts days since 1970 on the calendar date the time carries.
func dayOf(t time.Time) int64 {
	y, m, d := t.Date()
	return time.Date(y, m, d, 0, 0, 0, 0, time.UTC).Unix() / 86400
}

func abs(n int64) int64 {
	if n < 0 {
		return -n
	}
	return n
}
