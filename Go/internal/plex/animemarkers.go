package plex

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"slices"
	"strconv"
	"strings"
	"time"

	"github.com/Moonfin-Client/Plugin/Go/internal/api"
)

// plexAnimeItem is the part of a show, episode or movie the anime markers read.
type plexAnimeItem struct {
	RatingKey             string      `json:"ratingKey"`
	Type                  string      `json:"type"`
	Title                 string      `json:"title"`
	OriginalTitle         string      `json:"originalTitle"`
	Year                  int         `json:"year"`
	LibrarySectionID      json.Number `json:"librarySectionID"`
	ParentRatingKey       string      `json:"parentRatingKey"`
	GrandparentRatingKey  string      `json:"grandparentRatingKey"`
	ParentIndex           int         `json:"parentIndex"`
	Index                 int         `json:"index"`
	OriginallyAvailableAt string      `json:"originallyAvailableAt"`
	Genre                 []plexTag   `json:"Genre"`
	Label                 []plexTag   `json:"Label"`
	GUID                  plexGUIDs   `json:"Guid"`
	Media                 []struct {
		Part []struct {
			Stream []struct {
				StreamType   int    `json:"streamType"`
				LanguageCode string `json:"languageCode"`
			} `json:"Stream"`
		} `json:"Part"`
	} `json:"Media"`
}

type plexAnimeList struct {
	MediaContainer struct {
		Metadata []plexAnimeItem `json:"Metadata"`
	} `json:"MediaContainer"`
}

// AnimeSeries lists the shows in these libraries, or in every show library,
// as the server owner, since the sync covers the whole server.
func (c *Companion) AnimeSeries(ctx context.Context, libraryIDs []string) ([]api.AnimeItem, error) {
	token := c.ownerToken()
	if token == "" {
		return nil, errors.New("the server owner hasn't signed in on the Moonbase page yet")
	}
	sections, err := c.sections(ctx, token)
	if err != nil {
		return nil, err
	}
	var out []api.AnimeItem
	for _, s := range sections {
		if s.Type != "show" || (len(libraryIDs) > 0 && !slices.Contains(libraryIDs, s.Key)) {
			continue
		}
		shows, err := c.animeList(ctx, token, "/library/sections/"+url.PathEscape(s.Key)+"/all", url.Values{"type": {"2"}, "includeGuids": {"1"}})
		if err != nil {
			return nil, err
		}
		for _, show := range shows {
			show.LibraryID = s.Key
			out = append(out, show)
		}
	}
	return out, nil
}

// AnimeEpisodes lists a show's episodes as the caller.
func (c *Companion) AnimeEpisodes(ctx context.Context, caller api.Identity, seriesID string) ([]api.AnimeItem, error) {
	if _, err := strconv.ParseUint(seriesID, 10, 64); err != nil {
		return nil, nil
	}
	return c.animeList(ctx, caller.Token, "/library/metadata/"+seriesID+"/allLeaves", url.Values{})
}

// AnimeItems reads the items with their audio tracks as the caller, in
// batches through /library/metadata, which has every track.
func (c *Companion) AnimeItems(ctx context.Context, caller api.Identity, ids []string) ([]api.AnimeItem, error) {
	var keys []string
	for _, id := range ids {
		// Plex ids are numbers. Anything else is another server's id.
		if _, err := strconv.ParseUint(id, 10, 64); err == nil {
			keys = append(keys, id)
		}
	}
	var out []api.AnimeItem
	for batch := range slices.Chunk(keys, badgeBatch) {
		status, body, err := c.get(ctx, c.cfg.PMS+"/library/metadata/"+strings.Join(batch, ",")+"?includeGuids=1", caller.Token)
		if err != nil {
			return nil, err
		}
		// Plex answers 404 when none of the batch is there for this caller.
		if status == http.StatusNotFound {
			continue
		}
		if status != http.StatusOK {
			return nil, fmt.Errorf("read items: status %d", status)
		}
		var v plexAnimeList
		if err := json.Unmarshal(body, &v); err != nil {
			return nil, err
		}
		for _, it := range v.MediaContainer.Metadata {
			out = append(out, animeItemOf(it))
		}
	}
	return out, nil
}

// animeList reads a listing a page at a time, since a long-running show's
// episodes can outgrow one answer.
func (c *Companion) animeList(ctx context.Context, token, path string, q url.Values) ([]api.AnimeItem, error) {
	const page = 500
	var out []api.AnimeItem
	for start := 0; ; start += page {
		q.Set("X-Plex-Container-Start", strconv.Itoa(start))
		q.Set("X-Plex-Container-Size", strconv.Itoa(page))
		var v plexAnimeList
		if err := c.getJSON(ctx, c.cfg.PMS+path+"?"+q.Encode(), token, &v); err != nil {
			return nil, err
		}
		for _, it := range v.MediaContainer.Metadata {
			out = append(out, animeItemOf(it))
		}
		if len(v.MediaContainer.Metadata) < page {
			return out, nil
		}
	}
}

// animeItemOf keeps the provider ids from the Guid list by scheme (tvdb,
// tmdb, imdb), takes labels as tags, and reads the audio of the first version.
func animeItemOf(it plexAnimeItem) api.AnimeItem {
	item := api.AnimeItem{ID: it.RatingKey, Type: it.Type, Title: it.Title, OriginalTitle: it.OriginalTitle, Year: it.Year,
		LibraryID: it.LibrarySectionID.String(), SeriesID: it.GrandparentRatingKey, SeasonID: it.ParentRatingKey,
		Season: it.ParentIndex, Index: it.Index, Genres: tags(it.Genre), Tags: tags(it.Label), ProviderIDs: map[string]string{}}
	item.AirDate, _ = time.Parse(time.DateOnly, it.OriginallyAvailableAt)
	for _, g := range it.GUID {
		if scheme, id, ok := strings.Cut(g.ID, "://"); ok && id != "" {
			item.ProviderIDs[scheme] = id
		}
	}
	if len(it.Media) > 0 && len(it.Media[0].Part) > 0 {
		for _, s := range it.Media[0].Part[0].Stream {
			if s.StreamType == 2 {
				item.AudioLanguages = append(item.AudioLanguages, s.LanguageCode)
			}
		}
	}
	return item
}
