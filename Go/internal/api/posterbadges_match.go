package api

import (
	"cmp"
	"regexp"
	"slices"
	"strconv"
	"strings"
	"sync"
	"unicode"
	"unicode/utf8"
)

// posterBadge is what a client draws for one matched rule, every default
// filled in.
type posterBadge struct {
	Label     string `json:"label"`
	Color     string `json:"color"`
	TextColor string `json:"textColor"`
	Style     string `json:"style"`
	Opacity   int    `json:"opacity"`
	Size      string `json:"size"`
	Corner    string `json:"corner"`
	Shape     string `json:"shape"`
	Icon      string `json:"icon"`
}

var (
	badgeStyles  = []string{"solid", "glass", "outline", "dark", "light"}
	badgeSizes   = []string{"small", "medium", "large", "xlarge"}
	badgeCorners = []string{"topLeft", "topRight", "bottomLeft", "bottomRight"}
	badgeShapes  = []string{"rounded", "pill", "square"}
	badgeHex     = regexp.MustCompile(`^#[0-9a-fA-F]{6}$`)
)

// badgeIcons are the icons a label can carry, by their Material name, then
// the flags, the same list as PosterBadgeMatcher.Icons.
var badgeIcons = append([]string{
	"contrast", "hdr_on", "high_quality", "movie", "theaters", "aspect_ratio", "surround_sound",
	"speaker", "graphic_eq", "album", "translate", "subtitles", "record_voice_over", "star", "check_circle",
}, func() []string {
	flags := make([]string, len(badgeFlagCodes))
	for i, code := range badgeFlagCodes {
		flags[i] = "flag-" + code
	}
	return flags
}()...)

// matchBadges is the labels one file gets, in the rules' order. Rules that
// share a label are alternatives, so it shows once.
func matchBadges(rules []badgeRule, file BadgeFile, d badgeDefaults) []posterBadge {
	badges := []posterBadge{}
	shown := map[string]bool{}
	var presets map[string]bool
	for _, rule := range rules {
		key := strings.ToLower(shownKey(rule))
		if !rule.enabled() || key == "" || shown[key] {
			continue
		}
		if !ruleHolds(rule, file, &presets) {
			continue
		}
		shown[key] = true
		badges = append(badges, resolveBadge(rule, d))
	}
	return badges
}

// ruleHolds checks a rule, working out the built-in labels once per file.
func ruleHolds(rule badgeRule, file BadgeFile, presets *map[string]bool) bool {
	if preset := strings.TrimSpace(rule.Preset); preset != "" {
		if *presets == nil {
			*presets = detectPresets(file.Path, file.Tracks)
		}
		return (*presets)[strings.ToLower(preset)]
	}
	// A rule with no conditions would label every poster on the server.
	return len(rule.Conditions) > 0 && !slices.ContainsFunc(rule.Conditions, func(c badgeCondition) bool {
		return !conditionHolds(c, file.Path, file.Tracks)
	})
}

type badgeResult struct {
	index      int
	outcome    string
	conditions []bool
}

// explainBadges tells the admin page how each rule fared on a file: "shown",
// "off", "noText", "notMet" or "sameText".
func explainBadges(rules []badgeRule, file BadgeFile) []badgeResult {
	presets := detectPresets(file.Path, file.Tracks)
	shown := map[string]bool{}
	results := []badgeResult{}
	for i, rule := range rules {
		res := badgeResult{index: i, conditions: []bool{}}
		holds := false
		if strings.TrimSpace(rule.Preset) != "" {
			holds = ruleHolds(rule, file, &presets)
		} else {
			for _, c := range rule.Conditions {
				res.conditions = append(res.conditions, conditionHolds(c, file.Path, file.Tracks))
			}
			holds = len(res.conditions) > 0 && !slices.Contains(res.conditions, false)
		}
		key := strings.ToLower(shownKey(rule))
		switch {
		case !rule.enabled():
			res.outcome = "off"
		case key == "":
			res.outcome = "noText"
		case !holds:
			res.outcome = "notMet"
		case shown[key]:
			res.outcome = "sameText"
		default:
			shown[key] = true
			res.outcome = "shown"
		}
		results = append(results, res)
	}
	return results
}

