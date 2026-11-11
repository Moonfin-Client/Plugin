package plex

import (
	"context"
	"slices"
	"testing"

	"github.com/Moonfin-Client/Plugin/Go/internal/api"
)

func TestSeasonalMoviesListTheMoviesWithATMDBId(t *testing.T) {
	movies, err := calendarPlex(t).SeasonalMovies(context.Background())
	if err != nil || !slices.Equal(movies, []api.SeasonalMovie{{ID: "600", TmdbID: "603"}}) {
		t.Fatalf("movies = %v, %v", movies, err)
	}
}
