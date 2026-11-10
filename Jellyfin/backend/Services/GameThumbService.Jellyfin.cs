using MediaBrowser.Controller.Drawing;
using MediaBrowser.Model.Drawing;
using Microsoft.Extensions.Logging;

namespace Moonfin.Games;

public partial class GameThumbService
{
    public GameThumbService(
        IHttpClientFactory httpClientFactory,
        ILogger<GameThumbService> logger,
        RdbService rdbService,
        IImageEncoder? imageEncoder = null)
        : this(
            httpClientFactory,
            logger,
            rdbService,
            JellyfinThumbnailEncoder.Wrap(imageEncoder),
            null,
            null,
            ResolveCacheDirectory())
    {
    }

    // Lets tests point the cache at a fixture folder and shrink the encode limits.
    internal GameThumbService(
        IHttpClientFactory httpClientFactory,
        ILogger<GameThumbService> logger,
        RdbService rdbService,
        string dataFolderPath,
        IImageEncoder? imageEncoder = null,
        int? encodeConcurrencyForTests = null,
        TimeSpan? derivedThumbBudgetForTests = null)
        : this(
            httpClientFactory,
            logger,
            rdbService,
            JellyfinThumbnailEncoder.Wrap(imageEncoder),
            encodeConcurrencyForTests,
            derivedThumbBudgetForTests,
            CacheDirectoryFor(dataFolderPath))
    {
    }
}

/// <summary>Downscales artwork with Jellyfin's image encoder.</summary>
internal sealed class JellyfinThumbnailEncoder : IGameThumbnailEncoder
{
    private readonly IImageEncoder _encoder;

    private JellyfinThumbnailEncoder(IImageEncoder encoder) => _encoder = encoder;

    internal static IGameThumbnailEncoder? Wrap(IImageEncoder? encoder) =>
        encoder == null ? null : new JellyfinThumbnailEncoder(encoder);

    public bool CanWrite(string extension) =>
        _encoder.SupportsImageEncoding && _encoder.SupportedOutputFormats.Contains(FormatFor(extension));

    public string? Encode(string originalPath, string outputPath, string extension, int maxDimension, int quality)
    {
        var options = new ImageProcessingOptions
        {
            MaxWidth = maxDimension,
            MaxHeight = maxDimension,
            Quality = quality,
            SupportedOutputFormats = _encoder.SupportedOutputFormats,
        };

        return _encoder.EncodeImage(
            originalPath,
            File.GetLastWriteTimeUtc(originalPath),
            outputPath,
            false,
            null,
            quality,
            options,
            FormatFor(extension));
    }

    private static ImageFormat FormatFor(string extension) =>
        string.Equals(extension, ".webp", StringComparison.OrdinalIgnoreCase) ? ImageFormat.Webp : ImageFormat.Jpg;
}
