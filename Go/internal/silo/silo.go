// Package silo runs Moonbase as a native Silo plugin. Silo calls Handle over
// gRPC through its http_routes.v1 capability, and the request is replayed
// through the shared api handler.
package silo

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"net/url"
	"slices"
	"strconv"
	"strings"
	"sync"
	"time"

	pluginv1 "github.com/Silo-Server/silo-plugin-sdk/pkg/pluginproto/silo/plugin/v1"
	"github.com/Silo-Server/silo-plugin-sdk/pkg/pluginsdk/runtime"
	"github.com/Silo-Server/silo-plugin-sdk/pkg/pluginsdk/runtimehost"

	"github.com/Moonfin-Client/Plugin/Go/internal/api"
	"github.com/Moonfin-Client/Plugin/Go/internal/store"
)

var ErrHostUnavailable = errors.New("silo host isn't connected yet")

type Plugin struct {
	pluginv1.UnimplementedHttpRoutesServer
	*api.Server
}

func New(version string) *Plugin {
	return &Plugin{Server: api.NewServer(version, api.Host{
		Name:          "Silo",
		KV:            &store.Packed{KV: hostKV{}, Pack: api.PerProfileKey},
		Catalog:       &hostCatalog{host: connectedHost, genres: map[string]cachedGenres{}},
		Identify:      Identify,
		AdminBackURL:  "/admin/plugins",
		WebPluginMode: true,
	})}
}

// Handle is the http_routes.v1 entry point.
func (p *Plugin) Handle(ctx context.Context, req *pluginv1.HandleHTTPRequest) (*pluginv1.HandleHTTPResponse, error) {
	query := url.Values{}
	for k, v := range req.GetQuery().AsMap() {
		query.Set(k, stringify(v))
	}
	target := (&url.URL{Path: req.GetPath(), RawQuery: query.Encode()}).RequestURI()
	r := httptest.NewRequestWithContext(ctx, req.GetMethod(), target, bytes.NewReader(req.GetBody()))
	r.Header = http.Header{}
	for k, v := range req.GetHeaders() {
		r.Header.Set(k, v)
	}
	w := httptest.NewRecorder()
	p.ServeHTTP(w, r)

	headers := map[string]string{}
	for k, v := range w.Header() {
		if len(v) > 0 {
			headers[k] = v[0]
		}
	}
	return &pluginv1.HandleHTTPResponse{StatusCode: int32(w.Code), Headers: headers, Body: w.Body.Bytes()}, nil
}

func stringify(v any) string {
	switch t := v.(type) {
	case string:
		return t
	case nil:
		return ""
	default:
		b, _ := json.Marshal(t)
		return string(b)
	}
}

// Configure receives what Silo holds for the plugin form earlier releases
// had, once per start (see legacy_form.go in the api package).
func (p *Plugin) Configure(_ context.Context, entries []*pluginv1.ConfigEntry) error {
	native := map[string]api.M{}
	for _, e := range entries {
		native[e.GetKey()] = e.GetValue().AsMap()
	}
	p.SetNative(native)
	return nil
}

// Identify reads the caller from the headers Silo's proxy stamps itself. Silo
// never forwards client-sent X-Silo-* headers, so they can be trusted.
func Identify(r *http.Request) (api.Identity, error) {
	id, err := strconv.Atoi(r.Header.Get("X-Silo-User-Id"))
	if err != nil || id <= 0 {
		return api.Identity{}, api.ErrSignedOut
	}
	return api.Identity{
		UserID:      id,
		Admin:       r.Header.Get("X-Silo-User-Role") == "admin",
		UserName:    r.Header.Get("X-Silo-User-Name"),
		ProfileID:   r.Header.Get("X-Silo-Profile-Id"),
		ProfileName: r.Header.Get("X-Silo-Profile-Name"),
		Primary:     r.Header.Get("X-Silo-Profile-Primary") == "true",
	}, nil
}

// hostKV keeps Moonbase's data in the host's per-installation instance state,
// which Silo encrypts and stores in its database.
type hostKV struct{}

func (hostKV) Get(ctx context.Context, key string) ([]byte, bool, error) {
	host := runtime.Host()
	if host == nil {
		return nil, false, ErrHostUnavailable
	}
	return host.ReadInstanceState(ctx, key)
}

