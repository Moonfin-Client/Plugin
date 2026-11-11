// Package similar is Moonfin Recommends' scoring. It has to score exactly as
// the Jellyfin plugin does so the apps rank the same on every server.
package similar

import (
	"cmp"
	"math"
	"slices"
	"strings"
	"time"
	"unicode"
	"unicode/utf16"
)

type Role int

const (
	Other Role = iota
	Actor
	Director
	Writer
)

type Credit struct {
	Name string
	Role Role
}

// Facts is the metadata scoring reads off an item. A nil Year or
// CommunityRating is one the server doesn't have.
type Facts struct {
	Name                  string
	Genres, Tags, Studios []string
	Year                  *int
	CommunityRating       *float32
}

// Seed is the item the others are scored against, read once.
type Seed struct {
	genres, tags, studios      set
	year                       *int
	rating                     *float32
	keywords                   map[string]bool
	actors, directors, writers set
}

func NewSeed(f Facts, people []Credit) *Seed {
	s := &Seed{genres: newSet(f.Genres), tags: newSet(f.Tags), studios: newSet(f.Studios),
		year: f.Year, rating: f.CommunityRating, keywords: keywords(f.Name),
		actors: set{}, directors: set{}, writers: set{}}
	collectPeople(people, s.actors, s.directors, s.writers)
	return s
}

func (s *Seed) HasPeople() bool {
	return len(s.actors) > 0 || len(s.directors) > 0 || len(s.writers) > 0
}

const (
	PeopleScoringPoolSize = 60
	DefaultLimit          = 20
	MaxLimit              = 200

	genrePoints      = 7.0
	genreCap         = 35.0
	tagPoints        = 4.0
	tagCap           = 20.0
	yearMaxPoints    = 10.0
	yearDecayRange   = 15
	ratingMaxPoints  = 10.0
	titleMatchPoints = 25.0
)

var stopWords = map[string]bool{"the": true, "and": true, "with": true, "from": true, "under": true,
	"over": true, "about": true, "chapter": true, "part": true, "movie": true, "film": true}

func EffectiveLimit(limit int) int {
	if limit <= 0 {
		return DefaultLimit
	}
	return min(limit, MaxLimit)
}

// Rank orders the candidates against the seed and returns the best limit of
// them. Every candidate is scored on its own metadata first. Only the best
// of those get cast and crew overlap, since loadPeople can cost a read per
// item. loadPeople gets that shortlist and returns a lookup that answers nil
// for an item it has no credits for.
func Rank[T any](candidates []T, seed *Seed, limit int, factsOf func(T) Facts, premiereOf func(T) time.Time,
	loadPeople func([]T) func(T) []Credit) []T {
	type scored struct {
		item  T
		score float64
	}
	pool := make([]scored, 0, len(candidates))
	for _, c := range candidates {
		pool = append(pool, scored{c, scoreMetadata(factsOf(c), seed)})
	}
	byScore := func(a, b scored) int {
		if c := cmp.Compare(b.score, a.score); c != 0 {
			return c
		}
		return premiereOf(b.item).Compare(premiereOf(a.item))
	}
	slices.SortStableFunc(pool, byScore)
	pool = pool[:min(len(pool), max(limit, PeopleScoringPoolSize))]

	if n := min(len(pool), PeopleScoringPoolSize); seed.HasPeople() && n > 0 {
		shortlist := make([]T, n)
		for i := range n {
			shortlist[i] = pool[i].item
		}
		peopleOf := loadPeople(shortlist)
		for i := range n {
			if people := peopleOf(pool[i].item); people != nil {
				pool[i].score += scorePeople(people, seed)
			}
		}
	}

	slices.SortStableFunc(pool, byScore)
	out := make([]T, 0, min(len(pool), limit))
	for _, p := range pool[:min(len(pool), limit)] {
		out = append(out, p.item)
	}
	return out
}

// set ignores case by uppercasing, the way the Jellyfin plugin's
// OrdinalIgnoreCase sets do.
type set map[string]bool

func fold(s string) string { return strings.ToUpper(s) }

func newSet(values []string) set {
	s := set{}
	for _, v := range values {
		s[fold(v)] = true
	}
	return s
}

