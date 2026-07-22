using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Emby.Plugins.Moonfin.Models;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.Moonfin.Services
{
    public class MoonfinSettingsService
    {
        // Websocket frame name shared with the client. The frame's Data carries the event object.
        private const string EventMessageName = "MoonfinEvent";

        private readonly string _dataPath;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly ILogger _logger;
        private static readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        // Cached once. GetProperties() is called on the warm settings-resolve path.
        private static readonly System.Reflection.PropertyInfo[] ProfileProps = typeof(MoonfinSettingsProfile).GetProperties();
        private static readonly System.Reflection.PropertyInfo[] UserSettingsProps = typeof(MoonfinUserSettings).GetProperties();

        public MoonfinSettingsService(ILogger logger)
        {
            _logger = logger;
            _dataPath = Plugin.Instance?.DataFolderPath
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
        private MoonfinUserSettings? ReadSettingsWithRecovery(string filePath)
        {
            return AtomicFile.ReadWithRecovery(
                filePath,
                json => JsonSerializer.Deserialize<MoonfinUserSettings>(json, _jsonOptions));
        }

        public async Task<MoonfinUserSettings?> GetUserSettingsAsync(Guid userId)
        {
            var filePath = GetUserSettingsPath(userId);

            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var settings = await Task.Run(() => ReadSettingsWithRecovery(filePath)).ConfigureAwait(false);
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
            catch (Exception ex)
            {
                _logger.ErrorException("Error reading settings for user " + userId, ex);
                return null;
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

        public MoonfinSettingsProfile ResolveProfile(MoonfinUserSettings settings, string profileName)
        {
            var global = settings.Global;
            var deviceProfile = !string.IsNullOrEmpty(profileName) && !string.Equals(profileName, "global", StringComparison.OrdinalIgnoreCase)
                ? settings.GetProfile(profileName) : null;
            var adminDefaults = Plugin.Instance?.Configuration?.DefaultUserSettings;

            var resolved = new MoonfinSettingsProfile();
            var properties = ProfileProps;
            foreach (var prop in properties)
            {
                var value = deviceProfile != null ? prop.GetValue(deviceProfile) : null;
                if (value == null) value = global != null ? prop.GetValue(global) : null;
                if (value == null) value = adminDefaults != null ? prop.GetValue(adminDefaults) : null;
                if (value != null) prop.SetValue(resolved, value);
            }

            // Normalize the legacy web rating-source id to the client's expected key.
            if (resolved.MdblistRatingSources != null)
            {
                for (var i = 0; i < resolved.MdblistRatingSources.Count; i++)
                {
                    if (string.Equals(resolved.MdblistRatingSources[i], "rtAudience", StringComparison.OrdinalIgnoreCase))
                    {
                        resolved.MdblistRatingSources[i] = "tomatoes_audience";
                    }
                }
            }

            // Home layout (sections + row order) resolves as a unit from a single profile layer
            // rather than field-by-field, so a client's sections and order never come from
            // different profiles.
            var homeLayout = ResolveHomeLayout(deviceProfile, global, adminDefaults);
            resolved.HomeSections = homeLayout.HomeSections;
            resolved.HomeRowOrder = homeLayout.HomeRowOrder;

            if (string.IsNullOrWhiteSpace(resolved.TmdbApiKey))
            {
                resolved.TmdbApiKey = Plugin.Instance?.Configuration?.TmdbApiKey;
            }

            if (string.IsNullOrWhiteSpace(resolved.MdblistApiKey))
            {
                resolved.MdblistApiKey = Plugin.Instance?.Configuration?.MdblistApiKey;
            }

            return resolved;
        }

        private static (List<MoonfinHomeSectionConfig>? HomeSections, List<string>? HomeRowOrder) ResolveHomeLayout(
            MoonfinSettingsProfile? deviceProfile,
            MoonfinSettingsProfile? global,
            MoonfinSettingsProfile? adminDefaults)
        {
            foreach (var profile in new[] { deviceProfile, global, adminDefaults })
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
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                MoonfinUserSettings finalSettings;
                if (mergeMode == "merge")
                {
                    var existingSettings = await Task.Run(() => ReadSettingsWithRecovery(filePath)).ConfigureAwait(false);
                    if (existingSettings != null && existingSettings.NeedsMigration)
                        existingSettings = MigrateV1ToV2(existingSettings);
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

                MoveContentHidingToGlobal(finalSettings);

                var json = JsonSerializer.Serialize(finalSettings, _jsonOptions);
                await Task.Run(() => AtomicFile.WriteAllText(filePath, json)).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }

            NotifySettingsChanged(userId);
        }

        public async Task SaveProfileAsync(Guid userId, string profileName, MoonfinSettingsProfile profile, string? clientId = null, bool notifySettingsChanged = true)
        {
            var filePath = GetUserSettingsPath(userId);
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var settings = await Task.Run(() => ReadSettingsWithRecovery(filePath)).ConfigureAwait(false)
                    ?? new MoonfinUserSettings();
                if (settings.NeedsMigration) settings = MigrateV1ToV2(settings);

                var existingProfile = string.Equals(profileName, "global", StringComparison.OrdinalIgnoreCase)
                    ? settings.Global
                    : settings.GetProfile(profileName);

                if (existingProfile != null)
                    MergeProfile(existingProfile, profile);
                else
                    settings.SetProfile(profileName, profile);

                StripServerWideKeys(settings);
                settings.LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                settings.LastUpdatedBy = clientId ?? "unknown";
                settings.SchemaVersion = 2;

                MoveContentHidingToGlobal(settings);

                var serialized = JsonSerializer.Serialize(settings, _jsonOptions);
                await Task.Run(() => AtomicFile.WriteAllText(filePath, serialized)).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }

            if (notifySettingsChanged) NotifySettingsChanged(userId);
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

        /// <summary>Merges admin defaults into every existing user's global profile (keeps their other values/overrides).</summary>
        public async Task<int> MergeDefaultsToAllUsersAsync(MoonfinSettingsProfile defaults)
        {
            if (defaults == null) throw new ArgumentNullException("defaults");
            if (!HasAnyProfileValues(defaults)) return 0;

            EnsureDataDirectory();
            var usersUpdated = 0;
            foreach (var filePath in Directory.EnumerateFiles(_dataPath, "*.json", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileNameWithoutExtension(filePath);
                if (!Guid.TryParse(fileName, out var userId)) continue;
                try
                {
                    await MergeDefaultsToUserAsync(userId, defaults).ConfigureAwait(false);
                    usersUpdated++;
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Failed to push admin defaults for user " + userId, ex);
                }
            }
            return usersUpdated;
        }

        /// <summary>Merges admin defaults into one user's global profile.</summary>
        public Task MergeDefaultsToUserAsync(Guid userId, MoonfinSettingsProfile defaults)
        {
            if (defaults == null) throw new ArgumentNullException("defaults");
            return SaveProfileAsync(userId, "global", defaults, "admin-default-push", notifySettingsChanged: false);
        }

        /// <summary>Resets every server user to a clean defaults-only profile and deletes orphaned files for users that no longer exist.</summary>
        public async Task<(int UsersAffected, int OrphansDeleted)> ResetAllUsersToDefaultsAsync(
            MoonfinSettingsProfile defaults, IReadOnlyCollection<Guid> serverUserIds, bool deleteOrphans)
        {
            if (defaults == null) throw new ArgumentNullException("defaults");
            EnsureDataDirectory();

            var serverSet = new HashSet<Guid>(serverUserIds);
            var usersAffected = 0;

            foreach (var userId in serverSet)
            {
                try { await ResetUserToDefaultsAsync(userId, defaults).ConfigureAwait(false); usersAffected++; }
                catch (Exception ex) { _logger.ErrorException("Failed to reset user " + userId, ex); }
            }

            var orphansDeleted = 0;
            if (deleteOrphans)
            {
                foreach (var filePath in Directory.EnumerateFiles(_dataPath, "*.json", SearchOption.TopDirectoryOnly))
                {
                    var fileName = Path.GetFileNameWithoutExtension(filePath);
                    if (!Guid.TryParse(fileName, out var userId) || serverSet.Contains(userId)) continue;
                    try { AtomicFile.DeleteWithSidecars(filePath); orphansDeleted++; }
                    catch (Exception ex) { _logger.ErrorException("Failed to delete orphan settings " + userId, ex); }
                }
            }

            return (usersAffected, orphansDeleted);
        }

        /// <summary>Replaces a single user's settings with a clean global-only profile equal to the defaults.</summary>
        public async Task ResetUserToDefaultsAsync(Guid userId, MoonfinSettingsProfile defaults)
        {
            if (defaults == null) throw new ArgumentNullException("defaults");

            var clean = new MoonfinUserSettings
            {
                SchemaVersion = 2,
                SyncEnabled = true,
                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                LastUpdatedBy = "admin-default-reset",
                Global = CloneProfile(defaults)
            };

            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                var filePath = GetUserSettingsPath(userId);
                var json = JsonSerializer.Serialize(clean, _jsonOptions);
                await Task.Run(() => AtomicFile.WriteAllText(filePath, json)).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }

            NotifySettingsChanged(userId);
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
            }
            finally
            {
                _lock.Release();
            }
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
            }
            finally
            {
                _lock.Release();
            }
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

        private void MoveContentHidingToGlobal(MoonfinUserSettings settings)
        {
            if (settings.Global == null)
            {
                settings.Global = new MoonfinSettingsProfile();
            }

            // Content hiding is a global preference, so lift each device's hidden lists into the
            // global profile. Union across devices so a file that hid items on more than one device
            // keeps them all instead of the last device winning.
            var devices = new[] { settings.Desktop, settings.Mobile, settings.Tv };

            var hiddenContinueWatching = UnionHiddenEntries(devices, p => p.HiddenContinueWatchingItems);
            if (hiddenContinueWatching != null)
            {
                settings.Global.HiddenContinueWatchingItems = hiddenContinueWatching;
            }

            var hiddenNextUp = UnionHiddenEntries(devices, p => p.HiddenNextUpSeries);
            if (hiddenNextUp != null)
            {
                settings.Global.HiddenNextUpSeries = hiddenNextUp;
            }

            foreach (var device in devices)
            {
                if (device == null) continue;
                device.HiddenContinueWatchingItems = null;
                device.HiddenNextUpSeries = null;
            }
        }

        private static string? UnionHiddenEntries(
            MoonfinSettingsProfile?[] devices,
            Func<MoonfinSettingsProfile, string?> selector)
        {
            Dictionary<string, string>? merged = null;
            foreach (var device in devices)
            {
                if (device == null) continue;
                var value = selector(device);
                if (value == null) continue;

                if (merged == null)
                {
                    merged = new Dictionary<string, string>();
                }
                foreach (var pair in ParseHiddenEntries(value))
                {
                    merged[pair.Key] = pair.Value;
                }
            }

            return merged == null ? null : JsonSerializer.Serialize(merged);
        }

        private static Dictionary<string, string> ParseHiddenEntries(string value)
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
    }
}
