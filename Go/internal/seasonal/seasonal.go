// Package seasonal picks the holiday for the seasonal Home row. It mirrors
// shared/Seasonal in the C# plugins and reads the same
// shared/Seasonal/seasonal-holidays.json.
package seasonal

import (
	"encoding/json"
	"errors"
	"hash/fnv"
	"slices"
	"strconv"
	"strings"
	"time"
	"unicode"
)

// Holiday is one holiday the row can show: when it runs, where, and which
// TMDB keywords mark a movie as belonging to it.
type Holiday struct {
	ID string `json:"id"`
	// MinVotes is the TMDB vote count a suggestion needs, so a thin holiday
	// can set its own bar.
	MinVotes int       `json:"minVotes"`
	Windows  []Window  `json:"windows"`
	Keywords []Keyword `json:"keywords"`
}

func (h *Holiday) UnmarshalJSON(b []byte) error {
	type plain Holiday
	p := plain{MinVotes: 50}
	if err := json.Unmarshal(b, &p); err != nil {
		return err
	}
	*h = Holiday(p)
	return nil
}

// KeywordIDs are the TMDB keyword ids that mark a movie as the holiday's.
func (h *Holiday) KeywordIDs() map[int]bool {
	ids := make(map[int]bool, len(h.Keywords))
	for _, k := range h.Keywords {
		ids[k.ID] = true
	}
	return ids
}

// Window is a date range the holiday runs, in some countries or in all.
type Window struct {
	// Countries are ISO alpha-2 codes, or none when every country keeps it.
	Countries []string `json:"countries"`
	Start     DateRule `json:"start"`
	End       DateRule `json:"end"`
	// Dates is the holiday's day each year as "MM-dd", for holidays on
	// another calendar. A year the table leaves out has no window.
	Dates map[string]string `json:"dates"`
}

func (w Window) appliesTo(country string) bool {
	if len(w.Countries) == 0 {
		return true
	}
	return country != "" && slices.ContainsFunc(w.Countries, func(c string) bool { return strings.EqualFold(c, country) })
}

// DateRule is a day in some year. "fixed" is a month and day, "nthWeekday"
// the nth weekday of a month, "easter" Western Easter Sunday and "dates" the
// window's own table. Each can shift by OffsetDays.
type DateRule struct {
	Kind       string `json:"kind"`
	Month      int    `json:"month"`
	Day        int    `json:"day"`
	Weekday    string `json:"weekday"`
	N          int    `json:"n"`
	OffsetDays int    `json:"offsetDays"`
}

type Keyword struct {
	ID int `json:"id"`
}

// Parse reads the holidays file.
func Parse(data []byte) ([]Holiday, error) {
	var file struct {
		Holidays []Holiday `json:"holidays"`
	}
	if err := json.Unmarshal(data, &file); err != nil {
		return nil, err
	}
	for _, h := range file.Holidays {
		if strings.TrimSpace(h.ID) == "" {
			return nil, errors.New("a seasonal holiday has no id")
		}
		if len(h.Windows) == 0 {
			return nil, errors.New("seasonal holiday " + h.ID + " has no windows")
		}
		for _, w := range h.Windows {
			for _, r := range []DateRule{w.Start, w.End} {
				if r.Kind == "dates" && len(w.Dates) == 0 {
					return nil, errors.New("seasonal holiday " + h.ID + " uses a dates rule without a dates table")
				}
				if _, ok := weekday(r.Weekday); r.Kind == "nthWeekday" && !ok {
					return nil, errors.New("seasonal holiday " + h.ID + " has an unknown weekday " + strconv.Quote(r.Weekday))
				}
			}
		}
	}
	return file.Holidays, nil
}

// Active is a holiday whose window covers the day asked about.
type Active struct {
	Holiday    *Holiday
	Start, End time.Time
}

func (a Active) lengthDays() int { return int(a.End.Sub(a.Start).Hours()/24) + 1 }

// ActiveOn is the holidays running on date for a viewer in country, the
// shortest window first so Thanksgiving beats the month of Halloween around
// it. Hidden ids are left out, and no country matches only the windows open
// to everyone. With allYear, every other shown holiday follows, whatever its
// dates or country, in an order shuffled once a day so the holidays share the
// days evenly.
func ActiveOn(holidays []Holiday, date time.Time, country string, hidden []string, allYear bool) []Active {
	var shown []*Holiday
	for i := range holidays {
		if !slices.ContainsFunc(hidden, func(id string) bool { return strings.EqualFold(id, holidays[i].ID) }) {
			shown = append(shown, &holidays[i])
		}
	}
	var active []Active
	for _, h := range shown {
		for _, w := range h.Windows {
			if !w.appliesTo(country) {
				continue
			}
			if start, end, ok := activeWindow(w, date); ok {
				active = append(active, Active{Holiday: h, Start: start, End: end})
				break
			}
		}
	}
	slices.SortStableFunc(active, func(a, b Active) int {
		if d := a.lengthDays() - b.lengthDays(); d != 0 {
			return d
		}
		return strings.Compare(a.Holiday.ID, b.Holiday.ID)
	})
	if !allYear {
		return active
	}
	day := dayOf(date)
	for _, h := range Shuffle(shown, Seed("seasonal-all-year", date)) {
		if !slices.ContainsFunc(active, func(a Active) bool { return a.Holiday == h }) {
			active = append(active, Active{Holiday: h, Start: day, End: day})
		}
	}
	return active
}

