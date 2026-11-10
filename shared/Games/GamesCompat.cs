using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Moonfin.Games;

/// <summary>
/// Framework APIs the games code uses that netstandard2.1, the Emby build, doesn't have. On .NET 5
/// and later each one calls the framework's own.
/// </summary>
internal static class GamesCompat
{
    /// <summary>Lowercase hex, the form every hash in this code is stored in.</summary>
    internal static string LowerHex(byte[] bytes) =>
#if NET5_0_OR_GREATER
        Convert.ToHexString(bytes).ToLowerInvariant();
#else
        BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
#endif

    internal static byte[] Sha256(byte[] data)
    {
#if NET5_0_OR_GREATER
        return SHA256.HashData(data);
#else
        using var sha = SHA256.Create();
        return sha.ComputeHash(data);
#endif
    }

    internal static byte[] Sha256(Stream data)
    {
#if NET7_0_OR_GREATER
        return SHA256.HashData(data);
#else
        using var sha = SHA256.Create();
        return sha.ComputeHash(data);
#endif
    }

    internal static byte[] Sha1(byte[] data)
    {
#if NET5_0_OR_GREATER
        return SHA1.HashData(data);
#else
        using var sha = SHA1.Create();
        return sha.ComputeHash(data);
#endif
    }

    internal static void ThrowIfNull([NotNull] object? argument, [CallerArgumentExpression("argument")] string? paramName = null)
    {
#if NET6_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(argument, paramName);
#else
        if (argument == null) throw new ArgumentNullException(paramName);
#endif
    }

    internal static void ThrowIfNullOrEmpty([NotNull] string? argument, [CallerArgumentExpression("argument")] string? paramName = null)
    {
#if NET7_0_OR_GREATER
        ArgumentException.ThrowIfNullOrEmpty(argument, paramName);
#else
        if (argument == null) throw new ArgumentNullException(paramName);
        if (argument.Length == 0) throw new ArgumentException("The value cannot be an empty string.", paramName);
#endif
    }

    internal static void ThrowIfNullOrWhiteSpace([NotNull] string? argument, [CallerArgumentExpression("argument")] string? paramName = null)
    {
#if NET8_0_OR_GREATER
        ArgumentException.ThrowIfNullOrWhiteSpace(argument, paramName);
#else
        if (argument == null) throw new ArgumentNullException(paramName);
        if (string.IsNullOrWhiteSpace(argument)) throw new ArgumentException("The value cannot be an empty string or composed entirely of whitespace.", paramName);
#endif
    }

    internal static void ThrowIfLessThan(int value, int other, [CallerArgumentExpression("value")] string? paramName = null)
    {
#if NET8_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfLessThan(value, other, paramName);
#else
        if (value < other) throw new ArgumentOutOfRangeException(paramName, value, $"The value must be at least {other}.");
#endif
    }

    internal static bool IsWindows() =>
#if NET5_0_OR_GREATER
        OperatingSystem.IsWindows();
#else
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
#endif

    /// <summary>Moves a finished temp file over its target, replacing any copy already there.</summary>
    internal static void MoveOverwrite(string source, string destination)
    {
#if NET5_0_OR_GREATER
        File.Move(source, destination, overwrite: true);
#else
        if (File.Exists(destination)) File.Replace(source, destination, null);
        else File.Move(source, destination);
#endif
    }

    internal static string[] SplitTrimmed(string value, char separator) =>
#if NET5_0_OR_GREATER
        value.Split(separator, StringSplitOptions.TrimEntries);
#else
        value.Split(separator).Select(part => part.Trim()).ToArray();
#endif

    /// <summary>The raw Last-Modified header, or null.</summary>
    internal static string? LastModified(HttpContentHeaders headers) =>
#if NET5_0_OR_GREATER
        headers.NonValidated.TryGetValues("Last-Modified", out var modified) ? modified.ToString() : null;
#else
        headers.TryGetValues("Last-Modified", out var modified) ? string.Join(", ", modified) : null;
#endif

    internal static IEqualityComparer<T> ByReference<T>()
        where T : class =>
#if NET5_0_OR_GREATER
        ReferenceEqualityComparer.Instance;
#else
        ReferenceComparer<T>.Instance;

    private sealed class ReferenceComparer<T> : IEqualityComparer<T>
        where T : class
    {
        internal static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();

        public bool Equals(T? x, T? y) => ReferenceEquals(x, y);

        public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
    }
#endif

#if !NET5_0_OR_GREATER
    internal static async Task WaitAsync(this Task task, CancellationToken cancellationToken)
    {
        await WhenDoneOrCanceled(task, cancellationToken).ConfigureAwait(false);
        await task.ConfigureAwait(false);
    }

    internal static async Task<T> WaitAsync<T>(this Task<T> task, CancellationToken cancellationToken)
    {
        await WhenDoneOrCanceled(task, cancellationToken).ConfigureAwait(false);
        return await task.ConfigureAwait(false);
    }

    internal static async Task WaitAsync(this Task task, TimeSpan timeout)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false) != task) throw new TimeoutException();
        await task.ConfigureAwait(false);
    }

    // Returns once the task finishes, or throws once the token is canceled first. The registration
    // is dropped as soon as either happens, so a long-lived token doesn't collect one per call.
    private static async Task WhenDoneOrCanceled(Task task, CancellationToken cancellationToken)
    {
        if (task.IsCompleted || !cancellationToken.CanBeCanceled) return;
        var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => canceled.TrySetResult(true)))
        {
            if (await Task.WhenAny(task, canceled.Task).ConfigureAwait(false) != task)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
    }

    internal static int ReadAtLeast(this Stream stream, byte[] buffer, int minimumBytes, bool throwOnEndOfStream)
    {
        var total = 0;
        while (total < minimumBytes)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                if (throwOnEndOfStream) throw new EndOfStreamException();
                break;
            }

            total += read;
        }

        return total;
    }

    internal static async Task<int> ReadAtLeastAsync(this Stream stream, byte[] buffer, int minimumBytes, bool throwOnEndOfStream)
    {
        var total = 0;
        while (total < minimumBytes)
        {
            var read = await stream.ReadAsync(buffer, total, buffer.Length - total).ConfigureAwait(false);
            if (read == 0)
            {
                if (throwOnEndOfStream) throw new EndOfStreamException();
                break;
            }

            total += read;
        }

        return total;
    }

    internal static void ReadExactly(this Stream stream, byte[] buffer, int offset, int count)
    {
        while (count > 0)
        {
            var read = stream.Read(buffer, offset, count);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
            count -= read;
        }
    }

    internal static Task<Stream> ReadAsStreamAsync(this HttpContent content, CancellationToken cancellationToken) =>
        content.ReadAsStreamAsync();

    internal static bool TryRemove<TKey, TValue>(this ConcurrentDictionary<TKey, TValue> dictionary, KeyValuePair<TKey, TValue> item)
        where TKey : notnull =>
        ((ICollection<KeyValuePair<TKey, TValue>>)dictionary).Remove(item);
#endif
}
