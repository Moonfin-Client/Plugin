package plex

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/url"
	"strconv"
	"strings"

	"github.com/Moonfin-Client/Plugin/Go/internal/api"
)

// Items are read in batches of this many through /library/metadata, which
// answers with every track.
const badgeBatch = 50

type plexBadgeStream struct {
	StreamType     int    `json:"streamType"`
	Codec          string `json:"codec"`
	LanguageCode   string `json:"languageCode"`
	Title          string `json:"title"`
	DisplayTitle   string `json:"displayTitle"`
	Profile        string `json:"profile"`
	Channels       int    `json:"channels"`
	Width          int    `json:"width"`
	Height         int    `json:"height"`
	BitDepth       int    `json:"bitDepth"`
	ColorTrc       string `json:"colorTrc"`
	DOVIPresent    bool   `json:"DOVIPresent"`
	DOVIBLCompatID *int   `json:"DOVIBLCompatID"`
	Default        bool   `json:"default"`
	Forced         bool   `json:"forced"`
}

type plexBadgeItem struct {
	RatingKey string `json:"ratingKey"`
	Type      string `json:"type"`
	Title     string `json:"title"`
	Media     []struct {
		Part []struct {
			File string `json:"file"`
			// Nil when Plex hasn't read the file's tracks yet.
			Stream *[]plexBadgeStream `json:"Stream"`
		} `json:"Part"`
	} `json:"Media"`
}

// BadgeFiles reads the files of the movies and episodes among ids, asking
// Plex as the caller so a shared user only gets labels for what they can see.
func (c *Companion) BadgeFiles(ctx context.Context, caller api.Identity, ids []string) (map[string]api.BadgeFile, error) {
	var keys []string
	for _, id := range ids {
		// Plex ids are numbers. Anything else is another server's id.
		if _, err := strconv.ParseUint(id, 10, 64); err == nil {
			keys = append(keys, id)
		}
	}
	files := map[string]api.BadgeFile{}
	for start := 0; start < len(keys); start += badgeBatch {
		batch := keys[start:min(start+badgeBatch, len(keys))]
		status, body, err := c.get(ctx, c.cfg.PMS+"/library/metadata/"+strings.Join(batch, ","), caller.Token)
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
		var v struct {
			MediaContainer struct {
				Metadata []plexBadgeItem `json:"Metadata"`
			} `json:"MediaContainer"`
		}
		if err := json.Unmarshal(body, &v); err != nil {
			return nil, err
		}
		for _, item := range v.MediaContainer.Metadata {
			if file, ok := badgeFileOf(item); ok {
				files[item.RatingKey] = file
			}
		}
	}
	return files, nil
}

// badgeFileOf reads the version with the largest picture when a movie is
// stored more than once. False for anything but a movie or an episode, which
// have no file of their own.
func badgeFileOf(item plexBadgeItem) (api.BadgeFile, bool) {
	if item.Type != "movie" && item.Type != "episode" {
		return api.BadgeFile{}, false
	}
	file := api.BadgeFile{Name: item.Title}
	best := -1
	for _, media := range item.Media {
		if len(media.Part) == 0 {
			continue
		}
		part := media.Part[0]
		var tracks []api.BadgeTrack
		pixels := 0
		if part.Stream != nil {
			tracks = []api.BadgeTrack{}
			for _, s := range *part.Stream {
				t := badgeTrackOf(s)
				if t.Type == "" {
					continue
				}
				if t.Type == "Video" {
					pixels = max(pixels, t.Width*t.Height)
				}
				tracks = append(tracks, t)
			}
		}
		if pixels > best {
			best = pixels
			file.Path = part.File
			file.Tracks = tracks
		}
	}
	return file, best >= 0
}

