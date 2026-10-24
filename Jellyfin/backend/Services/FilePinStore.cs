using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moonfin.Pin;

namespace Moonfin.Server.Services;

/// <summary>PIN records as one file per user under the plugin data folder.</summary>
public class FilePinStore : IPinStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _directory;
    private readonly ILogger<FilePinStore> _logger;

    public FilePinStore(ILogger<FilePinStore> logger)
    {
        _logger = logger;
        _directory = Path.Combine(MoonfinPlugin.ResolveDataFolderPath(), "pins");
    }

    private string PathFor(Guid userId) => Path.Combine(_directory, $"{userId:N}.json");

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
            _logger.LogError(ex, "Failed to read the PIN record for user {UserId}", userId);
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
