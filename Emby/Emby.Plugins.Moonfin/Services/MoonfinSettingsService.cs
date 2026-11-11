using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.Moonfin.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;

namespace Emby.Plugins.Moonfin.Services
{
    public class MoonfinSettingsService : IDisposable
    {
        // Websocket frame name shared with the client. The frame's Data carries the event object.
        private const string EventMessageName = "MoonfinEvent";

        private readonly string _dataPath;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly ILogger _logger;
        private static readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        private Func<bool, CancellationToken, Task>? _upgradePreferenceChanged;
        private int _userHideUpgradesCache = -1;
        private int _lastKnownUserHideUpgrades = -1;
        private int _hideUpgradesCacheGeneration;
        private readonly object _hideUpgradesCacheGate = new object();
        private int _lastAppliedUpgradeState = -1;
        private int _upgradeScanRetryCount;
        private bool _upgradeScanRetryScheduled;
        private bool _upgradeWorkerRunning;
        private bool _upgradeSyncStopping;
        private bool _upgradeSyncPending;
        private CancellationTokenSource _upgradeSyncCts = new CancellationTokenSource();
        private readonly object _upgradeSyncGate = new object();
        private Task _upgradeSyncWorker = Task.CompletedTask;
        private readonly ConcurrentDictionary<Guid, long> _userSaveGenerations = new ConcurrentDictionary<Guid, long>();
        private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _nativeSyncLocks = new ConcurrentDictionary<Guid, SemaphoreSlim>();

        internal Action? BeforePublishHideUpgradesScanForTesting { get; set; }
        internal Action<Guid>? BeforeNativeSyncWaitForTesting { get; set; }

        internal bool IsUpgradeWorkerRunningForTesting
        {
            get
            {
                lock (_upgradeSyncGate)
                {
                    return _upgradeWorkerRunning;
                }
            }
        }

        internal Func<bool, CancellationToken, Task>? UpgradePreferenceChanged
        {
            get
            {
                lock (_upgradeSyncGate)
                {
                    return _upgradePreferenceChanged;
                }
            }
            set
            {
                lock (_upgradeSyncGate)
                {
                    _upgradePreferenceChanged = value;
                    if (value != null && _upgradeSyncStopping)
                    {
                        _upgradeSyncStopping = false;
                        _upgradeSyncCts.Dispose();
                        _upgradeSyncCts = new CancellationTokenSource();
                    }
                }
            }
        }

        public void InvalidateHideUpgradesCache()
        {
            lock (_hideUpgradesCacheGate)
            {
                _hideUpgradesCacheGeneration++;
                Volatile.Write(ref _userHideUpgradesCache, -1);
            }
        }

        // Cached once. GetProperties() is called on the warm settings-resolve path.
        private static readonly System.Reflection.PropertyInfo[] ProfileProps = typeof(MoonfinSettingsProfile).GetProperties();
        private static readonly System.Reflection.PropertyInfo[] UserSettingsProps = typeof(MoonfinUserSettings).GetProperties();

        // The admin page names settings by their JSON keys, which every profile property pins.
        private static readonly Dictionary<string, string> ProfileJsonNames = ProfileProps.ToDictionary(
            prop => prop.Name,
            prop => prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(prop.Name));

        // A user's own keys and hidden items rather than preferences, so a copy never hands them to someone else.
        private static readonly HashSet<string> PersonalProps = new HashSet<string>
        {
            nameof(MoonfinSettingsProfile.SeerrApiKey),
            nameof(MoonfinSettingsProfile.MdblistApiKey),
            nameof(MoonfinSettingsProfile.TmdbApiKey),
            nameof(MoonfinSettingsProfile.HiddenContinueWatchingItems),
            nameof(MoonfinSettingsProfile.HiddenNextUpSeries),
        };

        // XmlSerializer loads an unset list default as an empty list, and the admin page never saves
        // an empty sources list, so an empty one in the admin defaults means the admin left it unset.
        private static readonly HashSet<string> RatingSourceLists = new HashSet<string>
        {
            nameof(MoonfinSettingsProfile.MdblistRatingSources),
            nameof(MoonfinSettingsProfile.HomeRowsMdblistRatingSources),
            nameof(MoonfinSettingsProfile.MediaBarMdblistRatingSources),
        };

        public MoonfinSettingsService(ILogger logger, string? dataPath = null)
        {
            _logger = logger;
            _dataPath = dataPath
                ?? Plugin.Instance?.DataFolderPath
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Emby-Server", "programdata", "plugins", "Moonfin");

            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            EnsureDataDirectory();
        }

        private void EnsureDataDirectory()
        {
            if (!Directory.Exists(_dataPath))
                Directory.CreateDirectory(_dataPath);
        }

        private string GetUserSettingsPath(Guid userId) =>
            Path.Combine(_dataPath, $"{userId}.json");

        /// <summary>
        /// Loads settings, falling back to the backup copy when the file is missing or unreadable.
        /// </summary>
        private MoonfinUserSettings? ReadSettingsWithRecovery(string filePath) =>
            ReadSettingsWithRecovery(filePath, out _);

        private MoonfinUserSettings? ReadSettingsWithRecovery(string filePath, out Exception? accessError)
        {
            return AtomicFile.ReadWithRecovery(
                filePath,
                json => JsonSerializer.Deserialize<MoonfinUserSettings>(json, _jsonOptions),
                out accessError);
        }