// Country is the requested ISO alpha-2 code, or none for ZZ ("Other") and
// anything that isn't a code. None means only the holidays everyone keeps
// apply.
func Country(requested string) string {
	code := strings.ToUpper(strings.TrimSpace(requested))
	r := []rune(code)
	if len(r) != 2 || !unicode.IsLetter(r[0]) || !unicode.IsLetter(r[1]) || code == "ZZ" {
		return ""
	}
	return code
}

// easter is Western Easter Sunday by the anonymous Gregorian computus.
func easter(year int) time.Time {
	a, b, c := year%19, year/100, year%100
	d, e := b/4, b%4
	f := (b + 8) / 25
	g := (b - f + 1) / 3
	h := (19*a + b - d - g + 15) % 30
	i, k := c/4, c%4
	l := (32 + 2*e + 2*i - h - k) % 7
	m := (a + 11*h + 22*l) / 451
	month := (h + l - 7*m + 114) / 31
	day := (h+l-7*m+114)%31 + 1
	return date(year, month, day)
}

func nthWeekday(year, month int, wd time.Weekday, n int) time.Time {
	first := date(year, month, 1)
	return first.AddDate(0, 0, (int(wd)-int(first.Weekday())+7)%7+7*(n-1))
}

// resolve is the rule's day in year, or false when a dates rule has no entry
// for it.
func resolve(r DateRule, year int, dates map[string]string) (time.Time, bool) {
	var day time.Time
	switch r.Kind {
	case "easter":
		day = easter(year)
	case "nthWeekday":
		wd, ok := weekday(r.Weekday)
		if !ok {
			return time.Time{}, false
		}
		day = nthWeekday(year, r.Month, wd, r.N)
	case "dates":
		text, ok := dates[strconv.Itoa(year)]
		if !ok {
			return time.Time{}, false
		}
		parsed, err := time.Parse(time.DateOnly, strconv.Itoa(year)+"-"+text)
		if err != nil {
			return time.Time{}, false
		}
		day = parsed
	default:
		day = date(year, r.Month, r.Day)
	}
	return day.AddDate(0, 0, r.OffsetDays), true
}

// window is the window that starts in year. An end that lands before the
// start belongs to the next year, which is how Dec 27 to Jan 1 works.
func window(w Window, year int) (time.Time, time.Time, bool) {
	start, ok := resolve(w.Start, year, w.Dates)
	if !ok {
		return start, start, false
	}
	end, ok := resolve(w.End, year, w.Dates)
	if ok && end.Before(start) {
		end, ok = resolve(w.End, year+1, w.Dates)
	}
	return start, end, ok
}

// activeWindow says whether date falls inside the window, whichever year it
// started in.
func activeWindow(w Window, at time.Time) (time.Time, time.Time, bool) {
	day := dayOf(at)
	for year := day.Year() - 1; year <= day.Year()+1; year++ {
		if start, end, ok := window(w, year); ok && !day.Before(start) && !day.After(end) {
			return start, end, true
		}
	}
	return time.Time{}, time.Time{}, false
}

func weekday(name string) (time.Weekday, bool) {
	for d := time.Sunday; d <= time.Saturday; d++ {
		if strings.EqualFold(name, d.String()) {
			return d, true
		}
	}
	return 0, false
}

func date(year, month, day int) time.Time {
	return time.Date(year, time.Month(month), day, 0, 0, 0, 0, time.UTC)
}

func dayOf(t time.Time) time.Time { return date(t.Year(), int(t.Month()), t.Day()) }

// Seed makes a shuffle come out the same for one user all day on every
// device, and differently the next day.
func Seed(userKey string, day time.Time) uint32 {
	h := fnv.New32a()
	h.Write([]byte(userKey + "|" + day.Format(time.DateOnly)))
	if s := h.Sum32(); s != 0 {
		return s
	}
	return 1
}

// Shuffle carries its own generator, so a seed always gives the same order.
func Shuffle[T any](items []T, seed uint32) []T {
	out := slices.Clone(items)
	state := seed
	for i := len(out) - 1; i > 0; i-- {
		state ^= state << 13
		state ^= state >> 17
		state ^= state << 5
		j := int(state % uint32(i+1))
		out[i], out[j] = out[j], out[i]
	}
	return out
}

// IDKeys are a title's keys, like "tmdb:400155" and "imdb:tt5220122",
// leaving out the ids it doesn't have.
func IDKeys(tmdbID, imdbID string) []string {
	var keys []string
	if id := strings.TrimSpace(tmdbID); id != "" {
		keys = append(keys, "tmdb:"+id)
	}
	if id := strings.TrimSpace(imdbID); id != "" {
		keys = append(keys, "imdb:"+strings.ToLower(id))
	}
	return keys
}

// Distinct keeps the first entry of each title, in order. Entries are the
// same title when they share any key, and an entry without keys is kept.
func Distinct[T any](items []T, keys func(T) []string) []T {
	seen := map[string]bool{}
	var kept []T
	for _, it := range items {
		k := keys(it)
		if !slices.ContainsFunc(k, func(key string) bool { return seen[key] }) {
			kept = append(kept, it)
		}
		// A dropped copy's other ids still belong to the title it repeats.
		for _, key := range k {
			seen[key] = true
		}
	}
	return kept
}
