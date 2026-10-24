using System;
using System.IO;
using System.Text.Json;
using MediaBrowser.Model.Logging;
using Moonfin.Pin;

namespace Emby.Plugins.Moonfin.Services
{
    /// <summary>PIN records as one file per user under the plugin data folder.</summary>
    public class FilePinStore : IPinStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
        };

        private readonly string _directory;
        private readonly ILogger _logger;

        public FilePinStore(string directory, ILogger logger)
        {
            _directory = directory;
            _logger = logger;
        }

        private string PathFor(Guid userId) => Path.Combine(_directory, userId.ToString("N") + ".json");

        public PinRecord? Load(Guid userId)
        {
            try
            {
                return AtomicFile.ReadWithRecovery(
                    PathFor(userId),
                    json => JsonSerializer.Deserialize<PinRecord>(json, JsonOptions));
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Failed to read the PIN record for user " + userId.ToString("N"), ex);
                return null;
            }
        }

        public void Save(Guid userId, PinRecord record)
        {
            Directory.CreateDirectory(_directory);
            AtomicFile.WriteAllText(PathFor(userId), JsonSerializer.Serialize(record, JsonOptions));
        }

        public void Delete(Guid userId) => AtomicFile.DeleteWithSidecars(PathFor(userId));
    }
}
