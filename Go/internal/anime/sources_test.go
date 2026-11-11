package anime

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

const aniListResponse = `{"data":{"Page":{"media":[
  {"id":223,"idMal":223,"title":{"romaji":"Dragon Ball","english":"Dragon Ball"},
   "synonyms":["Dragonball","Bola de Drac","דרגון בול ","DB"]},
  {"id":235,"idMal":235,"title":{"romaji":"Meitantei Conan","english":"Detective Conan"},
   "synonyms":["Case Closed","Detectiu Conan","名侦探柯南","Détective Conan"]},
  {"id":2171,"idMal":2171,
   "title":{"romaji":"Meitantei Conan: Konpeki no Hitsugi (Jolly Roger)","english":"Detective Conan: Jolly Roger in the Deep Azure"},
   "synonyms":["Detective Conan 2007","Detective Conan Movie 11"]}
]}}}`

// The real table's entry for Detective Conan. IMDb ids come as an array there.
const conanMapping = `[{"type":"TV","anidb_id":266,"anilist_id":235,"imdb_id":["tt0131179"],"kitsu_id":210,
  "mal_id":235,"themoviedb_id":{"tv":30983},"tvdb_id":72454},
  {"type":"MOVIE","anidb_id":5000,"themoviedb_id":{"movie":1}}]`

func parsedMedia(t *testing.T) map[int]AniListMedia {
	t.Helper()
	media, ok := ParseMedia([]byte(aniListResponse))
	if !ok {
		t.Fatal("the answer wasn't read")
	}
	out := map[int]AniListMedia{}
	for _, m := range media {
		out[*m.ID] = m
	}
	return out
}

func TestParseMediaListsEnglishThenRomajiThenSynonymsAndDropsNonLatinTitles(t *testing.T) {
	conan := parsedMedia(t)[235]
	if *conan.MalID != 235 || !slices.Equal(conan.Titles, []string{"Detective Conan", "Meitantei Conan", "Case Closed", "Detectiu Conan", "Détective Conan"}) {
		t.Fatalf("conan = %+v", conan)
	}
}

func TestParseMediaCollapsesAnEnglishTitleThatRepeatsTheRomaji(t *testing.T) {
	if got := parsedMedia(t)[223].Titles; !slices.Equal(got, []string{"Dragon Ball", "Dragonball", "Bola de Drac", "DB"}) {
		t.Fatalf("titles = %v", got)
	}
}

func TestParseMediaRefusesAnAnswerThatIsntAPage(t *testing.T) {
	for _, answer := range []string{`{"data":null}`, `{"errors":[{"message":"Too Many Requests."}],"data":null}`} {
		if _, ok := ParseMedia([]byte(answer)); ok {
			t.Errorf("%s was read as a page", answer)
		}
	}
}

func TestTheTitlesOfTheRightEntryFindTheShow(t *testing.T) {
	index := BuildIndex([]*Show{{Slug: "detective-conan", Title: "Detective Conan"}, {Slug: "dragon-ball", Title: "Dragon Ball"}, {Slug: "jolly-roger", Title: "Jolly Roger"}})
	media := parsedMedia(t)
	for id, slug := range map[int]string{235: "detective-conan", 223: "dragon-ball"} {
		if show := MatchPrimary(index, media[id].Titles); show == nil || show.Slug != slug {
			t.Errorf("%d matched %+v", id, show)
		}
	}
	// The plain matcher would read "(Jolly Roger)" as another name for the show.
	if show := MatchPrimary(index, media[2171].Titles); show != nil {
		t.Errorf("the movie's subtitle matched %+v", show)
	}
}

func TestTheMappingTableResolvesTheIdsAPlexLibraryHas(t *testing.T) {
	s := New(t.TempDir())
	if err := os.WriteFile(s.mappingPath(), []byte(conanMapping), 0o600); err != nil {
		t.Fatal(err)
	}
	s.EnsureMapping(context.Background())
	for provider, value := range map[string]string{"imdb": "tt0131179", "tmdb": "30983", "tvdb": "72454", "anidb": "266"} {
		if mal, ok := s.ResolveProvider(provider, value); !ok || mal != 235 {
			t.Errorf("%s %s = %d %v", provider, value, mal, ok)
		}
	}
	// An entry without a MyAnimeList id leads nowhere.
	if _, ok := s.ResolveProvider("anidb", "5000"); ok {
		t.Error("an entry without a MAL id resolved")
	}
}

