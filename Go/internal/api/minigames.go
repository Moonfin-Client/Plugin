package api

import (
	"cmp"
	"math"
	"net/http"
	"slices"
	"time"
)

// The top 3 of each Moonfin After Hours game. Each profile counts with its
// best run only, so one person can't hold every spot, and a tie goes to
// whoever got there first. Every board shares one key.
const keyMiniGameScores = "minigame-scores"

const (
	miniGameBoardSize = 3
	miniGameMaxScore  = 9_999_999
)

var miniGameIDs = map[string]bool{
	"buffer-run": true, "fast-forward": true, "cable-crawl": true, "case-stack": true, "pixel-purge": true,
}

type miniGameScore struct {
	Player   string    `json:"userId"`
	UserName string    `json:"userName"`
	Score    int       `json:"score"`
	At       time.Time `json:"at"`
}

type miniGameEntry struct {
	UserName string `json:"userName"`
	Score    int    `json:"score"`
	At       string `json:"at"`
}

func (s *Server) miniGameRoutes() {
	s.mux.HandleFunc("GET /Moonfin/MiniGames/{gameId}/Scores", s.user(s.handleGetMiniGameScores))
	s.mux.HandleFunc("POST /Moonfin/MiniGames/{gameId}/Scores", s.user(s.handlePostMiniGameScore))
}

func miniGame(w http.ResponseWriter, r *http.Request) (string, bool) {
	game := r.PathValue("gameId")
	if !miniGameIDs[game] {
		writeError(w, http.StatusNotFound, "There is no such game")
		return "", false
	}
	return game, true
}

func (s *Server) handleGetMiniGameScores(w http.ResponseWriter, r *http.Request) {
	game, ok := miniGame(w, r)
	if !ok {
		return
	}
	var boards map[string][]miniGameScore
	if _, err := s.loadJSON(r.Context(), keyMiniGameScores, &boards); err != nil {
		storageFailed(w, r, err)
		return
	}
	out := []miniGameEntry{}
	for _, row := range boards[game] {
		out = append(out, miniGameEntry{row.UserName, row.Score, row.At.UTC().Format("2006-01-02T15:04:05Z")})
	}
	writeJSON(w, http.StatusOK, out)
}

func (s *Server) handlePostMiniGameScore(w http.ResponseWriter, r *http.Request) {
	game, ok := miniGame(w, r)
	if !ok {
		return
	}
	id := callerOf(r)
	name := cmp.Or(id.ProfileName, id.UserName)
	if name == "" {
		writeError(w, http.StatusUnauthorized, "A signed-in user is required")
		return
	}
	body, err := readBody(r)
	score, isNumber := body["score"].(float64)
	if err != nil || !isNumber || score != math.Trunc(score) || score < 0 || score > miniGameMaxScore {
		writeError(w, http.StatusBadRequest, "The score must be a whole number from 0 to 9,999,999")
		return
	}
	player := settingsKey(id)
	ctx := r.Context()
	s.mu.Lock()
	defer s.mu.Unlock()
	var boards map[string][]miniGameScore
	if _, err := s.loadJSON(ctx, keyMiniGameScores, &boards); err != nil {
		storageFailed(w, r, err)
		return
	}
	rows := boards[game]
	if i := slices.IndexFunc(rows, func(row miniGameScore) bool { return row.Player == player }); i >= 0 {
		if rows[i].Score >= int(score) {
			writeJSON(w, http.StatusOK, map[string]any{"rank": nil})
			return
		}
		rows = slices.Delete(rows, i, i+1)
	}
	row := miniGameScore{player, name, int(score), time.Now().UTC().Truncate(time.Second)}
	// The stable sort keeps an appended row behind equal ones, so a tie goes
	// to the earlier run.
	rows = append(rows, row)
	slices.SortStableFunc(rows, func(a, b miniGameScore) int { return cmp.Compare(b.Score, a.Score) })
	// Only the top 3 is kept, since a run below it can never climb back and
	// Silo's storage is small and shared.
	rows = rows[:min(len(rows), miniGameBoardSize)]
	rank := slices.Index(rows, row) + 1
	if rank == 0 {
		writeJSON(w, http.StatusOK, map[string]any{"rank": nil})
		return
	}
	if boards == nil {
		boards = map[string][]miniGameScore{}
	}
	boards[game] = rows
	if err := s.saveJSON(ctx, keyMiniGameScores, boards); err != nil {
		storageFailed(w, r, err)
		return
	}
	writeJSON(w, http.StatusOK, map[string]any{"rank": rank})
}
