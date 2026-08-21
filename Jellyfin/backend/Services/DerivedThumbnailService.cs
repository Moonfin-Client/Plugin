using System.Buffers.Binary;
using System.Collections.Concurrent;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Model.Drawing;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Generates and caches size/format-optimized thumbnails from original artwork files.
/// </summary>
internal sealed class DerivedThumbnailService
{
    private const int MaxThumbDimension = 512;
    private const int ThumbQuality = 85;

    // Ceiling on the *decoded* size of an original before it is handed to the encoder, in the same
    // explicit-documented-limit spirit as GameArtworkStore.MaxArtworkBytes. That cap bounds the
    // bytes on disk; it cannot bound what they expand to. PNG is a compressed format, so a file
    // comfortably under 16 MB can still declare a 40000x40000 canvas and cost gigabytes of RAM the
    // moment the encoder decodes it. Real libretro artwork tops out around 2000x2000 (4 MP), and
    // everything here is downscaled to MaxThumbDimension anyway, so 32 MP is generous for genuine
    // art while keeping the worst case to a few hundred MB.
    private const long MaxDecodedPixels = 32L * 1000 * 1000;
    private static readonly (ImageFormat Format, string Extension, string ContentType)[] PreferredDerivedFormats =
    {
        (ImageFormat.Jpg, ".jpg", "image/jpeg"),
        (ImageFormat.Webp, ".webp", "image/webp"),
    };

    private static ReadOnlySpan<byte> PngSignature => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private static readonly TimeSpan DerivedThumbBudget = TimeSpan.FromSeconds(5);
    private readonly ILogger _logger;
    private readonly string _cacheDir;
    private readonly IImageEncoder? _imageEncoder;
    private readonly SemaphoreSlim _encodeSemaphore;
    private readonly TimeSpan _derivedThumbBudget;
    private readonly ConcurrentDictionary<string, Lazy<Task<DerivedThumbnail>>> _derivedInFlight = new(StringComparer.OrdinalIgnoreCase);

    internal DerivedThumbnailService(
        ILogger logger,
        string cacheDir,
        IImageEncoder? imageEncoder,
        int? encodeConcurrencyForTests,
        TimeSpan? derivedThumbBudgetForTests)
    {
        _logger = logger;
        _cacheDir = cacheDir;
        _imageEncoder = imageEncoder;
        _encodeSemaphore = new SemaphoreSlim(encodeConcurrencyForTests ?? Math.Max(1, Environment.ProcessorCount / 2));
        _derivedThumbBudget = derivedThumbBudgetForTests ?? DerivedThumbBudget;
    }

