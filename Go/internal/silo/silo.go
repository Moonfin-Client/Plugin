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
	"strconv"

	pluginv1 "github.com/Silo-Server/silo-plugin-sdk/pkg/pluginproto/silo/plugin/v1"
	"github.com/Silo-Server/silo-plugin-sdk/pkg/pluginsdk/runtime"

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
		Name:         "Silo",
		KV:           &store.Packed{KV: hostKV{}, Pack: api.PerProfileKey},
		Catalog:      hostCatalog{},
		Identify:     Identify,
		AdminBackURL: "/admin/plugins",
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

type hostCatalog struct{}

func (hostCatalog) Libraries(ctx context.Context, caller api.Identity) ([]api.Library, error) {
	host := runtime.Host()
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
