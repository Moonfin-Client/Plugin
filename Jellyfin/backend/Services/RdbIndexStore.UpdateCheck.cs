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
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _stageGates = new(StringComparer.Ordinal);

    // Platforms whose staged file already existed when this process first looked. It is read before
    // this process can write one, so a file staged in this run is never applied in this run.
    private readonly Lazy<IReadOnlySet<string>> _inheritedStaged;

    internal TimeSpan UpdateTimeoutForTests { get; set; } = TimeSpan.FromSeconds(30);

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
            if (_inheritedStaged.Value.Contains(platform))
            {
                ApplyInheritedStaged(platform, localPath, sidecarPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Applying staged {Platform} game metadata failed; using the cached copy", platform);
        }

        try
        {
            var baseLocation = config.GamesMetadataDbUrlBase;
            if (!string.IsNullOrWhiteSpace(baseLocation))
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

    private async Task CheckSourceAsync(string platform, string baseLocation, string localPath, string sidecarPath)
    {
        var sidecar = ReadSidecar(sidecarPath) ?? new RdbSidecar();
        var candidate = localPath + ".update";
        try
        {
            var outcome = await FetchCandidateAsync(platform, baseLocation, candidate, sidecar, CancellationToken.None).ConfigureAwait(false);
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
        RdbSidecar sidecar,
        CancellationToken cancellationToken)
    {
        if (!baseLocation.StartsWith("http", StringComparison.OrdinalIgnoreCase))
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

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(UpdateTimeoutForTests);
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
        if (verdict == Verdict.Rejected)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (verdict == Verdict.Identical)
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

    // Applies a file staged by an earlier run with the same replace-and-mark sequence as a download,
    // or deletes it if it no longer validates.
    private void ApplyInheritedStaged(string platform, string localPath, string sidecarPath)
    {
        var next = localPath + ".next";
        var nextMeta = next + ".json";
        if (!File.Exists(next))
        {
            return;
        }

        var sidecar = ReadSidecar(sidecarPath) ?? new RdbSidecar();
        if (Judge(platform, next, localPath, sidecar, out var newCount, out var newSha, out _) != Verdict.Changed)
        {
            TryDelete(next);
            TryDelete(nextMeta);
            return;
        }

        var meta = ReadSidecar(nextMeta);
        lock (_sidecarLock)
        {
            sidecar.PendingReopenDigest = newSha;
            WriteSidecar(sidecarPath, sidecar);
            File.Replace(next, localPath, localPath + ".prev");
            sidecar.Etag = meta?.Etag;
            sidecar.LastModified = meta?.LastModified;
            sidecar.Sha256 = newSha;
            sidecar.RecordCount = newCount;
            sidecar.CheckedUtc = DateTime.UtcNow;
            WriteSidecar(sidecarPath, sidecar);
        }

        TryDelete(nextMeta);
        _logger.LogInformation("Applied staged game metadata for {Platform} ({Records} records)", platform, newCount);
    }

    /// <summary>
    /// Fetches a newer RDB and stages it as <c>.rdb.next</c> beside the live file. The live file, index
    /// and sidecar are untouched; the next start's update check applies it. True only when a changed,
    /// valid file was staged. Never throws except on cancellation.
    /// </summary>
    internal async Task<bool> StageUpdateAsync(string platform, PluginConfiguration config, CancellationToken cancellationToken)
    {
        // Captured before anything here can write a staged file.
        _ = _inheritedStaged.Value;
        try
        {
            await EnsureUpdateCheckedAsync(platform, config).WaitAsync(cancellationToken).ConfigureAwait(false);
            var localPath = LocalPath(platform);
            var baseLocation = config.GamesMetadataDbUrlBase;
            if (localPath == null || !File.Exists(localPath) || string.IsNullOrWhiteSpace(baseLocation))
            {
                return false;
            }

            var gate = _stageGates.GetOrAdd(platform, static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await StageLockedAsync(platform, baseLocation, localPath, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Staging {Platform} game metadata failed; nothing was staged", platform);
            return false;
        }
    }

    private async Task<bool> StageLockedAsync(string platform, string baseLocation, string localPath, CancellationToken cancellationToken)
    {
        var sidecarPath = SidecarPath(platform)!;
        var next = localPath + ".next";
        var candidate = next + ".tmp";
        var metaTemp = next + ".json.tmp";
        try
        {
            var sidecar = ReadSidecar(sidecarPath) ?? new RdbSidecar();
            var outcome = await FetchCandidateAsync(platform, baseLocation, candidate, sidecar, cancellationToken).ConfigureAwait(false);
            if (outcome.Kind == FetchKind.NoSource)
            {
                return false;
            }

            var verdict = outcome.Kind == FetchKind.NotModified
                ? Verdict.Identical
                : Judge(platform, candidate, localPath, sidecar, out _, out _, out _);
            if (verdict == Verdict.Rejected)
            {
                return false;
            }

            if (verdict == Verdict.Identical)
            {
                // Upstream matches the live file, so an earlier staged copy is out of date.
                TryDelete(next);
                TryDelete(next + ".json");
                return false;
            }

            var stagedSha = Sha256Of(candidate);
            var stagedCount = TryCountRecords(candidate);
            File.Move(candidate, next, overwrite: true);
            File.WriteAllText(
                metaTemp,
                JsonSerializer.Serialize(
                    new RdbSidecar
                    {
                        Etag = outcome.Etag,
                        LastModified = outcome.LastModified,
                        Sha256 = stagedSha,
                        RecordCount = stagedCount,
                        CheckedUtc = DateTime.UtcNow,
                    },
                    SidecarJson));
            File.Move(metaTemp, next + ".json", overwrite: true);
            _logger.LogInformation("Staged game metadata for {Platform} ({Records} records); it applies at the next start", platform, stagedCount);
            return true;
        }
        finally
        {
            TryDelete(candidate);
            TryDelete(metaTemp);
        }
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
