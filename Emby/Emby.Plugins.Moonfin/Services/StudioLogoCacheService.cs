using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// File-backed cache for TMDB studio (production company) logos. Logo images are stored as
    /// PNG files under studio_logos/, and a JSON index tracks each company (name, TMDB logo path,
    /// whether a logo was found) plus a per-item map keyed by "{type}:{tmdbId}". The scheduled
    /// task and the on-demand endpoint both populate it.
    /// </summary>
    public class StudioLogoCacheService
    {
        private readonly string _cacheDir;
        private readonly string _indexFilePath;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _fileLock = new SemaphoreSlim(1, 1);

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private StudioIndex? _index;

        // Guards reads and writes of the in-memory _index dictionaries. The on-demand endpoint
        // and the scheduled sync task both touch them, so SetItem and the flush snapshot must not
        // race. _fileLock stays responsible for disk I/O and the one-time load.
        private readonly object _indexGate = new object();

        public StudioLogoCacheService(ILogger logger)
        {
            _logger = logger;
            var dataPath = Plugin.Instance?.DataFolderPath
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Emby-Server", "programdata", "plugins", "Moonfin");

            _cacheDir = Path.Combine(dataPath, "studio_logos");
            _indexFilePath = Path.Combine(_cacheDir, "index.json");

            if (!Directory.Exists(_cacheDir)) Directory.CreateDirectory(_cacheDir);
        }

        public List<StudioCompanyInfo>? TryGetItem(string type, string tmdbId, TimeSpan maxAge)
        {
            var index = EnsureLoaded();
            lock (_indexGate)
            {
                if (!index.Items.TryGetValue(ItemKey(type, tmdbId), out var item)) return null;
                if (DateTimeOffset.UtcNow - item.CachedAt >= maxAge) return null;

                var companies = new List<StudioCompanyInfo>();
                foreach (var id in item.CompanyIds)
                {
                    if (index.Companies.TryGetValue(id.ToString(), out var c))
                        companies.Add(new StudioCompanyInfo { Id = c.Id, Name = c.Name, HasLogo = c.HasLogo });
                }
                return companies;
            }
        }

        public void SetItem(string type, string tmdbId, List<StudioCompanyEntry> companies)
        {
            var index = EnsureLoaded();
            lock (_indexGate)
            {
                foreach (var company in companies)
                    index.Companies[company.Id.ToString()] = company;
                index.Items[ItemKey(type, tmdbId)] = new StudioItemEntry
                {
                    CompanyIds = companies.Select(c => c.Id).ToList(),
                    CachedAt = DateTimeOffset.UtcNow
                };
            }
        }

        public HashSet<string> GetFreshItemKeys(TimeSpan maxAge)
        {
            var index = EnsureLoaded();
            var cutoff = DateTimeOffset.UtcNow - maxAge;
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (_indexGate)
            {
                foreach (var pair in index.Items)
                    if (pair.Value.CachedAt >= cutoff) keys.Add(pair.Key);
            }
            return keys;
        }

        public bool HasImage(int companyId) => File.Exists(GetImagePath(companyId));

        /// <summary>Networks are cached under a negated id, written as n123.png so no
        /// file name starts with a dash.</summary>
        public static string ImageFileName(int companyId) =>
            companyId < 0 ? "n" + (-companyId) + ".png" : companyId + ".png";

        public string GetImagePath(int companyId) => Path.Combine(_cacheDir, ImageFileName(companyId));

        public string? GetLogoPath(int companyId)
        {
            var index = EnsureLoaded();
            lock (_indexGate)
            {
                return index.Companies.TryGetValue(companyId.ToString(), out var c) ? c.LogoPath : null;
            }
        }

        public void SaveImage(int companyId, byte[] bytes) => File.WriteAllBytes(GetImagePath(companyId), bytes);

        public async Task FlushAsync()
        {
            var index = _index;
            if (index == null) return;

            // Serialize an atomic snapshot under the in-memory gate so a concurrent SetItem cannot
            // mutate the dictionaries mid-serialization, then write the bytes to disk.
            byte[] payload;
            int companyCount, itemCount;
            lock (_indexGate)
            {
                payload = JsonSerializer.SerializeToUtf8Bytes(index, JsonOptions);
                companyCount = index.Companies.Count;
                itemCount = index.Items.Count;
            }

            await _fileLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await Task.Run(() => File.WriteAllBytes(_indexFilePath, payload)).ConfigureAwait(false);
                _logger.Debug("Studio logo index flushed (" + companyCount + " companies, " + itemCount + " items)");
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Failed to flush studio logo index to disk", ex);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        private static string ItemKey(string type, string tmdbId) => type + ":" + tmdbId;

        private StudioIndex EnsureLoaded()
        {
            if (_index != null) return _index;

            _fileLock.Wait();
            try
            {
                if (_index != null) return _index;

                if (File.Exists(_indexFilePath))
                {
                    try
                    {
                        using var stream = File.OpenRead(_indexFilePath);
                        var loaded = JsonSerializer.Deserialize<StudioIndex>(stream, JsonOptions) ?? new StudioIndex();
                        if (loaded.Version != StudioIndex.CurrentVersion)
                        {
                            _logger.Info("Studio logo index is version " + loaded.Version + ", rebuilding at version " + StudioIndex.CurrentVersion, 0);
                            loaded = new StudioIndex();
                        }
                        _index = loaded;
                        _logger.Info("Studio logo index loaded (" + _index.Companies.Count + " companies, " + _index.Items.Count + " items)", 0);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn("Failed to load studio logo index, starting fresh: " + ex.Message);
                        _index = new StudioIndex();
                    }
                }
                else
                {
                    _index = new StudioIndex();
                }
            }
            finally
            {
                _fileLock.Release();
            }

            return _index;
        }
    }

    public class StudioCompanyInfo
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("hasLogo")] public bool HasLogo { get; set; }
    }

    internal class StudioIndex
    {
        /// <summary>Bumped when cached entries need rebuilding rather than reusing.
        /// Version 2 added TV networks, which entries written before it lack, and
        /// gave networks their own negated ids.</summary>
        public const int CurrentVersion = 2;

        [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;
        [JsonPropertyName("companies")] public Dictionary<string, StudioCompanyEntry> Companies { get; set; } = new Dictionary<string, StudioCompanyEntry>();
        [JsonPropertyName("items")] public Dictionary<string, StudioItemEntry> Items { get; set; } = new Dictionary<string, StudioItemEntry>();
    }

    public class StudioCompanyEntry
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("logoPath")] public string? LogoPath { get; set; }
        [JsonPropertyName("hasLogo")] public bool HasLogo { get; set; }
    }

    internal class StudioItemEntry
    {
        [JsonPropertyName("companyIds")] public List<int> CompanyIds { get; set; } = new List<int>();
        [JsonPropertyName("cachedAt")] public DateTimeOffset CachedAt { get; set; }
    }
}
