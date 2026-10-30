package api

import (
	"fmt"
	"html"
	"math"
	"net/http"
	"net/url"
	"regexp"
	"slices"
	"strings"
	"time"
)

// TMDB reviews, production companies and title details, ported from the
// Jellyfin plugin's TmdbController and its shared TmdbReview and
// TmdbTitleDetails.

const tmdbImages = "https://image.tmdb.org/t/p/"

func tmdbType(r *http.Request) string {
	if strings.EqualFold(strings.TrimSpace(r.URL.Query().Get("type")), "tv") {
		return "tv"
	}
	return "movie"
}

var reviewLanguage = regexp.MustCompile(`^[a-z]{2}(-[A-Z]{2})?$`)

func (s *Server) handleTmdbReviews(w http.ResponseWriter, r *http.Request) {
	id, ok := requiredTmdbID(w, r)
	if !ok {
		return
	}
	typ := tmdbType(r)
	lang := strings.TrimSpace(r.URL.Query().Get("language"))
	if !reviewLanguage.MatchString(lang) {
		lang = "en-US"
	}
	s.tmdbRoute(w, r, fmt.Sprintf("tmdb-reviews:%s:%s:%s", typ, id, lang), 24*time.Hour, func(key string) (M, string) {
		page, msg := s.tmdbReviews(r, key, typ, id, lang)
		// TMDB has far fewer reviews outside English.
		if msg == "" && len(page.Results) == 0 && !strings.HasPrefix(lang, "en") {
			page, msg = s.tmdbReviews(r, key, typ, id, "en-US")
		}
		if msg != "" {
			return nil, msg
		}
		reviews := []M{}
		for _, rv := range page.Results {
			if out := reviewOut(rv); out != nil {
				reviews = append(reviews, out)
			}
		}
		return M{"success": true, "totalResults": page.TotalResults, "reviews": reviews}, ""
	})
}

type reviewPage struct {
	TotalResults int `json:"total_results"`
	Results      []M `json:"results"`
}

func (s *Server) tmdbReviews(r *http.Request, key, typ, id, lang string) (reviewPage, string) {
	var page reviewPage
	msg := s.tmdb(r.Context(), key, typ+"/"+url.PathEscape(id)+"/reviews", url.Values{"language": {lang}}, &page)
	return page, msg
}

func text(m M, k string) string {
	v, _ := m[k].(string)
	return strings.TrimSpace(v)
}

// reviewOut is one review for the apps, or nil for one without text.
func reviewOut(rv M) M {
	content := cleanReview(text(rv, "content"))
	if content == "" {
		return nil
	}
	details, _ := rv["author_details"].(M)
	author := text(details, "name")
	if author == "" {
		author = text(rv, "author")
	}
	if author == "" {
		author = text(details, "username")
	}
	var rating any
	if n, ok := details["rating"].(float64); ok {
		rating = n
	}
	var avatar any
	// TMDB gives either its own image path or a whole Gravatar address
	// behind a stray slash.
	if p := text(details, "avatar_path"); strings.HasPrefix(p, "/") {
		if g := p[1:]; strings.HasPrefix(strings.ToLower(g), "http") {
			avatar = webAddress(g)
		} else {
			avatar = tmdbImages + "w185" + p
		}
	}
	return compact(M{"id": text(rv, "id"), "author": author, "rating": rating, "content": content,
		"createdAt": nilIfEmpty(text(rv, "created_at")), "avatarUrl": avatar, "url": webAddress(text(rv, "url"))})
}

func nilIfEmpty(s string) any {
	if s == "" {
		return nil
	}
	return s
}

// webAddress is an absolute http(s) address, or nil.
func webAddress(v string) any {
	u, err := url.Parse(v)
	if err != nil || !u.IsAbs() || (u.Scheme != "http" && u.Scheme != "https") {
		return nil
	}
	return u.String()
}

