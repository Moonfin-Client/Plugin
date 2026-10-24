using System;
using System.Linq;
using System.Security.Cryptography;

namespace Moonfin.Pin;

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

public readonly struct PinResult
{
    public PinResult(PinCheck check, int retryAfterSeconds = 0)
    {
        Check = check;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public PinCheck Check { get; }

    public int RetryAfterSeconds { get; }

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

        var salt = new byte[SaltBytes];
        RandomNumberGenerator.Fill(salt);
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

    // The Emby plugin's netstandard2.1 has no static Pbkdf2 helper, and this gives the same hash.
    private static byte[] Derive(string pin, byte[] salt, int iterations)
    {
        using var kdf = new Rfc2898DeriveBytes(pin, salt, iterations, HashAlgorithmName.SHA256);
        return kdf.GetBytes(HashBytes);
    }
}
