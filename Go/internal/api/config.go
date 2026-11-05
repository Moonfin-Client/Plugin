package api

import (
	"context"
	"log/slog"
	"maps"
	"strings"

	"github.com/Moonfin-Client/Plugin/Go/internal/pluginconfig"
	"github.com/Moonfin-Client/Plugin/Go/internal/settings"
)

// Config is what the routes need from the plugin configuration.
type Config struct {
	EnableSettingsSync bool
	MdblistAPIKey      string
	TmdbAPIKey         string
	SeerrDisplayName   string
	SmartSearchEnabled bool
	// SmartSearchModel is "english" or "multilingual".
	SmartSearchModel string
	// Defaults is DefaultUserSettings, the admin's defaults for every profile.
	Defaults M
	// DeviceDefaults is DefaultDeviceSettings, the admin's defaults for the
	// desktop, mobile and tv profiles, laid over Defaults.
	DeviceDefaults M
	// The calendar's window around today, in days.
	CalendarDaysBack, CalendarDaysAhead int
	// CalendarCacheMinutes is how long an arr calendar is kept. 0 keeps none.
	CalendarCacheMinutes int
	CalendarAdminsOnly   bool
	Sonarr, Radarr       arrServer
}

// DefaultsFor is the admin's defaults for one profile.
func (c Config) DefaultsFor(profile string) M {
	device, _ := c.DeviceDefaults[profile].(M)
	return settings.LayerDefaults(c.Defaults, device)
}

// keyFormMoved marks settings saved since the Moonbase page became the only
// place to set them, so the old form's values no longer count.
const keyFormMoved = "SiloFormMoved"

// loadPageConfig reads the settings the Moonbase page saved.
func (s *Server) loadPageConfig(ctx context.Context) (M, error) {
	var c M
	if _, err := s.loadJSON(ctx, keyConfig, &c); err != nil {
		return nil, err
	}
	return c, nil
}

// fullConfig is the whole PluginConfiguration as the Jellyfin plugin would
// have it.
func (s *Server) fullConfig(ctx context.Context) (M, error) {
	page, err := s.loadPageConfig(ctx)
	if err != nil {
		return nil, err
	}
	return s.mergeConfig(page), nil
}

// mergeConfig lays a fresh install's values, then (until the page is first
// saved) the old form's, then the page's.
func (s *Server) mergeConfig(page M) M {
	out := pluginconfig.Defaults()
	merged := M{}
	if moved, _ := page[keyFormMoved].(bool); !moved {
		top, defaults := fromNative(s.nativeConfig())
		maps.Copy(out, top)
		maps.Copy(merged, defaults)
	}
	maps.Copy(out, page)
	delete(out, keyFormMoved)
	if d, ok := page["DefaultUserSettings"].(M); ok {
		maps.Copy(merged, d)
	}
	if len(merged) > 0 {
		out["DefaultUserSettings"] = merged
	} else {
		delete(out, "DefaultUserSettings")
	}
	return out
}

// config reads the configuration. If storage fails it logs and carries on
// with the old form's values alone, so routes keep working.
func (s *Server) config(ctx context.Context) Config {
	c, err := s.fullConfig(ctx)
	if err != nil {
		slog.ErrorContext(ctx, "load plugin configuration", "error", err)
		c = s.mergeConfig(nil)
	}
	return configFrom(c)
}

func configFrom(c M) Config {
	cfg := Config{EnableSettingsSync: true}
	if v, ok := c["EnableSettingsSync"].(bool); ok {
		cfg.EnableSettingsSync = v
	}
	cfg.MdblistAPIKey = strings.TrimSpace(str(c["MdblistApiKey"]))
	cfg.TmdbAPIKey = strings.TrimSpace(str(c["TmdbApiKey"]))
	cfg.SeerrDisplayName = strings.TrimSpace(str(c["SeerrDisplayName"]))
	cfg.SmartSearchEnabled, _ = c["SmartSearchEnabled"].(bool)
	cfg.SmartSearchModel = str(c["SmartSearchModel"])
	cfg.Defaults, _ = c["DefaultUserSettings"].(M)
	cfg.DeviceDefaults, _ = c["DefaultDeviceSettings"].(M)
	cfg.CalendarDaysBack = wholeNumber(c["CalendarDaysBack"], 14)
	cfg.CalendarDaysAhead = wholeNumber(c["CalendarDaysAhead"], 90)
	cfg.CalendarCacheMinutes = wholeNumber(c["CalendarCacheMinutes"], 5)
	cfg.CalendarAdminsOnly, _ = c["CalendarAdminsOnly"].(bool)
	cfg.Sonarr = arrServerFrom(c, "Sonarr")
	cfg.Radarr = arrServerFrom(c, "Radarr")
	return cfg
}

// wholeNumber reads a number of at least 0 the page saved, or def when there is none.
func wholeNumber(v any, def int) int {
	if n, ok := v.(int); ok {
		return max(0, n)
	}
	if f, ok := num(v); ok {
		return max(0, int(f))
	}
	return def
}

func str(v any) string {
	s, _ := v.(string)
	return s
}