var (
	reviewBreak     = regexp.MustCompile(`(?i)<br\s*/?>`)
	reviewParagraph = regexp.MustCompile(`(?i)</?p(\s[^>]*)?>`)
	reviewStrong    = regexp.MustCompile(`(?is)<strong>(.*?)</strong>`)
	reviewB         = regexp.MustCompile(`(?is)<b>(.*?)</b>`)
	reviewEm        = regexp.MustCompile(`(?is)<em>(.*?)</em>`)
	reviewI         = regexp.MustCompile(`(?is)<i>(.*?)</i>`)
	reviewLink      = regexp.MustCompile(`(?is)<a\s[^>]*?href\s*=\s*["']([^"']+)["'][^>]*>(.*?)</a>`)
	reviewTag       = regexp.MustCompile(`</?[a-zA-Z][^>]*>`)
	reviewTrailing  = regexp.MustCompile(`[ \t]+\n`)
	reviewBlank     = regexp.MustCompile(`\n{3,}`)
)

// cleanReview turns a review's HTML into the plain CommonMark the apps draw.
func cleanReview(raw string) string {
	t := strings.NewReplacer("\r\n", "\n", "\r", "\n").Replace(raw)
	t = reviewBreak.ReplaceAllString(t, "\n")
	t = reviewParagraph.ReplaceAllString(t, "\n\n")
	t = reviewStrong.ReplaceAllString(t, "**$1**")
	t = reviewB.ReplaceAllString(t, "**$1**")
	t = reviewEm.ReplaceAllString(t, "*$1*")
	t = reviewI.ReplaceAllString(t, "*$1*")
	t = reviewLink.ReplaceAllStringFunc(t, func(m string) string {
		g := reviewLink.FindStringSubmatch(m)
		if href, ok := webAddress(g[1]).(string); ok {
			return "[" + g[2] + "](" + href + ")"
		}
		return g[2]
	})
	t = reviewTag.ReplaceAllString(t, "")
	t = html.UnescapeString(t)
	t = reviewTrailing.ReplaceAllString(t, "\n")
	t = reviewBlank.ReplaceAllString(t, "\n\n")
	return strings.TrimSpace(t)
}

// handleTmdbCompanies lists a title's production companies, and a show's
// networks with negated ids, as the Jellyfin plugin does. That plugin serves
// the logos itself; here logoUrl points at TMDB, since an app can't send
// Silo's headers with an image URL.
func (s *Server) handleTmdbCompanies(w http.ResponseWriter, r *http.Request) {
	id, ok := requiredTmdbID(w, r)
	if !ok {
		return
	}
	typ := tmdbType(r)
	s.tmdbRoute(w, r, "tmdb-companies:"+typ+":"+id, 7*24*time.Hour, func(key string) (M, string) {
		var data struct {
			Companies []M `json:"production_companies"`
			Networks  []M `json:"networks"`
		}
		if msg := s.tmdb(r.Context(), key, typ+"/"+url.PathEscape(id), nil, &data); msg != "" {
			return nil, msg
		}
		companies := []M{}
		add := func(c M, sign float64) {
			n, ok := num(c["id"])
			name := text(c, "name")
			if !ok || name == "" {
				return
			}
			out := M{"id": sign * n, "name": name, "hasLogo": false}
			if logo := text(c, "logo_path"); logo != "" {
				out["hasLogo"], out["logoUrl"] = true, tmdbImages+"w500"+logo
			}
			companies = append(companies, out)
		}
		for _, c := range data.Companies {
			add(c, 1)
		}
		for _, c := range data.Networks {
			add(c, -1)
		}
		return M{"success": true, "companies": companies}, ""
	})
}