    internal async Task<DerivedThumbnail> GetDerivedThumbAsync(string originalPath, CancellationToken cancellationToken)
    {
        var fallback = DerivedThumbnail.Unsupported(originalPath);
        var encoder = _imageEncoder;
        if (encoder == null || !encoder.SupportsImageEncoding)
        {
            return fallback;
        }

        var chosen = Array.Find(PreferredDerivedFormats, f => encoder.SupportedOutputFormats.Contains(f.Format));
        if (chosen.Extension == null)
        {
            return fallback;
        }

        var derivedPath = Path.Combine(
            _cacheDir,
            Path.GetFileNameWithoutExtension(originalPath) + "_thumb" + chosen.Extension);
        if (File.Exists(derivedPath))
        {
            return DerivedThumbnail.Encoded(derivedPath, chosen.ContentType);
        }

        var lazy = _derivedInFlight.GetOrAdd(
            derivedPath,
            _ => new Lazy<Task<DerivedThumbnail>>(
                () => EncodeDerivedThumbAsync(originalPath, derivedPath, chosen.Format, chosen.ContentType, fallback)));

        _ = lazy.Value.ContinueWith(
            _ => ((ICollection<KeyValuePair<string, Lazy<Task<DerivedThumbnail>>>>)_derivedInFlight)
                .Remove(new KeyValuePair<string, Lazy<Task<DerivedThumbnail>>>(derivedPath, lazy)),
            TaskScheduler.Default);

        try
        {
            using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budgetCts.CancelAfter(_derivedThumbBudget);
            return await lazy.Value.WaitAsync(budgetCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Not a final answer: the background encode (started above and left running -- only the
            // wait is cancelled) may still finish and populate the cache. The caller must not record
            // this as "thumbnail ready" or the entry never gets revisited; see
            // GameArtworkReconciliationService's ThumbnailWorkerAsync, which retries on TimedOut.
            _logger.LogInformation(
                "Derived thumbnail for {OriginalPath} exceeded its {BudgetSeconds}s budget; serving the original for now and retrying later",
                originalPath,
                _derivedThumbBudget.TotalSeconds);
            return DerivedThumbnail.TimedOut(originalPath);
        }
    }

    private async Task<DerivedThumbnail> EncodeDerivedThumbAsync(
        string originalPath,
        string derivedPath,
        ImageFormat format,
        string contentType,
        DerivedThumbnail fallback)
    {
        var temp = derivedPath + ".tmp";
        await _encodeSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (await IsDecodeBombAsync(originalPath).ConfigureAwait(false))
            {
                return fallback;
            }

            var options = new ImageProcessingOptions
            {
                MaxWidth = MaxThumbDimension,
                MaxHeight = MaxThumbDimension,
                Quality = ThumbQuality,
                SupportedOutputFormats = _imageEncoder!.SupportedOutputFormats,
            };

            var encodedPath = await Task.Run(
                () => _imageEncoder!.EncodeImage(
                    originalPath,
                    File.GetLastWriteTimeUtc(originalPath),
                    temp,
                    false,
                    null,
                    ThumbQuality,
                    options,
                    format),
                CancellationToken.None).ConfigureAwait(false);

            if (!string.Equals(encodedPath, temp, StringComparison.Ordinal) || !File.Exists(temp))
            {
                TryDelete(temp);
                return fallback;
            }

            File.Move(temp, derivedPath, overwrite: true);
            return DerivedThumbnail.Encoded(derivedPath, contentType);
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            _logger.LogDebug(ex, "Generating a derived thumbnail for {OriginalPath} failed; serving the original", originalPath);
            return fallback;
        }
        finally
        {
            _encodeSemaphore.Release();
        }
    }

    /// <summary>
    /// True when the original declares more than <see cref="MaxDecodedPixels"/> pixels and must not
    /// be decoded. Reads the PNG IHDR directly rather than asking the encoder: the dimensions have
    /// to be known *before* anything hands the file to a decoder, and every original this service
    /// sees comes from GameArtworkStore, which only ever fetches <c>.png</c>. Anything that is not
    /// a well-formed PNG header is left alone -- the encoder rejects it cleanly on its own.
    /// </summary>
    private async Task<bool> IsDecodeBombAsync(string originalPath)
    {
        // 8-byte signature, then the IHDR chunk: 4-byte length, 4-byte type, then width and height
        // as big-endian uint32s.
        const int PngHeaderBytes = 24;
        var header = new byte[PngHeaderBytes];
        try
        {
            var stream = File.OpenRead(originalPath);
            await using (stream.ConfigureAwait(false))
            {
                if (await stream.ReadAtLeastAsync(header, PngHeaderBytes, throwOnEndOfStream: false).ConfigureAwait(false) < PngHeaderBytes)
                {
                    return false;
                }
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Reading the image header of {OriginalPath} failed", originalPath);
            return false;
        }

        var (width, height) = ReadPngDimensions(header);
        if (width * height <= MaxDecodedPixels)
        {
            return false;
        }

        _logger.LogWarning(
            "Refusing to decode {OriginalPath}: {Width}x{Height} exceeds the {MaxPixels}-pixel ceiling",
            originalPath,
            width,
            height,
            MaxDecodedPixels);
        return true;
    }

    /// <summary>
    /// Reads width/height out of a 24-byte PNG header, or (0, 0) when it is not a PNG header.
    /// Separate from <see cref="IsDecodeBombAsync"/> because spans cannot live in an async method.
    /// </summary>
    private static (long Width, long Height) ReadPngDimensions(ReadOnlySpan<byte> header)
    {
        if (!header[..8].SequenceEqual(PngSignature) || !header[12..16].SequenceEqual("IHDR"u8))
        {
            return (0, 0);
        }

        return (
            BinaryPrimitives.ReadUInt32BigEndian(header[16..20]),
            BinaryPrimitives.ReadUInt32BigEndian(header[20..24]));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a leftover .tmp is never served.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }
}
