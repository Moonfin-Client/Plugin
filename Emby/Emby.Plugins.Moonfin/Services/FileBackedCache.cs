using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>
    /// Shared scaffold for the file-backed JSON caches: data-path resolution, serializer
    /// options, flush-to-disk, and the lazy double-checked load. Subclasses add their own
    /// typed accessors over the dictionary returned by EnsureLoaded.
    /// </summary>
    public abstract class FileBackedCache<TEntry> where TEntry : class
    {
        private readonly string _cacheFilePath;
        private readonly string _logLabel;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _fileLock = new SemaphoreSlim(1, 1);

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private ConcurrentDictionary<string, TEntry>? _cache;

        protected FileBackedCache(ILogger logger, string fileName, string logLabel)
        {
            _logger = logger;
            _logLabel = logLabel;
            var dataPath = Plugin.Instance?.DataFolderPath
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Emby-Server", "programdata", "plugins", "Moonfin");

            if (!Directory.Exists(dataPath)) Directory.CreateDirectory(dataPath);
            _cacheFilePath = Path.Combine(dataPath, fileName);
        }

        public async Task FlushAsync()
        {
            var cache = _cache;
            if (cache == null) return;

            await _fileLock.WaitAsync().ConfigureAwait(false);
            try
            {
                using var stream = File.Create(_cacheFilePath);
                await JsonSerializer.SerializeAsync(stream, cache, JsonOptions).ConfigureAwait(false);
                _logger.Debug(_logLabel + " cache flushed (" + cache.Count + " entries)");
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Failed to flush " + _logLabel + " cache to disk", ex);
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// Empties the cache and writes the cleared state to disk so it survives a restart.
        /// Returns how many entries were dropped.
        /// </summary>
        public async Task<int> ClearAsync()
        {
            var cache = EnsureLoaded();
            var removed = cache.Count;
            cache.Clear();
            await FlushAsync().ConfigureAwait(false);
            _logger.Info(_logLabel + " cache cleared (" + removed + " entries removed)");
            return removed;
        }

        protected ConcurrentDictionary<string, TEntry> EnsureLoaded()
        {
            if (_cache != null) return _cache;

            _fileLock.Wait();
            try
            {
                if (_cache != null) return _cache;

                if (File.Exists(_cacheFilePath))
                {
                    try
                    {
                        using var stream = File.OpenRead(_cacheFilePath);
                        var loaded = JsonSerializer.Deserialize<Dictionary<string, TEntry>>(stream, JsonOptions);
                        _cache = loaded != null
                            ? new ConcurrentDictionary<string, TEntry>(loaded, StringComparer.OrdinalIgnoreCase)
                            : new ConcurrentDictionary<string, TEntry>(StringComparer.OrdinalIgnoreCase);
                        _logger.Info(_logLabel + " cache loaded (" + _cache.Count + " entries)", 0);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn("Failed to load " + _logLabel + " cache, starting fresh: " + ex.Message);
                        _cache = new ConcurrentDictionary<string, TEntry>(StringComparer.OrdinalIgnoreCase);
                    }
                }
                else
                {
                    _cache = new ConcurrentDictionary<string, TEntry>(StringComparer.OrdinalIgnoreCase);
                }
            }
            finally
            {
                _fileLock.Release();
            }

            return _cache;
        }
    }
}
