using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Moonfin.Server.Models;

public class BookmarkDto
{
    [JsonPropertyName("positionMs")]
    public int PositionMs { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class NoteDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("positionMs")]
    public int PositionMs { get; set; }

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class UserItemUserDataDto
{
    [JsonPropertyName("bookmarks")]
    public List<BookmarkDto> Bookmarks { get; set; } = new();

    [JsonPropertyName("notes")]
    public List<NoteDto> Notes { get; set; } = new();
}

public class UserBookmarksEnvelope
{
    [JsonPropertyName("userId")]
    public Guid UserId { get; set; }

    [JsonPropertyName("items")]
    public Dictionary<string, UserItemUserDataDto> Items { get; set; } = new();
}