        public async Task<MoonfinUserSettings?> GetUserSettingsAsync(Guid userId)
        {
            var filePath = GetUserSettingsPath(userId);

            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var (settings, accessError) = await Task.Run(() =>
                {
                    Exception? err;
                    var s = ReadSettingsWithRecovery(filePath, out err);
                    return (s, err);
                }).ConfigureAwait(false);
                if (accessError != null)
                {
                    _logger.ErrorException("Failed to read settings for user " + userId + " due to access error", accessError);
                    throw accessError;
                }

                if (settings == null) return null;

                if (settings.NeedsMigration)
                {
                    _logger.Info("Migrating v1 settings to v2 for user " + userId, 0);
                    settings = MigrateV1ToV2(settings);
                    var migratedJson = JsonSerializer.Serialize(settings, _jsonOptions);
                    await Task.Run(() => AtomicFile.WriteAllText(filePath, migratedJson)).ConfigureAwait(false);
                }
                return settings;
            }
            catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException)
            {
                _logger.ErrorException("Error reading settings for user " + userId, ex);
                return null;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Notes what a profile was just handed for the hidden content maps, which is what the
        /// next push from it gets merged against.
        /// </summary>
        public async Task RecordHiddenContentBaselineAsync(Guid userId, string profileName, MoonfinSettingsProfile resolved)
        {
            if (string.IsNullOrEmpty(profileName)) return;

            var name = profileName.ToLowerInvariant();
            var filePath = GetUserSettingsPath(userId);

            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var (settings, accessError) = await Task.Run(() =>
                {
                    var s = ReadSettingsWithRecovery(filePath, out var err);
                    return (s, err);
                }).ConfigureAwait(false);
                if (accessError != null || settings == null) return;

                if (settings.HiddenContentBaselines != null &&
                    settings.HiddenContentBaselines.TryGetValue(name, out var known) &&
                    known.ContinueWatching == resolved.HiddenContinueWatchingItems &&
                    known.NextUpSeries == resolved.HiddenNextUpSeries)
                {
                    return;
                }

                var baseline = BaselineFor(settings, name);
                baseline.ContinueWatching = resolved.HiddenContinueWatchingItems;
                baseline.NextUpSeries = resolved.HiddenNextUpSeries;

                var serialized = JsonSerializer.Serialize(settings, _jsonOptions);
                await Task.Run(() => AtomicFile.WriteAllText(filePath, serialized)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Could not record the hidden content baseline for user " + userId, ex);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<MoonfinSettingsProfile?> GetResolvedProfileAsync(Guid userId, string profileName)
        {
            var settings = await GetUserSettingsAsync(userId).ConfigureAwait(false);
            if (settings == null) return null;
            return ResolveProfile(settings, profileName);
        }

        /// <summary>Resolves a flat profile: device override, global, admin device defaults, admin defaults.</summary>
        public MoonfinSettingsProfile ResolveProfile(MoonfinUserSettings settings, string profileName)
            => ResolveProfile(settings, profileName, Plugin.Instance?.Configuration);

        internal MoonfinSettingsProfile ResolveProfile(MoonfinUserSettings settings, string profileName, PluginConfiguration? config)
        {
            var global = settings.Global;
            var isDevice = IsDeviceProfile(profileName);
            var deviceProfile = isDevice ? settings.GetProfile(profileName) : null;
            var adminDefaults = config?.DefaultUserSettings;
            var adminDevice = isDevice ? config?.GetDefaultsFor(profileName) : null;

            var resolved = new MoonfinSettingsProfile();
            var properties = ProfileProps;
            foreach (var prop in properties)
            {
                var value = deviceProfile != null ? prop.GetValue(deviceProfile) : null;
                if (value == null) value = global != null ? prop.GetValue(global) : null;
                if (value == null) value = AdminDeviceValue(prop, adminDevice);
                if (value == null && adminDefaults != null)
                {
                    value = prop.GetValue(adminDefaults);
                    if (value is List<string> { Count: 0 } && RatingSourceLists.Contains(prop.Name)) value = null;
                }
                if (value != null) prop.SetValue(resolved, value);
            }

            // Normalize the legacy web rating-source id to the client's expected key.
            foreach (var sources in new[] { resolved.MdblistRatingSources, resolved.HomeRowsMdblistRatingSources, resolved.MediaBarMdblistRatingSources })
            {
                if (sources == null) continue;
                for (var i = 0; i < sources.Count; i++)
                {
                    if (string.Equals(sources[i], "rtAudience", StringComparison.OrdinalIgnoreCase))
                    {
                        sources[i] = "tomatoes_audience";
                    }
                }
            }

            // Home layout (sections + row order) resolves as a unit from a single profile layer
            // rather than field-by-field, so a client's sections and order never come from
            // different profiles.
            var homeLayout = ResolveHomeLayout(deviceProfile, global, HasHomeLayout(adminDevice) ? adminDevice : null, adminDefaults);
            resolved.HomeSections = homeLayout.HomeSections;
            resolved.HomeRowOrder = homeLayout.HomeRowOrder;

            if (string.IsNullOrWhiteSpace(resolved.TmdbApiKey))
            {
                resolved.TmdbApiKey = config?.TmdbApiKey;
            }

            if (string.IsNullOrWhiteSpace(resolved.MdblistApiKey))
            {
                resolved.MdblistApiKey = config?.MdblistApiKey;
            }

            return resolved;
        }

        /// <summary>
        /// What a profile resolves to for a user with no settings of their own: the device's admin
        /// defaults over the global ones. Global, or no device, is the global defaults as stored.
        /// </summary>
        public MoonfinSettingsProfile? AdminDefaultsFor(string? profileName)
            => AdminDefaultsFor(profileName, Plugin.Instance?.Configuration);

        internal static MoonfinSettingsProfile? AdminDefaultsFor(string? profileName, PluginConfiguration? config)
        {
            var global = config?.DefaultUserSettings;
            var device = IsDeviceProfile(profileName) ? config?.GetDefaultsFor(profileName) : null;
            if (device == null) return global;

            var merged = new MoonfinSettingsProfile();
            foreach (var prop in ProfileProps)
            {
                var value = AdminDeviceValue(prop, device) ?? (global != null ? prop.GetValue(global) : null);
                if (value != null) prop.SetValue(merged, value);
            }

            // The home layout moves as one unit, the same as in ResolveProfile.
            var layout = HasHomeLayout(device) ? device : global;
            merged.HomeSections = layout?.HomeSections;
            merged.HomeRowOrder = layout?.HomeRowOrder;
            return merged;
        }

        private static bool IsDeviceProfile(string? profileName) =>
            !string.IsNullOrEmpty(profileName) && !string.Equals(profileName, "global", StringComparison.OrdinalIgnoreCase);

        // XmlSerializer loads every list a device's defaults never set as an empty one, and an empty
        // list there would hide the global default, so it counts as not set.
        private static object? AdminDeviceValue(PropertyInfo prop, MoonfinSettingsProfile? deviceDefaults)
        {
            var value = deviceDefaults != null ? prop.GetValue(deviceDefaults) : null;
            return value is System.Collections.ICollection { Count: 0 } ? null : value;
        }

        private static bool HasHomeLayout(MoonfinSettingsProfile? profile) =>
            profile != null && (profile.HomeSections is { Count: > 0 } || profile.HomeRowOrder is { Count: > 0 });

        private static (List<MoonfinHomeSectionConfig>? HomeSections, List<string>? HomeRowOrder) ResolveHomeLayout(
            MoonfinSettingsProfile? deviceProfile,
            MoonfinSettingsProfile? global,
            MoonfinSettingsProfile? adminDevice,
            MoonfinSettingsProfile? adminDefaults)
        {
            foreach (var profile in new[] { deviceProfile, global, adminDevice, adminDefaults })
            {
                if (profile == null) continue;
                if (profile.HomeSections == null && profile.HomeRowOrder == null) continue;
                return (profile.HomeSections, ResolveHomeRowOrder(profile));
            }

            return (null, null);
        }

        private static List<string>? ResolveHomeRowOrder(MoonfinSettingsProfile profile)
        {
            if (profile.HomeRowOrder != null) return profile.HomeRowOrder;
            if (profile.HomeSections == null) return null;

            var homeRowOrder = profile.HomeSections
                .Where(section => !string.Equals(section.Kind, "pluginDynamic", StringComparison.OrdinalIgnoreCase))
                .Where(section => section.Enabled != false)
                .Where(section => !string.IsNullOrWhiteSpace(section.Type) &&
                    !string.Equals(section.Type, "none", StringComparison.OrdinalIgnoreCase))
                .OrderBy(section => section.Order ?? int.MaxValue)
                .Select(section => section.Type!)
                .ToList();

            return homeRowOrder.Count > 0 ? homeRowOrder : null;
        }

        private void StripServerWideKeys(MoonfinSettingsProfile? profile)
        {
            if (profile == null) return;
            var config = Plugin.Instance?.Configuration;
            if (config == null) return;

            if (profile.TmdbApiKey == config.TmdbApiKey)
            {
                profile.TmdbApiKey = null;
            }
            if (profile.MdblistApiKey == config.MdblistApiKey)
            {
                profile.MdblistApiKey = null;
            }
        }

        private void StripServerWideKeys(MoonfinUserSettings? settings)
        {
            if (settings == null) return;
            var config = Plugin.Instance?.Configuration;
            if (config == null) return;

            if (settings.TmdbApiKey == config.TmdbApiKey)
            {
                settings.TmdbApiKey = null;
            }
            if (settings.MdblistApiKey == config.MdblistApiKey)
            {
                settings.MdblistApiKey = null;
            }

            StripServerWideKeys(settings.Global);
            StripServerWideKeys(settings.Desktop);
            StripServerWideKeys(settings.Mobile);
            StripServerWideKeys(settings.Tv);
        }

        public async Task SaveUserSettingsAsync(Guid userId, MoonfinUserSettings settings, string? clientId = null, string mergeMode = "merge")
        {
            var filePath = GetUserSettingsPath(userId);
            bool settingsChanged;
            MoonfinUserSettings? savedSnapshot = null;
            long? savedGeneration = null;
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                MoonfinUserSettings finalSettings;
                string? beforeComparisonJson = null;
                string? storedHiddenContinueWatching = null;
                string? storedHiddenNextUp = null;
                if (mergeMode == "merge")
                {
                    var (existingSettings, accessError) = await Task.Run(() =>
                    {
                        Exception? err;
                        var s = ReadSettingsWithRecovery(filePath, out err);
                        return (s, err);
                    }).ConfigureAwait(false);
                    if (accessError != null)
                    {
                        _logger.ErrorException("Failed to read existing settings for user " + userId + " due to access error; aborting merge", accessError);
                        throw accessError;
                    }

                    if (existingSettings != null && existingSettings.NeedsMigration)
                        existingSettings = MigrateV1ToV2(existingSettings);
                    beforeComparisonJson = SerializeForComparison(existingSettings ?? new MoonfinUserSettings());
                    storedHiddenContinueWatching = existingSettings?.Global?.HiddenContinueWatchingItems;
                    storedHiddenNextUp = existingSettings?.Global?.HiddenNextUpSeries;
                    finalSettings = MergeSettings(existingSettings, settings);
                }
                else
                {
                    finalSettings = settings;
                }

                StripServerWideKeys(finalSettings);
                finalSettings.LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                finalSettings.LastUpdatedBy = clientId ?? "unknown";
                finalSettings.SchemaVersion = 2;

                MoveContentHidingToGlobal(finalSettings, storedHiddenContinueWatching, storedHiddenNextUp);

                settingsChanged = SettingsDiffer(beforeComparisonJson, finalSettings);
                savedSnapshot = finalSettings;

                var json = JsonSerializer.Serialize(finalSettings, _jsonOptions);
                await Task.Run(() => AtomicFile.WriteAllText(filePath, json)).ConfigureAwait(false);
                savedGeneration = _userSaveGenerations.AddOrUpdate(userId, 1, static (_, prev) => prev + 1);
                InvalidateHideUpgradesCache();
            }
            finally
            {
                _lock.Release();
            }

            if (savedSnapshot != null)
            {
                await SyncRecentRowPreferencesAsync(userId, savedSnapshot, null, savedGeneration).ConfigureAwait(false);
            }

            // A merge that changed nothing is the client echoing state it already
            // holds. Broadcasting it anyway sends the echo straight back and keeps
            // a push-apply loop alive on the other end.
            if (settingsChanged) NotifySettingsChanged(userId);
        }

        public async Task SaveProfileAsync(Guid userId, string profileName, MoonfinSettingsProfile profile, string? clientId = null, bool notifySettingsChanged = true, bool replace = false)
        {
            var filePath = GetUserSettingsPath(userId);
            bool settingsChanged;
            MoonfinUserSettings? savedSnapshot = null;
            long? savedGeneration = null;
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var (settings, accessError) = await Task.Run(() =>
                {
                    Exception? err;
                    var s = ReadSettingsWithRecovery(filePath, out err);
                    return (s, err);
                }).ConfigureAwait(false);
                if (accessError != null)
                {
                    _logger.ErrorException("Failed to read existing settings for user " + userId + " due to access error; aborting profile save", accessError);
                    throw accessError;
                }
                settings ??= new MoonfinUserSettings();
                if (settings.NeedsMigration) settings = MigrateV1ToV2(settings);

                var beforeComparisonJson = SerializeForComparison(settings);
                var storedHiddenContinueWatching = settings.Global?.HiddenContinueWatchingItems;
                var storedHiddenNextUp = settings.Global?.HiddenNextUpSeries;

                var existingProfile = string.Equals(profileName, "global", StringComparison.OrdinalIgnoreCase)
                    ? settings.Global
                    : settings.GetProfile(profileName);

                if (existingProfile != null && replace)
                {
                    // A replace swaps the preferences only, the user's own keys and hidden items stay.
                    foreach (var prop in ProfileProps)
                    {
                        if (PersonalProps.Contains(prop.Name)) prop.SetValue(profile, prop.GetValue(existingProfile));
                    }
                    settings.SetProfile(profileName, profile);
                }
                else if (existingProfile != null)
                    MergeProfile(existingProfile, profile);
                else
                    settings.SetProfile(profileName, profile);

                StripServerWideKeys(settings);
                settings.LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                settings.LastUpdatedBy = clientId ?? "unknown";
                settings.SchemaVersion = 2;

                MoveContentHidingToGlobal(settings, storedHiddenContinueWatching, storedHiddenNextUp);

                settingsChanged = SettingsDiffer(beforeComparisonJson, settings);
                savedSnapshot = settings;

                var serialized = JsonSerializer.Serialize(settings, _jsonOptions);
                await Task.Run(() => AtomicFile.WriteAllText(filePath, serialized)).ConfigureAwait(false);
                savedGeneration = _userSaveGenerations.AddOrUpdate(userId, 1, static (_, prev) => prev + 1);
                InvalidateHideUpgradesCache();
            }
            finally
            {
                _lock.Release();
            }

            if (savedSnapshot != null)
            {
                await SyncRecentRowPreferencesAsync(userId, savedSnapshot, profileName, savedGeneration).ConfigureAwait(false);
            }

            // Same echo guard as SaveUserSettingsAsync.
            if (notifySettingsChanged && settingsChanged) NotifySettingsChanged(userId);
        }

        /// <summary>
        /// Whether the save altered anything a client would act on. A snapshot that
        /// could not be taken counts as changed, so an unknown answer still reaches
        /// the clients rather than silently holding the broadcast back.
        /// </summary>
        private bool SettingsDiffer(string? before, MoonfinUserSettings after)
        {
            var afterJson = SerializeForComparison(after);
            return before == null || afterJson == null || before != afterJson;
        }

        /// <summary>
        /// Serializes settings for change detection, leaving out the metadata
        /// stamped on every save since it would mask a merge that changed nothing
        /// else. Null when the settings cannot be read back as an object.
        /// </summary>
        private string? SerializeForComparison(MoonfinUserSettings settings)
        {
            if (JsonSerializer.SerializeToNode(settings, _jsonOptions) is not JsonObject node)
                return null;

            node.Remove("schemaVersion");
            node.Remove("lastUpdated");
            node.Remove("lastUpdatedBy");
            node.Remove("hiddenContentBaselines");
            return node.ToJsonString();
        }

        public void NotifySettingsChanged(Guid userId)
        {
            SendToUser(userId, new Dictionary<string, object> { ["type"] = "settingsUpdated" });
        }

        /// <summary>
        /// Sends a raw JSON event to every session a single user has open, over Emby's session
        /// websocket. Returns the number of sessions targeted (0 when the user has no live
        /// client). Used to push Seerr notifications to a foreground client. Backgrounded
        /// clients still get the push path independently.
        /// </summary>
        public int NotifyUser(Guid userId, string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return 0;

            var payload = ParseEventPayload(json);
            if (payload == null) return 0;

            return SendToUser(userId, payload);
        }

        public int BroadcastMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return 0;
            return SendToAllUsers(new Dictionary<string, object> { ["type"] = "adminMessage", ["text"] = message });
        }