func TestProviderKeysPutsTheAnimeDatabasesBeforeTheGeneralOnes(t *testing.T) {
	ids := map[string]string{"Imdb": "tt0131179", "Tvdb": "72454", "Tmdb": "30983", "AniList": "2171", "AniDB": "266", "Zap2It": "SH03296957", "TvdbSlug": "detective-conan"}
	var keys []string
	for _, k := range ProviderKeys(ids) {
		keys = append(keys, k.Key)
	}
	if !slices.Equal(keys, []string{"anilist:2171", "anidb:266", "tvdb:72454", "tmdb:30983", "imdb:tt0131179"}) {
		t.Fatalf("keys = %v", keys)
	}
	// Plex names its ids by the scheme of its Guid list.
	if keys := ProviderKeys(map[string]string{"tvdb": "72454", "imdb": "tt0131179"}); len(keys) != 2 || keys[0].Key != "tvdb:72454" {
		t.Fatalf("plex keys = %v", keys)
	}
}

// fakeSites stands in for AnimeFillerList, the mapping table, AniList and the
// episode API, and counts the requests each gets.
type fakeSites struct {
	*httptest.Server
	hits map[string]*atomic.Int32
}

func newFakeSites(t *testing.T) *fakeSites {
	t.Helper()
	f := &fakeSites{hits: map[string]*atomic.Int32{}}
	for _, k := range []string{"index", "show", "mapping", "anilist", "episodes"} {
		f.hits[k] = &atomic.Int32{}
	}
	var index strings.Builder
	for i := range 60 {
		fmt.Fprintf(&index, `<li><a href="/shows/filler-%d">Filler Show %d</a></li>`, i, i)
	}
	index.WriteString(`<li><a href="/shows/detective-conan">Detective Conan (Case Closed)</a></li>`)
	f.Server = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch {
		case r.URL.Path == "/shows":
			f.hits["index"].Add(1)
			io.WriteString(w, index.String())
		case r.URL.Path == "/shows/detective-conan":
			f.hits["show"].Add(1)
			io.WriteString(w, `<tr class="manga_canon odd" id="eps-1"><td class="Title">Roller Coaster Murder Case</td><td class="Date">1996-01-08</td></tr>
<tr class="filler even" id="eps-2"><td class="Title">The Company President's Daughter</td><td class="Date">1996-01-15</td></tr>
<tr class="filler odd" id="eps-3"><td class="Title">The Idol's Secret</td><td class="Date">1996-01-22</td></tr>`)
		case r.URL.Path == "/mapping.json":
			f.hits["mapping"].Add(1)
			io.WriteString(w, conanMapping)
		case r.URL.Path == "/graphql":
			f.hits["anilist"].Add(1)
			var body struct {
				Query     string         `json:"query"`
				Variables map[string]any `json:"variables"`
			}
			_ = json.NewDecoder(r.Body).Decode(&body)
			switch {
			case strings.Contains(body.Query, "idMal_in"):
				io.WriteString(w, aniListResponse)
			case strings.Contains(body.Query, "search"):
				fmt.Fprintf(w, `{"data":{"Media":{"idMal":99,"title":{"romaji":%q}}}}`, "Kobato.")
			default:
				io.WriteString(w, `{"data":{"Page":{"media":[]}}}`)
			}
		case r.URL.Path == "/v1/anime/235/episodes":
			f.hits["episodes"].Add(1)
			if r.URL.Query().Get("page") == "1" {
				io.WriteString(w, `{"pagination":{"has_next_page":true},"data":[{"mal_id":1,"recap":false},{"mal_id":2,"recap":true}]}`)
			} else {
				io.WriteString(w, `{"pagination":{"has_next_page":false},"data":[{"mal_id":3,"recap":false}]}`)
			}
		default:
			http.NotFound(w, r)
		}
	}))
	t.Cleanup(f.Close)
	return f
}

// Point aims the sources at the fake sites with no waiting between requests.
func (f *fakeSites) Point(s *Sources) {
	s.FillerListBase, s.MappingURL, s.AniListURL, s.EpisodeAPIBase = f.URL, f.URL+"/mapping.json", f.URL+"/graphql", f.URL+"/v1"
	s.CrawlDelay, s.AniListSpacing, s.EpisodeSpacing, s.RetryDelay = 0, 0, 0, 0
}

