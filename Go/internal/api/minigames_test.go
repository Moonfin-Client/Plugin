package api

import (
	"context"
	"encoding/json"
	"slices"
	"testing"
)

func board(t *testing.T, s *Server, c caller, game string) []miniGameEntry {
	t.Helper()
	resp := serve(context.Background(), s, c, "GET", "/Moonfin/MiniGames/"+game+"/Scores", "", nil)
	if resp.StatusCode != 200 {
		t.Fatalf("GET %s board: %d", game, resp.StatusCode)
	}
	var out []miniGameEntry
	if err := json.Unmarshal(readAll(t, resp), &out); err != nil {
		t.Fatal(err)
	}
	return out
}

func post(t *testing.T, s *Server, c caller, game, body string) (int, any) {
	t.Helper()
	status, out := call(t, s, c, "POST", "/Moonfin/MiniGames/"+game+"/Scores", body, nil)
	return status, out["rank"]
}

func names(rows []miniGameEntry) []string {
	out := []string{}
	for _, r := range rows {
		out = append(out, r.UserName)
	}
	return out
}

func TestMiniGameScoresRefuseBadInput(t *testing.T) {
	s := newTestServer()
	cases := []struct {
		c            caller
		method, game string
		body         string
		want         int
	}{
		{anon, "GET", "buffer-run", "", 401},
		{anon, "POST", "buffer-run", `{"score":1}`, 401},
		{userMain, "GET", "marquee-hop", "", 404},
		{userMain, "POST", "marquee-hop", `{"score":1}`, 404},
		{userMain, "POST", "buffer-run", `{"score":-1}`, 400},
		{userMain, "POST", "buffer-run", `{"score":10000000}`, 400},
		{userMain, "POST", "buffer-run", `{"score":1.5}`, 400},
		{userMain, "POST", "buffer-run", `{"score":"100"}`, 400},
		{userMain, "POST", "buffer-run", `{}`, 400},
		{userMain, "POST", "buffer-run", `[100]`, 400},
		{userMain, "POST", "buffer-run", ``, 400},
		{userMain, "POST", "buffer-run", `{"Score":9999999}`, 200},
		{userMain, "POST", "buffer-run", `{"score":0}`, 200},
	}
	for _, tc := range cases {
		if got, _ := call(t, s, tc.c, tc.method, "/Moonfin/MiniGames/"+tc.game+"/Scores", tc.body, nil); got != tc.want {
			t.Errorf("%s %s %q: got %d, want %d", tc.method, tc.game, tc.body, got, tc.want)
		}
	}
}

func TestMiniGameBoardKeepsEachProfilesBest(t *testing.T) {
	s := newTestServer()
	if got := board(t, s, userMain, "case-stack"); len(got) != 0 {
		t.Fatalf("empty board: %v", got)
	}
	steps := []struct {
		c    caller
		body string
		rank any
	}{
		{userMain, `{"score":100}`, 1.0},
		{userKids, `{"score":300}`, 1.0},
		{adminMain, `{"score":200}`, 2.0},
		{other, `{"score":50}`, nil},
		{userKids, `{"score":250}`, nil},
		{userKids, `{"score":300}`, nil},
		{other, `{"score":150}`, 3.0},
		{userMain, `{"score":400}`, 1.0},
	}
	for i, st := range steps {
		if status, rank := post(t, s, st.c, "case-stack", st.body); status != 200 || rank != st.rank {
			t.Fatalf("step %d: got %d %v, want 200 %v", i, status, rank, st.rank)
		}
	}
	got := board(t, s, adminKid, "case-stack")
	if want := []string{"Main", "Kids", "test"}; !slices.Equal(names(got), want) {
		t.Fatalf("board %v, want %v", names(got), want)
	}
	if got[0].Score != 400 || len(got[0].At) != len("2026-11-06T21:04:00Z") {
		t.Fatalf("top row %+v", got[0])
	}
	if other := board(t, s, adminKid, "pixel-purge"); len(other) != 0 {
		t.Fatalf("another game's board: %v", other)
	}
}

func TestMiniGameTieGoesToTheEarlierRun(t *testing.T) {
	s := newTestServer()
	post(t, s, userMain, "cable-crawl", `{"score":10}`)
	post(t, s, userKids, "cable-crawl", `{"score":500}`)
	if _, rank := post(t, s, userMain, "cable-crawl", `{"score":500}`); rank != 2.0 {
		t.Fatalf("tied run ranked %v", rank)
	}
	if got := names(board(t, s, userMain, "cable-crawl")); !slices.Equal(got, []string{"Kids", "Main"}) {
		t.Fatalf("board %v", got)
	}
}
