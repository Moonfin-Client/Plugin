using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Moonfin.WatchParty;

/// <summary>
/// The routes under Moonfin/WatchParty and Moonfin/Admin/WatchParty, so each server only maps a
/// catch-all route here and writes the answer. Paths are relative to those bases.
/// </summary>
public static class WatchPartyRoutes
{
    private const int MaxBodyBytes = 64 * 1024;

    public static async Task<WatchPartyResult> HandleAsync(
        WatchPartyEngine engine,
        string method,
        string? path,
        WatchPartyCaller caller,
        string? roomToken,
        byte[]? body,
        Func<string, string?> query,
        CancellationToken cancellationToken = default)
    {
        var p = Segments(path);
        var get = Is(method, "GET");
        var post = Is(method, "POST");
        if (get && Match(p, "Config"))
        {
            return engine.Config();
        }

        if (!engine.Settings.Enabled)
        {
            return WatchPartyResult.Error("disabled", "Watch Parties are turned off on this server.");
        }

        if (get && Match(p, "Time"))
        {
            return engine.Time();
        }

        if (Match(p, "Rooms"))
        {
            if (get)
            {
                return engine.Rooms(caller);
            }

            if (post)
            {
                return Read<CreateBody>(body, b => engine.Create(caller, b));
            }
        }

        if (post && Match(p, "Join"))
        {
            return Read<JoinBody>(body, b => engine.Join(caller, b));
        }

        if (p.Length < 3 || !Is(p[0], "Rooms"))
        {
            return NotFound();
        }

        var id = p[1];
        if (p.Length == 3)
        {
            switch (p[2].ToLowerInvariant())
            {
                case "events" when get:
                    long.TryParse(query("cursor"), out var cursor);
                    var wait = int.TryParse(query("wait"), out var w) ? w : WatchPartyEngine.MaxWaitSeconds;
                    return await engine.Events(caller, id, roomToken, cursor, wait, cancellationToken).ConfigureAwait(false);
                case "messages" when post:
                    return Read<MessagesBody>(body, b => engine.Messages(caller, id, roomToken, b));
                case "suggestions" when get:
                    return engine.Suggestions(caller, id, roomToken);
                case "suggestions" when post:
                    return Read<RoomBody>(body, b => engine.Suggest(caller, id, roomToken, b));
                case "stage" when post:
                    return Read<RoomBody>(body, b => engine.Stage(caller, id, roomToken, b));
                case "start" when post:
                    return Read<RoomBody>(body, b => engine.Start(caller, id, roomToken, b));
                case "stop" when post:
                    return engine.Stop(caller, id, roomToken);
                case "policy" when post:
                    return Read<RoomBody>(body, b => engine.Policy(caller, id, roomToken, b));
                case "pin" when post:
                    return Read<RoomBody>(body, b => engine.SetPin(caller, id, roomToken, b));
                case "voting" when post:
                    return Read<RoomBody>(body, b => engine.Voting(caller, id, roomToken, b));
                case "promote" when post:
                    return Read<RoomBody>(body, b => engine.Promote(caller, id, roomToken, b));
                case "remove" when post:
                    return Read<RoomBody>(body, b => engine.Remove(caller, id, roomToken, b));
                case "close" when post:
                    return engine.Close(caller, id, roomToken);
                case "leave" when post:
                    return engine.Leave(caller, id, roomToken);
            }
        }
        else if (p.Length == 4 && Is(p[2], "Suggestions") && Is(method, "DELETE"))
        {
            return engine.RemoveSuggestion(caller, id, roomToken, p[3]);
        }
        else if (p.Length == 5 && Is(p[2], "Suggestions") && Is(p[4], "Vote") && post)
        {
            return Read<RoomBody>(body, b => engine.Vote(caller, id, roomToken, p[3], b));
        }

        return NotFound();
    }

    /// <summary>The admin routes. The server has already checked the caller is an admin.</summary>
    public static WatchPartyResult HandleAdmin(WatchPartyEngine engine, string method, string? path, byte[]? body)
    {
        var p = Segments(path);
        if (Is(method, "GET") && Match(p, "Rooms"))
        {
            return engine.AdminRooms();
        }

        if (Is(method, "POST") && p.Length == 3 && Is(p[0], "Rooms"))
        {
            if (Is(p[2], "Close"))
            {
                return engine.AdminClose(p[1]);
            }

            if (Is(p[2], "Remove"))
            {
                return Read<RoomBody>(body, b => engine.AdminRemove(p[1], b));
            }
        }

        return NotFound();
    }

    public static async Task<byte[]> ReadBodyAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                // Too big reads as broken JSON, which the routes answer with bad_request.
                return Encoding.UTF8.GetBytes("{");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static WatchPartyResult Read<T>(byte[]? body, Func<T, WatchPartyResult> handle)
        where T : new() =>
        WatchPartyJson.TryRead<T>(body, out var parsed)
            ? handle(parsed)
            : WatchPartyResult.Error("bad_request", "The body isn't valid JSON.");

    private static WatchPartyResult NotFound() => WatchPartyResult.Error("not_found", "There's no such Watch Parties route.");

    private static string[] Segments(string? path) =>
        (path ?? string.Empty).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

    private static bool Match(string[] p, string only) => p.Length == 1 && Is(p[0], only);

    private static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
