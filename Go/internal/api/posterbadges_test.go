package api

import (
	"context"
	"encoding/json"
	"net/http"
	"os"
	"path/filepath"
	"reflect"
	"regexp"
	"slices"
	"strings"
	"testing"

	"github.com/Moonfin-Client/Plugin/Go/internal/store"
)

var (
	atmos4K = []BadgeTrack{
		{Type: "Video", Codec: "hevc", Width: 3840, Height: 2076, VideoRangeType: "DOVI"},
		{Type: "Audio", Codec: "eac3", Channels: 6, Language: "eng", Profile: "dolby digital plus + dolby atmos"},
		{Type: "Subtitle", Codec: "srt", Language: "eng"},
	}
	frenchDub = []BadgeTrack{
		{Type: "Video", Codec: "h264", Width: 1920, Height: 1080, VideoRangeType: "SDR"},
		{Type: "Audio", Codec: "dts", Channels: 6, Language: "fre", Profile: "ma", Title: "VFQ"},
		{Type: "Audio", Codec: "ac3", Channels: 2, Language: "fra"},
		{Type: "Audio", Codec: "aac", Channels: 2, Language: "und"},
	}
)

func TestBadgePresetsReadTheTracks(t *testing.T) {
	cases := []struct {
		path   string
		tracks []BadgeTrack
		want   []string
	}{
		{"/m/Avatar.mkv", atmos4K, []string{"4k", "dolbyvision", "hevc", "atmos", "5.1"}},
		{"/m/Film_REMUX_1080p.mkv", frenchDub, []string{"1080p", "h264", "dtshdma", "5.1", "remux"}},
		{"/m/Film.NON-IMAX.mkv", nil, []string{}},
		{"/m/Film.IMAX.Enhanced.mkv", nil, []string{"imax"}},
		{"/m/RemuxedCut.mkv", nil, []string{}},
	}
	for _, c := range cases {
		got := []string{}
		for id := range detectPresets(c.path, c.tracks) {
			got = append(got, id)
		}
		slices.Sort(got)
		slices.Sort(c.want)
		if !reflect.DeepEqual(got, c.want) {
			t.Errorf("%s: got %v, want %v", c.path, got, c.want)
		}
	}
}

func TestBadgeConditions(t *testing.T) {
	cond := func(field, match, op, value string) badgeCondition {
		return badgeCondition{Field: field, Match: match, Operator: op, Value: value}
	}
	cases := []struct {
		name   string
		c      badgeCondition
		tracks []BadgeTrack
		want   bool
	}{
		{"a title matches", cond("Audio.Title", "any", "contains", "vfq"), frenchDub, true},
		{"no track is German", cond("Audio.Language", "none", "oneOf", "ger,deu"), frenchDub, true},
		{"every track is French", cond("Audio.Language", "every", "oneOf", "fre,fra"), frenchDub, false},
		{"fre and fra are one language", cond("Audio.LanguageCount", "any", "is", "1"), frenchDub, true},
		{"three audio tracks", cond("Audio.Count", "any", "atLeast", "3"), frenchDub, true},
		{"six channels is 6.0", cond("Audio.Channels", "any", "is", "6.0"), frenchDub, true},
		{"a pattern", cond("Audio.Codec", "any", "matches", "^(dts|truehd)$"), frenchDub, true},
		{"the old does not match", cond("Audio.Codec", "", "notMatches", "truehd"), frenchDub, true},
		{"a pattern Go can't run", cond("Path", "any", "matches", "(?<!x)y"), frenchDub, false},
		{"a misspelled property", cond("Audio.Lang", "none", "is", "x"), frenchDub, false},
		{"a misspelled type", cond("Audoi.Language", "none", "is", "x"), frenchDub, false},
		{"a number that isn't one", cond("Video.Height", "any", "atLeast", "tall"), frenchDub, false},
		{"unread tracks never say none", cond("Audio.Language", "none", "is", "ger"), nil, false},
		{"no video track for every", cond("Video.Codec", "every", "is", "h264"), []BadgeTrack{}, false},
		{"the HDR type", cond("Video.VideoRangeType", "any", "is", "dovi"), atmos4K, true},
	}
	for _, c := range cases {
		if got := conditionHolds(c.c, "/m/x.mkv", c.tracks); got != c.want {
			t.Errorf("%s: got %v", c.name, got)
		}
	}
}

