using System;
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
    private static readonly SemaphoreSlim _lock = new(1, 1);

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

    public async Task<UserBookmarksEnvelope> GetUserBookmarksAsync(Guid userId)
    {
        var path = GetFilePath(userId);
        await _lock.WaitAsync();
        try
        {
            if (!File.Exists(path))
            {
                return new UserBookmarksEnvelope { UserId = userId };
            }

            var json = await File.ReadAllTextAsync(path);
            var envelope = JsonSerializer.Deserialize<UserBookmarksEnvelope>(json, _jsonOptions);
            return envelope ?? new UserBookmarksEnvelope { UserId = userId };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading bookmarks for user {UserId}", userId);
            return new UserBookmarksEnvelope { UserId = userId };
        }
        finally
        {
            _lock.Release();
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

    public async Task SaveItemUserDataAsync(Guid userId, string itemId, UserItemUserDataDto data)
    {
        await _lock.WaitAsync();
        try
        {
            var path = GetFilePath(userId);
            UserBookmarksEnvelope env;
            if (File.Exists(path))
            {
                var json = await File.ReadAllTextAsync(path);
                env = JsonSerializer.Deserialize<UserBookmarksEnvelope>(json, _jsonOptions) ?? new UserBookmarksEnvelope { UserId = userId };
            }
            else
            {
                env = new UserBookmarksEnvelope { UserId = userId };
            }

            env.Items[itemId] = data;

            var outJson = JsonSerializer.Serialize(env, _jsonOptions);
            AtomicFile.WriteAllText(path, outJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving item user data for user {UserId}, item {ItemId}", userId, itemId);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveItemBookmarksAsync(Guid userId, string itemId, List<BookmarkDto> bookmarks)
    {
        var current = await GetItemUserDataAsync(userId, itemId);
        current.Bookmarks = bookmarks;
        await SaveItemUserDataAsync(userId, itemId, current);
    }

    public async Task SaveItemNotesAsync(Guid userId, string itemId, List<NoteDto> notes)
    {
        var current = await GetItemUserDataAsync(userId, itemId);
        current.Notes = notes;
        await SaveItemUserDataAsync(userId, itemId, current);
    }
}
