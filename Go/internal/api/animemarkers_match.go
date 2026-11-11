package api

import (
	"cmp"
	"context"
	"fmt"
	"log/slog"
	"slices"
	"strconv"
	"strings"
	"time"

	"github.com/Moonfin-Client/Plugin/Go/internal/anime"
)

// matchSeries finds a series' AnimeFillerList show by its library titles,
// then by the titles behind its provider ids, which is what finds a show whose
// library name is localized. aliasKey names the provider id that matched, and
// malID is the MyAnimeList entry behind it, which the recap pass should use.
func (a *animeMarkers) matchSeries(series AnimeItem) (show *anime.Show, aliasKey string, malID *int) {
	index, _ := a.src.Catalog()
	if show := matchByTitle(index, series); show != nil {
		return show, "", nil
	}
	for _, alias := range a.src.Aliases(series.ProviderIDs) {
		if show := anime.MatchPrimary(index, alias.Entry.Titles); show != nil {
			return show, alias.Key, alias.Entry.MalID
		}
	}
	return nil, "", nil
}

func matchByTitle(index map[string]*anime.Show, series AnimeItem) *anime.Show {
	withYear := ""
	if series.Year > 0 && strings.TrimSpace(series.Title) != "" {
		withYear = fmt.Sprintf("%s (%d)", series.Title, series.Year)
	}
	return anime.Match(index, withYear, series.Title, series.OriginalTitle)
}

// refreshAliases fetches the provider id titles of every series whose library
// title matches nothing, so a library that matches today won't change.
func (a *animeMarkers) refreshAliases(ctx context.Context, series []AnimeItem) error {
	index, _ := a.src.Catalog()
	var ids []map[string]string
	for _, s := range series {
		if matchByTitle(index, s) == nil {
			ids = append(ids, s.ProviderIDs)
		}
	}
	_, err := a.src.RefreshAliases(ctx, ids)
	return err
}

type seriesMarkers struct {
	slug, title, mode   string
	pending, recapKnown bool
	// markers is keyed by episode id. Episodes without a marker are left out.
	markers map[string]*anime.Episode
}

// markersFor fills in one series' markers from the cache. They're empty when
// the series matches no show or its table hasn't been fetched yet.
func (a *animeMarkers) markersFor(series AnimeItem, maxAge time.Duration, episodes func() ([]AnimeItem, error), out *seriesMarkers) error {
	show, _, _ := a.matchSeries(series)
	if show == nil {
		return nil
	}
	out.slug, out.title = show.Slug, show.Title
	entry := a.src.Tables.Get(show.Slug, maxAge)
	if entry == nil {
		out.pending = true
		return nil
	}
	out.recapKnown = entry.RecapMalID != nil
	list, err := episodes()
	if err != nil {
		return err
	}
	matched := matchEpisodes(list, entry)
	out.mode = matched.result.Mode
	for _, slot := range matched.slots {
		if marker := matched.bySlot[slot.key]; marker != nil {
			for _, it := range slot.items {
				out.markers[it.ID] = marker
			}
		}
	}
	return nil
}

type episodeSlot struct {
	key    anime.SeasonIndex
	number int
	// items are the library episodes in the slot, such as a second cut of one episode.
	items []AnimeItem
}

type episodeMatch struct {
	// numbering is absolute or per-season, the most useful thing to see when
	// markers land on the wrong episodes.
	numbering string
	slots     []episodeSlot
	items     int
	result    anime.MatchResult
	bySlot    map[anime.SeasonIndex]*anime.Episode
}

