package anime

import (
	"slices"
	"testing"
	"time"
)

const indexHTML = `
<div class="Group"><h2>A</h2><ul>
<li><a href="/shows/certain-magical-index">A Certain Magical Index (Toaru Majutsu No Index)</a></li>
<li><a href="/shows/sh%C5%8Dnan-pure-love-gang">A Certain Scientific Accelerator (Toaru Kagaku no Accelerator)</a></li>
<li><a href="/shows/attack-titan">Attack on Titan (Shingeki no Kyojin)</a></li>
<li><a href="/shows/akame-ga-kill">Akame ga Kill!</a></li>
</ul></div>`

const episodeHTML = `
<table class="EpisodeList"><tbody>
<tr class="manga_canon odd" id="eps-1"><td class="Number">1</td><td class="Type"><span>Manga Canon</span></td></tr>
<tr class="filler even" id="eps-2"><td class="Number">2</td><td class="Type"><span>Filler</span></td></tr>
<tr class="mixed_canon/filler odd" id="eps-3"><td class="Number">3</td><td class="Type"><span>Mixed Canon/Filler</span></td></tr>
<tr class="anime_canon even" id="eps-4"><td class="Number">4</td><td class="Type"><span>Anime Canon</span></td></tr>
</tbody></table>`

func TestParseShowIndexReadsSlugAndTitle(t *testing.T) {
	shows := ParseShowIndex(indexHTML)
	if len(shows) != 4 || shows[2].Slug != "attack-titan" || shows[2].Title != "Attack on Titan (Shingeki no Kyojin)" {
		t.Fatalf("shows = %+v", shows)
	}
}

func TestParseShowIndexKeepsPercentEncodedSlugVerbatim(t *testing.T) {
	// The slug is the URL path segment, so re-encoding or decoding it would 404.
	if !slices.ContainsFunc(ParseShowIndex(indexHTML), func(s Show) bool { return s.Slug == "sh%C5%8Dnan-pure-love-gang" }) {
		t.Fatal("the percent-encoded slug was changed")
	}
}

func TestParseEpisodesReadsEveryCategory(t *testing.T) {
	var kinds []Kind
	for _, e := range ParseEpisodes(episodeHTML) {
		kinds = append(kinds, e.Kind)
	}
	if !slices.Equal(kinds, []Kind{MangaCanon, Filler, Mixed, AnimeCanon}) {
		t.Fatalf("kinds = %v", kinds)
	}
}

func TestParseEpisodesSortsByNumber(t *testing.T) {
	episodes := ParseEpisodes(`
<tr class="filler odd" id="eps-12"><td class="Number">12</td></tr>
<tr class="manga_canon even" id="eps-3"><td class="Number">3</td></tr>`)
	if len(episodes) != 2 || episodes[0].Number != 3 || episodes[1].Number != 12 {
		t.Fatalf("episodes = %+v", episodes)
	}
}

func TestParseEpisodesSkipsUnknownCategories(t *testing.T) {
	// A category the site adds later mustn't be recorded as canon.
	episodes := ParseEpisodes(`
<tr class="brand_new_category odd" id="eps-1"><td class="Number">1</td></tr>
<tr class="filler even" id="eps-2"><td class="Number">2</td></tr>`)
	if len(episodes) != 1 || episodes[0].Number != 2 {
		t.Fatalf("episodes = %+v", episodes)
	}
}

func TestParseEpisodesReadsTitleAndAirDate(t *testing.T) {
	// Copied from the site's Pokémon table.
	episodes := ParseEpisodes(`<tr class="manga_canon odd" id="eps-11"><td class="Number">11</td><td class="Title"><a href="/shows/pokemon/charmander-%E2%80%93-stray-pok%C3%A9mon" rel="nofollow">Charmander-The Stray Pok&eacute;mon</a></td><td class="Type"><span>Manga Canon</span></td><td class="Date">1997-06-10</td> </tr>`)
	if len(episodes) != 1 || episodes[0].Title != "Charmander-The Stray Pokémon" || !episodes[0].AirDate.Equal(date(1997, 6, 10)) {
		t.Fatalf("episodes = %+v", episodes)
	}
}

func TestParseEpisodesDoesNotBorrowTheNextRowsCells(t *testing.T) {
	// The first row has no title or date, so reading past its end would hand it
	// the second row's air date and pair it with the wrong episode.
	episodes := ParseEpisodes(`
<tr class="filler odd" id="eps-1"><td class="Number">1</td></tr>
<tr class="filler even" id="eps-2"><td class="Number">2</td><td class="Title">Two</td><td class="Date">2001-01-08</td></tr>`)
	if episodes[0].Title != "" || !episodes[0].AirDate.IsZero() {
		t.Fatalf("episode 1 = %+v", episodes[0])
	}
	if episodes[1].Title != "Two" || !episodes[1].AirDate.Equal(date(2001, 1, 8)) {
		t.Fatalf("episode 2 = %+v", episodes[1])
	}
}

func TestParseEpisodesReturnsEmptyForUnrelatedMarkup(t *testing.T) {
	if got := ParseEpisodes("<html><body>Not found</body></html>"); len(got) != 0 {
		t.Fatalf("episodes = %+v", got)
	}
}

func date(y int, m time.Month, d int) time.Time { return time.Date(y, m, d, 0, 0, 0, 0, time.UTC) }