// describeTracks is each track's type and the values conditions read,
// leaving out empty ones.
func describeTracks(tracks []BadgeTrack) []M {
	rows := []M{}
	for _, t := range tracks {
		row := M{"Type": t.Type}
		for _, name := range []string{"Language", "Title", "Codec", "Profile", "Channels", "Width", "Height", "VideoRangeType", "VideoRange", "IsDefault", "IsForced"} {
			if v, _ := t.property(name); v != "" {
				row[name] = v
			}
		}
		rows = append(rows, row)
	}
	return rows
}

// shownKey is what makes two labels the same: the text, or the icon for a
// label without any. Empty for a label with neither.
func shownKey(rule badgeRule) string {
	if label := strings.TrimSpace(rule.Label); label != "" {
		return label
	}
	if icon := iconOf(rule); icon != "" {
		return "icon:" + icon
	}
	return ""
}

func iconOf(rule badgeRule) string {
	icon := strings.TrimSpace(rule.Icon)
	for _, known := range badgeIcons {
		if strings.EqualFold(known, icon) {
			return known
		}
	}
	return ""
}

// resolveBadge fills every empty or unknown setting from the defaults, so a
// client draws exactly what it gets.
func resolveBadge(rule badgeRule, d badgeDefaults) posterBadge {
	opacity := d.Opacity
	if rule.Opacity != nil {
		opacity = int(*rule.Opacity)
	}
	return posterBadge{
		Label:     strings.TrimSpace(rule.Label),
		Color:     cmp.Or(badgeHexOf(rule.Color), "#2563EB"),
		TextColor: badgeHexOf(rule.TextColor),
		Style:     pickBadge(badgeStyles, rule.Style, d.Style),
		Opacity:   min(100, max(10, opacity)),
		Size:      pickBadge(badgeSizes, rule.Size, d.Size),
		Corner:    pickBadge(badgeCorners, rule.Corner, d.Corner),
		Shape:     pickBadge(badgeShapes, rule.Shape, d.Shape),
		Icon:      iconOf(rule),
	}
}

func badgeHexOf(v string) string {
	if v = strings.TrimSpace(v); badgeHex.MatchString(v) {
		return strings.ToUpper(v)
	}
	return ""
}

func pickBadge(allowed []string, value, fallback string) string {
	for _, candidate := range []string{value, fallback} {
		for _, a := range allowed {
			if strings.EqualFold(a, strings.TrimSpace(candidate)) {
				return a
			}
		}
	}
	return allowed[0]
}

// property reads a track value by its Jellyfin name. False for a name tracks
// don't have, which is a typo rather than an empty value.
func (t BadgeTrack) property(name string) (string, bool) {
	number := func(n int) string {
		if n <= 0 {
			return ""
		}
		return strconv.Itoa(n)
	}
	yesNo := func(b bool) string {
		if b {
			return "True"
		}
		return "False"
	}
	switch strings.ToLower(name) {
	case "language":
		return t.Language, true
	case "title":
		return t.Title, true
	case "codec":
		return t.Codec, true
	case "profile":
		return t.Profile, true
	case "channels":
		return number(t.Channels), true
	case "width":
		return number(t.Width), true
	case "height":
		return number(t.Height), true
	case "bitdepth":
		return number(t.BitDepth), true
	case "videorangetype":
		return t.VideoRangeType, true
	case "videorange":
		return t.VideoRange, true
	case "isdefault":
		return yesNo(t.IsDefault), true
	case "isforced":
		return yesNo(t.IsForced), true
	}
	return "", false
}

var badgeTrackTypes = []string{"Audio", "Video", "Subtitle", "EmbeddedImage", "Data", "Attachment", "Lyric"}

// conditionHolds checks one condition. A track condition looks at every
// track of its type: "any" holds when one passes, "none" when none does and
// "every" when there is at least one and all of them pass.
func conditionHolds(c badgeCondition, path string, tracks []BadgeTrack) bool {
	field := strings.TrimSpace(c.Field)
	op := strings.TrimSpace(c.Operator)
	match := strings.TrimSpace(c.Match)
	// "notMatches" is the older way to write "none" with "matches".
	if strings.EqualFold(op, "notMatches") {
		op, match = "matches", "none"
	}
	// A blank value, a pattern that doesn't compile or a number that isn't
	// one fails the whole condition, so a typo never reads as "no track has it".
	if field == "" || !badgeValueUsable(op, c.Value) {
		return false
	}
	values, ok := conditionValues(field, path, tracks)
	if !ok {
		return false
	}
	passing := 0
	for _, v := range values {
		if badgeValuePasses(op, c.Value, v) {
			passing++
		}
	}
	switch {
	case strings.EqualFold(match, "none"):
		return passing == 0
	case strings.EqualFold(match, "every"):
		// A file with no track of that type doesn't pass "every audio track is French".
		return len(values) > 0 && passing == len(values)
	}
	return passing > 0
}

