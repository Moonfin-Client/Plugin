using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Persists per-user notification preferences and registered push devices.
/// State lives next to the Seerr sessions under the plugin data folder.
/// </summary>
public class NotificationStore
{
    private readonly string _prefsPath;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<NotificationStore> _logger;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    public NotificationStore(ILogger<NotificationStore> logger)
    {
        _logger = logger;

        var dataPath = MoonfinPlugin.Instance?.DataFolderPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jellyfin", "plugins", "Moonfin");

        _prefsPath = Path.Combine(dataPath, "notifications");

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        EnsureDirectory();
    }

    private void EnsureDirectory()
    {
        if (!Directory.Exists(_prefsPath))
        {
            Directory.CreateDirectory(_prefsPath);
        }
    }

    private string GetPrefsPath(Guid userId) => Path.Combine(_prefsPath, $"{userId}.json");

    /// <summary>
    /// Gets the stored preferences for a user, or defaults when none are stored.
    /// </summary>
    public NotificationPrefs GetPrefs(Guid userId)
    {
        var path = GetPrefsPath(userId);

        _lock.Wait();
        try
        {
            var prefs = AtomicFile.ReadWithRecovery(
                path,
                json => JsonSerializer.Deserialize<NotificationPrefs>(json, _jsonOptions));
            if (prefs != null)
            {
                prefs.JellyfinUserId = userId;
                return prefs;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read notification prefs for user {UserId}", userId);
        }
        finally
        {
            _lock.Release();
        }

        return new NotificationPrefs { JellyfinUserId = userId };
    }

    public void SavePrefs(
        Guid userId,
        bool notifyOnNewRequests,
        bool notifyOnLibraryAdded,
        bool? notifyOnIssues = null)
    {
        var prefs = GetPrefs(userId);
        prefs.NotifyOnNewRequests = notifyOnNewRequests;
        prefs.NotifyOnLibraryAdded = notifyOnLibraryAdded;
        if (notifyOnIssues.HasValue)
        {
            prefs.NotifyOnIssues = notifyOnIssues.Value;
        }

        Write(userId, prefs);
    }

    public void RegisterDevice(Guid userId, string token, string? platform, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        var prefs = GetPrefs(userId);
        prefs.Devices.RemoveAll(d =>
            d.Token == token || (!string.IsNullOrEmpty(deviceId) && d.DeviceId == deviceId));
        prefs.Devices.Add(new DeviceRegistration
        {
            Token = token,
            Platform = platform,
            DeviceId = deviceId
        });
        Write(userId, prefs);
    }

    public void UnregisterDevice(Guid userId, string? token, string? deviceId)
    {
        var prefs = GetPrefs(userId);
        var removed = prefs.Devices.RemoveAll(d =>
            (!string.IsNullOrEmpty(token) && d.Token == token) ||
            (!string.IsNullOrEmpty(deviceId) && d.DeviceId == deviceId));
        if (removed > 0)
        {
            Write(userId, prefs);
        }
    }

    /// <summary>Returns the registered push devices for a user.</summary>
    public List<DeviceRegistration> GetUserDevices(Guid userId) => GetPrefs(userId).Devices;

    /// <summary>Removes a single device by its push token (used to prune dead tokens).</summary>
    public void RemoveDeviceByToken(Guid userId, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        var prefs = GetPrefs(userId);
        var removed = prefs.Devices.RemoveAll(d => d.Token == token);
        if (removed > 0)
        {
            Write(userId, prefs);
        }
    }

    /// <summary>Enumerates users who opted in to new-request notifications.</summary>
    public IEnumerable<Guid> GetUsersWantingNewRequests() => EnumerateUsers(p => p.NotifyOnNewRequests);

    /// <summary>Enumerates users who opted in to library-added notifications.</summary>
    public IEnumerable<Guid> GetUsersWantingLibraryAdded() => EnumerateUsers(p => p.NotifyOnLibraryAdded);

    private IEnumerable<Guid> EnumerateUsers(Func<NotificationPrefs, bool> predicate)
    {
        if (!Directory.Exists(_prefsPath))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(_prefsPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            if (!Guid.TryParse(fileName, out var userId))
            {
                continue;
            }

            var prefs = GetPrefs(userId);
            if (predicate(prefs))
            {
                yield return userId;
            }
        }
    }

    private void Write(Guid userId, NotificationPrefs prefs)
    {
        _lock.Wait();
        try
        {
            EnsureDirectory();
            var json = JsonSerializer.Serialize(prefs, _jsonOptions);
            AtomicFile.WriteAllText(GetPrefsPath(userId), json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write notification prefs for user {UserId}", userId);
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>
/// Per-user notification preferences and registered push devices.
/// </summary>
public class NotificationPrefs
{
    [JsonPropertyName("jellyfinUserId")]
    public Guid JellyfinUserId { get; set; }

    [JsonPropertyName("notifyOnNewRequests")]
    public bool NotifyOnNewRequests { get; set; }

    // These default to true to match the client toggles, which show enabled
    // before the first prefs sync ever reaches the server.
    [JsonPropertyName("notifyOnLibraryAdded")]
    public bool NotifyOnLibraryAdded { get; set; } = true;

    [JsonPropertyName("notifyOnIssues")]
    public bool NotifyOnIssues { get; set; } = true;

    [JsonPropertyName("devices")]
    public List<DeviceRegistration> Devices { get; set; } = new();
}

/// <summary>
/// A push token registered by a Moonfin client, used to deliver push notifications.
/// </summary>
public class DeviceRegistration
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("platform")]
    public string? Platform { get; set; }

    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; set; }
}