// matchEpisodes pairs a series' episodes with a show's table, by absolute
// number unless the air dates show the numbers are off. Markers are kept by
// season and episode, so every copy of an episode gets the same one.
func matchEpisodes(episodes []AnimeItem, entry *anime.CacheEntry) episodeMatch {
	// Season 0 is specials, which the site doesn't list with the main run.
	var numbered []AnimeItem
	seen := map[string]bool{}
	for _, e := range episodes {
		if e.Season > 0 && e.Index > 0 && !seen[e.ID] {
			seen[e.ID] = true
			numbered = append(numbered, e)
		}
	}
	slices.SortStableFunc(numbered, func(a, b AnimeItem) int {
		return cmp.Or(cmp.Compare(a.Season, b.Season), cmp.Compare(a.Index, b.Index))
	})

	// The numbering is worked out over distinct slots, since two items in one
	// slot are one episode as far as the site is concerned.
	var slots []episodeSlot
	var keys []anime.SeasonIndex
	for _, e := range numbered {
		key := anime.SeasonIndex{Season: e.Season, Index: e.Index}
		if n := len(slots); n > 0 && slots[n-1].key == key {
			slots[n-1].items = append(slots[n-1].items, e)
			continue
		}
		slots = append(slots, episodeSlot{key: key, items: []AnimeItem{e}})
		keys = append(keys, key)
	}
	numbers := anime.AssignAbsoluteNumbers(keys)
	for i := range slots {
		slots[i].number = numbers[i]
	}
	slices.SortStableFunc(slots, func(a, b episodeSlot) int { return a.number - b.number })

	matchSlots := make([]anime.Slot, len(slots))
	for i, slot := range slots {
		matchSlots[i] = anime.Slot{Number: slot.number, Title: slot.items[0].Title}
		for _, it := range slot.items {
			if !it.AirDate.IsZero() {
				matchSlots[i].AirDate = it.AirDate
				break
			}
		}
	}
	result := anime.MatchEpisodes(matchSlots, entry.Episodes)
	bySlot := map[anime.SeasonIndex]*anime.Episode{}
	for i, slot := range slots {
		if result.Markers[i] != nil {
			bySlot[slot.key] = result.Markers[i]
		}
	}
	numbering := "per-season"
	if anime.IsAlreadyAbsolute(keys) {
		numbering = "absolute"
	}
	return episodeMatch{numbering: numbering, slots: slots, items: len(numbered), result: result, bySlot: bySlot}
}

var animeProviderKeys = []string{"AniList", "AniDB", "AniSearch", "Kitsu", "KitsuIo", "MyAnimeList", "Mal"}

// audioCandidate says whether an item may get subbed or dubbed pills: it's in
// a chosen library, or every library when none are chosen, and looks like
// anime unless the admin trusts the chosen libraries to be anime only.
func audioCandidate(item AnimeItem, series *AnimeItem, set animeSettings) bool {
	if set.libraries == nil {
		return looksLikeAnime(item, series)
	}
	return slices.Contains(set.libraries, item.LibraryID) && (set.trustLibraries || looksLikeAnime(item, series))
}

// looksLikeAnime checks the item and its series for an anime provider id or
// an anime genre or tag.
func looksLikeAnime(item AnimeItem, series *AnimeItem) bool {
	if series != nil && looksLikeAnime(*series, nil) {
		return true
	}
	for name := range item.ProviderIDs {
		if slices.ContainsFunc(animeProviderKeys, func(k string) bool { return strings.EqualFold(k, name) }) {
			return true
		}
	}
	isAnime := func(t string) bool { return strings.Contains(strings.ToLower(t), "anime") }
	return slices.ContainsFunc(item.Genres, isAnime) || slices.ContainsFunc(item.Tags, isAnime)
}

const audioCacheTTL = 10 * time.Minute

// seriesAudio is the subbed or dubbed verdict for each of a series' episodes,
// and for each season whose episodes all agree, kept for a few minutes.
func (a *animeMarkers) seriesAudio(ctx context.Context, caller Identity, series AnimeItem, episodes func() ([]AnimeItem, error)) (cachedAudio, error) {
	key := strconv.Itoa(caller.UserID) + "|" + series.ID
	if v, ok := a.audio.get(key, audioCacheTTL); ok {
		return v.(cachedAudio), nil
	}
	list, err := episodes()
	if err != nil {
		return cachedAudio{}, err
	}
	ids := make([]string, len(list))
	for i, e := range list {
		ids[i] = e.ID
	}
	items, err := a.catalog.AnimeItems(ctx, caller, ids)
	if err != nil {
		return cachedAudio{}, err
	}
	result := cachedAudio{episodes: map[string]anime.AudioKind{}, seasons: map[string]anime.AudioKind{}}
	bySeason := map[string][]anime.AudioKind{}
	for _, it := range items {
		kind := anime.ClassifyAudio(it.AudioLanguages)
		if kind != "" {
			result.episodes[it.ID] = kind
		}
		if it.SeasonID != "" {
			bySeason[it.SeasonID] = append(bySeason[it.SeasonID], kind)
		}
	}
	for season, kinds := range bySeason {
		if kind := anime.ClassifySeason(kinds); kind != "" {
			result.seasons[season] = kind
		}
	}
	a.audio.setBounded(key, result, audioCacheTTL, 200)
	return result, nil
}

