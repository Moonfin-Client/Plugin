package anime

import (
	"html"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"time"
)

var (
	showLink = regexp.MustCompile(`(?i)<a\s+href="/shows/(?P<slug>[^"/]+)"\s*>(?P<title>[^<]+)</a>`)
	// The row's category is the first token of its class, followed by the
	// site's striping token: class="mixed_canon/filler even".
	episodeRow = regexp.MustCompile(`(?i)<tr\s+class="(?P<kind>[^"]+?)(?:\s+(?:odd|even))?"\s+id="eps-(?P<number>\d+)"`)
	titleCell  = regexp.MustCompile(`(?is)<td\s+class="Title"[^>]*>(?P<title>.*?)</td>`)
	dateCell   = regexp.MustCompile(`(?i)<td\s+class="Date"[^>]*>\s*(?P<date>\d{4}-\d{2}-\d{2})\s*</td>`)
	rowEnd     = regexp.MustCompile(`(?i)</tr>`)
	tag        = regexp.MustCompile(`<[^>]+>`)
)

// ParseShowIndex reads the show list out of /shows. A repeated slug keeps the
// first title seen.
func ParseShowIndex(page string) []Show {
	shows := []Show{}
	seen := map[string]bool{}
	for _, m := range showLink.FindAllStringSubmatch(page, -1) {
		slug := m[1]
		title := strings.TrimSpace(html.UnescapeString(m[2]))
		if slug == "" || title == "" || seen[strings.ToLower(slug)] {
			continue
		}
		seen[strings.ToLower(slug)] = true
		shows = append(shows, Show{Slug: slug, Title: title})
	}
	return shows
}

// ParseEpisodes reads one show's episode table, sorted by number with repeats dropped.
func ParseEpisodes(page string) []Episode {
	byNumber := map[int]Episode{}
	for _, m := range episodeRow.FindAllStringSubmatchIndex(page, -1) {
		number, err := strconv.Atoi(page[m[4]:m[5]])
		if err != nil || number <= 0 {
			continue
		}
		kind, ok := parseKind(page[m[2]:m[3]])
		if !ok {
			continue
		}
		row := rowAt(page, m[0])
		byNumber[number] = Episode{Number: number, Kind: kind, Title: parseTitle(row), AirDate: parseAirDate(row)}
	}
	episodes := make([]Episode, 0, len(byNumber))
	for _, e := range byNumber {
		episodes = append(episodes, e)
	}
	sort.Slice(episodes, func(i, j int) bool { return episodes[i].Number < episodes[j].Number })
	return episodes
}

// rowAt is the markup of the row starting at start, so a missing cell can't be
// read from the next row.
func rowAt(page string, start int) string {
	if end := rowEnd.FindStringIndex(page[start:]); end != nil {
		return page[start : start+end[0]]
	}
	return page[start:]
}

func parseTitle(row string) string {
	m := titleCell.FindStringSubmatch(row)
	if m == nil {
		return ""
	}
	return strings.TrimSpace(html.UnescapeString(tag.ReplaceAllString(m[1], "")))
}

func parseAirDate(row string) time.Time {
	m := dateCell.FindStringSubmatch(row)
	if m == nil {
		return time.Time{}
	}
	date, _ := time.Parse(time.DateOnly, m[1])
	return date
}

func parseKind(class string) (Kind, bool) {
	switch strings.ToLower(strings.TrimSpace(class)) {
	case "manga_canon":
		return MangaCanon, true
	case "anime_canon":
		return AnimeCanon, true
	case "mixed_canon/filler":
		return Mixed, true
	case "filler":
		return Filler, true
	}
	return "", false
}