        public int BroadcastSystemEvent(string eventType)
        {
            if (string.IsNullOrWhiteSpace(eventType)) return 0;
            return SendToAllUsers(new Dictionary<string, object> { ["type"] = eventType.Trim() });
        }

        // Sends a MoonfinEvent frame to every session of one user. Returns the session count
        // targeted, or 0 when the session manager is unavailable, the user is unknown, or offline.
        private int SendToUser(Guid userId, Dictionary<string, object> payload)
        {
            try
            {
                var sessionManager = PluginServices.SessionManager;
                if (sessionManager == null) return 0;

                var user = PluginServices.UserManager?.GetUserById(userId);
                if (user == null) return 0;

                var internalId = user.InternalId;
                var sessions = sessionManager.Sessions.Count(s => s.ContainsUser(internalId));
                if (sessions == 0) return 0;

                Dispatch(sessionManager, new[] { internalId }, payload);
                return sessions;
            }
            catch (Exception ex)
            {
                _logger.Debug("MoonfinEvent send failed for user " + userId + ": " + ex.Message);
                return 0;
            }
        }

        // Sends a MoonfinEvent frame to every user with an active session. Returns the number
        // of user-bearing sessions targeted.
        private int SendToAllUsers(Dictionary<string, object> payload)
        {
            try
            {
                var sessionManager = PluginServices.SessionManager;
                if (sessionManager == null) return 0;

                var userIds = new HashSet<long>();
                var sessions = 0;
                foreach (var session in sessionManager.Sessions)
                {
                    if (session == null || !session.HasUser || session.UserInternalId <= 0) continue;
                    sessions++;
                    userIds.Add(session.UserInternalId);
                }

                if (userIds.Count == 0) return 0;

                Dispatch(sessionManager, userIds.ToArray(), payload);
                return sessions;
            }
            catch (Exception ex)
            {
                _logger.Debug("MoonfinEvent broadcast failed: " + ex.Message);
                return 0;
            }
        }

