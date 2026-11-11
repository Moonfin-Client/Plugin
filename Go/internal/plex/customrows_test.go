package plex

import (
	"context"
	"maps"
	"net/http"
	"net/http/httptest"
	"testing"

	"github.com/Moonfin-Client/Plugin/Go/internal/api"
)

func TestLibraryGUIDsReadTheCallersLibraries(t *testing.T) {
	c := calendarPlex(t)
	movies, err := c.LibraryGUIDs(context.Background(), api.Identity{Token: "member"}, false)
	if err != nil || !maps.Equal(movies, map[string]string{"imdb://tt0133093": "600", "tmdb://603": "600"}) {
		t.Fatalf("movies = %v, %v", movies, err)
	}
	shows, err := c.LibraryGUIDs(context.Background(), api.Identity{Token: "member"}, true)
	if err != nil || !maps.Equal(shows, map[string]string{"tvdb://100": "500", "imdb://tt0944947": "510", "tmdb://1399": "510"}) {
		t.Fatalf("shows = %v, %v", shows, err)
	}
}

// The admin page checks a new custom list through this route, signed by its
// cookie.
func TestCustomRowsAnswerTheAdminPage(t *testing.T) {
	c := calendarPlex(t)
	r := httptest.NewRequest("GET", "/Moonfin/CustomRows/Items?source=nope&type=x&params=%7B%7D", nil)
	r.AddCookie(&http.Cookie{Name: cookieToken, Value: "owner"})
	if w, body := send(c, r); w.Code != http.StatusBadRequest || body["Error"] != "Unsupported custom row source: nope" {
		t.Fatalf("custom rows = %d %v", w.Code, body)
	}
}
