using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Moonfin.Server.Services;

/// <summary>
/// Bridges Jellyfin's websocket to clients that can't open one themselves.
/// Roku devices have no TLS-capable raw socket, so on https servers they can't
/// receive server-pushed messages (SyncPlay commands and group updates). For each
/// such device this service opens a websocket to Jellyfin's own /socket endpoint
/// using the caller's token and device id, buffers the SyncPlay messages, and the
/// device drains them over normal https long polling.
/// </summary>
public sealed class SocketRelayManager : IDisposable
{
    internal const int MaxRelaysPerUser = 10;
    internal const int MaxRelays = 200;
    private const int MaxBufferedMessages = 200;
    private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan MaxReconnectBackoff = TimeSpan.FromSeconds(30);

    private readonly Func<bool, string> _apiUrl;
    private readonly ILogger<SocketRelayManager> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<(Guid UserId, string DeviceId), SocketRelay> _relays = new();

    public SocketRelayManager(IServerApplicationHost appHost, ILogger<SocketRelayManager> logger)
        : this(https => appHost.GetApiUrlForLocalAccess(allowHttps: https), logger)
    {
    }

    internal SocketRelayManager(Func<bool, string> apiUrl, ILogger<SocketRelayManager> logger)
    {
        _apiUrl = apiUrl;
        _logger = logger;
    }

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _relays.Count;
            }
        }
    }

    /// <summary>
    /// Returns the caller's relay, starting one when there is none or the old one is gone or
    /// belongs to an older token. Returns null when the per-user or server-wide cap is reached.
    /// </summary>
    public SocketRelayOpenResult? EnsureRelay(Guid userId, string deviceId, string token)
    {
        var key = (userId, deviceId);
        SocketRelay relay;
        SocketRelay? replaced = null;
        var created = false;
        lock (_gate)
        {
            if (_relays.TryGetValue(key, out var existing) && existing.State != RelayState.Gone && existing.TokenMatches(token))
            {
                relay = existing;
            }
            else
            {
                if (existing == null
                    && (_relays.Count >= MaxRelays || _relays.Keys.Count(k => k.UserId == userId) >= MaxRelaysPerUser))
                {
                    return null;
                }

                replaced = existing;
                relay = new SocketRelay(deviceId, token, SocketUri, _logger);
                _relays[key] = relay;
                created = true;
            }
        }

        replaced?.Shutdown("replaced");
        if (created)
        {
            relay.Start();
        }

        return new SocketRelayOpenResult(created, relay.StateName, relay.Head);
    }

    public async Task<SocketRelayPollResult?> PollAsync(Guid userId, string deviceId, string token, long cursor, TimeSpan maxWait, CancellationToken requestAborted)
    {
        SocketRelay? relay;
        lock (_gate)
        {
            _relays.TryGetValue((userId, deviceId), out relay);
        }

        if (relay == null || !relay.TokenMatches(token))
        {
            return null;
        }

        return await relay.PollAsync(cursor, maxWait, requestAborted).ConfigureAwait(false);
    }

    public void CloseRelay(Guid userId, string deviceId, string token)
    {
        SocketRelay? relay;
        lock (_gate)
        {
            if (!_relays.TryGetValue((userId, deviceId), out relay) || !relay.TokenMatches(token))
            {
                return;
            }

            _relays.Remove((userId, deviceId));
        }

        relay.Shutdown("closed by client");
    }

    /// <summary>Stops relays whose device stopped polling and relays whose upstream is gone for good.</summary>
    public void Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        StopWhere(relay => relay.State == RelayState.Gone || now - relay.LastPollUtc > IdleAfter, "idle or gone");
    }

    public void CloseAll() => StopWhere(_ => true, "relay off or server stopping");

    public void Dispose() => CloseAll();

    private void StopWhere(Func<SocketRelay, bool> match, string reason)
    {
        var stopped = new List<SocketRelay>();
        lock (_gate)
        {
            foreach (var (key, relay) in _relays)
            {
                if (match(relay))
                {
                    _relays.Remove(key);
                    stopped.Add(relay);
                }
            }
        }

        foreach (var relay in stopped)
        {
            relay.Shutdown(reason);
        }
    }

    // Uses the address Jellyfin itself hands out for LAN access, so it honors the bound
    // interfaces, ports and BaseUrl instead of assuming 127.0.0.1 is listening.
    private Uri SocketUri(bool https) => new("ws" + _apiUrl(https)[4..] + "/socket");

    internal enum RelayState
    {
        Connecting,
        Connected,
        Reconnecting,
        Gone
    }

    private sealed class SocketRelay
    {
        private readonly string _deviceId;
        private readonly string _token;
        private readonly byte[] _tokenBytes;
        private readonly Func<bool, Uri> _socketUri;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _lifeCts = new();
        private readonly object _lock = new();
        private readonly Queue<(long Seq, string Payload)> _buffer = new();

        private TaskCompletionSource<bool> _signal = NewSignal();
        private Task? _keepAliveTask;
        private volatile ClientWebSocket? _socket;
        private volatile RelayState _state = RelayState.Connecting;
        private long _nextSeq = 1;
        private long _droppedThrough;
        private DateTimeOffset _lastPollUtc = DateTimeOffset.UtcNow;

        public SocketRelay(string deviceId, string token, Func<bool, Uri> socketUri, ILogger logger)
        {
            _deviceId = deviceId;
            _token = token;
            _tokenBytes = Encoding.UTF8.GetBytes(token);
            _socketUri = socketUri;
            _logger = logger;
        }

        public RelayState State => _state;

        public string StateName => _state.ToString().ToLowerInvariant();

        public long Head
        {
            get
            {
                lock (_lock)
                {
                    return _nextSeq - 1;
                }
            }
        }

        public DateTimeOffset LastPollUtc
        {
            get
            {
                lock (_lock)
                {
                    return _lastPollUtc;
                }
            }
        }

        public bool TokenMatches(string token)
            => CryptographicOperations.FixedTimeEquals(_tokenBytes, Encoding.UTF8.GetBytes(token));

        public void Start()
        {
            _ = Task.Run(RunAsync);
        }

        public void Shutdown(string reason)
        {
            _logger.LogDebug("Moonfin SocketRelay {DeviceId}: shutdown ({Reason})", _deviceId, reason);
            MarkGone();
            _lifeCts.Cancel();
        }

        public async Task<SocketRelayPollResult> PollAsync(long cursor, TimeSpan maxWait, CancellationToken requestAborted)
        {
            Task signal;
            lock (_lock)
            {
                // Clamp so a cursor from an older relay doesn't skip messages this one hasn't sent yet
                cursor = Math.Clamp(cursor, 0, _nextSeq - 1);
                _lastPollUtc = DateTimeOffset.UtcNow;
                while (_buffer.Count > 0 && _buffer.Peek().Seq <= cursor)
                {
                    _buffer.Dequeue();
                }

                if (_buffer.Count > 0 || cursor < _droppedThrough || _state == RelayState.Gone || maxWait <= TimeSpan.Zero)
                {
                    return Snapshot(cursor);
                }

                signal = _signal.Task;
            }

            using (var stop = CancellationTokenSource.CreateLinkedTokenSource(requestAborted))
            {
                await Task.WhenAny(signal, Task.Delay(maxWait, stop.Token)).ConfigureAwait(false);
                stop.Cancel();
            }

            lock (_lock)
            {
                _lastPollUtc = DateTimeOffset.UtcNow;
                return Snapshot(cursor);
            }
        }

        private SocketRelayPollResult Snapshot(long cursor)
        {
            var messages = new List<string>();
            var lastSeq = cursor;
            foreach (var (seq, payload) in _buffer)
            {
                if (seq > cursor)
                {
                    messages.Add(payload);
                    lastSeq = seq;
                }
            }

            return new SocketRelayPollResult(lastSeq, messages, StateName, cursor < _droppedThrough);
        }

        private static TaskCompletionSource<bool> NewSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Callers must hold _lock.
        private void Wake()
        {
            var signal = _signal;
            _signal = NewSignal();
            signal.TrySetResult(true);
        }

        private void SetState(RelayState state)
        {
            lock (_lock)
            {
                if (_state != RelayState.Gone)
                {
                    _state = state;
                }
            }
        }

        private void MarkGone()
        {
            lock (_lock)
            {
                _state = RelayState.Gone;
                Wake();
            }
        }

        private async Task RunAsync()
        {
            var backoff = TimeSpan.FromSeconds(1);
            var https = false;

            while (!_lifeCts.IsCancellationRequested)
            {
                var redirected = false;
                var ws = CreateSocket(https);
                try
                {
                    await ws.ConnectAsync(_socketUri(https), _lifeCts.Token).ConfigureAwait(false);
                    _socket = ws;
                    SetState(RelayState.Connected);
                    backoff = TimeSpan.FromSeconds(1);

                    await ReceiveLoopAsync(ws).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifeCts.IsCancellationRequested)
                {
                    break;
                }
                catch (WebSocketException) when (ws.HttpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    // The token was revoked or the user was disabled, so the client has to sign in again
                    _logger.LogDebug("Moonfin SocketRelay {DeviceId}: upstream refused the token ({Status})", _deviceId, (int)ws.HttpStatusCode);
                    break;
                }
                catch (Exception ex)
                {
                    // RequireHttps answers the plain handshake with a redirect to the https listener
                    redirected = !https && (int)ws.HttpStatusCode is >= 300 and < 400;
                    _logger.LogDebug(ex, "Moonfin SocketRelay {DeviceId}: connection dropped", _deviceId);
                }
                finally
                {
                    _socket = null;
                    ws.Dispose();
                }

                https = redirected;
                if (redirected || _lifeCts.IsCancellationRequested)
                {
                    continue;
                }

                SetState(RelayState.Reconnecting);
                try
                {
                    await Task.Delay(backoff, _lifeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, MaxReconnectBackoff.TotalSeconds));
            }

            MarkGone();
        }

        private ClientWebSocket CreateSocket(bool https)
        {
            var ws = new ClientWebSocket();
            // Liveness is Jellyfin's application level KeepAlive JSON, not ws pings
            ws.Options.KeepAliveInterval = TimeSpan.Zero;
            ws.Options.CollectHttpResponseDetails = true;
            ws.Options.SetRequestHeader(
                "Authorization",
                $"MediaBrowser Token=\"{Uri.EscapeDataString(_token)}\", DeviceId=\"{Uri.EscapeDataString(_deviceId)}\"");
            if (https)
            {
                // This connects to the server's own LAN address, so the certificate name won't match
                ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            }

            return ws;
        }

        private async Task ReceiveLoopAsync(ClientWebSocket ws)
        {
            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();

            while (ws.State == WebSocketState.Open && !_lifeCts.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, _lifeCts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                HandleInbound(text);
            }
        }

        private void HandleInbound(string text)
        {
            switch (PeekMessageType(text))
            {
                case "ForceKeepAlive":
                    StartKeepAlive(ParseKeepAliveSeconds(text));
                    break;
                // Only what the Roku SyncPlay manager reads goes to the device
                case "SyncPlayCommand":
                case "SyncPlayGroupUpdate":
                    Enqueue(text);
                    break;
            }
        }

        private void Enqueue(string payload)
        {
            lock (_lock)
            {
                _buffer.Enqueue((_nextSeq++, payload));
                while (_buffer.Count > MaxBufferedMessages)
                {
                    _droppedThrough = _buffer.Dequeue().Seq;
                }

                Wake();
            }
        }

        private void StartKeepAlive(int intervalSeconds)
        {
            if (_keepAliveTask != null)
            {
                return;
            }

            var sendEvery = TimeSpan.FromSeconds(Math.Max(intervalSeconds / 2, 5));
            _keepAliveTask = Task.Run(async () =>
            {
                var payload = Encoding.UTF8.GetBytes("{\"MessageType\":\"KeepAlive\"}");
                using var timer = new PeriodicTimer(sendEvery);
                try
                {
                    await SendKeepAliveAsync(payload).ConfigureAwait(false);
                    while (await timer.WaitForNextTickAsync(_lifeCts.Token).ConfigureAwait(false))
                    {
                        await SendKeepAliveAsync(payload).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });
        }

        private async Task SendKeepAliveAsync(byte[] payload)
        {
            var ws = _socket;
            if (ws is { State: WebSocketState.Open })
            {
                try
                {
                    await ws.SendAsync(payload, WebSocketMessageType.Text, true, _lifeCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Moonfin SocketRelay {DeviceId}: keepalive send failed", _deviceId);
                }
            }
        }

        private static string? PeekMessageType(string text)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                return doc.RootElement.TryGetProperty("MessageType", out var typeProp) && typeProp.ValueKind == JsonValueKind.String
                    ? typeProp.GetString()
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static int ParseKeepAliveSeconds(string text)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("Data", out var dataProp) && dataProp.TryGetInt32(out var seconds) && seconds > 0)
                {
                    return seconds;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
            }

            return 60;
        }
    }
}

public record SocketRelayOpenResult(bool Created, string State, long Cursor);

public record SocketRelayPollResult(long Cursor, IReadOnlyList<string> Messages, string State, bool Reset);
