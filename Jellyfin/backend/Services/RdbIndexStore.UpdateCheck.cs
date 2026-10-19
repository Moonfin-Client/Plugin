using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>Outcome of the per-process RDB update check.</summary>
/// <param name="ReopenPending">True when the cached file was replaced and its Missing artwork is not reopened yet.</param>
/// <param name="Digest">SHA-256 of the replaced file; pass to <see cref="RdbIndexStore.ClearPendingReopen"/> once reopened.</param>
internal sealed record RdbCheckResult(bool ReopenPending, string? Digest)
{
    internal static readonly RdbCheckResult None = new(false, null);
}

internal sealed partial class RdbIndexStore
{
    // A replacement RDB with fewer than this share of the current records is treated as damaged.
    private const int MinRecordPercentOfCurrent = 90;

    private static readonly JsonSerializerOptions SidecarJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly ConcurrentDictionary<string, Lazy<Task<RdbCheckResult>>> _checks = new(StringComparer.Ordinal);
    private readonly object _sidecarLock = new();

    internal TimeSpan UpdateTimeoutForTests { get; set; } = TimeSpan.FromSeconds(30);

    internal sealed class RdbSidecar
    {
        public string? Etag { get; set; }

        public string? LastModified { get; set; }

        public string? Sha256 { get; set; }

        public int? RecordCount { get; set; }

        public DateTime? CheckedUtc { get; set; }

        public string? PendingReopenDigest { get; set; }
    }

