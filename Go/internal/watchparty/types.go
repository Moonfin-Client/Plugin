package watchparty

// The JSON the routes read and write, with the same snake_case names as the
// C# engine. Nullable fields are pointers so they come out as null.

type CreateBody struct {
	RoomID             string  `json:"room_id"`
	ContentID          string  `json:"content_id"`
	Pin                *string `json:"pin"`
	GuestControlPolicy string  `json:"guest_control_policy"`
}

type JoinBody struct {
	Code   string  `json:"code"`
	RoomID string  `json:"room_id"`
	Pin    *string `json:"pin"`
}

// RoomBody is one shape for the small host calls, each reading only its own field.
type RoomBody struct {
	ContentID          string   `json:"content_id"`
	PositionSeconds    *float64 `json:"position_seconds"`
	GuestControlPolicy string   `json:"guest_control_policy"`
	Pin                *string  `json:"pin"`
	Voting             *bool    `json:"voting"`
	Voted              *bool    `json:"voted"`
	MemberID           string   `json:"member_id"`
}

type MessagesBody struct {
	Messages []Inbound `json:"messages"`
}

// Inbound has every client message type's fields in one shape.
type Inbound struct {
	Type            string   `json:"type"`
	ClientSentAt    any      `json:"client_sent_at"`
	Action          string   `json:"action"`
	PositionSeconds *float64 `json:"position_seconds"`
	IsPaused        *bool    `json:"is_paused"`
	IsReady         *bool    `json:"is_ready"`
	SessionID       *string  `json:"session_id"`
	CommandID       *string  `json:"command_id"`
	Status          string   `json:"status"`
	Text            string   `json:"text"`
	Reaction        string   `json:"reaction"`
}

type roomJSON struct {
	RoomID                  string       `json:"room_id"`
	Code                    string       `json:"code"`
	InvitePath              string       `json:"invite_path"`
	InviteURL               *string      `json:"invite_url"`
	Phase                   string       `json:"phase"`
	PlaybackState           string       `json:"playback_state"`
	SelectionMode           string       `json:"selection_mode"`
	SelectionRevision       int          `json:"selection_revision"`
	SelectedContentID       *string      `json:"selected_content_id"`
	SelectedFileID          *string      `json:"selected_file_id"`
	GuestControlPolicy      string       `json:"guest_control_policy"`
	IsPaused                bool         `json:"is_paused"`
	AnchorPositionSeconds   float64      `json:"anchor_position_seconds"`
	AnchorAt                string       `json:"anchor_at"`
	Generation              int64        `json:"generation"`
	SelfRole                string       `json:"self_role"`
	SelfCanControlTransport bool         `json:"self_can_control_transport"`
	SelfMemberID            string       `json:"self_member_id"`
	AttachedSessionID       *string      `json:"attached_session_id"`
	Members                 []memberJSON `json:"members"`
	HasPin                  bool         `json:"has_pin"`
	ContentTitle            *string      `json:"content_title"`
	ContentType             *string      `json:"content_type"`
	SeriesName              *string      `json:"series_name"`
	Season                  *int         `json:"season"`
	Episode                 *int         `json:"episode"`
	ChatEnabled             bool         `json:"chat_enabled"`
	ReactionsEnabled        bool         `json:"reactions_enabled"`
	MaxMembers              int          `json:"max_members"`
	CreatedAt               string       `json:"created_at"`
}

type memberJSON struct {
	ProfileID   string  `json:"profile_id"`
	DisplayName string  `json:"display_name"`
	IsHost      bool    `json:"is_host"`
	IsSelf      bool    `json:"is_self"`
	Connected   bool    `json:"connected"`
	UserID      string  `json:"user_id"`
	Status      string  `json:"status"`
	AvatarURL   *string `json:"avatar_url"`
}

type summaryJSON struct {
	RoomID             string   `json:"room_id"`
	HostName           string   `json:"host_name"`
	MemberCount        int      `json:"member_count"`
	MembersPreview     []string `json:"members_preview"`
	Phase              string   `json:"phase"`
	PlaybackState      string   `json:"playback_state"`
	PositionSeconds    float64  `json:"position_seconds"`
	SelectedContentID  *string  `json:"selected_content_id"`
	ContentTitle       *string  `json:"content_title"`
	ContentType        *string  `json:"content_type"`
	SeriesName         *string  `json:"series_name"`
	Season             *int     `json:"season"`
	Episode            *int     `json:"episode"`
	HasPin             bool     `json:"has_pin"`
	GuestControlPolicy string   `json:"guest_control_policy"`
	SelectionMode      string   `json:"selection_mode"`
	IsMember           bool     `json:"is_member"`
	CreatedAt          string   `json:"created_at"`
}

type adminRoomJSON struct {
	RoomID          string            `json:"room_id"`
	Code            string            `json:"code"`
	HostName        string            `json:"host_name"`
	Phase           string            `json:"phase"`
	PlaybackState   string            `json:"playback_state"`
	PositionSeconds float64           `json:"position_seconds"`
	ContentTitle    *string           `json:"content_title"`
	ContentLabel    *string           `json:"content_label"`
	HasPin          bool              `json:"has_pin"`
	CreatedAt       string            `json:"created_at"`
	Members         []adminMemberJSON `json:"members"`
}

type adminMemberJSON struct {
	MemberID    string `json:"member_id"`
	UserID      string `json:"user_id"`
	DisplayName string `json:"display_name"`
	IsHost      bool   `json:"is_host"`
	Connected   bool   `json:"connected"`
	Status      string `json:"status"`
}

type commandJSON struct {
	CommandID         string  `json:"command_id"`
	Generation        int64   `json:"generation"`
	SelectionRevision int     `json:"selection_revision"`
	Action            string  `json:"action"`
	PositionSeconds   float64 `json:"position_seconds"`
	ExecuteAt         string  `json:"execute_at"`
	PlaybackState     string  `json:"playback_state"`
	SessionID         *string `json:"session_id"`
	ByMemberID        *string `json:"by_member_id"`
	ByDisplayName     *string `json:"by_display_name"`
	Countdown         bool    `json:"countdown"`
}

type chatJSON struct {
	ID          string `json:"id"`
	MemberID    string `json:"member_id"`
	DisplayName string `json:"display_name"`
	Text        string `json:"text"`
	SentAt      string `json:"sent_at"`
}

type suggestionJSON struct {
	ID        string `json:"id"`
	ContentID string `json:"content_id"`
	Title     string `json:"title"`
	Subtitle  string `json:"subtitle"`
	VoteCount int    `json:"vote_count"`
	VotedByMe bool   `json:"voted_by_me"`
	CreatedAt string `json:"created_at"`
}