// badgeTrackOf names a Plex track the way Jellyfin does, so the rules and the
// admin page read the same on every server.
func badgeTrackOf(s plexBadgeStream) api.BadgeTrack {
	t := api.BadgeTrack{
		Language:  s.LanguageCode,
		Title:     s.Title,
		Codec:     s.Codec,
		Profile:   s.Profile,
		Channels:  s.Channels,
		Width:     s.Width,
		Height:    s.Height,
		BitDepth:  s.BitDepth,
		IsDefault: s.Default,
		IsForced:  s.Forced,
		Extra:     s.DisplayTitle,
	}
	switch s.StreamType {
	case 1:
		t.Type = "Video"
		t.VideoRangeType = videoRangeType(s)
		t.VideoRange = "HDR"
		if t.VideoRangeType == "SDR" {
			t.VideoRange = "SDR"
		}
	case 2:
		t.Type = "Audio"
		// Jellyfin and ffmpeg call DTS dts, Plex dca.
		if strings.EqualFold(s.Codec, "dca") {
			t.Codec = "dts"
		}
	case 3:
		t.Type = "Subtitle"
	case 4:
		t.Type = "Lyric"
	}
	return t
}

// videoRangeType is Jellyfin's name for the picture's dynamic range. Plex
// says Dolby Vision outright, and the rest shows in the transfer curve.
func videoRangeType(s plexBadgeStream) string {
	pq := s.ColorTrc == "smpte2084"
	hlg := s.ColorTrc == "arib-std-b67"
	plus := strings.Contains(s.DisplayTitle, "HDR10+")
	if s.DOVIPresent {
		compat := -1
		if s.DOVIBLCompatID != nil {
			compat = *s.DOVIBLCompatID
		}
		switch {
		case compat == 1 || (compat < 0 && pq):
			if plus {
				return "DOVIWithHDR10Plus"
			}
			return "DOVIWithHDR10"
		case compat == 4 || (compat < 0 && hlg):
			return "DOVIWithHLG"
		case compat == 2:
			return "DOVIWithSDR"
		}
		return "DOVI"
	}
	switch {
	case plus:
		return "HDR10Plus"
	case pq:
		return "HDR10"
	case hlg:
		return "HLG"
	}
	return "SDR"
}

// BadgeSearch finds movies and episodes by name for the admin page's test.
func (c *Companion) BadgeSearch(ctx context.Context, caller api.Identity, term string, limit int) ([]api.M, error) {
	q := url.Values{"query": {term}, "limit": {strconv.Itoa(limit)}}
	status, body, err := c.get(ctx, c.cfg.PMS+"/hubs/search?"+q.Encode(), caller.Token)
	if err != nil || status != http.StatusOK {
		return nil, fmt.Errorf("search: status %d, %v", status, err)
	}
	var v struct {
		MediaContainer struct {
			Hub []struct {
				Metadata []struct {
					RatingKey            string `json:"ratingKey"`
					Type                 string `json:"type"`
					Title                string `json:"title"`
					GrandparentTitle     string `json:"grandparentTitle"`
					GrandparentRatingKey string `json:"grandparentRatingKey"`
					ParentIndex          *int   `json:"parentIndex"`
					Index                *int   `json:"index"`
					Year                 int    `json:"year"`
				} `json:"Metadata"`
			} `json:"Hub"`
		} `json:"MediaContainer"`
	}
	if err := json.Unmarshal(body, &v); err != nil {
		return nil, err
	}
	items := []api.M{}
	for _, hub := range v.MediaContainer.Hub {
		for _, m := range hub.Metadata {
			if len(items) == limit {
				return items, nil
			}
			switch m.Type {
			case "movie":
				item := api.M{"Id": m.RatingKey, "Name": m.Title, "Type": "Movie"}
				if m.Year > 0 {
					item["ProductionYear"] = m.Year
				}
				items = append(items, item)
			case "episode":
				items = append(items, api.M{"Id": m.RatingKey, "Name": m.Title, "Type": "Episode", "SeriesName": m.GrandparentTitle,
					"SeriesId": m.GrandparentRatingKey, "ParentIndexNumber": m.ParentIndex, "IndexNumber": m.Index})
			}
		}
	}
	return items, nil
}