func TestTheCatalogIsDownloadedOnceAndKeptOnDisk(t *testing.T) {
	sites := newFakeSites(t)
	dir := t.TempDir()
	s := New(dir)
	sites.Point(s)
	ctx := context.Background()
	s.EnsureCatalog(ctx)
	s.EnsureCatalog(ctx)
	index, count := s.Catalog()
	if count != 61 || Match(index, "Case Closed") == nil || sites.hits["index"].Load() != 1 {
		t.Fatalf("count %d, downloads %d", count, sites.hits["index"].Load())
	}
	// A restart reads it back without asking the site.
	again := New(dir)
	sites.Point(again)
	again.EnsureCatalog(ctx)
	if _, count := again.Catalog(); count != 61 || sites.hits["index"].Load() != 1 {
		t.Fatalf("count %d after a restart, downloads %d", count, sites.hits["index"].Load())
	}
	if _, ok := s.FetchEpisodes(ctx, "missing"); ok {
		t.Fatal("a missing page read as a table")
	}
	if episodes, ok := s.FetchEpisodes(ctx, "detective-conan"); !ok || len(episodes) != 3 || episodes[1].Kind != Filler {
		t.Fatalf("episodes = %+v", episodes)
	}
}

func TestFetchRefusesAnAnswerOverItsLimit(t *testing.T) {
	sites := newFakeSites(t)
	s := New(t.TempDir())
	if _, _, err := s.fetch(context.Background(), http.MethodGet, sites.URL+"/mapping.json", nil, time.Second, 10); err == nil {
		t.Fatal("a cut-off answer was read as whole")
	}
}

func TestAliasesComeFromTheMalIdTheMappingLeadsTo(t *testing.T) {
	sites := newFakeSites(t)
	s := New(t.TempDir())
	sites.Point(s)
	ctx := context.Background()
	ids := map[string]string{"tvdb": "72454", "imdb": "tt0131179"}
	if n, err := s.RefreshAliases(ctx, []map[string]string{ids}); err != nil || n != 2 {
		t.Fatalf("stored %d, %v", n, err)
	}
	aliases := s.Aliases(ids)
	if len(aliases) != 2 || aliases[0].Key != "tvdb:72454" || aliases[0].Entry.Titles[0] != "Detective Conan" || *aliases[0].Entry.MalID != 235 {
		t.Fatalf("aliases = %+v", aliases)
	}
	// Fresh entries aren't asked about again.
	before := sites.hits["anilist"].Load()
	if n, _ := s.RefreshAliases(ctx, []map[string]string{ids}); n != 0 || sites.hits["anilist"].Load() != before {
		t.Fatalf("asked again: %d", n)
	}
}

func TestRecapsResolveThroughTheMappingAndReadEveryPage(t *testing.T) {
	sites := newFakeSites(t)
	s := New(t.TempDir())
	sites.Point(s)
	ctx := context.Background()
	mal, ok := s.ResolveMalID(ctx, map[string]string{"tmdb": "30983"}, "El detectiu Conan")
	if !ok || mal != 235 {
		t.Fatalf("mal = %d %v", mal, ok)
	}
	lookup, ok := s.FetchRecaps(ctx, mal)
	if !ok || lookup.Episodes != 3 || !lookup.Recaps[2] || lookup.Recaps[1] {
		t.Fatalf("lookup = %+v %v", lookup, ok)
	}
	// The title search only counts when the answer carries the name asked for.
	if mal, ok := s.ResolveMalID(ctx, nil, "Kobato"); !ok || mal != 99 {
		t.Fatalf("search = %d %v", mal, ok)
	}
	if _, ok := s.ResolveMalID(ctx, nil, "Something Else"); ok {
		t.Fatal("a different show's search answer counted")
	}
	// A 404 isn't retried and fails the lookup.
	if _, ok := s.FetchRecaps(ctx, 1); ok {
		t.Fatal("a missing entry read as no recaps")
	}
}

func TestTheLogOnlyWritesWhileOnAndKeepsItsTail(t *testing.T) {
	l := NewLog(filepath.Join(t.TempDir(), "markers.log"))
	l.Printf("ignored")
	if _, ok := l.Size(); ok {
		t.Fatal("wrote while off")
	}
	l.On.Store(true)
	for i := range 5 {
		l.Printf("line %d", i)
	}
	tail := l.Tail(2)
	if len(tail) != 2 || !strings.HasSuffix(tail[1], "line 4") {
		t.Fatalf("tail = %q", tail)
	}
	if !l.Clear() || len(l.Tail(10)) != 0 || l.Clear() {
		t.Fatal("clearing didn't empty the log")
	}
}