const (
	// How long a sync may take before it stops and leaves the rest for tomorrow.
	animeSyncBudget = 45 * time.Minute
	animeFlushEvery = 10
	// Failed fetches in a row before the sync gives up. The site being down
	// looks the same from every show, so there's nothing to gain from the rest.
	animeMaxFailures = 5
)

type animeMatch struct {
	series AnimeItem
	show   *anime.Show
	malID  *int
}

// syncAnimeMarkers fetches the show catalog, then the tables of the matched
// shows that aren't cached or have gone stale, then their recap flags. Only
// one sync runs at a time.
func (s *Server) syncAnimeMarkers(ctx context.Context) {
	a := s.anime
	if !a.syncing.CompareAndSwap(false, true) {
		return
	}
	defer a.syncing.Store(false)
	set := s.animeSettings(ctx)
	if !set.filler {
		slog.InfoContext(ctx, "anime markers sync skipped: off in settings")
		return
	}
	started := time.Now()
	a.src.EnsureCatalog(ctx)
	if index, _ := a.src.Catalog(); index == nil {
		slog.WarnContext(ctx, "anime markers sync stopped: the show catalog couldn't be loaded. Check that the server can reach animefillerlist.com")
		return
	}
	candidates, err := a.catalog.AnimeSeries(ctx, set.libraries)
	if err != nil {
		slog.ErrorContext(ctx, "anime markers sync: list the series", "error", err)
		return
	}
	// A localized library name never matches the site's English titles, so
	// the titles behind those series' provider ids are fetched first.
	if err := a.refreshAliases(ctx, candidates); err != nil {
		slog.WarnContext(ctx, "anime markers sync: the provider id title lookup failed, matching on library titles only", "error", err)
	}

	var matches []animeMatch
	var pending []string
	shows := map[string]*anime.Show{}
	fresh := a.src.Tables.FreshSlugs(set.maxAge)
	for _, series := range candidates {
		show, _, malID := a.matchSeries(series)
		if show == nil {
			continue
		}
		matches = append(matches, animeMatch{series, show, malID})
		if _, ok := shows[show.Slug]; !ok {
			shows[show.Slug] = show
			if !fresh[show.Slug] {
				pending = append(pending, show.Slug)
			}
		}
	}
	slog.InfoContext(ctx, "anime markers sync: matched series to shows", "series", len(matches), "shows", len(shows), "toFetch", len(pending))
	a.src.Log.Printf("sync  run started: %d series matched %d shows, recapLookup=%t", len(matches), len(shows), set.recapLookup)

	a.fetchShows(ctx, pending, shows, started)
	if set.recapLookup {
		a.fetchRecaps(ctx, matches, started)
	} else {
		a.src.Log.Printf("recap pass skipped: the recap lookup is switched off in settings")
	}
	if err := a.src.Tables.Flush(); err != nil {
		slog.ErrorContext(ctx, "anime markers sync: save the cache", "error", err)
	}
	cached, episodes, flagged := a.src.Tables.Counts()
	slog.InfoContext(ctx, "anime markers sync finished", "took", time.Since(started).Round(time.Second), "shows", cached, "episodes", episodes, "fillerOrMixed", flagged)
}

