package watchparty

import (
	"bytes"
	"context"
	"encoding/json"
	"html"
	"net/url"
	"strconv"
	"strings"
)

// Handle answers the routes under Moonfin/WatchParty. path is relative to it.
func (e *Engine) Handle(ctx context.Context, method, path string, c Caller, roomToken string, body []byte, query url.Values) Result {
	p := segments(path)
	get, post := method == "GET", method == "POST"
	if get && match(p, "Config") {
		return e.Config()
	}
	if !e.Settings().Enabled {
		return fail("disabled", "Watch Parties are turned off on this server.")
	}
	switch {
	case get && match(p, "Time"):
		return e.TimeNow()
	case get && match(p, "Rooms"):
		return e.Rooms(ctx, c)
	case post && match(p, "Rooms"):
		return read(body, func(b CreateBody) Result { return e.Create(ctx, c, b) })
	case post && match(p, "Join"):
		return read(body, func(b JoinBody) Result { return e.Join(ctx, c, b) })
	}
	if len(p) < 3 || !is(p[0], "Rooms") {
		return notFound("There's no such Watch Parties route.")
	}
	id := p[1]
	if len(p) == 3 {
		switch name := strings.ToLower(p[2]); {
		case get && name == "events":
			cursor, _ := strconv.ParseInt(query.Get("cursor"), 10, 64)
			wait, err := strconv.Atoi(query.Get("wait"))
			if err != nil {
				wait = MaxWaitSeconds
			}
			return e.Events(ctx, c, id, roomToken, cursor, wait)
		case post && name == "messages":
			return read(body, func(b MessagesBody) Result { return e.Messages(c, id, roomToken, b) })
		case get && name == "suggestions":
			return e.Suggestions(c, id, roomToken)
		case post && name == "suggestions":
			return read(body, func(b RoomBody) Result { return e.Suggest(ctx, c, id, roomToken, b) })
		case post && name == "stage":
			return read(body, func(b RoomBody) Result { return e.Stage(ctx, c, id, roomToken, b) })
		case post && name == "start":
			return read(body, func(b RoomBody) Result { return e.Start(c, id, roomToken, b) })
		case post && name == "stop":
			return e.Stop(c, id, roomToken)
		case post && name == "policy":
			return read(body, func(b RoomBody) Result { return e.Policy(c, id, roomToken, b) })
		case post && name == "pin":
			return read(body, func(b RoomBody) Result { return e.SetPin(c, id, roomToken, b) })
		case post && name == "voting":
			return read(body, func(b RoomBody) Result { return e.Voting(c, id, roomToken, b) })
		case post && name == "promote":
			return read(body, func(b RoomBody) Result { return e.Promote(c, id, roomToken, b) })
		case post && name == "remove":
			return read(body, func(b RoomBody) Result { return e.Remove(c, id, roomToken, b) })
		case post && name == "close":
			return e.Close(c, id, roomToken)
		case post && name == "leave":
			return e.Leave(c, id, roomToken)
		}
	} else if len(p) == 4 && is(p[2], "Suggestions") && method == "DELETE" {
		return e.RemoveSuggestion(c, id, roomToken, p[3])
	} else if len(p) == 5 && is(p[2], "Suggestions") && is(p[4], "Vote") && post {
		return read(body, func(b RoomBody) Result { return e.Vote(c, id, roomToken, p[3], b) })
	}
	return notFound("There's no such Watch Parties route.")
}

// HandleAdmin answers the routes under Moonfin/Admin/WatchParty. The caller
// has already been checked as an admin.
func (e *Engine) HandleAdmin(method, path string, body []byte) Result {
	p := segments(path)
	if method == "GET" && match(p, "Rooms") {
		return e.AdminRooms()
	}
	if method == "POST" && len(p) == 3 && is(p[0], "Rooms") {
		switch {
		case is(p[2], "Close"):
			return e.AdminClose(p[1])
		case is(p[2], "Remove"):
			return read(body, func(b RoomBody) Result { return e.AdminRemove(p[1], b) })
		}
	}
	return notFound("There's no such Watch Parties route.")
}

// Routed reports whether path (relative to Moonfin/WatchParty) is one of
// these routes rather than the older per-room routes the shared api serves.
func Routed(path string) bool {
	p := segments(path)
	return len(p) > 0 && (is(p[0], "Config") || is(p[0], "Time") || is(p[0], "Rooms") || is(p[0], "Join"))
}

// read decodes the body into T. An empty body reads as a blank one.
func read[T any](body []byte, do func(T) Result) Result {
	var v T
	if len(bytes.TrimSpace(body)) > 0 {
		if err := json.Unmarshal(body, &v); err != nil {
			return badRequest("The body isn't valid JSON.")
		}
	}
	return do(v)
}

func segments(path string) []string {
	return strings.FieldsFunc(path, func(r rune) bool { return r == '/' })
}

func match(p []string, only string) bool { return len(p) == 1 && is(p[0], only) }

func is(a, b string) bool { return strings.EqualFold(a, b) }

// PublicBase is the address people reach the server at: the request's scheme
// and host, or the ones a reverse proxy passed on.
func PublicBase(scheme, host, forwardedProto, forwardedHost string) string {
	proto := strings.ToLower(first(forwardedProto))
	if proto != "http" && proto != "https" {
		proto = scheme
	}
	if h := first(forwardedHost); h != "" {
		host = h
	}
	return proto + "://" + host
}

func first(header string) string { return strings.TrimSpace(strings.Split(header, ",")[0]) }

// DeepLink opens the party in Moonfin.
func DeepLink(serverID, code, publicBase string) string {
	return "moonfin://party?server=" + url.QueryEscape(serverID) + "&code=" + url.QueryEscape(code) +
		"&address=" + url.QueryEscape(publicBase)
}

// InvitePage fills shared/WatchParty/invite.html for an already normalized
// code. It never looks the code up, so it names no title, host or members.
func InvitePage(template, serverName, serverID, publicBase, code string) string {
	if strings.TrimSpace(serverName) == "" {
		serverName = "this server"
	}
	host := publicBase
	if u, err := url.Parse(publicBase); err == nil && u.Host != "" {
		host = u.Host
	}
	shown := code
	if len(code) == 6 {
		shown = code[:3] + " " + code[3:]
	}
	return strings.NewReplacer(
		"{{name}}", html.EscapeString(serverName),
		"{{host}}", html.EscapeString(host),
		"{{code}}", html.EscapeString(code),
		"{{shown}}", html.EscapeString(shown),
		"{{link}}", html.EscapeString(DeepLink(serverID, code, publicBase)),
		"{{image}}", html.EscapeString(publicBase+"/Moonfin/Party/preview.png"),
	).Replace(template)
}
