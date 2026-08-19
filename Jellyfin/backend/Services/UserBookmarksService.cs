using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moonfin.Server.Models;

namespace Moonfin.Server.Services;

/// <summary>
/// Service for persisting and managing user audiobook &amp; book bookmarks &amp; notes server-side.
/// </summary>
public class UserBookmarksService
{
    private readonly string _dataPath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<UserBookmarksService> _logger;
    /// One gate per user rather than one for the server, so a large file for
    /// one account doesn't hold up every other account's saves.
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public UserBookmarksService(ILogger<UserBookmarksService> logger)
    {
        _logger = logger;
        _dataPath = Path.Combine(
            MoonfinPlugin.Instance?.DataFolderPath 
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jellyfin", "plugins", "Moonfin"),
            "Bookmarks");

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        EnsureDirectory();
    }

    private void EnsureDirectory()
    {
        if (!Directory.Exists(_dataPath))
        {
            Directory.CreateDirectory(_dataPath);
        }
    }

    private string GetFilePath(Guid userId) => Path.Combine(_dataPath, $"{userId}.json");

    private static SemaphoreSlim GateFor(Guid userId) =>
        _locks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));

    /// Reads without taking the gate. Callers hold it around whatever they are
    /// doing, so a read followed by a write stays one operation.
    private async Task<UserBookmarksEnvelope> ReadEnvelopeAsync(Guid userId)
    {
        var path = GetFilePath(userId);
        if (!File.Exists(path))
        {
            return new UserBookmarksEnvelope { UserId = userId };
        }

        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<UserBookmarksEnvelope>(json, _jsonOptions)
            ?? new UserBookmarksEnvelope { UserId = userId };
    }

    public async Task<UserBookmarksEnvelope> GetUserBookmarksAsync(Guid userId)
    {
        var gate = GateFor(userId);
        await gate.WaitAsync();
        try
        {
            return await ReadEnvelopeAsync(userId);
        }
        catch (Exception ex)
        {
            // Answering with an empty envelope would read as "you have none",
            // and a client that trusts that clears what it is holding.
            _logger.LogError(ex, "Error reading bookmarks for user {UserId}", userId);
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<UserItemUserDataDto> GetItemUserDataAsync(Guid userId, string itemId)
    {
        var env = await GetUserBookmarksAsync(userId);
        if (env.Items.TryGetValue(itemId, out var data))
        {
            return data;
        }
        return new UserItemUserDataDto();
    }

    /// Reads, applies the change and writes under a single hold on the gate.
    /// Splitting those apart lets a bookmark save and a note save for the same
    /// item interleave, and the second one written wins for both lists.
    private async Task UpdateItemAsync(
        Guid userId,
        string itemId,
        Action<UserItemUserDataDto> apply)
    {
        var gate = GateFor(userId);
        await gate.WaitAsync();
        try
        {
            var env = await ReadEnvelopeAsync(userId);
            if (!env.Items.TryGetValue(itemId, out var data))
            {
                data = new UserItemUserDataDto();
                env.Items[itemId] = data;
            }

            apply(data);
            AtomicFile.WriteAllText(
                GetFilePath(userId),
                JsonSerializer.Serialize(env, _jsonOptions));
        }
        catch (Exception ex)
        {
            // Reporting success on a write that never landed lets the client
            // drop the copy it was holding.
            _logger.LogError(ex, "Error saving user data for user {UserId}, item {ItemId}", userId, itemId);
            throw;
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