func (a *animeMarkers) fetchShows(ctx context.Context, pending []string, shows map[string]*anime.Show, started time.Time) {
	fetched, failed, inARow, sinceFlush := 0, 0, 0, 0
	for i, slug := range pending {
		if ctx.Err() != nil {
			return
		}
		if time.Since(started) > animeSyncBudget {
			slog.InfoContext(ctx, "anime markers sync: out of time, the rest are fetched on the next run", "left", len(pending)-i)
			return
		}
		episodes, ok := a.src.FetchEpisodes(ctx, slug)
		if !ok {
			a.src.Log.Printf("sync  fetch %s -> the page could not be read", slug)
			failed++
			if inARow++; inARow >= animeMaxFailures {
				slog.WarnContext(ctx, "anime markers sync stopped early: the site is unreachable rather than the shows missing", "failedInARow", inARow, "cached", fetched)
				return
			}
			continue
		}
		a.src.Log.Printf("sync  fetch %s -> %d episodes", slug, len(episodes))
		inARow = 0
		if len(episodes) == 0 {
			continue
		}
		a.src.Tables.Set(&anime.CacheEntry{Slug: slug, Title: shows[slug].Title, Episodes: episodes})
		fetched++
		if sinceFlush++; sinceFlush >= animeFlushEvery {
			sinceFlush = 0
			if err := a.src.Tables.Flush(); err != nil {
				slog.ErrorContext(ctx, "anime markers sync: save the cache", "error", err)
			}
			slog.InfoContext(ctx, "anime markers sync progress", "done", i+1, "of", len(pending), "cached", fetched, "unreadable", failed)
		}
	}
}

// fetchRecaps adds MyAnimeList's recap flags to every matched show that has a
// table but no recap flags yet.
func (a *animeMarkers) fetchRecaps(ctx context.Context, matches []animeMatch, started time.Time) {
	log := a.src.Log
	updated := 0
	seen := map[string]bool{}
	for _, m := range matches {
		if ctx.Err() != nil || time.Since(started) > animeSyncBudget {
			break
		}
		slug := m.show.Slug
		if seen[slug] {
			continue
		}
		seen[slug] = true
		entry := a.src.Tables.GetAny(slug)
		if entry == nil {
			log.Printf("recap %s: skipped, the show has no cached table yet", slug)
			continue
		}
		if entry.RecapMalID != nil {
			log.Printf("recap %s: already done from MAL %d", slug, *entry.RecapMalID)
			continue
		}
		// A match through a provider id's titles already knows which entry named
		// the show. Resolving again could land on another, such as a movie
		// sharing the series' ids.
		mal := 0
		if m.malID != nil {
			mal = *m.malID
		} else if found, ok := a.src.ResolveMalID(ctx, m.series.ProviderIDs, m.series.Title); ok {
			mal = found
		} else {
			log.Printf("recap %s: no MyAnimeList id for %q from its provider ids (%v), the offline mapping table, or the AniList search", slug, m.series.Title, m.series.ProviderIDs)
			continue
		}
		log.Printf("recap %s: resolved to MAL %d, asking the episode API", slug, mal)
		lookup, ok := a.src.FetchRecaps(ctx, mal)
		if !ok {
			log.Printf("recap %s: the episode API would not answer for MAL %d after retries, leaving it for the next run", slug, mal)
			continue
		}
		log.Printf("recap %s: MAL %d covers %d episodes, %d of them recaps", slug, mal, lookup.Episodes, len(lookup.Recaps))
		updatedEntry := *entry
		updatedEntry.Episodes = slices.Clone(entry.Episodes)
		for i, e := range updatedEntry.Episodes {
			if e.Number <= lookup.Episodes {
				updatedEntry.Episodes[i].Recap = lookup.Recaps[e.Number]
			}
		}
		updatedEntry.RecapMalID = &mal
		a.src.Tables.Update(&updatedEntry)
		updated++
	}
	if updated > 0 {
		slog.InfoContext(ctx, "anime markers sync: recap flags added", "shows", updated)
	}
}

// scheduleAnimeMarkers syncs every night at 4:00 and never at startup, since
// a first run walks the whole library at the site's ten second crawl delay.
func (s *Server) scheduleAnimeMarkers() {
	go func() {
		for {
			now := time.Now()
			next := time.Date(now.Year(), now.Month(), now.Day(), 4, 0, 0, 0, now.Location())
			if !next.After(now) {
				next = next.AddDate(0, 0, 1)
			}
			time.Sleep(time.Until(next))
			s.syncAnimeMarkers(context.Background())
		}
	}()
}
