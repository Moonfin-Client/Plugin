using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>Outcome of an RDB update check.</summary>
/// <param name="ReopenPending">True when the cached file was replaced by one that matches games differently and its Missing artwork isn't reopened yet.</param>
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

    // A platform checked more recently than this isn't asked again at startup, so frequent restarts
    // don't keep fetching the same files.
    internal TimeSpan UpdateCheckIntervalForTests { get; set; } = TimeSpan.FromDays(7);

    private enum FetchKind { NotModified, NoSource, Fetched }

    private readonly record struct FetchOutcome(FetchKind Kind, string? Etag = null, string? LastModified = null);

    private enum Verdict { Rejected, Identical, Changed }

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
        _checks.GetOrAdd(platform, _ => NewCheck(platform, config, force: false)).Value;

    /// <summary>
    /// Checks a platform now, ignoring the check interval, or joins a check already running for it.
    /// It never throws.
    /// </summary>
    internal Task<RdbCheckResult> CheckForUpdateNowAsync(string platform, PluginConfiguration config)
    {
        while (true)
        {
            if (!_checks.TryGetValue(platform, out var current))
            {
                var first = NewCheck(platform, config, force: true);
                if (_checks.TryAdd(platform, first))
                {
                    return first.Value;
                }

                continue;
            }

            var running = current.Value;
            if (!running.IsCompleted)
            {
                return running;
            }

            var fresh = NewCheck(platform, config, force: true);
            if (_checks.TryUpdate(platform, fresh, current))
            {
                return fresh.Value;
            }
        }
    }

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

    private Lazy<Task<RdbCheckResult>> NewCheck(string platform, PluginConfiguration config, bool force) =>
        new(() => Task.Run(() => CheckForUpdateAsync(platform, config, force)), LazyThreadSafetyMode.ExecutionAndPublication);

    private async Task<RdbCheckResult> CheckForUpdateAsync(string platform, PluginConfiguration config, bool force)
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
            if (!string.IsNullOrWhiteSpace(baseLocation) && (force || !IsHttp(baseLocation) || IsCheckDue(sidecarPath)))
            {
                await CheckSourceAsync(platform, baseLocation, localPath, sidecarPath).ConfigureAwait(false);
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

    private bool IsCheckDue(string sidecarPath) =>
        ReadSidecar(sidecarPath)?.CheckedUtc is not { } checkedUtc ||
        DateTime.UtcNow - checkedUtc >= UpdateCheckIntervalForTests;

    private static bool IsHttp(string baseLocation) => baseLocation.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    private async Task CheckSourceAsync(string platform, string baseLocation, string localPath, string sidecarPath)
    {
        var sidecar = ReadSidecar(sidecarPath) ?? new RdbSidecar();
        var candidate = localPath + ".update";
        try
        {
            var outcome = await FetchCandidateAsync(platform, baseLocation, candidate, sidecar).ConfigureAwait(false);
            if (outcome.Kind == FetchKind.NotModified)
            {
                sidecar.CheckedUtc = DateTime.UtcNow;
                lock (_sidecarLock)
                {
                    WriteSidecar(sidecarPath, sidecar);
                }
            }
            else if (outcome.Kind == FetchKind.Fetched)
            {
                ValidateAndApply(platform, candidate, localPath, sidecarPath, sidecar, outcome.Etag, outcome.LastModified);
            }
        }
        finally
        {
            TryDelete(candidate);
        }
    }

    // Fetches the configured source into candidatePath, sending the sidecar's validators for HTTP.
    private async Task<FetchOutcome> FetchCandidateAsync(
        string platform,
        string baseLocation,
        string candidatePath,
        RdbSidecar sidecar)
    {
        if (!IsHttp(baseLocation))
        {
            var source = Path.Combine(baseLocation, platform + ".rdb");
            if (!File.Exists(source))
            {
                return new FetchOutcome(FetchKind.NoSource);
            }

            if (new FileInfo(source).Length > RdbReader.MaxRdbFileBytes)
            {
                throw new RdbTooLargeException(new FileInfo(source).Length, RdbReader.MaxRdbFileBytes);
            }

            File.Copy(source, candidatePath, overwrite: true);
            return new FetchOutcome(FetchKind.Fetched);
        }

        using var deadline = new CancellationTokenSource(UpdateTimeoutForTests);
        var downloadToken = deadline.Token;
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

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, downloadToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
        {
            return new FetchOutcome(FetchKind.NotModified);
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } contentLength && contentLength > RdbReader.MaxRdbFileBytes)
        {
            throw new RdbTooLargeException(contentLength, RdbReader.MaxRdbFileBytes);
        }

        await using (var responseStream = await response.Content.ReadAsStreamAsync(downloadToken).ConfigureAwait(false))
        await using (var file = File.Create(candidatePath))
        {
            await CopyToLimitedAsync(responseStream, file, RdbReader.MaxRdbFileBytes, downloadToken).ConfigureAwait(false);
        }

        return new FetchOutcome(
            FetchKind.Fetched,
            response.Headers.ETag?.ToString(),
            response.Content.Headers.NonValidated.TryGetValues("Last-Modified", out var modified) ? modified.ToString() : null);
    }

    // A replacement is usable only if it parses completely and is not drastically smaller than the
    // current file.
    private Verdict Judge(
        string platform,
        string candidate,
        string localPath,
        RdbSidecar sidecar,
        out int newCount,
        out string newSha,
        out string currentSha)
    {
        newSha = string.Empty;
        currentSha = string.Empty;
        try
        {
            newCount = RdbReader.ReadAllComplete(candidate).Count;
        }
        catch (Exception ex) when (ex is FormatException or RdbTooLargeException or IOException)
        {
            newCount = 0;
            _logger.LogInformation(ex, "Rejected downloaded {Platform} game metadata; keeping the cached copy", platform);
            return Verdict.Rejected;
        }

        newSha = Sha256Of(candidate);
        currentSha = Sha256Of(localPath);
        if (string.Equals(newSha, currentSha, StringComparison.Ordinal))
        {
            return Verdict.Identical;
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
            return Verdict.Rejected;
        }

        return Verdict.Changed;
    }

    // The reopen marker is written before the replace so an interruption between the two is
    // recoverable at the next start.
    private void ValidateAndApply(
        string platform,
        string candidate,
        string localPath,
        string sidecarPath,
        RdbSidecar sidecar,
        string? etag,
        string? lastModified)
    {
        var verdict = Judge(platform, candidate, localPath, sidecar, out var newCount, out var newSha, out var currentSha);
        var now = DateTime.UtcNow;
        if (verdict != Verdict.Changed)
        {
            // A rejected copy keeps its validators too, so the next check asks for it conditionally
            // instead of downloading the same file again.
            sidecar.Etag = etag ?? sidecar.Etag;
            sidecar.LastModified = lastModified ?? sidecar.LastModified;
            if (verdict == Verdict.Identical)
            {
                sidecar.Sha256 = currentSha;
                sidecar.RecordCount = newCount;
            }

            sidecar.CheckedUtc = now;
            lock (_sidecarLock)
            {
                WriteSidecar(sidecarPath, sidecar);
            }

            return;
        }

        var matchingChanged = MatchingChanged(platform, localPath, candidate);
        lock (_sidecarLock)
        {
            // Artwork is reopened only when the new file can match a game differently. A reopen still
            // owed from an earlier replacement carries over to this file.
            sidecar.PendingReopenDigest = matchingChanged || sidecar.PendingReopenDigest != null ? newSha : null;
            WriteSidecar(sidecarPath, sidecar);
            File.Replace(candidate, localPath, destinationBackupFileName: null);
            sidecar.Etag = etag;
            sidecar.LastModified = lastModified;
            sidecar.Sha256 = newSha;
            sidecar.RecordCount = newCount;
            sidecar.CheckedUtc = now;
            WriteSidecar(sidecarPath, sidecar);
        }

        DropReplacedIndex(platform);
        _logger.LogInformation("Updated game metadata for {Platform} ({Records} records)", platform, newCount);
    }

    // Lookups only read the checksum and name keys and the names they lead to, so when both maps
    // agree no game can resolve to a different name.
    private bool MatchingChanged(string platform, string currentPath, string replacementPath)
    {
        try
        {
            var current = _indexes.TryGetValue(platform, out var live) ? live : BuildIndex(currentPath);
            var replacement = BuildIndex(replacementPath);
            return !SameNames(current.ByCrc, replacement.ByCrc) || !SameNames(current.ByName, replacement.ByName);
        }
        catch (Exception ex) when (ex is FormatException or RdbTooLargeException or IOException)
        {
            return true;
        }
    }

    private static bool SameNames<TKey>(IReadOnlyDictionary<TKey, RdbRecord> a, IReadOnlyDictionary<TKey, RdbRecord> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (var (key, record) in a)
        {
            if (!b.TryGetValue(key, out var other) || !string.Equals(record.Name, other.Name, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
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
