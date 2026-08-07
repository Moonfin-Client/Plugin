namespace Moonfin.Server.Services;

/// <summary>
/// Thrown by <see cref="GameArtworkDeliveryLimiter"/> when a caller cannot be granted a delivery
/// slot within the bounded wait — either the server-wide pool stayed saturated for the whole
/// acquisition timeout, or the caller already holds its per-user share of it.
/// </summary>
public sealed class ArtworkDeliveryUnavailableException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ArtworkDeliveryUnavailableException"/> class.
    /// </summary>
    /// <param name="retryAfter">How long a caller should wait before trying again.</param>
    public ArtworkDeliveryUnavailableException(TimeSpan retryAfter)
        : base("Artwork delivery capacity is saturated.")
    {
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// Gets the suggested wait before the caller retries.
    /// </summary>
    public TimeSpan RetryAfter { get; }
}
