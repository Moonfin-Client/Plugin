package plex

import (
	"context"
	"io"
	"net/http"
	"net/http/httptest"
	"reflect"
	"testing"

	"github.com/Moonfin-Client/Plugin/Go/internal/api"
)

// Shapes Plex answers with: a Dolby Vision profile 5 movie, a movie stored
// twice, an episode Plex hasn't read yet and a show.
const badgeMetadata = `{"MediaContainer":{"Metadata":[
{"ratingKey":"10","type":"movie","title":"Fire","Media":[{"Part":[{"file":"/m/Fire.mkv","Stream":[
 {"streamType":1,"codec":"hevc","width":3840,"height":2076,"DOVIPresent":true,"DOVIBLCompatID":0,"DOVIProfile":5,"displayTitle":"4K DoVi (HEVC Main 10)"},
 {"streamType":2,"codec":"eac3","channels":6,"languageCode":"eng","profile":"dolby digital plus + dolby atmos","default":true,"displayTitle":"English (EAC3 5.1 + Atmos)"},
 {"streamType":3,"codec":"srt","languageCode":"eng","forced":true,"title":"Forced"}]}]}]},
{"ratingKey":"11","type":"movie","title":"Ralph","Media":[
 {"Part":[{"file":"/m/Ralph.720p.mp4","Stream":[{"streamType":1,"codec":"h264","width":1280,"height":534},{"streamType":2,"codec":"aac","channels":2}]}]},
 {"Part":[{"file":"/m/Ralph.2160p.mkv","Stream":[{"streamType":1,"codec":"hevc","width":3840,"height":1608,"colorTrc":"smpte2084","displayTitle":"4K HDR10"},{"streamType":2,"codec":"dca","channels":6,"profile":"ma"}]}]}]},
{"ratingKey":"12","type":"episode","title":"Pilot","Media":[{"Part":[{"file":"/tv/Pilot.mkv"}]}]},
{"ratingKey":"13","type":"show","title":"Show"}]}}`

func TestBadgeFilesReadPlexTracks(t *testing.T) {
	var asked []string
	pms := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		asked = append(asked, r.URL.Path+" "+r.Header.Get("X-Plex-Token"))
		io.WriteString(w, badgeMetadata)
	}))
	t.Cleanup(pms.Close)
	c, err := New(Config{PMS: pms.URL, DataDir: t.TempDir()})
	if err != nil {
		t.Fatal(err)
	}

	files, err := c.BadgeFiles(context.Background(), api.Identity{Token: "viewer"}, []string{"10", "11", "12", "13", "not-plex"})
	if err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(asked, []string{"/library/metadata/10,11,12,13 viewer"}) {
		t.Errorf("asked %v", asked)
	}
	if len(files) != 3 {
		t.Fatalf("files %v", files)
	}

	fire := files["10"]
	want := []api.BadgeTrack{
		{Type: "Video", Codec: "hevc", Width: 3840, Height: 2076, VideoRangeType: "DOVI", VideoRange: "HDR", Extra: "4K DoVi (HEVC Main 10)"},
		{Type: "Audio", Codec: "eac3", Channels: 6, Language: "eng", Profile: "dolby digital plus + dolby atmos", IsDefault: true, Extra: "English (EAC3 5.1 + Atmos)"},
		{Type: "Subtitle", Codec: "srt", Language: "eng", Title: "Forced", IsForced: true},
	}
	if fire.Path != "/m/Fire.mkv" || !reflect.DeepEqual(fire.Tracks, want) {
		t.Errorf("fire: %+v", fire)
	}

	ralph := files["11"]
	if ralph.Path != "/m/Ralph.2160p.mkv" || ralph.Tracks[0].VideoRangeType != "HDR10" || ralph.Tracks[1].Codec != "dts" {
		t.Errorf("the larger version wasn't read the Jellyfin way: %+v", ralph)
	}
	if pilot := files["12"]; pilot.Tracks != nil || pilot.Name != "Pilot" {
		t.Errorf("unread tracks should stay nil: %+v", pilot)
	}
}

func TestVideoRangeType(t *testing.T) {
	compat := func(n int) *int { return &n }
	cases := []struct {
		s    plexBadgeStream
		want string
	}{
		{plexBadgeStream{ColorTrc: "bt709"}, "SDR"},
		{plexBadgeStream{ColorTrc: "smpte2084"}, "HDR10"},
		{plexBadgeStream{ColorTrc: "smpte2084", DisplayTitle: "4K HDR10+"}, "HDR10Plus"},
		{plexBadgeStream{ColorTrc: "arib-std-b67"}, "HLG"},
		{plexBadgeStream{DOVIPresent: true, DOVIBLCompatID: compat(1), ColorTrc: "smpte2084"}, "DOVIWithHDR10"},
		{plexBadgeStream{DOVIPresent: true, DOVIBLCompatID: compat(4)}, "DOVIWithHLG"},
		{plexBadgeStream{DOVIPresent: true, DOVIBLCompatID: compat(2)}, "DOVIWithSDR"},
		{plexBadgeStream{DOVIPresent: true, ColorTrc: "smpte2084"}, "DOVIWithHDR10"},
	}
	for _, c := range cases {
		if got := videoRangeType(c.s); got != c.want {
			t.Errorf("%+v: %s, want %s", c.s, got, c.want)
		}
	}
}
