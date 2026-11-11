package plex

import (
	"context"
	"path/filepath"

	"github.com/Moonfin-Client/Plugin/Go/internal/api"
)

// GameLibraries lists the caller's libraries with their folders. Plex has no
// game library type, so any library can be picked to hold games, and
// Moonbase reads the files itself. A member's listing may leave the folders
// out, so the owner's fills them in.
func (c *Companion) GameLibraries(ctx context.Context, caller api.Identity) ([]api.GameLibrary, error) {
	type directory struct {
		Key      string `json:"key"`
		Title    string `json:"title"`
		Location []struct {
			Path string `json:"path"`
		} `json:"Location"`
	}
	list := func(token string) ([]directory, error) {
		var v struct {
			MediaContainer struct {
				Directory []directory `json:"Directory"`
			} `json:"MediaContainer"`
		}
		err := c.getJSON(ctx, c.cfg.PMS+"/library/sections", token, &v)
		return v.MediaContainer.Directory, err
	}
	mine, err := list(caller.Token)
	if err != nil {
		return nil, err
	}
	folders := map[string][]string{}
	missing := false
	for _, d := range mine {
		for _, l := range d.Location {
			folders[d.Key] = append(folders[d.Key], l.Path)
		}
		missing = missing || len(d.Location) == 0
	}
	if owner := c.ownerToken(); missing && !caller.Admin && owner != "" {
		all, err := list(owner)
		if err != nil {
			return nil, err
		}
		for _, d := range all {
			if len(folders[d.Key]) == 0 {
				for _, l := range d.Location {
					folders[d.Key] = append(folders[d.Key], l.Path)
				}
			}
		}
	}
	out := []api.GameLibrary{}
	for _, d := range mine {
		out = append(out, api.GameLibrary{ID: sectionPrefix + d.Key, ConfigID: d.Key, Name: d.Title, Folders: folders[d.Key]})
	}
	return out, nil
}

func (c *Companion) GamesDir() string { return filepath.Join(c.cfg.DataDir, "games") }

func (c *Companion) EmulatorJSDir() string { return c.cfg.EmulatorJSDir }