func TestBadgeMatchFillsTheLook(t *testing.T) {
	half := 50.0
	rules := []badgeRule{
		{Label: "VF", Color: "#00ff00", Conditions: []badgeCondition{{Field: "Audio.Language", Match: "any", Operator: "is", Value: "fre"}}, Style: "nope", Opacity: &half},
		{Label: "vf", Conditions: []badgeCondition{{Field: "Path", Operator: "contains", Value: "x"}}},
		{Preset: "1080p", Label: "1080p", Corner: "bottomRight"},
		{Icon: "Flag-FR", Conditions: []badgeCondition{{Field: "Audio.Language", Match: "any", Operator: "is", Value: "fra"}}},
		{Label: "Off", Enabled: new(bool), Preset: "1080p"},
		{Label: "Empty"},
	}
	got := matchBadges(rules, BadgeFile{Path: "/m/x.mkv", Tracks: frenchDub}, newBadgeDefaults())
	want := []posterBadge{
		{Label: "VF", Color: "#00FF00", Style: "solid", Opacity: 50, Size: "medium", Corner: "topLeft", Shape: "rounded"},
		{Label: "1080p", Color: "#2563EB", Style: "solid", Opacity: 100, Size: "medium", Corner: "bottomRight", Shape: "rounded"},
		{Color: "#2563EB", Style: "solid", Opacity: 100, Size: "medium", Corner: "topLeft", Shape: "rounded", Icon: "flag-fr"},
	}
	if !reflect.DeepEqual(got, want) {
		t.Errorf("got %+v\nwant %+v", got, want)
	}

	results := explainBadges(rules, BadgeFile{Path: "/m/x.mkv", Tracks: frenchDub})
	outcomes := []string{}
	for _, r := range results {
		outcomes = append(outcomes, r.outcome)
	}
	if want := []string{"shown", "sameText", "shown", "shown", "off", "notMet"}; !reflect.DeepEqual(outcomes, want) {
		t.Errorf("outcomes %v, want %v", outcomes, want)
	}
}

func TestBadgesForViewer(t *testing.T) {
	cond := []badgeCondition{{Field: "Path", Operator: "contains", Value: "x"}}
	rules := []badgeRule{
		{Label: "All", Conditions: cond},
		{Label: "Kid", Conditions: cond, UserIds: []string{"42"}},
		{Label: "TV", Conditions: cond, Profiles: []string{"tv"}},
		{Label: "Guid", Conditions: cond, UserIds: []string{"AB-CD"}},
	}
	labels := func(kept []badgeRule) []string {
		out := []string{}
		for _, r := range kept {
			out = append(out, r.Label)
		}
		return out
	}
	if got := labels(badgesForViewer(rules, "42", "TV ")); !reflect.DeepEqual(got, []string{"All", "Kid", "TV"}) {
		t.Errorf("kid on tv: %v", got)
	}
	if got := labels(badgesForViewer(rules, "abcd", "")); !reflect.DeepEqual(got, []string{"All", "Guid"}) {
		t.Errorf("dashless id: %v", got)
	}
	if badgeVersion(rules, newBadgeDefaults()) == badgeVersion(rules[:3], newBadgeDefaults()) {
		t.Error("the version didn't change with the rules")
	}
}

type badgeCatalog struct {
	fakeCatalog
	files map[string]BadgeFile
	asked []string
}

func (b *badgeCatalog) BadgeFiles(_ context.Context, _ Identity, ids []string) (map[string]BadgeFile, error) {
	b.asked = ids
	out := map[string]BadgeFile{}
	for _, id := range ids {
		if f, ok := b.files[id]; ok {
			out[id] = f
		}
	}
	return out, nil
}

func (b *badgeCatalog) BadgeSearch(_ context.Context, _ Identity, term string, _ int) ([]M, error) {
	return []M{{"Id": "1", "Name": term, "Type": "Movie"}}, nil
}

func TestBadgeRoutes(t *testing.T) {
	catalog := &badgeCatalog{files: map[string]BadgeFile{
		"1": {Name: "Avatar", Path: "/m/Avatar.mkv", Tracks: atmos4K},
		"2": {Name: "Unread", Path: "/m/Unread.mkv"},
	}}
	host := testHost(store.NewMemKV())
	host.Catalog = catalog
	s := NewServer("1.2.3", host)

	if _, got := call(t, s, userMain, "GET", "/Moonfin/PosterBadges/Items", "", map[string]any{"ids": "1"}); got["enabled"] != false {
		t.Fatalf("no rules: %v", got)
	}

	config, _ := json.Marshal(M{
		"PosterBadgeRules": []M{
			{"Label": "4K", "Preset": "4k", "Enabled": true},
			{"Label": "No German", "Enabled": true, "Conditions": []M{{"Field": "Audio.Language", "Match": "none", "Operator": "is", "Value": "ger"}}},
			{"Label": "Desktop", "Enabled": true, "Preset": "4k", "Profiles": []string{"desktop"}},
		},
		"PosterBadgeDefaults": M{"MaxPerCorner": 3},
	})
	if code, _ := call(t, s, adminMain, "POST", "/Moonfin/Admin/Configuration", string(config), nil); code != http.StatusNoContent {
		t.Fatalf("save: %d", code)
	}

	_, got := call(t, s, userMain, "GET", "/Moonfin/PosterBadges/Items", "", map[string]any{"ids": "1, 2,1,9", "profile": "tv"})
	if got["enabled"] != true || got["maxPerCorner"] != 3.0 || got["version"] == "" {
		t.Fatalf("answer: %v", got)
	}
	items := got["items"].(map[string]any)
	if len(items) != 1 || len(items["1"].([]any)) != 2 {
		t.Errorf("items: %v", items)
	}
	if !reflect.DeepEqual(catalog.asked, []string{"1", "2", "9"}) {
		t.Errorf("asked for %v", catalog.asked)
	}

	if code, _ := call(t, s, userMain, "POST", "/Moonfin/PosterBadges/Test", `{"ItemId":"1"}`, nil); code != http.StatusForbidden {
		t.Errorf("a user ran the test: %d", code)
	}
	_, test := call(t, s, adminMain, "POST", "/Moonfin/PosterBadges/Test", `{"ItemId":"1","Rules":[{"Label":"HDR","Enabled":true,"Conditions":[{"Field":"Video.VideoRangeType","Match":"any","Operator":"contains","Value":"dovi"}]}]}`, nil)
	if test["name"] != "Avatar" || len(test["tracks"].([]any)) != 3 || len(test["badges"].([]any)) != 1 {
		t.Errorf("test: %v", test)
	}
	if _, found := call(t, s, adminMain, "GET", "/Moonfin/PosterBadges/Search", "", map[string]any{"term": "ava"}); len(found["Items"].([]any)) != 1 {
		t.Errorf("search: %v", found)
	}
	resp := serve(context.Background(), s, adminMain, "GET", "/Moonfin/PosterBadges/Presets", "", nil)
	var presets []M
	_ = json.NewDecoder(resp.Body).Decode(&presets)
	if len(presets) != len(badgePresets) {
		t.Errorf("presets: %d", len(presets))
	}
}

