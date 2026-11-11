package seasonal

import (
	"fmt"
	"os"
	"slices"
	"testing"
	"time"
)

func holidays(t *testing.T) []Holiday {
	t.Helper()
	data, err := os.ReadFile("../../../shared/Seasonal/seasonal-holidays.json")
	if err != nil {
		t.Fatal(err)
	}
	h, err := Parse(data)
	if err != nil {
		t.Fatal(err)
	}
	return h
}

func ids(active []Active) []string {
	out := []string{}
	for _, a := range active {
		out = append(out, a.Holiday.ID)
	}
	return out
}

func on(y, m, d int) time.Time { return time.Date(y, time.Month(m), d, 12, 0, 0, 0, time.Local) }

func TestCalendar(t *testing.T) {
	for _, want := range []time.Time{date(2026, 4, 5), date(2027, 3, 28), date(2028, 4, 16), date(2029, 4, 1), date(2030, 4, 21)} {
		if got := easter(want.Year()); !got.Equal(want) {
			t.Errorf("Easter %d = %v", want.Year(), got)
		}
	}
	for _, want := range []time.Time{date(2026, 11, 26), date(2027, 11, 25), date(2028, 11, 23)} {
		if got := nthWeekday(want.Year(), 11, time.Thursday, 4); !got.Equal(want) {
			t.Errorf("US Thanksgiving %d = %v", want.Year(), got)
		}
	}
	if got, _ := resolve(DateRule{Kind: "nthWeekday", Month: 11, Weekday: "thursday", N: 4, OffsetDays: 1}, 2026, nil); !got.Equal(date(2026, 11, 27)) {
		t.Errorf("the day after Thanksgiving = %v", got)
	}

	newYear := Window{Start: DateRule{Kind: "fixed", Month: 12, Day: 27}, End: DateRule{Kind: "fixed", Month: 1, Day: 1}}
	if start, end, _ := window(newYear, 2026); !start.Equal(date(2026, 12, 27)) || !end.Equal(date(2027, 1, 1)) {
		t.Errorf("a window ending before it starts = %v to %v", start, end)
	}
	if start, _, ok := activeWindow(newYear, time.Date(2027, 1, 1, 23, 0, 0, 0, time.Local)); !ok || !start.Equal(date(2026, 12, 27)) {
		t.Errorf("Jan 1 = %v %v, want the window from the year before", ok, start)
	}
	if _, _, ok := activeWindow(newYear, on(2027, 1, 2)); ok {
		t.Error("Jan 2 is past the window")
	}

	lunar := Window{Dates: map[string]string{"2026": "02-17", "2027": "02-06"},
		Start: DateRule{Kind: "dates", OffsetDays: -5}, End: DateRule{Kind: "dates", OffsetDays: 7}}
	if start, end, _ := window(lunar, 2026); !start.Equal(date(2026, 2, 12)) || !end.Equal(date(2026, 2, 24)) {
		t.Errorf("a dates window = %v to %v", start, end)
	}
	if _, _, ok := window(lunar, 2028); ok {
		t.Error("a year the table leaves out has no window")
	}
}

func TestParseRefusesWhatItCantResolve(t *testing.T) {
	for _, bad := range []string{
		`{"holidays":[{"id":" ","windows":[{}]}]}`,
		`{"holidays":[{"id":"x"}]}`,
		`{"holidays":[{"id":"x","windows":[{"start":{"kind":"dates"},"end":{"kind":"dates"}}]}]}`,
		`{"holidays":[{"id":"x","windows":[{"start":{"kind":"nthWeekday","weekday":"someday"}}]}]}`,
	} {
		if _, err := Parse([]byte(bad)); err == nil {
			t.Errorf("Parse(%s) took it", bad)
		}
	}
	h, err := Parse([]byte(`{"holidays":[{"id":"x","windows":[{}]},{"id":"y","minVotes":0,"windows":[{}]}]}`))
	if err != nil || h[0].MinVotes != 50 || h[1].MinVotes != 0 {
		t.Fatalf("minVotes = %v %v, want 50 when left out", h, err)
	}
}

