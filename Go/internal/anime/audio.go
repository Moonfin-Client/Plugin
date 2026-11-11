package anime

import "strings"

// AudioKind says whether a file is the original Japanese audio or carries a
// dub. Empty means no verdict.
type AudioKind string

const (
	Subbed AudioKind = "Subbed"
	Dubbed AudioKind = "Dubbed"
	// SubbedAndDubbed carries Japanese and another language, so the viewer can choose.
	SubbedAndDubbed AudioKind = "SubbedAndDubbed"
)

// Spellings of Japanese that turn up in real files. Servers normalize most
// tracks, but a remux carries whatever the muxer wrote.
var japaneseCodes = map[string]bool{"jpn": true, "jp": true, "ja": true, "jap": true, "japanese": true, "japan": true}

// Values that mean nobody tagged the track, which mustn't be read as a dub.
var unknownCodes = map[string]bool{"und": true, "unknown": true, "unk": true, "zxx": true, "mis": true, "mul": true}

// ClassifyAudio is the verdict for one file from its audio track languages:
// Japanese alone is subbed and any other spoken language makes it dubbed.
// Empty when nothing usable was tagged, since guessing would put a wrong pill
// on the common untagged rip.
func ClassifyAudio(languages []string) AudioKind {
	japanese, other := false, false
	for _, raw := range languages {
		language := strings.ToLower(strings.TrimSpace(raw))
		switch {
		case language == "" || unknownCodes[language]:
		case japaneseCodes[language]:
			japanese = true
		default:
			other = true
		}
	}
	switch {
	case other && japanese:
		return SubbedAndDubbed
	case other:
		return Dubbed
	case japanese:
		return Subbed
	}
	return ""
}

// CollapseAudio folds dual audio back into Dubbed for admins who'd rather see
// one label. It's applied at the edge, so the classifier always reports what
// the file holds.
func CollapseAudio(kind AudioKind, separateDualAudio bool) AudioKind {
	if !separateDualAudio && kind == SubbedAndDubbed {
		return Dubbed
	}
	return kind
}

// ClassifySeason is a whole season's verdict, given only when every episode
// that has one agrees. A season holding both a dub and a sub stays blank
// rather than being labeled by majority, and episodes without a verdict
// don't count against the rest.
func ClassifySeason(kinds []AudioKind) AudioKind {
	var agreed AudioKind
	for _, kind := range kinds {
		switch {
		case kind == "":
		case agreed == "":
			agreed = kind
		case agreed != kind:
			return ""
		}
	}
	return agreed
}