    /// <summary>
    /// The shared once-per-process check for a newer RDB. It never throws; every failure leaves the
    /// current file in place.
    /// </summary>
    internal Task<RdbCheckResult> EnsureUpdateCheckedAsync(string platform, PluginConfiguration config) =>
        _checks.GetOrAdd(
            platform,
            _ => new Lazy<Task<RdbCheckResult>>(
                () => Task.Run(() => CheckForUpdateAsync(platform, config)),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>Clears the reopen marker only if it still holds <paramref name="digest"/>.</summary>
    internal void ClearPendingReopen(string platform, string digest)
    {
        var path = SidecarPath(platform);
        if (path == null)
        {
            return;
        }

        lock (_sidecarLock)
        {
            var sidecar = ReadSidecar(path);
            if (sidecar != null && string.Equals(sidecar.PendingReopenDigest, digest, StringComparison.Ordinal))
            {
                sidecar.PendingReopenDigest = null;
                WriteSidecar(path, sidecar);
            }
        }
    }

    private async Task<RdbCheckResult> CheckForUpdateAsync(string platform, PluginConfiguration config)
    {
        var localPath = LocalPath(platform);
        if (localPath == null || !File.Exists(localPath))
        {
            return RdbCheckResult.None;
        }

        var sidecarPath = SidecarPath(platform)!;
        try
        {
            var baseLocation = config.GamesMetadataDbUrlBase;
            if (!string.IsNullOrWhiteSpace(baseLocation))
            {
                if (baseLocation.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    await CheckHttpAsync(platform, baseLocation, localPath, sidecarPath).ConfigureAwait(false);
                }
                else
                {
                    CheckLocalFolder(platform, baseLocation, localPath, sidecarPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Checking {Platform} game metadata for updates failed; using the cached copy", platform);
        }

        try
        {
            lock (_sidecarLock)
            {
                var sidecar = ReadSidecar(sidecarPath);
                if (sidecar?.PendingReopenDigest is not { Length: > 0 } marker)
                {
                    return RdbCheckResult.None;
                }

                if (string.Equals(marker, Sha256Of(localPath), StringComparison.Ordinal))
                {
                    return new RdbCheckResult(true, marker);
                }

                // The file was never replaced, so there is nothing to reopen.
                sidecar.PendingReopenDigest = null;
                WriteSidecar(sidecarPath, sidecar);
                return RdbCheckResult.None;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading the {Platform} game metadata reopen marker failed", platform);
            return RdbCheckResult.None;
        }
    }

    private async Task CheckHttpAsync(string platform, string baseLocation, string localPath, string sidecarPath)
    {
        using var deadline = new CancellationTokenSource(UpdateTimeoutForTests);
        var sidecar = ReadSidecar(sidecarPath) ?? new RdbSidecar();
        var url = baseLocation.TrimEnd('/') + "/" + Uri.EscapeDataString(platform) + ".rdb";
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Moonfin/1.0");

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(sidecar.Etag))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", sidecar.Etag);
        }

        if (!string.IsNullOrEmpty(sidecar.LastModified))
        {
            request.Headers.TryAddWithoutValidation("If-Modified-Since", sidecar.LastModified);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
        {
            sidecar.CheckedUtc = DateTime.UtcNow;
            lock (_sidecarLock)
            {
                WriteSidecar(sidecarPath, sidecar);
            }

            return;
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } contentLength && contentLength > RdbReader.MaxRdbFileBytes)
        {
            throw new RdbTooLargeException(contentLength, RdbReader.MaxRdbFileBytes);
        }

        var candidate = localPath + ".update";
        try
        {
            await using (var responseStream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false))
            await using (var file = File.Create(candidate))
            {
                await CopyToLimitedAsync(responseStream, file, RdbReader.MaxRdbFileBytes, deadline.Token).ConfigureAwait(false);
            }

            ValidateAndApply(
                platform,
                candidate,
                localPath,
                sidecarPath,
                sidecar,
                response.Headers.ETag?.ToString(),
                response.Content.Headers.NonValidated.TryGetValues("Last-Modified", out var modified) ? modified.ToString() : null);
        }
        finally
        {
            TryDelete(candidate);
        }
    }

    private void CheckLocalFolder(string platform, string baseLocation, string localPath, string sidecarPath)
    {
        var source = Path.Combine(baseLocation, platform + ".rdb");
        if (!File.Exists(source))
        {
            return;
        }

        var sidecar = ReadSidecar(sidecarPath) ?? new RdbSidecar();
        if (new FileInfo(source).Length > RdbReader.MaxRdbFileBytes)
        {
            throw new RdbTooLargeException(new FileInfo(source).Length, RdbReader.MaxRdbFileBytes);
        }

        var candidate = localPath + ".update";
        try
        {
            File.Copy(source, candidate, overwrite: true);
            ValidateAndApply(platform, candidate, localPath, sidecarPath, sidecar, etag: null, lastModified: null);
        }
        finally
        {
            TryDelete(candidate);
        }
    }

    // Accepts a fetched RDB only if it parses completely and is not drastically smaller than the
    // current one. The reopen marker is written before the replace so an interruption between the
    // two is recoverable at the next start.
    private void ValidateAndApply(
        string platform,
        string candidate,
        string localPath,
        string sidecarPath,
        RdbSidecar sidecar,
        string? etag,
        string? lastModified)
    {
        int newCount;
        try
        {
            newCount = RdbReader.ReadAllComplete(candidate).Count;
        }
        catch (Exception ex) when (ex is FormatException or RdbTooLargeException or IOException)
        {
            _logger.LogInformation(ex, "Rejected downloaded {Platform} game metadata; keeping the cached copy", platform);
            return;
        }

        var newSha = Sha256Of(candidate);
        var currentSha = Sha256Of(localPath);
        var now = DateTime.UtcNow;

        if (string.Equals(newSha, currentSha, StringComparison.Ordinal))
        {
            sidecar.Etag = etag ?? sidecar.Etag;
            sidecar.LastModified = lastModified ?? sidecar.LastModified;
            sidecar.Sha256 = currentSha;
            sidecar.RecordCount = newCount;
            sidecar.CheckedUtc = now;
            lock (_sidecarLock)
            {
                WriteSidecar(sidecarPath, sidecar);
            }

            return;
        }

        var currentCount = string.Equals(sidecar.Sha256, currentSha, StringComparison.Ordinal) && sidecar.RecordCount is { } known
            ? known
            : TryCountRecords(localPath);
        if (newCount == 0 || (currentCount > 0 && (long)newCount * 100 < (long)currentCount * MinRecordPercentOfCurrent))
        {
            _logger.LogInformation(
                "Rejected downloaded {Platform} game metadata with {New} records against {Current}; keeping the cached copy",
                platform,
                newCount,
                currentCount);
            return;
        }

        lock (_sidecarLock)
        {
            sidecar.PendingReopenDigest = newSha;
            WriteSidecar(sidecarPath, sidecar);
            File.Replace(candidate, localPath, localPath + ".prev");
            sidecar.Etag = etag;
            sidecar.LastModified = lastModified;
            sidecar.Sha256 = newSha;
            sidecar.RecordCount = newCount;
            sidecar.CheckedUtc = now;
            WriteSidecar(sidecarPath, sidecar);
        }

        _logger.LogInformation("Updated game metadata for {Platform} ({Records} records)", platform, newCount);
    }

    private void RecordFirstDownload(string platform, string localPath, string? etag, string? lastModified)
    {
        // The file is as fresh as a check could make it, so this process needs no further check.
        _checks[platform] = new Lazy<Task<RdbCheckResult>>(Task.FromResult(RdbCheckResult.None));
        try
        {
            var path = SidecarPath(platform);
            if (path == null)
            {
                return;
            }

            lock (_sidecarLock)
            {
                WriteSidecar(path, new RdbSidecar
                {
                    Etag = etag,
                    LastModified = lastModified,
                    Sha256 = Sha256Of(localPath),
                    RecordCount = TryCountRecords(localPath),
                    CheckedUtc = DateTime.UtcNow,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Writing the {Platform} game metadata sidecar failed", platform);
        }
    }

    private static int TryCountRecords(string path)
    {
        try
        {
            return RdbReader.ReadAll(path).Count;
        }
        catch (Exception ex) when (ex is FormatException or RdbTooLargeException or IOException)
        {
            return 0;
        }
    }

    private string? SidecarPath(string platform)
    {
        var local = LocalPath(platform);
        return local == null ? null : local + ".json";
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    // A missing or malformed sidecar is simply absent.
    private static RdbSidecar? ReadSidecar(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<RdbSidecar>(File.ReadAllText(path), SidecarJson)
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static void WriteSidecar(string path, RdbSidecar sidecar)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(sidecar, SidecarJson));
        File.Move(temp, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