        // Fire-and-forget so callers stay synchronous. Faults are observed and debug-logged.
        private void Dispatch(ISessionManager sessionManager, long[] userIds, Dictionary<string, object> payload)
        {
            var task = sessionManager.SendMessageToUserSessions(userIds, EventMessageName, payload, CancellationToken.None);
            task.ContinueWith(
                t => _logger.Debug("MoonfinEvent delivery faulted: " + (t.Exception?.GetBaseException().Message ?? "unknown")),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        // The Seerr path hands us a pre-serialized JSON object, but Emby's serializer needs a real
        // object graph to write the frame's Data, so rebuild it as plain dictionaries and lists.
        private Dictionary<string, object>? ParseEventPayload(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                return ToPlainObject(doc.RootElement) as Dictionary<string, object>;
            }
            catch (JsonException ex)
            {
                _logger.Debug("Dropping malformed event payload: " + ex.Message);
                return null;
            }
        }

        private static object? ToPlainObject(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var map = new Dictionary<string, object>();
                    foreach (var prop in element.EnumerateObject())
                    {
                        var value = ToPlainObject(prop.Value);
                        if (value != null) map[prop.Name] = value;
                    }
                    return map;
                case JsonValueKind.Array:
                    var list = new List<object>();
                    foreach (var entry in element.EnumerateArray())
                    {
                        var value = ToPlainObject(entry);
                        if (value != null) list.Add(value);
                    }
                    return list;
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.Number:
                    return element.TryGetInt64(out var l) ? l : (object)element.GetDouble();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Merges admin defaults into users' existing settings. Only the values set in the pushed
        /// profiles are written, narrowed to <paramref name="keys"/> when given, and each user keeps
        /// their own values for the rest. Null <paramref name="userIds"/> means every user with a
        /// settings file, since the others already resolve to the admin defaults.
        /// </summary>
        public async Task<int> MergeDefaultsAsync(
            PluginConfiguration config,
            IReadOnlyCollection<string> profiles,
            IReadOnlyCollection<string>? keys,
            IReadOnlyCollection<Guid>? userIds)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            var push = DefaultsToPush(config, profiles, keys);
            if (push.Count == 0) return 0;

            // Every device's own defaults, pushed or not, so Global's never lands where a device differs.
            var deviceDefaults = new Dictionary<string, MoonfinSettingsProfile?>();
            foreach (var name in MoonfinUserSettings.ValidProfiles.Where(IsDeviceProfile))
            {
                var own = config.GetDefaultsFor(name);
                deviceDefaults[name] = own != null ? SetDefaultsOnly(own) : null;
            }

            EnsureDataDirectory();
            var usersUpdated = 0;
            foreach (var userId in userIds ?? StoredUserIds())
            {
                try
                {
                    await MergeDefaultsIntoProfilesAsync(userId, push, deviceDefaults).ConfigureAwait(false);
                    usersUpdated++;
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Failed to push admin defaults for user " + userId, ex);
                }
            }
            return usersUpdated;
        }

        // A device profile wins over global, so Global's defaults also go into each device profile
        // the user already has, except where that device has defaults of its own. A device's own
        // defaults create its profile when the user has none, or they would never reach that device.
        private async Task MergeDefaultsIntoProfilesAsync(
            Guid userId,
            IReadOnlyDictionary<string, MoonfinSettingsProfile> push,
            IReadOnlyDictionary<string, MoonfinSettingsProfile?> deviceDefaults)
        {
            var existing = await GetUserSettingsAsync(userId).ConfigureAwait(false);
            push.TryGetValue("global", out var global);
            foreach (var profileName in MoonfinUserSettings.ValidProfiles)
            {
                MoonfinSettingsProfile? layer;
                if (!IsDeviceProfile(profileName))
                {
                    layer = global;
                }
                else
                {
                    layer = global != null && existing?.GetProfile(profileName) != null
                        ? Without(global, deviceDefaults[profileName])
                        : null;
                    if (push.TryGetValue(profileName, out var own)) layer = Overlay(layer, own);
                }

                if (layer == null || !HasAnyProfileValues(layer)) continue;

                // SaveProfileAsync changes what it's handed, so each profile gets its own copy.
                await SaveProfileAsync(userId, profileName, CloneProfile(layer), "admin-default-push", notifySettingsChanged: false).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Copies one user's saved profiles onto other users. Merge keeps each target's values for the
        /// settings the copy leaves out, replace makes every copied profile an exact copy. Personal data
        /// such as API keys and hidden items always stays with its owner.
        /// </summary>
        public async Task<int> CopySettingsAsync(
            Guid fromUserId,
            IReadOnlyCollection<Guid> toUserIds,
            IReadOnlyCollection<string> profiles,
            IReadOnlyCollection<string>? keys,
            bool replace)
        {
            var source = await GetUserSettingsAsync(fromUserId).ConfigureAwait(false);
            if (source == null) return 0;

            var copies = new Dictionary<string, MoonfinSettingsProfile>();
            foreach (var profileName in profiles)
            {
                var profile = IsDeviceProfile(profileName) ? source.GetProfile(profileName) : source.Global;
                if (profile == null) continue;

                var copy = KeepOnly(CloneProfile(profile), keys);
                foreach (var prop in ProfileProps)
                {
                    if (PersonalProps.Contains(prop.Name)) prop.SetValue(copy, null);
                }

                if (replace || HasAnyProfileValues(copy)) copies[profileName.ToLowerInvariant()] = copy;
            }
            if (copies.Count == 0) return 0;

            var usersUpdated = 0;
            foreach (var userId in toUserIds)
            {
                if (userId == fromUserId) continue;
                try
                {
                    foreach (var entry in copies)
                    {
                        await SaveProfileAsync(userId, entry.Key, CloneProfile(entry.Value), "admin-copy", notifySettingsChanged: false, replace: replace).ConfigureAwait(false);
                    }

                    NotifySettingsChanged(userId);
                    usersUpdated++;
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Failed to copy settings to user " + userId, ex);
                }
            }
            return usersUpdated;
        }

        // The admin defaults to push into each profile, without the values they leave unset and
        // narrowed to the keys. A profile left with nothing is skipped.
        private static Dictionary<string, MoonfinSettingsProfile> DefaultsToPush(
            PluginConfiguration config, IEnumerable<string> profiles, IReadOnlyCollection<string>? keys)
        {
            var push = new Dictionary<string, MoonfinSettingsProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var profileName in profiles)
            {
                var defaults = config.GetDefaultsFor(profileName);
                if (defaults == null) continue;
                var copy = KeepOnly(SetDefaultsOnly(defaults), keys);
                if (HasAnyProfileValues(copy)) push[profileName.ToLowerInvariant()] = copy;
            }
            return push;
        }

        private List<Guid> StoredUserIds()
        {
            var ids = new List<Guid>();
            foreach (var filePath in Directory.EnumerateFiles(_dataPath, "*.json", SearchOption.TopDirectoryOnly))
            {
                if (Guid.TryParse(Path.GetFileNameWithoutExtension(filePath), out var userId)) ids.Add(userId);
            }
            return ids;
        }

        // A copy of source without the settings mask sets.
        private static MoonfinSettingsProfile Without(MoonfinSettingsProfile source, MoonfinSettingsProfile? mask)
        {
            var copy = CloneProfile(source);
            if (mask == null) return copy;
            foreach (var prop in ProfileProps)
            {
                if (prop.GetValue(mask) != null) prop.SetValue(copy, null);
            }
            if (HasHomeLayout(mask))
            {
                copy.HomeSections = null;
                copy.HomeRowOrder = null;
            }
            return copy;
        }

        // Writes every value over sets onto under, or onto a new profile when there's none.
        private static MoonfinSettingsProfile Overlay(MoonfinSettingsProfile? under, MoonfinSettingsProfile over)
        {
            var result = under ?? new MoonfinSettingsProfile();
            foreach (var prop in ProfileProps)
            {
                var value = prop.GetValue(over);
                if (value != null) prop.SetValue(result, value);
            }
            if (HasHomeLayout(over))
            {
                result.HomeSections = over.HomeSections;
                result.HomeRowOrder = over.HomeRowOrder;
            }
            return result;
        }

        // Clears every setting outside keys, matched by their JSON names.
        private static MoonfinSettingsProfile KeepOnly(MoonfinSettingsProfile profile, IReadOnlyCollection<string>? keys)
        {
            if (keys == null) return profile;
            var keep = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
            foreach (var prop in ProfileProps)
            {
                if (!keep.Contains(ProfileJsonNames[prop.Name])) prop.SetValue(profile, null);
            }
            return profile;
        }

        // XmlSerializer loads every list the admin never set as an empty one, so an empty list
        // counts as not set rather than wiping a user's own.
        private static MoonfinSettingsProfile SetDefaultsOnly(MoonfinSettingsProfile defaults)
        {
            var copy = CloneProfile(defaults);
            foreach (var prop in ProfileProps)
            {
                if (prop.GetValue(copy) is System.Collections.ICollection { Count: 0 }) prop.SetValue(copy, null);
            }
            return copy;
        }

        /// <summary>
        /// Resets users to clean settings files holding only the admin defaults of the given profiles,
        /// and when deleteOrphans is set also deletes the files of users that no longer exist.
        /// </summary>
        public async Task<(int UsersAffected, int OrphansDeleted)> ResetUsersToDefaultsAsync(
            PluginConfiguration config, IReadOnlyCollection<string> profiles, IReadOnlyCollection<Guid> userIds, bool deleteOrphans)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            EnsureDataDirectory();

            var push = DefaultsToPush(config, profiles, null);
            var userSet = new HashSet<Guid>(userIds);
            var usersAffected = 0;

            foreach (var userId in userSet)
            {
                try
                {
                    var (writtenSettings, saveGen) = await WriteDefaultsOnlyAsync(userId, push).ConfigureAwait(false);
                    usersAffected++;
                    await SyncRecentRowPreferencesAsync(userId, writtenSettings, null, saveGen).ConfigureAwait(false);
                }
                catch (Exception ex) { _logger.ErrorException("Failed to reset user " + userId, ex); }
            }

            var orphansDeleted = 0;
            if (deleteOrphans)
            {
                foreach (var filePath in Directory.EnumerateFiles(_dataPath, "*.json", SearchOption.TopDirectoryOnly))
                {
                    var fileName = Path.GetFileNameWithoutExtension(filePath);
                    if (!Guid.TryParse(fileName, out var userId) || userSet.Contains(userId)) continue;
                    try { AtomicFile.DeleteWithSidecars(filePath); InvalidateHideUpgradesCache(); orphansDeleted++; }
                    catch (Exception ex) { _logger.ErrorException("Failed to delete orphan settings " + userId, ex); }
                }
            }

            _ = ScheduleUpgradePreferenceSyncIfNeeded(Guid.Empty);
            return (usersAffected, orphansDeleted);
        }

        private async Task<(MoonfinUserSettings Settings, long SaveGeneration)> WriteDefaultsOnlyAsync(Guid userId, IReadOnlyDictionary<string, MoonfinSettingsProfile> push)
        {
            var clean = new MoonfinUserSettings
            {
                SchemaVersion = 2,
                SyncEnabled = true,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                LastUpdatedBy = "admin-default-reset",
                Global = push.TryGetValue("global", out var global) ? CloneProfile(global) : new MoonfinSettingsProfile()
            };
            foreach (var entry in push)
            {
                if (IsDeviceProfile(entry.Key)) clean.SetProfile(entry.Key, CloneProfile(entry.Value));
            }

            long saveGen;
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var filePath = GetUserSettingsPath(userId);
                var json = JsonSerializer.Serialize(clean, _jsonOptions);
                await Task.Run(() => AtomicFile.WriteAllText(filePath, json)).ConfigureAwait(false);
                saveGen = _userSaveGenerations.AddOrUpdate(userId, 1, static (_, prev) => prev + 1);
                InvalidateHideUpgradesCache();
            }
            finally
            {
                _lock.Release();
            }

            return (clean, saveGen);
        }