// conditionValues is what a condition looks at: the path, how many tracks of
// a type there are, or one property of each. False for a field naming nothing,
// and for tracks that were never read.
func conditionValues(field, path string, tracks []BadgeTrack) ([]string, bool) {
	if strings.EqualFold(field, "Path") {
		return []string{path}, true
	}
	trackType, property, found := strings.Cut(field, ".")
	if !found || trackType == "" || property == "" || tracks == nil || !slices.ContainsFunc(badgeTrackTypes, func(t string) bool { return strings.EqualFold(t, trackType) }) {
		return nil, false
	}
	count := strings.EqualFold(property, "Count")
	languageCount := strings.EqualFold(property, "LanguageCount")
	if languageCount {
		property = "Language"
	}
	values := []string{}
	for _, t := range tracks {
		if !strings.EqualFold(t.Type, trackType) {
			continue
		}
		v := ""
		if !count {
			var known bool
			if v, known = t.property(property); !known {
				return nil, false
			}
		}
		values = append(values, v)
	}
	if languageCount {
		languages := map[string]bool{}
		for _, v := range values {
			if key := languageKey(v); key != "" {
				languages[key] = true
			}
		}
		return []string{strconv.Itoa(len(languages))}, true
	}
	if count {
		return []string{strconv.Itoa(len(values))}, true
	}
	return values, true
}

// ISO 639-2 gives some languages two codes, and files use both.
var sameLanguage = map[string]string{
	"alb": "sqi", "arm": "hye", "baq": "eus", "bur": "mya", "chi": "zho",
	"cze": "ces", "dut": "nld", "fre": "fra", "geo": "kat", "ger": "deu",
	"gre": "ell", "ice": "isl", "mac": "mkd", "mao": "mri", "may": "msa",
	"per": "fas", "rum": "ron", "slo": "slk", "tib": "bod", "wel": "cym",
}

// languageKey is one key per language, so a French dub and a French audio
// description count once, and so do "fre" and "fra". Untagged tracks don't
// count. Two-letter codes aren't turned into three-letter ones, since Plex
// always sends three.
func languageKey(language string) string {
	code := strings.ToLower(strings.TrimSpace(language))
	if cut := strings.IndexAny(code, "-_"); cut > 0 {
		code = code[:cut]
	}
	switch code {
	case "", "und", "mis", "zxx", "mul":
		return ""
	}
	if same, ok := sameLanguage[code]; ok {
		return same
	}
	return code
}

func isNumericOp(op string) bool {
	for _, n := range []string{"atLeast", "moreThan", "atMost", "lessThan"} {
		if strings.EqualFold(op, n) {
			return true
		}
	}
	return false
}

func badgeNumber(text string) (float64, bool) {
	n, err := strconv.ParseFloat(strings.TrimSpace(text), 64)
	return n, err == nil
}

func badgeValueUsable(op, target string) bool {
	if strings.TrimSpace(target) == "" {
		return false
	}
	if strings.EqualFold(op, "matches") {
		return badgeRegex(target) != nil
	}
	if isNumericOp(op) {
		_, ok := badgeNumber(target)
		return ok
	}
	return strings.EqualFold(op, "is") || strings.EqualFold(op, "contains") || strings.EqualFold(op, "oneOf")
}

func badgeValuePasses(op, target, value string) bool {
	if value == "" {
		return false
	}
	if isNumericOp(op) {
		n, ok := badgeNumber(value)
		limit, _ := badgeNumber(target)
		if !ok {
			return false
		}
		switch strings.ToLower(op) {
		case "atleast":
			return n >= limit
		case "morethan":
			return n > limit
		case "atmost":
			return n <= limit
		}
		return n < limit
	}
	switch {
	case strings.EqualFold(op, "is"):
		return sameBadgeValue(value, target)
	case strings.EqualFold(op, "contains"):
		return strings.Contains(strings.ToLower(value), strings.ToLower(strings.TrimSpace(target)))
	case strings.EqualFold(op, "oneOf"):
		return slices.ContainsFunc(strings.Split(target, ","), func(option string) bool {
			return strings.TrimSpace(option) != "" && sameBadgeValue(value, option)
		})
	}
	return badgeRegex(target).MatchString(value)
}

