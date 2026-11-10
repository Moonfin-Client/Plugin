using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Moonfin.LiveTv;

namespace Moonfin.Bookmarks;

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

/// <summary>Each user's audiobook and book bookmarks and notes, one file per user.</summary>
public class UserBookmarkStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // One gate per user, so a large file for one account doesn't hold up every other account's saves.
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private readonly string _folder;
    private readonly IStateFile _files;

    public UserBookmarkStore(string folder, IStateFile files)
    {
        _folder = folder;
        _files = files;
    }

    private string FilePath(Guid userId) => Path.Combine(_folder, $"{userId}.json");

    private SemaphoreSlim GateFor(Guid userId) => _locks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));

    // Callers hold the gate, so a read followed by a write stays one operation.
    private UserBookmarksEnvelope Read(Guid userId)
    {
        var path = FilePath(userId);
        var env = _files.Read(path, json => JsonSerializer.Deserialize<UserBookmarksEnvelope>(json, JsonOptions));
        if (env != null)
        {
            return env;
        }

        // Answering with an empty envelope would read as "you have none", and a client that
        // trusts that clears what it's holding.
        if (File.Exists(path) || File.Exists(path + ".bak"))
        {
            throw new InvalidDataException($"The bookmarks file for user {userId} could not be read.");
        }

        return new UserBookmarksEnvelope { UserId = userId };
    }

    public async Task<UserBookmarksEnvelope> GetUserBookmarksAsync(Guid userId)
    {
        var gate = GateFor(userId);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return Read(userId);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<UserItemUserDataDto> GetItemUserDataAsync(Guid userId, string itemId)
    {
        var env = await GetUserBookmarksAsync(userId).ConfigureAwait(false);
        return env.Items.TryGetValue(itemId, out var data) ? data : new UserItemUserDataDto();
    }

    // Reading, changing and writing under one hold keeps a bookmark save and a note save for the
    // same item from interleaving, where the second one written would win for both lists.
    private async Task UpdateItemAsync(Guid userId, string itemId, Action<UserItemUserDataDto> apply)
    {
        var gate = GateFor(userId);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var env = Read(userId);
            if (!env.Items.TryGetValue(itemId, out var data))
            {
                data = new UserItemUserDataDto();
                env.Items[itemId] = data;
            }

            apply(data);
            Directory.CreateDirectory(_folder);
            _files.Write(FilePath(userId), JsonSerializer.Serialize(env, JsonOptions));
        }
        finally
        {
            gate.Release();
        }
    }

    public Task SaveItemBookmarksAsync(Guid userId, string itemId, List<BookmarkDto> bookmarks) =>
        UpdateItemAsync(userId, itemId, data => data.Bookmarks = bookmarks);

    public Task SaveItemNotesAsync(Guid userId, string itemId, List<NoteDto> notes) =>
        UpdateItemAsync(userId, itemId, data => data.Notes = notes);
}