func collectPeople(people []Credit, actors, directors, writers set) {
	for _, p := range people {
		if p.Name == "" {
			continue
		}
		switch p.Role {
		case Actor:
			actors[fold(p.Name)] = true
		case Director:
			directors[fold(p.Name)] = true
		case Writer:
			writers[fold(p.Name)] = true
		}
	}
}

func scoreDiminishing(count int, first, second, third float64) float64 {
	switch {
	case count <= 0:
		return 0
	case count == 1:
		return first
	case count == 2:
		return first + second
	}
	return first + second + third
}

func scoreMetadata(c Facts, seed *Seed) float64 {
	score := 0.0
	score += math.Min(float64(countOverlap(c.Genres, seed.genres))*genrePoints, genreCap)
	score += math.Min(float64(countOverlap(c.Tags, seed.tags))*tagPoints, tagCap)
	score += scoreDiminishing(countOverlap(c.Studios, seed.studios), 12, 8, 0)

	if c.Year != nil && seed.year != nil {
		diff := *c.Year - *seed.year
		if diff < 0 {
			diff = -diff
		}
		if diff < yearDecayRange {
			score += yearMaxPoints * (1 - float64(diff)/yearDecayRange)
		}
	}

	if c.CommunityRating != nil {
		if seed.rating != nil {
			// Subtracted in float32 so the points match the Jellyfin plugin's exactly.
			diff := math.Abs(float64(*c.CommunityRating - *seed.rating))
			score += ratingMaxPoints * math.Max(0, 1-diff/10)
		} else {
			score += ratingMaxPoints * (float64(*c.CommunityRating) / 10)
		}
	}

	if len(seed.keywords) > 0 && similarTitle(seed.keywords, c.Name) {
		score += titleMatchPoints
	}
	return score
}

func scorePeople(people []Credit, seed *Seed) float64 {
	actors, directors, writers := set{}, set{}, set{}
	collectPeople(people, actors, directors, writers)
	return scoreDiminishing(overlap(actors, seed.actors), 10, 6, 4) +
		scoreDiminishing(overlap(directors, seed.directors), 15, 10, 5) +
		scoreDiminishing(overlap(writers, seed.writers), 15, 10, 5)
}

func overlap(a, b set) int {
	n := 0
	for k := range a {
		if b[k] {
			n++
		}
	}
	return n
}

// countOverlap counts every value in the list the seed has, repeats included.
func countOverlap(values []string, seed set) int {
	if len(seed) == 0 {
		return 0
	}
	n := 0
	for _, v := range values {
		if seed[fold(v)] {
			n++
		}
	}
	return n
}

// isWord is the \w the Jellyfin plugin splits titles on: letters, nonspacing
// marks, decimal digits and connector punctuation such as the underscore.
func isWord(r rune) bool {
	return unicode.IsLetter(r) || unicode.Is(unicode.Mn, r) || unicode.Is(unicode.Nd, r) || unicode.Is(unicode.Pc, r)
}

// length counts UTF-16 code units, since that's how the Jellyfin plugin
// measures a word.
func length(s string) int {
	n := 0
	for _, r := range s {
		n += utf16.RuneLen(r)
	}
	return n
}

func keywords(title string) map[string]bool {
	out := map[string]bool{}
	if strings.TrimSpace(title) == "" {
		return out
	}
	cleaned := strings.Map(func(r rune) rune {
		if isWord(r) || unicode.IsSpace(r) {
			return r
		}
		return ' '
	}, strings.ToLower(title))
	for _, word := range strings.Split(cleaned, " ") {
		if length(word) >= 4 && !stopWords[word] {
			out[word] = true
		}
	}
	return out
}

func subset(a, b map[string]bool) bool {
	for k := range a {
		if !b[k] {
			return false
		}
	}
	return true
}

func similarTitle(seed map[string]bool, title string) bool {
	words := keywords(title)
	if len(words) == 0 {
		return false
	}
	if subset(seed, words) || subset(words, seed) {
		return true
	}
	// One subject word that differs only by a suffix of two characters or
	// fewer, so a plural sequel like "Aliens" matches "Alien" while "The
	// Alienist" doesn't.
	if len(seed) == 1 && len(words) == 1 {
		var a, b string
		for k := range seed {
			a = k
		}
		for k := range words {
			b = k
		}
		short, long := a, b
		if length(a) > length(b) {
			short, long = b, a
		}
		return strings.HasPrefix(long, short) && length(long)-length(short) <= 2
	}
	return false
}