func TestActiveOn(t *testing.T) {
	all := holidays(t)
	cases := []struct {
		at      time.Time
		country string
		hidden  []string
		want    []string
	}{
		{on(2026, 10, 8), "CA", nil, []string{"thanksgiving", "halloween"}},
		{on(2026, 10, 8), "US", nil, []string{"halloween"}},
		{on(2026, 10, 8), "", nil, []string{"halloween"}},
		{on(2026, 11, 24), "", nil, []string{}},
		{on(2026, 11, 24), "us", nil, []string{"thanksgiving"}},
		{on(2026, 11, 26), "US", nil, []string{"thanksgiving"}},
		{on(2026, 11, 27), "US", nil, []string{"christmas"}},
		{on(2026, 12, 26), "GB", nil, []string{"christmas"}},
		{on(2026, 12, 27), "GB", nil, []string{"newYear"}},
		{on(2027, 1, 2), "GB", nil, []string{}},
		{on(2026, 3, 29), "DE", nil, []string{"easter"}},
		{on(2026, 4, 7), "DE", nil, []string{}},
		{on(2026, 10, 8), "CA", []string{"Thanksgiving"}, []string{"halloween"}},
		{on(2026, 11, 8), "IN", nil, []string{"diwali"}},
		{on(2026, 11, 8), "US", nil, []string{}},
		{on(2027, 2, 6), "VN", nil, []string{"lunarNewYear", "valentines"}},
	}
	for _, c := range cases {
		if got := ids(ActiveOn(all, c.at, c.country, c.hidden, false)); !slices.Equal(got, c.want) {
			t.Errorf("%s in %q hiding %v = %v, want %v", c.at.Format(time.DateOnly), c.country, c.hidden, got, c.want)
		}
	}
}

func TestAllYear(t *testing.T) {
	all := holidays(t)
	got := ids(ActiveOn(all, on(2026, 10, 8), "US", nil, true))
	if got[0] != "halloween" || len(got) != len(all) || len(slices.Compact(slices.Sorted(slices.Values(got)))) != len(all) {
		t.Fatalf("all year in October = %v", got)
	}
	hidden := ids(ActiveOn(all, on(2026, 7, 15), "US", []string{"christmas"}, true))
	if slices.Contains(hidden, "christmas") || !slices.Contains(hidden, "diwali") || len(hidden) != len(all)-1 {
		t.Fatalf("all year hiding Christmas = %v", hidden)
	}
	morning := ids(ActiveOn(all, time.Date(2026, 7, 15, 1, 0, 0, 0, time.Local), "US", nil, true))
	evening := ids(ActiveOn(all, time.Date(2026, 7, 15, 23, 0, 0, 0, time.Local), "US", nil, true))
	if !slices.Equal(morning, evening) {
		t.Fatalf("the order changed during the day: %v then %v", morning, evening)
	}
	firsts := map[string]bool{}
	for d := range 90 {
		firsts[ids(ActiveOn(all, on(2026, 7, 1).AddDate(0, 0, d), "US", nil, true))[0]] = true
	}
	if len(firsts) != len(all) {
		t.Fatalf("90 days led with %d of %d holidays", len(firsts), len(all))
	}
}

func TestCountry(t *testing.T) {
	for _, c := range [][2]string{{"us", "US"}, {" CA ", "CA"}, {"", ""}, {"zz", ""}, {"USA", ""}, {"1A", ""}} {
		if got := Country(c[0]); got != c[1] {
			t.Errorf("Country(%q) = %q, want %q", c[0], got, c[1])
		}
	}
}

func TestShuffle(t *testing.T) {
	titles := make([]string, 40)
	for i := range titles {
		titles[i] = fmt.Sprint("Movie ", i+1)
	}
	today := Seed("user-1", time.Date(2026, 12, 20, 8, 0, 0, 0, time.Local))
	if today != Seed("user-1", time.Date(2026, 12, 20, 22, 30, 0, 0, time.Local)) {
		t.Fatal("the seed changed during the day")
	}
	shuffled := Shuffle(titles, today)
	if slices.Equal(shuffled, titles) || !slices.Equal(slices.Sorted(slices.Values(shuffled)), slices.Sorted(slices.Values(titles))) {
		t.Fatalf("shuffle = %v", shuffled)
	}
	if slices.Equal(shuffled, Shuffle(titles, Seed("user-1", on(2026, 12, 21)))) || slices.Equal(shuffled, Shuffle(titles, Seed("user-2", on(2026, 12, 20)))) {
		t.Fatal("the order should change the next day and between users")
	}
}

func TestDistinct(t *testing.T) {
	type entry struct{ name, tmdb, imdb string }
	names := func(entries ...entry) []string {
		var out []string
		for _, e := range Distinct(entries, func(e entry) []string { return IDKeys(e.tmdb, e.imdb) }) {
			out = append(out, e.name)
		}
		return out
	}
	if got := names(entry{"first", "400155", "tt5220122"}, entry{"second", "", "TT5220122"}); !slices.Equal(got, []string{"first"}) {
		t.Errorf("IMDb ids ignore case: %v", got)
	}
	if got := names(entry{"a", "400155", ""}, entry{"b", "400155", "tt5220122"}, entry{"c", "", "tt5220122"}); !slices.Equal(got, []string{"a"}) {
		t.Errorf("a dropped copy's other ids still count: %v", got)
	}
	if got := names(entry{name: "a"}, entry{name: "b"}); !slices.Equal(got, []string{"a", "b"}) {
		t.Errorf("entries without ids are kept: %v", got)
	}
}