func (hostKV) Put(ctx context.Context, key string, value []byte) error {
	host := runtime.Host()
	if host == nil {
		return ErrHostUnavailable
	}
	return host.WriteInstanceState(ctx, key, value)
}

// siloHost is the part of the Silo host the catalog reads.
type siloHost interface {
	ListLibraries(ctx context.Context, userID string) ([]*pluginv1.Library, error)
	ListLibraryMedia(ctx context.Context, req runtimehost.ListLibraryMediaRequest) (*runtimehost.ListLibraryMediaResponse, error)
}

// connectedHost is the Silo host, or nil until it connects.
func connectedHost() siloHost {
	if host := runtime.Host(); host != nil {
		return host
	}
	return nil
}

type hostCatalog struct {
	host func() siloHost

	mu     sync.Mutex
	genres map[string]cachedGenres
}

type cachedGenres struct {
	at     time.Time
	genres []api.Genre
}

// genresTTL is how long a set of libraries keeps its genres, since collecting
// them reads every item in those libraries.
const (
	genresTTL      = 5 * time.Minute
	genresPageSize = 500
)

func (c *hostCatalog) Libraries(ctx context.Context, caller api.Identity) ([]api.Library, error) {
	host := c.host()
	if host == nil {
		return nil, ErrHostUnavailable
	}
	libs, err := host.ListLibraries(ctx, strconv.Itoa(caller.UserID))
	if err != nil {
		return nil, err
	}
	out := make([]api.Library, 0, len(libs))
	for _, l := range libs {
		out = append(out, api.Library{ID: l.GetId(), Name: l.GetName(), MediaType: l.GetMediaType()})
	}
	return out, nil
}

// Genres collects the genres of every item in the caller's libraries. Silo
// names genres only, so each id is the one the app makes from the name.
func (c *hostCatalog) Genres(ctx context.Context, caller api.Identity) ([]api.Genre, error) {
	libs, err := c.Libraries(ctx, caller)
	if err != nil {
		return nil, err
	}
	ids := make([]string, 0, len(libs))
	for _, l := range libs {
		ids = append(ids, l.ID)
	}
	// No library ids would ask Silo for every library.
	if len(ids) == 0 {
		return []api.Genre{}, nil
	}
	slices.Sort(ids)
	key := strings.Join(ids, "\x00")
	c.mu.Lock()
	hit, ok := c.genres[key]
	c.mu.Unlock()
	if ok && time.Since(hit.at) < genresTTL {
		return hit.genres, nil
	}
	genres, err := walkGenres(ctx, c.host(), ids)
	if err != nil {
		return nil, err
	}
	c.mu.Lock()
	for k, e := range c.genres {
		if time.Since(e.at) >= genresTTL {
			delete(c.genres, k)
		}
	}
	c.genres[key] = cachedGenres{at: time.Now(), genres: genres}
	c.mu.Unlock()
	return genres, nil
}

func walkGenres(ctx context.Context, host siloHost, libraryIDs []string) ([]api.Genre, error) {
	if host == nil {
		return nil, ErrHostUnavailable
	}
	out := []api.Genre{}
	seen := map[string]bool{}
	token := ""
	for {
		page, err := host.ListLibraryMedia(ctx, runtimehost.ListLibraryMediaRequest{LibraryIDs: libraryIDs, PageSize: genresPageSize, PageToken: token})
		if err != nil {
			return nil, err
		}
		for _, item := range page.Items {
			for _, name := range item.Genres {
				if folded := strings.ToLower(name); name != "" && !seen[folded] {
					seen[folded] = true
					out = append(out, api.Genre{ID: genreID(name), Name: name})
				}
			}
		}
		// A host that hands back the token it was given would page forever.
		if page.NextPageToken == "" || page.NextPageToken == token {
			return out, nil
		}
		token = page.NextPageToken
	}
}

// genreID is the app's id for a Silo genre, the name percent-encoded the way
// Dart's Uri.encodeComponent does it.
func genreID(name string) string {
	return "silo-genre:" + encodeComponent.Replace(url.QueryEscape(name))
}

// encodeComponent turns url.QueryEscape's output into encodeComponent's,
// which writes a space as %20 and leaves !'()* as they are.
var encodeComponent = strings.NewReplacer("+", "%20", "%21", "!", "%27", "'", "%28", "(", "%29", ")", "%2A", "*")