// sameBadgeValue compares numbers as numbers, so "6" is "6.0", and text
// without caring about case.
func sameBadgeValue(value, target string) bool {
	a, aok := badgeNumber(value)
	b, bok := badgeNumber(target)
	if aok && bok {
		return a == b
	}
	return strings.EqualFold(strings.TrimSpace(value), strings.TrimSpace(target))
}

// Go's patterns run in linear time, so unlike .NET they need no timeout. A
// .NET-only pattern, such as one with a lookbehind, doesn't compile and fails
// its condition.
var badgeRegexes sync.Map

func badgeRegex(pattern string) *regexp.Regexp {
	if cached, ok := badgeRegexes.Load(pattern); ok {
		return cached.(*regexp.Regexp)
	}
	re, _ := regexp.Compile("(?i)" + pattern)
	badgeRegexes.Store(pattern, re)
	return re
}

type badgePreset struct {
	id, label, group, color, description string
}

// badgePresets are the built-in labels, the same as PosterBadgePresets.All.
var badgePresets = []badgePreset{
	{"8k", "8K", "resolution", "#DC2626", "Width from 7600 or height from 4300."},
	{"4k", "4K", "resolution", "#7C3AED", "Width from 3800 or height from 2000, so a 3840x1600 film counts."},
	{"1440p", "1440p", "resolution", "#C026D3", "Width from 2500 or height from 1400."},
	{"1080p", "1080p", "resolution", "#0284C7", "Width from 1800 or height from 1000."},
	{"720p", "720p", "resolution", "#0D9488", "Width from 1200 or height from 700."},
	{"480p", "480p", "resolution", "#A16207", "Width from 600 or height from 400."},
	{"sd", "SD", "resolution", "#6B7280", "Anything smaller."},

	{"dolbyVision", "DV", "hdr", "#1F2937", "Dolby Vision, with or without an HDR10 fallback."},
	{"hdr10plus", "HDR10+", "hdr", "#D97706", "HDR10+ without Dolby Vision."},
	{"hdr10", "HDR10", "hdr", "#CA8A04", "Plain HDR10."},
	{"hlg", "HLG", "hdr", "#65A30D", "HLG, the broadcast HDR."},

	{"atmos", "Atmos", "audio", "#2563EB", "Dolby Atmos on any audio track."},
	{"dtsx", "DTS:X", "audio", "#EA580C", "DTS:X on any audio track."},
	{"truehd", "TrueHD", "audio", "#16A34A", "Dolby TrueHD without Atmos."},
	{"dtshdma", "DTS-HD MA", "audio", "#C2410C", "DTS-HD Master Audio without DTS:X."},
	{"dts", "DTS", "audio", "#F97316", "Other DTS."},
	{"ddplus", "DD+", "audio", "#0891B2", "Dolby Digital Plus (E-AC-3) without Atmos."},
	{"dd", "DD", "audio", "#64748B", "Dolby Digital (AC-3)."},

	{"7.1", "7.1", "channels", "#9333EA", "An audio track with 8 channels or more."},
	{"5.1", "5.1", "channels", "#6D28D9", "An audio track with 6 or 7 channels."},

	{"av1", "AV1", "codec", "#DB2777", "AV1 video."},
	{"hevc", "HEVC", "codec", "#4F46E5", "HEVC (H.265) video."},
	{"h264", "H.264", "codec", "#475569", "H.264 (AVC) video."},
	{"vp9", "VP9", "codec", "#A21CAF", "VP9 video."},

	{"remux", "REMUX", "release", "#BE123C", "\"REMUX\" in the file path."},
	{"imax", "IMAX", "release", "#0369A1", "\"IMAX\" in the file path, but not \"NON-IMAX\"."},
}

// Best first. A file with several audio tracks gets the best one's label.
var badgeAudioOrder = []string{"atmos", "dtsx", "truehd", "dtshdma", "dts", "ddplus", "dd"}

var (
	badgeDtsX        = regexp.MustCompile(`(?i)DTS[:\- ]?X\b`)
	badgeMasterAudio = regexp.MustCompile(`(?i)\bMA\b|Master Audio`)
)

