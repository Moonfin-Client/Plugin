package plex

import (
	"context"
	"encoding/xml"
	"fmt"
	"maps"
	"net/http"
	"net/url"
	"slices"
	"strconv"
	"strings"
	"time"

	"github.com/Moonfin-Client/Plugin/Go/internal/api"
)

// Calendar reminders read the libraries as the owner, since they run in the
// background for every account.

type arrivedItem struct {
	RatingKey            string    `json:"ratingKey"`
	AddedAt              int64     `json:"addedAt"`
	GrandparentRatingKey string    `json:"grandparentRatingKey"`
	ParentIndex          *int      `json:"parentIndex"`
	Index                *int      `json:"index"`
	GUID                 plexGUIDs `json:"Guid"`
}

// RecentArrivals gives each episode its show's ids. Before the owner signs in
// to the admin page nothing can be read, so nothing has arrived.
func (c *Companion) RecentArrivals(ctx context.Context, since time.Time) ([]api.Arrival, error) {
	owner := c.ownerToken()
	if owner == "" {
		return nil, nil
	}
	sections, err := c.sections(ctx, owner)
	if err != nil {
		return nil, err
	}
	var out []api.Arrival
	shows := map[string][]int{}
	for _, s := range sections {
		itemType := map[string]string{"movie": "1", "show": "4"}[s.Type]
		if itemType == "" {
			continue
		}
		items, err := c.addedSince(ctx, owner, s.Key, itemType, since)
		if err != nil {
			return nil, err
		}
		for _, it := range items {
			a := api.Arrival{ItemID: it.RatingKey}
			if itemType == "4" {
				a.IsEpisode, a.Season, a.Episode = true, it.ParentIndex, it.Index
				shows[it.GrandparentRatingKey] = append(shows[it.GrandparentRatingKey], len(out))
			} else {
				a.TvdbID, a.TmdbID, a.ImdbID = guidIDs(it.GUID)
			}
			out = append(out, a)
		}
	}
	keys := slices.Sorted(maps.Keys(shows))
	for start := 0; start < len(keys); start += smartBatch {
		items, err := c.metadata(ctx, owner, keys[start:min(start+smartBatch, len(keys))])
		if err != nil {
			return nil, err
		}
		for _, show := range items {
			tvdb, tmdb, imdb := guidIDs(show.GUID)
			for _, i := range shows[show.RatingKey] {
				out[i].TvdbID, out[i].TmdbID, out[i].ImdbID = tvdb, tmdb, imdb
			}
		}
	}
	return out, nil
}

// addedSince pages through one type of item in a library, newest first, until
// it reaches one added before since.
func (c *Companion) addedSince(ctx context.Context, token, sectionKey, itemType string, since time.Time) ([]arrivedItem, error) {
	const page = 100
	var out []arrivedItem
	for start := 0; ; start += page {
		var v struct {
			MediaContainer struct {
				Metadata []arrivedItem `json:"Metadata"`
			} `json:"MediaContainer"`
		}
		q := url.Values{"type": {itemType}, "sort": {"addedAt:desc"}, "X-Plex-Container-Start": {strconv.Itoa(start)}, "X-Plex-Container-Size": {strconv.Itoa(page)}}
		if itemType == "1" {
			q.Set("includeGuids", "1")
		}
		if err := c.getJSON(ctx, c.cfg.PMS+"/library/sections/"+url.PathEscape(sectionKey)+"/all?"+q.Encode(), token, &v); err != nil {
			return nil, err
		}
		for _, it := range v.MediaContainer.Metadata {
			if it.AddedAt < since.Unix() {
				return out, nil
			}
			out = append(out, it)
		}
		if len(v.MediaContainer.Metadata) < page {
			return out, nil
		}
	}
}

func guidIDs(guids plexGUIDs) (tvdb, tmdb, imdb string) {
	for _, g := range guids {
		scheme, id, _ := strings.Cut(g.ID, "://")
		switch scheme {
		case "tvdb":
			tvdb = id
		case "tmdb":
			tmdb = id
		case "imdb":
			imdb = id
		}
	}
	return tvdb, tmdb, imdb
}

// CanSee asks Plex for the item as the account: the owner with the owner's
// token, and a member with the token the owner's share list holds for them,
// so their library shares and restrictions apply. A member's token is only
// used for this one request and never kept.
func (c *Companion) CanSee(ctx context.Context, userID int, itemID string) (bool, error) {
	token := c.ownerToken()
	if token == "" {
		return false, nil
	}
	ownerID, _, err := c.plexTVUser(ctx, token)
	if err != nil {
		return false, err
	}
	if userID != ownerID {
		shares, err := c.shareList(ctx, token)
		if err != nil {
			return false, err
		}
		i := slices.IndexFunc(shares, func(s share) bool { return s.UserID == userID })
		if i < 0 || shares[i].AccessToken == "" {
			return false, nil
		}
		token = shares[i].AccessToken
	}
	status, _, err := c.get(ctx, c.cfg.PMS+"/library/metadata/"+url.PathEscape(itemID), token)
	if err != nil {
		return false, err
	}
	return status == http.StatusOK, nil
}

type share struct {
	UserID   int    `xml:"userID,attr"`
	Username string `xml:"username,attr"`
	Email    string `xml:"email,attr"`
	// AccessToken is the member's token for this server.
	AccessToken string `xml:"accessToken,attr"`
}

// shareList is plex.tv's list of who this server is shared with, read with
// the owner's token.
func (c *Companion) shareList(ctx context.Context, owner string) ([]share, error) {
	machine, err := c.machineIdentifier(ctx)
	if err != nil {
		return nil, err
	}
	status, body, err := c.get(ctx, c.cfg.PlexTV+"/api/servers/"+url.PathEscape(machine)+"/shared_servers", owner)
	if err != nil {
		return nil, fmt.Errorf("reach plex.tv: %w", err)
	}
	if status != http.StatusOK {
		return nil, fmt.Errorf("plex.tv answered %d to shared_servers", status)
	}
	var list struct {
		Shares []share `xml:"SharedServer"`
	}
	if err := xml.Unmarshal(body, &list); err != nil {
		return nil, fmt.Errorf("plex.tv sent an unreadable share list: %w", err)
	}
	return list.Shares, nil
}
