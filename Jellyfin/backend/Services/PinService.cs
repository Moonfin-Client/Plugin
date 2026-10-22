using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>What the server keeps for one user's PIN. Never the PIN itself.</summary>
public class PinRecord
{
    public string Salt { get; set; } = string.Empty;

    public string Hash { get; set; } = string.Empty;

    public int Iterations { get; set; }

    /// <summary>Wrong guesses since the last right one.</summary>
    public int Failed { get; set; }

    public DateTime? LockedUntilUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}

/// <summary>Where PIN records are kept.</summary>
public interface IPinStore
{
    PinRecord? Load(Guid userId);

    void Save(Guid userId, PinRecord record);

    void Delete(Guid userId);
}

/// <summary>What a check of a PIN found.</summary>
public enum PinCheck
{
    /// <summary>The PIN is right.</summary>
    Valid,

    /// <summary>The PIN is wrong.</summary>
    Wrong,

    /// <summary>Too many wrong guesses, so nothing was checked.</summary>
    Locked,

    /// <summary>The user has no PIN.</summary>
    NoPin,
}

public readonly record struct PinResult(PinCheck Check, int RetryAfterSeconds = 0)
{
    public bool IsValid => Check == PinCheck.Valid;
}

/// <summary>
/// Stores and checks per-user PINs on the server, so a PIN set on one device holds on every
/// other. A PIN is exactly four digits, which is only ten thousand combinations, so the record
/// holds a salted slow hash and a guess costs a growing wait after the first few wrong ones,
/// counted here rather than on the device where clearing app data would reset it.
/// </summary>
public class PinService
{
    /// <summary>Digits in a PIN.</summary>
    public const int PinLength = 4;

    /// <summary>Wrong guesses allowed before each further one costs a wait.</summary>
    public const int FreeAttempts = 5;

    /// <summary>How much longer each wrong guess past the free ones makes the wait, in seconds.</summary>
    public const int LockoutStepSeconds = 30;

    /// <summary>The longest wait, in seconds.</summary>
    public const int MaxLockoutSeconds = 900;

    private const int HashIterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>Bounds on a stored count, so a damaged record can't throw or stall every check.</summary>
    private const int MinIterations = 10_000;

    private const int MaxIterations = 2_000_000;

    private readonly IPinStore _store;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();

    public PinService(IPinStore store, Func<DateTime>? utcNow = null)
    {
        _store = store;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public static bool IsPinShaped(string? pin) =>
        pin != null && pin.Length == PinLength && pin.All(c => c is >= '0' and <= '9');

    /// <summary>How long the next guess has to wait after <paramref name="failed"/> wrong ones.</summary>
    public static int LockoutSeconds(int failed) =>
        failed <= FreeAttempts ? 0 : Math.Min(MaxLockoutSeconds, (failed - FreeAttempts) * LockoutStepSeconds);

    public bool HasPin(Guid userId) => _store.Load(userId) is { Hash.Length: > 0 };

    /// <summary>
    /// Sets the PIN, replacing any. Returns false when <paramref name="pin"/> isn't four digits.
    /// Whoever calls this has already been checked: the user with their current PIN, or an
    /// administrator.
    /// </summary>
    public bool Set(Guid userId, string? pin)
    {
        if (!IsPinShaped(pin))
        {
            return false;
        }

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        lock (_gate)
        {
            _store.Save(userId, new PinRecord
            {
                Salt = Convert.ToBase64String(salt),
                Hash = Convert.ToBase64String(Derive(pin!, salt, HashIterations)),
                Iterations = HashIterations,
                UpdatedUtc = _utcNow(),
            });
        }

        return true;
    }

    public void Remove(Guid userId)
    {
        lock (_gate)
        {
            _store.Delete(userId);
        }
    }

    /// <summary>
    /// Checks a guess. A wrong one is counted, and past the free attempts the user has to wait
    /// before the next is looked at, whether or not it would have been right.
    /// </summary>
    public PinResult Verify(Guid userId, string? guess)
    {
        lock (_gate)
        {
            var record = _store.Load(userId);
            if (record == null || record.Hash.Length == 0)
            {
                return new PinResult(PinCheck.NoPin);
            }

            var now = _utcNow();
            if (record.LockedUntilUtc is { } until && until > now)
            {
                return new PinResult(PinCheck.Locked, (int)Math.Ceiling((until - now).TotalSeconds));
            }

            if (IsPinShaped(guess) && Matches(record, guess!))
            {
                if (record.Failed != 0 || record.LockedUntilUtc != null)
                {
                    record.Failed = 0;
                    record.LockedUntilUtc = null;
                    _store.Save(userId, record);
                }

                return new PinResult(PinCheck.Valid);
            }

            record.Failed++;
            var wait = LockoutSeconds(record.Failed);
            record.LockedUntilUtc = wait > 0 ? now.AddSeconds(wait) : null;
            _store.Save(userId, record);
            return wait > 0 ? new PinResult(PinCheck.Locked, wait) : new PinResult(PinCheck.Wrong);
        }
    }

    private static bool Matches(PinRecord record, string guess)
    {
        if (record.Iterations is < MinIterations or > MaxIterations)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(record.Salt);
            var expected = Convert.FromBase64String(record.Hash);
            if (salt.Length == 0 || expected.Length != HashBytes)
            {
                return false;
            }

            var actual = Derive(guess, salt, record.Iterations);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static byte[] Derive(string pin, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(pin, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
}

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