// handleTmdbDetails answers what an app shows for a title outside the
// library: overview, score, genres, runtime, a backdrop without text, the
// logo and a trailer. Not found when there's no key or TMDB doesn't know
// the title. Silo doesn't share its metadata language with plugins, so it's
// English.
func (s *Server) handleTmdbDetails(w http.ResponseWriter, r *http.Request) {
	q := r.URL.Query()
	typ := tmdbType(r)
	id := strings.TrimSpace(q.Get("tmdbId"))
	imdb := strings.TrimSpace(q.Get("imdbId"))
	key, err := s.tmdbKey(r)
	if err != nil || key == "" || (id == "" && imdb == "") ||
		(id != "" && !providerID.MatchString(id)) || (imdb != "" && !providerID.MatchString(imdb)) {
		http.NotFound(w, r)
		return
	}
	if id == "" {
		var asked bool
		if typ, id, asked = s.tmdbFind(r.Context(), key, imdb); !asked || id == "" {
			http.NotFound(w, r)
			return
		}
	}
	cacheKey := "tmdb-details:" + typ + ":" + id
	out, hit := s.ext.cache.get(cacheKey, 24*time.Hour)
	if !hit {
		var details M
		query := url.Values{"language": {"en"}, "append_to_response": {"images,videos"},
			"include_image_language": {"en,null"}, "include_video_language": {"en,null"}}
		msg := s.tmdb(r.Context(), key, typ+"/"+url.PathEscape(id), query, &details)
		switch {
		case strings.Contains(msg, "status 404"):
			out = nil
		case msg != "":
			http.NotFound(w, r)
			return
		default:
			out = titleDetails(details)
		}
		s.ext.cache.set(cacheKey, out)
	}
	if out == nil {
		http.NotFound(w, r)
		return
	}
	writeJSON(w, http.StatusOK, out)
}

// titleDetails ports TmdbTitleDetails.Parse for English.
func titleDetails(d M) M {
	out := M{"genres": []string{}}
	if o := text(d, "overview"); o != "" {
		out["overview"] = o
	}
	if votes, _ := num(d["vote_count"]); votes > 0 {
		if avg, ok := num(d["vote_average"]); ok {
			out["rating"] = math.RoundToEven(avg*10) / 10
		}
	}
	genres, _ := d["genres"].([]any)
	for _, g := range genres {
		if gm, _ := g.(M); text(gm, "name") != "" {
			out["genres"] = append(out["genres"].([]string), text(gm, "name"))
		}
	}
	// A movie has one runtime, a show lists what its episodes run.
	minutes, ok := num(d["runtime"])
	if !ok {
		if runs, _ := d["episode_run_time"].([]any); len(runs) > 0 {
			minutes, _ = num(runs[0])
		}
	}
	if minutes > 0 {
		out["runTimeTicks"] = int64(minutes) * 600_000_000
	}
	images, _ := d["images"].(M)
	lang := func(m M) string { return text(m, "iso_639_1") }
	backdrops, _ := images["backdrops"].([]any)
	for _, b := range backdrops {
		if bm, _ := b.(M); lang(bm) == "" && text(bm, "file_path") != "" {
			out["backdropPath"] = text(bm, "file_path")
			break
		}
	}
	if _, has := out["backdropPath"]; !has && text(d, "backdrop_path") != "" {
		out["backdropPath"] = text(d, "backdrop_path")
	}
	// Most of the apps can't draw an SVG.
	logos, _ := images["logos"].([]any)
	for _, want := range []string{"en", ""} {
		if i := slices.IndexFunc(logos, func(l any) bool {
			lm, _ := l.(M)
			p := text(lm, "file_path")
			return p != "" && !strings.HasSuffix(strings.ToLower(p), ".svg") && lang(lm) == want
		}); i >= 0 {
			out["logoPath"] = text(logos[i].(M), "file_path")
			break
		}
	}
	// A trailer beats a teaser, then an official upload, then one in English.
	videos, _ := d["videos"].(M)
	results, _ := videos["results"].([]any)
	best, bestRank := "", math.MaxInt
	for _, v := range results {
		vm, _ := v.(M)
		kind := text(vm, "type")
		if !strings.EqualFold(text(vm, "site"), "YouTube") || text(vm, "key") == "" || (kind != "Trailer" && kind != "Teaser") {
			continue
		}
		rank := 0
		if kind != "Trailer" {
			rank += 4
		}
		if vm["official"] != true {
			rank += 2
		}
		if lang(vm) != "en" {
			rank++
		}
		if rank < bestRank {
			best, bestRank = text(vm, "key"), rank
		}
	}
	if best != "" {
		out["trailerUrl"] = "https://www.youtube.com/watch?v=" + best
	}
	return out
}
