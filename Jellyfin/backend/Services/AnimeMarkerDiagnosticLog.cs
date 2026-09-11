using System.Text;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// A dedicated log for working out why a badge is or is not appearing. 
/// It records every marker request a client makes and what it was answered with.
/// </summary>
public class AnimeMarkerDiagnosticLog
{
    /// <summary>
    /// The log file name, so the admin page can tell the user where to look.
    /// </summary>
    private const string LogFileName = "moonfin-anime-markers.log";

    /// <summary>
    /// The maximum size of the log file before it is rotated. The log is for debugging, 
    /// so it is not expected to be large. The log is rotated to a .1 file, which is also deleted when it exceeds
    /// </summary>
    private const long MaxBytes = 2 * 1024 * 1024;

    private readonly ILogger<AnimeMarkerDiagnosticLog> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _path;

    public AnimeMarkerDiagnosticLog(ILogger<AnimeMarkerDiagnosticLog> logger)
    {
        _logger = logger;
        _path = Path.Combine(MoonfinPlugin.ResolveLogFolderPath(), LogFileName);
    }

    public static bool Enabled =>
        MoonfinPlugin.Instance?.Configuration?.AnimeMarkerVerboseLogging == true;

    /// <summary>The log file's path, so the admin page can tell the user where to look.</summary>
    public string Path_ => _path;

    /// <summary>Bytes currently on disk, or null when nothing has been written.</summary>
    public long? SizeBytes => File.Exists(_path) ? new FileInfo(_path).Length : null;

    /// <summary>
    /// Appends one line, timestamped. Does nothing while verbose logging is off, so callers
    /// can call it unconditionally. Never throws: a diagnostic that breaks the request it is
    /// diagnosing would be worse than no diagnostic.
    /// </summary>
    public void Write(string message)
    {
        if (!Enabled)
        {
            return;
        }

        // Mirrored to the server log so the two can be lined up by timestamp.
        _logger.LogInformation("Anime markers: {Message}", message);

        _writeLock.Wait();
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
            {
                File.Move(_path, _path + ".1", overwrite: true);
            }

            File.AppendAllText(
                _path,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}  {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Anime marker diagnostic log could not be written");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Deletes the log so a fresh reproduction starts from an empty file.</summary>
    public async Task<bool> ClearAsync()
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var removed = false;
            foreach (var path in new[] { _path, _path + ".1" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    removed = true;
                }
            }

            return removed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Anime marker diagnostic log could not be cleared");
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>The last <paramref name="lines"/> lines, newest last, for the admin page.</summary>
    public IReadOnlyList<string> Tail(int lines)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return Array.Empty<string>();
            }

            var all = File.ReadAllLines(_path);
            return all.Length <= lines ? all : all[^lines..];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Anime marker diagnostic log could not be read");
            return Array.Empty<string>();
        }
    }
}