func TestBadgeItemsOnAHostWithoutFiles(t *testing.T) {
	s := newTestServer()
	config := `{"PosterBadgeRules":[{"Label":"4K","Preset":"4k","Enabled":true}]}`
	call(t, s, adminMain, "POST", "/Moonfin/Admin/Configuration", config, nil)
	if _, got := call(t, s, userMain, "GET", "/Moonfin/PosterBadges/Items", "", map[string]any{"ids": "1"}); got["enabled"] != false {
		t.Errorf("Silo can't read files, so it has no labels: %v", got)
	}
}

func TestBadgeFlagsAreServedWithoutSignIn(t *testing.T) {
	dir := t.TempDir()
	os.WriteFile(filepath.Join(dir, "flag-jp.svg"), []byte("<svg/>"), 0o600)
	host := testHost(store.NewMemKV())
	host.FlagsDir = dir
	s := NewServer("1.2.3", host)
	for path, want := range map[string]int{
		"/Moonfin/Assets/flag-jp.svg":  http.StatusOK,
		"/Moonfin/Assets/flag-jp.webp": http.StatusNotFound,
		"/Moonfin/Assets/flag-zz.svg":  http.StatusNotFound,
		"/Moonfin/Assets/jp.svg":       http.StatusNotFound,
	} {
		if resp := serve(context.Background(), s, anon, "GET", path, "", nil); resp.StatusCode != want {
			t.Errorf("%s: %d, want %d", path, resp.StatusCode, want)
		}
	}
}

// The lists are copies of the C# ones, so a change there has to reach here.
func TestBadgeListsMatchTheSharedCode(t *testing.T) {
	read := func(name string) string {
		b, err := os.ReadFile(filepath.Join("..", "..", "..", "shared", "PosterBadges", name))
		if err != nil {
			t.Fatal(err)
		}
		return string(b)
	}
	quoted := regexp.MustCompile(`"([^"]+)"`)
	strs := func(text string) []string {
		out := []string{}
		for _, m := range quoted.FindAllStringSubmatch(text, -1) {
			out = append(out, m[1])
		}
		return out
	}

	flags := read("PosterBadgeFlags.cs")
	if got := strs(flags[strings.Index(flags, "Codes"):]); !reflect.DeepEqual(got, badgeFlagCodes) {
		t.Errorf("flags differ from PosterBadgeFlags.cs")
	}

	rules := read("PosterBadgeRules.cs")
	icons := rules[strings.Index(rules, "Icons = new[]"):]
	if got := strs(icons[:strings.Index(icons, "}")]); !reflect.DeepEqual(got, badgeIcons[:len(got)]) || len(got) != len(badgeIcons)-len(badgeFlagCodes) {
		t.Errorf("icons differ from PosterBadgeMatcher.Icons: %v", got)
	}

	presets := regexp.MustCompile(`P\("([^"]+)", "([^"]+)", "([^"]+)", "([^"]+)", "((?:[^"\\]|\\.)*)"\)`).FindAllStringSubmatch(read("PosterBadgePresets.cs"), -1)
	if len(presets) != len(badgePresets) {
		t.Fatalf("%d presets in C#, %d here", len(presets), len(badgePresets))
	}
	for i, m := range presets {
		want := badgePreset{m[1], m[2], m[3], m[4], strings.ReplaceAll(m[5], `\"`, `"`)}
		if badgePresets[i] != want {
			t.Errorf("preset %d: %+v, want %+v", i, badgePresets[i], want)
		}
	}
}