        private static MoonfinSettingsProfile CloneProfile(MoonfinSettingsProfile source)
        {
            var clone = new MoonfinSettingsProfile();
            foreach (var prop in ProfileProps)
            {
                var value = prop.GetValue(source);
                if (value != null) prop.SetValue(clone, value);
            }
            return clone;
        }

        public async Task DeleteUserSettingsAsync(Guid userId)
        {
            var filePath = GetUserSettingsPath(userId);
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                // Takes the backup with it, otherwise a deleted user's settings could come back
                // through recovery.
                AtomicFile.DeleteWithSidecars(filePath);
                InvalidateHideUpgradesCache();
            }
            finally
            {
                _lock.Release();
            }

            _ = ScheduleUpgradePreferenceSyncIfNeeded(userId);
        }

        public async Task DeleteProfileAsync(Guid userId, string profileName)
        {
            if (string.Equals(profileName, "global", StringComparison.OrdinalIgnoreCase)) return;
            var settings = await GetUserSettingsAsync(userId).ConfigureAwait(false);
            if (settings == null) return;

            settings.SetProfile(profileName, null);
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var filePath = GetUserSettingsPath(userId);
                settings.LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var json = JsonSerializer.Serialize(settings, _jsonOptions);
                await Task.Run(() => AtomicFile.WriteAllText(filePath, json)).ConfigureAwait(false);
                InvalidateHideUpgradesCache();
            }
            finally
            {
                _lock.Release();
            }

            _ = ScheduleUpgradePreferenceSyncIfNeeded(userId);
        }

        // Settings that only survive as a backup still count as existing.
        public bool UserSettingsExist(Guid userId)
        {
            var path = GetUserSettingsPath(userId);
            return File.Exists(path) || File.Exists(AtomicFile.BackupPath(path));
        }

        /// <summary>
        /// One-time healing sweep over every stored settings file. Files corrupted by the
        /// in-place writes that shipped before AtomicFile are repaired through JsonSalvage when
        /// a usable prefix survives, and quarantined otherwise. Healthy files are left untouched.
        /// </summary>
        public async Task<HealSummary> HealDataFilesAsync(CancellationToken cancellationToken)
        {
            (bool Ok, string Healed) Salvage(string raw) =>
                (JsonSalvage.TrySalvage(raw, ValidateSalvagedEnvelope, out var healed), healed);

            var summary = await FileHealer.HealDirectoryAsync(
                _dataPath,
                Path.Combine(_dataPath, "quarantine", "settings"),
                _lock,
                ValidateSalvagedEnvelope,
                Salvage,
                cancellationToken).ConfigureAwait(false);

            foreach (var note in summary.Notes)
            {
                _logger.Warn("Settings heal: {0}", note);
            }

            return summary;
        }

        // The floor a salvaged envelope must clear before it replaces the corrupt file: it has
        // to deserialize, carry a known schema version, and hold either a device profile or the
        // v1 flat fields the lazy migration understands. Anything reduced to bare metadata is
        // the same as data loss, so it goes to quarantine where the bytes stay recoverable by
        // hand.
        private bool ValidateSalvagedEnvelope(string text)
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<MoonfinUserSettings>(text, _jsonOptions);
                if (envelope == null || envelope.SchemaVersion < 1 || envelope.SchemaVersion > 2)
                {
                    return false;
                }

                return envelope.Global != null ||
                    envelope.Desktop != null ||
                    envelope.Mobile != null ||
                    envelope.Tv != null ||
                    envelope.NeedsMigration;
            }
            catch
            {
                return false;
            }
        }

        private MoonfinUserSettings MigrateV1ToV2(MoonfinUserSettings v1)
        {
            var global = new MoonfinSettingsProfile();
            var profileProps = ProfileProps;
            var v1Props = UserSettingsProps;

            foreach (var profileProp in profileProps)
            {
                var v1Prop = System.Array.Find(v1Props, p => p.Name == profileProp.Name && p.DeclaringType == typeof(MoonfinUserSettings));
                if (v1Prop != null)
                {
                    var value = v1Prop.GetValue(v1);
                    if (value != null) profileProp.SetValue(global, value);
                }
            }

            return new MoonfinUserSettings
            {
                SchemaVersion = 2,
                LastUpdated = v1.LastUpdated,
                LastUpdatedBy = v1.LastUpdatedBy,
                SyncEnabled = true,
                Global = global
            };
        }

        private static readonly (string Name, Func<MoonfinUserSettings, MoonfinSettingsProfile?> Get)[] HidingProfiles =
        {
            ("global", s => s.Global),
            ("desktop", s => s.Desktop),
            ("mobile", s => s.Mobile),
            ("tv", s => s.Tv),
        };

        /// <summary>
        /// Content hiding is a global preference, so each profile's hidden lists get lifted into
        /// the global one. Everything a profile pushed is merged in, anything its baseline still
        /// lists that the push leaves out counts as unhidden and is dropped, and hides it hasn't
        /// pulled yet are left alone. The stored global maps are passed in because a push to the
        /// global profile overwrites them in place before this runs.
        /// </summary>
        private static void MoveContentHidingToGlobal(
            MoonfinUserSettings settings,
            string? storedContinueWatching,
            string? storedNextUp)
        {
            if (settings.Global == null)
            {
                settings.Global = new MoonfinSettingsProfile();
            }

            var hiddenContinueWatching = MergeHiddenEntries(
                settings,
                storedContinueWatching,
                p => p.HiddenContinueWatchingItems,
                b => b.ContinueWatching,
                (b, v) => b.ContinueWatching = v);

            if (hiddenContinueWatching != null)
            {
                settings.Global.HiddenContinueWatchingItems = hiddenContinueWatching;
            }

            var hiddenNextUp = MergeHiddenEntries(
                settings,
                storedNextUp,
                p => p.HiddenNextUpSeries,
                b => b.NextUpSeries,
                (b, v) => b.NextUpSeries = v);

            if (hiddenNextUp != null)
            {
                settings.Global.HiddenNextUpSeries = hiddenNextUp;
            }

            var devices = new[] { settings.Desktop, settings.Mobile, settings.Tv };
            foreach (var device in devices)
            {
                if (device == null) continue;
                device.HiddenContinueWatchingItems = null;
                device.HiddenNextUpSeries = null;
            }
        }

        /// <summary>
        /// Folds every profile's push of one hidden map into the stored global value. Null when no
        /// profile pushed the field, so a partial save leaves what is on file alone.
        /// </summary>
        private static string? MergeHiddenEntries(
            MoonfinUserSettings settings,
            string? stored,
            Func<MoonfinSettingsProfile, string?> selector,
            Func<HiddenContentBaseline, string?> readBaseline,
            Action<HiddenContentBaseline, string?> writeBaseline)
        {
            var merged = ParseHiddenEntries(stored);
            var pushed = false;

            foreach (var (name, get) in HidingProfiles)
            {
                var profile = get(settings);
                if (profile == null) continue;

                var value = selector(profile);
                if (value == null) continue;

                // The global profile is where the stored map lives, so it only counts as a push
                // when a client sent something other than what was already on file.
                if (name == "global" && value == stored) continue;

                pushed = true;
                var entries = ParseHiddenEntries(value);

                string? baseline = null;
                if (settings.HiddenContentBaselines != null &&
                    settings.HiddenContentBaselines.TryGetValue(name, out var known))
                {
                    baseline = readBaseline(known);
                }

                foreach (var key in ParseHiddenEntries(baseline).Keys)
                {
                    if (!entries.ContainsKey(key))
                    {
                        merged.Remove(key);
                    }
                }

                foreach (var pair in entries)
                {
                    if (!merged.TryGetValue(pair.Key, out var existingDate) ||
                        IsLaterHide(pair.Value, existingDate))
                    {
                        merged[pair.Key] = pair.Value;
                    }
                }

                writeBaseline(BaselineFor(settings, name), value);
            }

            return pushed ? JsonSerializer.Serialize(merged) : null;
        }

        private static HiddenContentBaseline BaselineFor(MoonfinUserSettings settings, string profileName)
        {
            if (settings.HiddenContentBaselines == null)
            {
                settings.HiddenContentBaselines = new Dictionary<string, HiddenContentBaseline>();
            }

            if (!settings.HiddenContentBaselines.TryGetValue(profileName, out var baseline))
            {
                baseline = new HiddenContentBaseline();
                settings.HiddenContentBaselines[profileName] = baseline;
            }

            return baseline;
        }

        /// <summary>
        /// Whether one hide timestamp is later than another. Clients write ISO-8601, but the field
        /// is free-form, so anything that won't parse falls back to comparing the text.
        /// </summary>
        private static bool IsLaterHide(string candidate, string existing)
        {
            if (DateTimeOffset.TryParse(candidate, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var candidateAt) &&
                DateTimeOffset.TryParse(existing, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var existingAt))
            {
                return candidateAt > existingAt;
            }

            return string.Compare(candidate, existing, StringComparison.Ordinal) > 0;
        }

        private static Dictionary<string, string> ParseHiddenEntries(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new Dictionary<string, string>();
            }

            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(value)
                    ?? new Dictionary<string, string>();
            }
            catch (JsonException)
            {
                return new Dictionary<string, string>();
            }
        }

        private static void MergeProfile(MoonfinSettingsProfile existing, MoonfinSettingsProfile incoming)
        {
            // An older client that syncs homeRowOrder but not homeSections should clear the stale
            // sections so the two layout fields can't drift apart.
            if (incoming.HomeSections == null && incoming.HomeRowOrder != null)
            {
                existing.HomeSections = null;
            }

            foreach (var prop in ProfileProps)
            {
                var incomingValue = prop.GetValue(incoming);
                if (incomingValue == null) continue;

                // Older clients push empty API keys, which should not wipe a stored key.
                if (incomingValue is string text
                    && prop.Name.Contains("ApiKey", StringComparison.Ordinal)
                    && (string.IsNullOrWhiteSpace(text) || text == "null")) continue;

                prop.SetValue(existing, incomingValue);
            }
        }

        private MoonfinUserSettings MergeSettings(MoonfinUserSettings? existing, MoonfinUserSettings incoming)
        {
            if (existing == null) return incoming;

            if (incoming.SyncEnabled != existing.SyncEnabled) existing.SyncEnabled = incoming.SyncEnabled;

            if (incoming.Global != null)
            {
                if (existing.Global == null) existing.Global = incoming.Global;
                else MergeProfile(existing.Global, incoming.Global);
            }
            if (incoming.Desktop != null)
            {
                if (existing.Desktop == null) existing.Desktop = incoming.Desktop;
                else MergeProfile(existing.Desktop, incoming.Desktop);
            }
            if (incoming.Mobile != null)
            {
                if (existing.Mobile == null) existing.Mobile = incoming.Mobile;
                else MergeProfile(existing.Mobile, incoming.Mobile);
            }
            if (incoming.Tv != null)
            {
                if (existing.Tv == null) existing.Tv = incoming.Tv;
                else MergeProfile(existing.Tv, incoming.Tv);
            }

            var props = UserSettingsProps;
            foreach (var prop in props)
            {
                if (prop.Name is "LastUpdated" or "LastUpdatedBy" or "SchemaVersion" or "SyncEnabled"
                    or "Global" or "Desktop" or "Mobile" or "Tv" or "NeedsMigration") continue;

                var incomingValue = prop.GetValue(incoming);
                if (incomingValue != null) prop.SetValue(existing, incomingValue);
            }

            return existing;
        }

        private static bool HasAnyProfileValues(MoonfinSettingsProfile profile)
        {
            foreach (var prop in ProfileProps)
                if (prop.GetValue(profile) != null) return true;
            return false;
        }

        internal TimeSpan UpgradeScanRetryDelayForTesting { get; set; } = TimeSpan.FromSeconds(2);
        internal Func<string, MoonfinSettingsProfile?>? AdminDefaultsOverrideForTesting { get; set; }

        public bool IsAnyHideUpgradesEnabled() => IsAnyHideUpgradesEnabled(out _);

        internal bool IsAnyHideUpgradesEnabled(out bool scanIncomplete)
        {
            scanIncomplete = false;
            var config = Plugin.Instance?.Configuration;
            if (config?.DefaultUserSettings?.HideUpgradesInRecentRows == true ||
                config?.GetDefaultsFor("tv")?.HideUpgradesInRecentRows == true ||
                config?.GetDefaultsFor("desktop")?.HideUpgradesInRecentRows == true ||
                config?.GetDefaultsFor("mobile")?.HideUpgradesInRecentRows == true)
            {
                return true;
            }

            while (true)
            {
                int scanGeneration;
                lock (_hideUpgradesCacheGate)
                {
                    var cached = Volatile.Read(ref _userHideUpgradesCache);
                    if (cached != -1)
                    {
                        return cached == 1;
                    }

                    scanGeneration = _hideUpgradesCacheGeneration;
                }

                if (!Directory.Exists(_dataPath))
                {
                    lock (_hideUpgradesCacheGate)
                    {
                        if (_hideUpgradesCacheGeneration == scanGeneration)
                        {
                            Volatile.Write(ref _lastKnownUserHideUpgrades, 0);
                            Volatile.Write(ref _userHideUpgradesCache, 0);
                            return false;
                        }
                    }

                    continue;
                }

                var anyConfirmedEnabled = false;
                var anyBackupEnabled = false;
                var hadAccessError = false;
                foreach (var file in Directory.EnumerateFiles(_dataPath, "*.json"))
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    if (!Guid.TryParse(fileName, out _))
                    {
                        continue;
                    }

                    var settings = ReadSettingsWithRecovery(file, out var accessError);
                    if (accessError != null)
                    {
                        hadAccessError = true;
                        _logger.Debug("Could not scan HideUpgrades preference from " + file + ": " + accessError.Message);
                    }

                    if (settings == null)
                    {
                        continue;
                    }

                    var hasEnabled =
                        settings.Global?.HideUpgradesInRecentRows == true ||
                        settings.Tv?.HideUpgradesInRecentRows == true ||
                        settings.Desktop?.HideUpgradesInRecentRows == true ||
                        settings.Mobile?.HideUpgradesInRecentRows == true;

                    if (hasEnabled)
                    {
                        if (accessError == null)
                        {
                            anyConfirmedEnabled = true;
                            break;
                        }

                        anyBackupEnabled = true;
                    }
                }

                BeforePublishHideUpgradesScanForTesting?.Invoke();

                if (!anyConfirmedEnabled && hadAccessError)
                {
                    scanIncomplete = true;
                    var known = Volatile.Read(ref _lastKnownUserHideUpgrades);
                    if (known != -1)
                    {
                        return known == 1;
                    }

                    var applied = Volatile.Read(ref _lastAppliedUpgradeState);
                    return applied != -1 ? applied == 1 : anyBackupEnabled;
                }

                lock (_hideUpgradesCacheGate)
                {
                    if (_hideUpgradesCacheGeneration == scanGeneration)
                    {
                        Volatile.Write(ref _lastKnownUserHideUpgrades, anyConfirmedEnabled ? 1 : 0);
                        Volatile.Write(ref _userHideUpgradesCache, anyConfirmedEnabled ? 1 : 0);
                        return anyConfirmedEnabled;
                    }
                }
            }
        }

        internal async Task SyncHideWatchedForAffectedUsersAsync(CancellationToken cancellationToken = default)
        {
            var userManager = PluginServices.UserManager;
            if (userManager == null)
            {
                return;
            }

            var users = userManager.GetUserList(new UserQuery());
            if (users == null)
            {
                return;
            }

            foreach (var user in users)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (user == null || user.Id == Guid.Empty)
                {
                    continue;
                }

                var expectedGen = _userSaveGenerations.GetOrAdd(user.Id, 0L);
                MoonfinUserSettings? settings;
                try
                {
                    settings = ReadSettingsWithRecovery(GetUserSettingsPath(user.Id), out var accessError);
                    if (accessError != null)
                    {
                        _logger.Debug("Skipping HidePlayedInLatest default sync for user " + user.Id + ": " + accessError.Message);
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug("Skipping HidePlayedInLatest default sync for user " + user.Id + ": " + ex.Message);
                    continue;
                }

                var effectiveSettings = settings ?? new MoonfinUserSettings();
                var hideWatched = ResolveHideWatchedWithDefaults(effectiveSettings, "global");
                if (hideWatched.HasValue)
                {
                    await SyncNativeHideWatchedAsync(user.Id, hideWatched.Value, expectedGen).ConfigureAwait(false);
                }
            }
        }

        private MoonfinSettingsProfile? GetAdminDefaultsForProfile(string profileName) =>
            AdminDefaultsOverrideForTesting != null
                ? AdminDefaultsOverrideForTesting(profileName)
                : AdminDefaultsFor(profileName);

        /// <summary>
        /// Resolves effective <c>HideWatchedInRecentRows</c> for a user's single native
        /// <c>HidePlayedInLatest</c> toggle. When neither the active/global user profile nor the
        /// admin global default sets a value, device profiles/defaults resolve with <c>true</c> taking
        /// precedence over <c>false</c> across devices so hiding watched items on any device is honored.
        /// </summary>
        internal bool? ResolveHideWatchedWithDefaults(MoonfinUserSettings settings, string? activeProfileName)
        {
            var profileName = string.IsNullOrEmpty(activeProfileName) ? "global" : activeProfileName;
            var resolved = ResolveProfile(settings, profileName);
            if (resolved.HideWatchedInRecentRows.HasValue)
            {
                return resolved.HideWatchedInRecentRows;
            }

            if (settings.Global?.HideWatchedInRecentRows.HasValue == true)
            {
                return settings.Global.HideWatchedInRecentRows;
            }

            var userDeviceValues = new[]
            {
                settings.Tv?.HideWatchedInRecentRows,
                settings.Desktop?.HideWatchedInRecentRows,
                settings.Mobile?.HideWatchedInRecentRows,
            };
            if (userDeviceValues.Any(v => v == true))
            {
                return true;
            }

            if (userDeviceValues.Any(v => v == false))
            {
                return false;
            }

            var activeAdminDefault = GetAdminDefaultsForProfile(profileName)?.HideWatchedInRecentRows;
            if (activeAdminDefault.HasValue)
            {
                return activeAdminDefault;
            }

            var globalAdminDefault = GetAdminDefaultsForProfile("global")?.HideWatchedInRecentRows;
            if (globalAdminDefault.HasValue)
            {
                return globalAdminDefault;
            }

            var adminDeviceDefaults = new[]
            {
                GetAdminDefaultsForProfile("tv")?.HideWatchedInRecentRows,
                GetAdminDefaultsForProfile("desktop")?.HideWatchedInRecentRows,
                GetAdminDefaultsForProfile("mobile")?.HideWatchedInRecentRows,
            };
            if (adminDeviceDefaults.Any(v => v == true))
            {
                return true;
            }

            if (adminDeviceDefaults.Any(v => v == false))
            {
                return false;
            }

            return null;
        }

        private async Task SyncNativeHideWatchedAsync(Guid userId, bool hideWatched, long? expectedSaveGeneration)
        {
            var syncGate = _nativeSyncLocks.GetOrAdd(userId, static _ => new SemaphoreSlim(1, 1));
            BeforeNativeSyncWaitForTesting?.Invoke(userId);
            await syncGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (expectedSaveGeneration.HasValue &&
                    _userSaveGenerations.TryGetValue(userId, out var currentGen) &&
                    currentGen != expectedSaveGeneration.Value)
                {
                    return;
                }

                var userManager = PluginServices.UserManager;
                var user = userManager?.GetUserById(userId);
                if (userManager != null && user != null)
                {
                    var userConfig = userManager.GetUserConfiguration(user);
                    if (userConfig != null && userConfig.HidePlayedInLatest != hideWatched)
                    {
                        userConfig.HidePlayedInLatest = hideWatched;
                        userManager.UpdateConfiguration(user, userConfig);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug("Could not sync HidePlayedInLatest for user " + userId + ": " + ex.Message);
            }
            finally
            {
                syncGate.Release();
            }
        }

        private async Task SyncRecentRowPreferencesAsync(
            Guid userId,
            MoonfinUserSettings settings,
            string? activeProfileName,
            long? expectedSaveGeneration = null)
        {
            var hideWatched = ResolveHideWatchedWithDefaults(settings, activeProfileName);
            if (hideWatched.HasValue)
            {
                await SyncNativeHideWatchedAsync(userId, hideWatched.Value, expectedSaveGeneration).ConfigureAwait(false);
            }

            _ = ScheduleUpgradePreferenceSyncIfNeeded(userId);
        }

        internal Task ScheduleUpgradePreferenceSyncIfNeeded(Guid userId, bool force = false)
        {
            lock (_upgradeSyncGate)
            {
                if (force)
                {
                    _lastAppliedUpgradeState = -1;
                }

                if (_upgradeSyncStopping || _upgradePreferenceChanged == null)
                {
                    return _upgradeSyncWorker;
                }

                _upgradeSyncPending = true;

                if (!_upgradeWorkerRunning)
                {
                    _upgradeWorkerRunning = true;
                    var token = _upgradeSyncCts.Token;
                    _upgradeSyncWorker = Task.Run(() => DrainUpgradePreferenceSyncAsync(userId, token), CancellationToken.None);
                }

                return _upgradeSyncWorker;
            }
        }

        private TimeSpan ComputeUpgradeRetryDelay(int retryAttempt)
        {
            var baseTicks = Math.Max(1L, UpgradeScanRetryDelayForTesting.Ticks);
            var shift = Math.Clamp(retryAttempt, 0, 3);
            var scaledTicks = baseTicks <= (long.MaxValue >> shift) ? baseTicks << shift : long.MaxValue;
            var capTicks = Math.Max(baseTicks, TimeSpan.FromMinutes(1).Ticks);
            return TimeSpan.FromTicks(Math.Min(scaledTicks, capTicks));
        }

        private void ScheduleUpgradeScanRetry(Guid userId, CancellationToken cancellationToken)
        {
            TimeSpan delay;
            lock (_upgradeSyncGate)
            {
                if (_upgradeSyncStopping || cancellationToken.IsCancellationRequested || _upgradeScanRetryScheduled)
                {
                    return;
                }

                _upgradeScanRetryScheduled = true;
                delay = ComputeUpgradeRetryDelay(_upgradeScanRetryCount);
                if (_upgradeScanRetryCount < 16)
                {
                    _upgradeScanRetryCount++;
                }
            }

            _ = Task.Delay(delay, cancellationToken).ContinueWith(
                t =>
                {
                    lock (_upgradeSyncGate)
                    {
                        _upgradeScanRetryScheduled = false;
                    }

                    if (!t.IsCanceled)
                    {
                        _ = ScheduleUpgradePreferenceSyncIfNeeded(userId);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private async Task DrainUpgradePreferenceSyncAsync(Guid userId, CancellationToken cancellationToken)
        {
            while (true)
            {
                lock (_upgradeSyncGate)
                {
                    if (_upgradeSyncStopping || cancellationToken.IsCancellationRequested)
                    {
                        _upgradeWorkerRunning = false;
                        _upgradeSyncPending = false;
                        return;
                    }

                    _upgradeSyncPending = false;
                }

                bool anyEnabled;
                bool scanIncomplete;
                try
                {
                    anyEnabled = IsAnyHideUpgradesEnabled(out scanIncomplete);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    lock (_upgradeSyncGate)
                    {
                        _lastAppliedUpgradeState = -1;
                        _upgradeWorkerRunning = false;
                        _upgradeSyncPending = false;
                        return;
                    }
                }
                catch (Exception ex)
                {
                    lock (_upgradeSyncGate)
                    {
                        _lastAppliedUpgradeState = -1;
                        if (_upgradeSyncPending && !_upgradeSyncStopping && !cancellationToken.IsCancellationRequested)
                        {
                            continue;
                        }

                        _upgradeWorkerRunning = false;
                    }

                    _logger.Debug("Could not scan HideUpgrades preference for user " + userId + ": " + ex.Message);
                    ScheduleUpgradeScanRetry(userId, cancellationToken);
                    return;
                }

                var targetState = anyEnabled ? 1 : 0;

                Func<bool, CancellationToken, Task>? callback;
                var shouldReturn = false;
                var shouldScheduleRetry = false;
                lock (_upgradeSyncGate)
                {
                    if (_upgradeSyncStopping || cancellationToken.IsCancellationRequested)
                    {
                        _upgradeWorkerRunning = false;
                        _upgradeSyncPending = false;
                        return;
                    }

                    callback = _upgradePreferenceChanged;
                    if (callback == null)
                    {
                        _upgradeWorkerRunning = false;
                        _upgradeSyncPending = false;
                        return;
                    }

                    if (_lastAppliedUpgradeState == targetState)
                    {
                        if (!scanIncomplete)
                        {
                            _upgradeScanRetryCount = 0;
                        }

                        if (_upgradeSyncPending)
                        {
                            continue;
                        }

                        _upgradeWorkerRunning = false;
                        shouldScheduleRetry = scanIncomplete;
                        shouldReturn = true;
                    }
                }

                if (shouldReturn)
                {
                    if (shouldScheduleRetry)
                    {
                        ScheduleUpgradeScanRetry(userId, cancellationToken);
                    }

                    return;
                }

                try
                {
                    await callback(targetState == 1, cancellationToken).ConfigureAwait(false);

                    var scheduleRetryForIncomplete = false;
                    lock (_upgradeSyncGate)
                    {
                        if (_upgradeSyncStopping || cancellationToken.IsCancellationRequested)
                        {
                            _lastAppliedUpgradeState = -1;
                            _upgradeWorkerRunning = false;
                            _upgradeSyncPending = false;
                            return;
                        }

                        _lastAppliedUpgradeState = targetState;
                        if (!scanIncomplete)
                        {
                            _upgradeScanRetryCount = 0;
                        }
                        else if (!_upgradeSyncPending)
                        {
                            _upgradeWorkerRunning = false;
                            scheduleRetryForIncomplete = true;
                        }
                    }

                    if (scheduleRetryForIncomplete)
                    {
                        ScheduleUpgradeScanRetry(userId, cancellationToken);
                        return;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    lock (_upgradeSyncGate)
                    {
                        _lastAppliedUpgradeState = -1;
                        _upgradeWorkerRunning = false;
                        _upgradeSyncPending = false;
                        return;
                    }
                }
                catch (Exception ex)
                {
                    lock (_upgradeSyncGate)
                    {
                        _lastAppliedUpgradeState = -1;
                        if (_upgradeSyncPending && !_upgradeSyncStopping && !cancellationToken.IsCancellationRequested)
                        {
                            continue;
                        }

                        _upgradeWorkerRunning = false;
                    }

                    _logger.Debug("Could not apply upgrade date preference for user " + userId + ": " + ex.Message);
                    ScheduleUpgradeScanRetry(userId, cancellationToken);
                    return;
                }
            }
        }

        internal async Task<bool> StopUpgradePreferenceSyncAsync(CancellationToken cancellationToken = default)
        {
            Task workerToDrain;
            CancellationTokenSource ctsToCancel;
            lock (_upgradeSyncGate)
            {
                _upgradeSyncStopping = true;
                _upgradeSyncPending = false;
                _upgradePreferenceChanged = null;
                ctsToCancel = _upgradeSyncCts;
                workerToDrain = _upgradeSyncWorker;
            }

            try
            {
                ctsToCancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            if (!workerToDrain.IsCompleted)
            {
                var completed = await Task.WhenAny(workerToDrain, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
                if (completed != workerToDrain)
                {
                    return false;
                }

                await workerToDrain.ConfigureAwait(false);
            }

            return true;
        }

        internal bool StopUpgradePreferenceSync(TimeSpan? timeout = null)
        {
            using (var drainTimeout = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5)))
            {
                return StopUpgradePreferenceSyncAsync(drainTimeout.Token).GetAwaiter().GetResult();
            }
        }

        public void Dispose()
        {
            if (!StopUpgradePreferenceSync(TimeSpan.FromSeconds(5)))
            {
                _logger.Warn("Media upgrade preference sync worker did not finish before settings service disposal");
            }

            lock (_upgradeSyncGate)
            {
                _upgradeSyncCts.Dispose();
            }
        }
    }
}
