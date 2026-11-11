// Package anime holds the AnimeFillerList parser, the title and episode
// matching, and the subbed or dubbed verdicts, plus the outside sources and
// caches the markers draw from.
package anime

import "time"

// Kind is one of the four categories AnimeFillerList puts episodes in. The
// site has no recap category, so recaps are kept apart in Episode.Recap.
type Kind string

const (
	MangaCanon Kind = "MangaCanon"
	AnimeCanon Kind = "AnimeCanon"
	Mixed      Kind = "Mixed"
	Filler     Kind = "Filler"
)

// Show is one AnimeFillerList show as the site's A-Z index lists it.
type Show struct {
	// Slug is the path segment under /shows/, kept as the site wrote it,
	// since the site's slugs can't always be worked out from the title.
	Slug  string `json:"slug"`
	Title string `json:"title"`
}

// Episode is one episode's category, numbered the way AnimeFillerList numbers
// it: absolute across the whole show.
type Episode struct {
	Number int  `json:"number"`
	Kind   Kind `json:"kind"`
	// Recap is MyAnimeList's recap flag, only filled in when the recap lookup is on.
	Recap bool `json:"recap"`
	// Title is usually the English dub title, and only tells apart two
	// episodes that aired on the same day.
	Title string `json:"title,omitempty"`
	// AirDate is zero in tables cached before dates were read.
	AirDate time.Time `json:"airDate,omitzero"`
}

// CurrentFormat is the format tables are parsed in today. Format 2 added each
// episode's title and air date, so an older entry still serves its markers but
// is fetched again on the next sync.
const CurrentFormat = 2

// CacheEntry is one cached show and every episode the site lists for it.
type CacheEntry struct {
	Format   int       `json:"format"`
	Slug     string    `json:"slug"`
	Title    string    `json:"title"`
	Episodes []Episode `json:"episodes"`
	// RecapMalID is the MyAnimeList id the recap flags came from. Nil means the
	// recap pass hasn't run for this show, so the flags are unknown rather than false.
	RecapMalID *int      `json:"recapMalId,omitempty"`
	CachedAt   time.Time `json:"cachedAt"`
}

// AliasEntry is the titles AniList knows one provider id by, so a series
// whose library name is localized can still be found under the English or
// romaji title AnimeFillerList uses.
type AliasEntry struct {
	MalID *int `json:"malId,omitempty"`
	// Titles is English, then romaji, then synonyms. Empty when AniList had no entry.
	Titles    []string  `json:"titles"`
	FetchedAt time.Time `json:"fetchedAt"`
}
