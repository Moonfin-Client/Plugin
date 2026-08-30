using System.Text;

namespace Moonfin.Server;

/// <summary>
/// Keeps text safe to store in the plugin configuration file.
///
/// Jellyfin writes that file with the legacy <see cref="System.Xml.XmlTextWriter"/>, which emits
/// whatever character it is handed without checking, and reads it back with a strict reader that
/// enforces the XML 1.0 Char production. Anything outside that production saves without complaint
/// and leaves the file unreadable on the next restart, at which point Jellyfin replaces the whole
/// configuration with defaults. So every piece of free text that reaches the config comes through
/// here first.
/// </summary>
public static class XmlText
{
    /// <summary>
    /// Drops every character XML 1.0 can't carry. Tab, newline and carriage return stay, as do
    /// astral characters such as emoji whose surrogates are correctly paired.
    /// </summary>
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var clean = new StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    clean.Append(c).Append(value[i + 1]);
                    i++;
                }

                continue;
            }

            if (char.IsLowSurrogate(c))
            {
                continue;
            }

            if (IsLegal(c))
            {
                clean.Append(c);
            }
        }

        return clean.ToString();
    }

    /// <summary>
    /// Sanitizes, then answers null when nothing usable is left, for optional fields where empty
    /// and absent mean the same.
    /// </summary>
    public static string? SanitizeOrNull(string? value)
    {
        var clean = Sanitize(value);
        return string.IsNullOrWhiteSpace(clean) ? null : clean;
    }

    /// <summary>
    /// Cuts text to at most <paramref name="maxLength"/> chars. Cutting through a surrogate pair
    /// would leave behind the very thing <see cref="Sanitize"/> exists to remove.
    /// </summary>
    public static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        var cut = maxLength;

        // A high surrogate on the boundary owns the character after it, so drop both.
        if (cut > 0 && char.IsHighSurrogate(value[cut - 1]))
        {
            cut--;
        }

        return value.Substring(0, cut);
    }

    /// <summary>
    /// The XML 1.0 Char production, minus the surrogate range the caller handles in pairs:
    /// #x9 | #xA | #xD | [#x20-#xD7FF] | [#xE000-#xFFFD]. DEL and the C1 controls are legal in
    /// XML 1.0 and are deliberately kept.
    /// </summary>
    private static bool IsLegal(char c) =>
        c == '\t' || c == '\n' || c == '\r'
        || (c >= 0x20 && c <= 0xD7FF)
        || (c >= 0xE000 && c <= 0xFFFD);
}
