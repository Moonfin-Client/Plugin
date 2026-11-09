using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Text;

namespace Moonfin.WatchParty;

/// <summary>
/// The anonymous invite page at Moonfin/Party/{code} and its link preview image. The page never
/// looks the code up, so it shows no title, host or members, only how to join.
/// </summary>
public static class WatchPartyInvite
{
    private static readonly Lazy<byte[]?> Preview = new Lazy<byte[]?>(() => Resource("Moonfin.WatchParty.preview.png"));

    // invite.html is shared with the Plex companion, so it's a file of its own.
    private static readonly Lazy<string> Template = new Lazy<string>(() => Encoding.UTF8.GetString(Resource("Moonfin.WatchParty.invite.html") ?? Array.Empty<byte>()));

    /// <summary>The 1200x630 preview image embedded in the build, or null when it's missing.</summary>
    public static byte[]? PreviewPng() => Preview.Value;

    /// <summary>
    /// The address people reach this server at: the request's scheme and host, or the ones a
    /// reverse proxy passed on, plus any base path the server runs under.
    /// </summary>
    public static string PublicBase(string scheme, string host, string? forwardedProto, string? forwardedHost, string? basePath)
    {
        var proto = First(forwardedProto)?.ToLowerInvariant();
        if (proto != "http" && proto != "https")
        {
            proto = scheme;
        }

        return proto + "://" + (First(forwardedHost) ?? host) + (basePath ?? string.Empty).TrimEnd('/');
    }

    /// <summary>
    /// The address for invite links: the server's configured public one when there is a usable one,
    /// else the address of the request, so a link doesn't depend on how the inviter signed in.
    /// </summary>
    public static string ChooseBase(string? configured, string requestBase) =>
        Uri.TryCreate(configured?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https")
            ? uri.GetLeftPart(UriPartial.Path).TrimEnd('/')
            : requestBase;

    /// <summary>
    /// The address from Jellyfin's PublishedServerUriBySubnet entries ("all=https://..."): the one for
    /// everyone or for outside callers, else the first, or null when none is set.
    /// </summary>
    public static string? Published(IEnumerable<string>? entries)
    {
        var parsed = (entries ?? Array.Empty<string>())
            .Select(e => e.Split(new[] { '=' }, 2))
            .Where(p => p.Length == 2 && p[1].Trim().Length > 0)
            .ToList();
        var wanted = parsed.FirstOrDefault(p => p[0].Trim() is var key && (key.Equals("all", StringComparison.OrdinalIgnoreCase) || key.Equals("external", StringComparison.OrdinalIgnoreCase) || key == "0.0.0.0/0"))
            ?? parsed.FirstOrDefault();
        return wanted?[1].Trim();
    }

    /// <summary>"K7Q 29M" for "K7Q29M".</summary>
    public static string Display(string code) => code.Length == 6 ? code.Substring(0, 3) + " " + code.Substring(3) : code;

    public static string DeepLink(string serverId, string code, string publicBase) =>
        "moonfin://party?server=" + Uri.EscapeDataString(serverId)
        + "&code=" + Uri.EscapeDataString(code)
        + "&address=" + Uri.EscapeDataString(publicBase);

    /// <summary>The page for an already normalized code.</summary>
    public static string Page(string serverName, string serverId, string publicBase, string code)
    {
        var name = Html(string.IsNullOrWhiteSpace(serverName) ? "this server" : serverName);
        var host = Html(HostOf(publicBase));
        var shown = Html(Display(code));
        var link = Html(DeepLink(serverId, code, publicBase));
        var image = Html(publicBase + "/Moonfin/Party/preview.png");
        return Template.Value
            .Replace("{{name}}", name)
            .Replace("{{host}}", host)
            .Replace("{{code}}", Html(code))
            .Replace("{{shown}}", shown)
            .Replace("{{link}}", link)
            .Replace("{{image}}", image);
    }

    private static string? First(string? header)
    {
        var first = header?.Split(',')[0].Trim();
        return string.IsNullOrEmpty(first) ? null : first;
    }

    private static string HostOf(string publicBase) =>
        Uri.TryCreate(publicBase, UriKind.Absolute, out var uri) ? uri.Authority : publicBase;

    private static string Html(string text) => WebUtility.HtmlEncode(text);

    private static byte[]? Resource(string name)
    {
        using var stream = typeof(WatchPartyInvite).Assembly.GetManifestResourceStream(name);
        if (stream == null)
        {
            return null;
        }

        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