// detectPresets is the ids, in lower case, of every built-in label a file
// meets. Each group gives one answer, so a 4K file never also says 1080p.
func detectPresets(path string, tracks []BadgeTrack) map[string]bool {
	found := map[string]bool{}
	add := func(id string) {
		if id != "" {
			found[strings.ToLower(id)] = true
		}
	}
	// The widest video track, since some files carry a small second one.
	var video *BadgeTrack
	bestAudio, channels := len(badgeAudioOrder), 0
	for i := range tracks {
		t := &tracks[i]
		switch {
		case strings.EqualFold(t.Type, "Video"):
			if video == nil || t.Width > video.Width {
				video = t
			}
		case strings.EqualFold(t.Type, "Audio"):
			bestAudio = min(bestAudio, audioRank(*t))
			channels = max(channels, t.Channels)
		}
	}
	if video != nil {
		add(badgeResolution(video.Width, video.Height))
		add(dynamicRange(video.VideoRangeType))
		add(videoCodecPreset(video.Codec))
	}
	if bestAudio < len(badgeAudioOrder) {
		add(badgeAudioOrder[bestAudio])
	}
	if channels >= 8 {
		add("7.1")
	} else if channels >= 6 {
		add("5.1")
	}
	if standaloneWord(path, "remux", false) {
		add("remux")
	}
	if standaloneWord(path, "imax", true) {
		add("imax")
	}
	return found
}

// badgeResolution counts width too, since scope films are wide but short.
func badgeResolution(width, height int) string {
	switch {
	case width <= 0 && height <= 0:
		return ""
	case width >= 7600 || height >= 4300:
		return "8k"
	case width >= 3800 || height >= 2000:
		return "4k"
	case width >= 2500 || height >= 1400:
		return "1440p"
	case width >= 1800 || height >= 1000:
		return "1080p"
	case width >= 1200 || height >= 700:
		return "720p"
	case width >= 600 || height >= 400:
		return "480p"
	}
	return "sd"
}

func dynamicRange(rangeType string) string {
	switch {
	case strings.HasPrefix(strings.ToUpper(rangeType), "DOVI") && !strings.EqualFold(rangeType, "DOVIInvalid"):
		return "dolbyVision"
	case strings.EqualFold(rangeType, "HDR10Plus"):
		return "hdr10plus"
	case strings.EqualFold(rangeType, "HDR10"):
		return "hdr10"
	case strings.EqualFold(rangeType, "HLG"):
		return "hlg"
	}
	return ""
}

func videoCodecPreset(codec string) string {
	switch strings.ToLower(codec) {
	case "av1":
		return "av1"
	case "hevc", "h265":
		return "hevc"
	case "h264", "avc":
		return "h264"
	case "vp9":
		return "vp9"
	}
	return ""
}

func audioRank(t BadgeTrack) int {
	codec := strings.ToLower(t.Codec)
	// Atmos and DTS:X show in the profile, and releases often name them in the title.
	text := t.Profile + " " + t.Title + " " + t.Extra
	isDts := codec == "dts" || codec == "dca"
	switch {
	case strings.Contains(strings.ToLower(text), "atmos"):
		return 0
	case isDts && badgeDtsX.MatchString(text):
		return 1
	case codec == "truehd":
		return 2
	case isDts && badgeMasterAudio.MatchString(text):
		return 3
	case isDts:
		return 4
	case codec == "eac3":
		return 5
	case codec == "ac3":
		return 6
	}
	return len(badgeAudioOrder)
}

// standaloneWord finds word in text with no letter or digit on either side,
// so "Movie_REMUX_2160p" counts. notAfterNon leaves out "NON-IMAX".
func standaloneWord(text, word string, notAfterNon bool) bool {
	lower := strings.ToLower(text)
	for from := 0; from < len(lower); {
		at := strings.Index(lower[from:], word)
		if at < 0 {
			return false
		}
		start, end := from+at, from+at+len(word)
		before, _ := utf8.DecodeLastRuneInString(lower[:start])
		after, _ := utf8.DecodeRuneInString(lower[end:])
		ok := !isWordRune(before) && !isWordRune(after)
		if ok && notAfterNon {
			prefix := lower[:start]
			if r, size := utf8.DecodeLastRuneInString(prefix); size > 0 && strings.ContainsRune(" ._-", r) {
				prefix = prefix[:len(prefix)-size]
			}
			ok = !strings.HasSuffix(prefix, "non")
		}
		if ok {
			return true
		}
		from = start + 1
	}
	return false
}

func isWordRune(r rune) bool {
	return r != utf8.RuneError && (unicode.IsLetter(r) || unicode.IsNumber(r))
}
