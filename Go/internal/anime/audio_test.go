package anime

import "testing"

func TestAudioVerdicts(t *testing.T) {
	for _, c := range []struct {
		name      string
		languages []string
		want      AudioKind
	}{
		{"Japanese only is subbed", []string{"jpn"}, Subbed},
		{"dual audio is both", []string{"jpn", "ger"}, SubbedAndDubbed},
		{"another language on its own is dubbed", []string{"eng"}, Dubbed},
		{"ja", []string{"ja"}, Subbed},
		{"jp", []string{"jp"}, Subbed},
		{"jap", []string{"jap"}, Subbed},
		{"Japanese", []string{"Japanese"}, Subbed},
		{"JPN", []string{"JPN"}, Subbed},
		{"und", []string{"und"}, ""},
		{"unknown", []string{"unknown"}, ""},
		{"empty", []string{""}, ""},
		{"untagged tracks aren't a dub", []string{"jpn", "und"}, Subbed},
		{"no audio at all", nil, ""},
	} {
		if got := ClassifyAudio(c.languages); got != c.want {
			t.Errorf("%s: got %q, want %q", c.name, got, c.want)
		}
	}
}

func TestDualAudioCollapsesToDubbedUnlessAskedFor(t *testing.T) {
	if CollapseAudio(SubbedAndDubbed, false) != Dubbed || CollapseAudio(SubbedAndDubbed, true) != SubbedAndDubbed {
		t.Fatal("dual audio collapsed the wrong way")
	}
	if CollapseAudio(Subbed, false) != Subbed || CollapseAudio(Dubbed, false) != Dubbed {
		t.Fatal("collapsing changed another verdict")
	}
}

func TestSeasonVerdicts(t *testing.T) {
	for _, c := range []struct {
		name  string
		kinds []AudioKind
		want  AudioKind
	}{
		{"episodes that agree", []AudioKind{Dubbed, Dubbed}, Dubbed},
		// Labeling by majority would be wrong for the episodes in the minority.
		{"a season with both", []AudioKind{Dubbed, Subbed}, ""},
		// One untagged file in a subbed season shouldn't silence the pill.
		{"unclassified episodes", []AudioKind{Subbed, "", Subbed}, Subbed},
		{"nothing classified", []AudioKind{"", ""}, ""},
	} {
		if got := ClassifySeason(c.kinds); got != c.want {
			t.Errorf("%s: got %q, want %q", c.name, got, c.want)
		}
	}
}
