namespace Moonfin.Server.Services;

/// <summary>
/// Repairs truncated JSON objects left behind by the in-place writes that shipped before
/// AtomicFile. A crash mid-write cut those files off at an arbitrary byte, so this walks the
/// text once, remembers every offset where a complete top-level property ended, and tries to
/// close the envelope at each of them, longest first. The caller's validate callback decides
/// which candidate is acceptable, so this class needs no JSON serializer of its own.
///
/// Salvage is top-level only. A file cut inside a nested object keeps its complete leading
/// properties and loses the partial one.
/// </summary>
public static class JsonSalvage
{
    // A truncated envelope rarely has more than a few dozen top-level boundaries. Trying
    // every one of a pathological file would just re-run the validator for no gain.
    private const int MaxCandidates = 64;

    /// <summary>
    /// Attempts to repair truncated JSON. Returns true and the repaired text when some prefix
    /// of raw forms a complete object that validate accepts. validate must return true only
    /// for candidates that deserialize into an acceptable model.
    /// </summary>
    public static bool TrySalvage(string raw, Func<string, bool> validate, out string healed)
    {
        healed = string.Empty;
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }

        var text = raw;

        // Strip a UTF-8 BOM that survived decoding.
        if (text[0] == '\uFEFF')
        {
            text = text.Substring(1);
        }

        // Crash tails on some filesystems come back as NUL bytes instead of a short file.
        var nul = text.IndexOf('\0');
        if (nul >= 0)
        {
            text = text.Substring(0, nul);
        }

        text = text.TrimEnd();
        var start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        if (start >= text.Length || text[start] != '{')
        {
            return false;
        }

        // One forward scan. Braces and commas inside strings don't count, and a string the
        // truncation never closed simply records no boundaries after it opens.
        var cuts = new List<int>();
        var completeEnd = -1;
        var inString = false;
        var escape = false;
        var depth = 0;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];

            if (inString)
            {
                if (escape)
                {
                    escape = false;
                }
                else if (c == '\\')
                {
                    escape = true;
                }
                else if (c == '"')
                {
                    inString = false;
                    if (depth == 1)
                    {
                        cuts.Add(i + 1);
                    }
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                case '[':
                    depth++;
                    break;
                case '}':
                case ']':
                    depth--;
                    if (depth == 1)
                    {
                        cuts.Add(i + 1);
                    }
                    else if (depth == 0)
                    {
                        completeEnd = i + 1;
                    }

                    break;
                case ',':
                    if (depth == 1)
                    {
                        // Cut before the comma so the candidate never carries a trailing one.
                        cuts.Add(i);
                    }

                    break;
            }

            if (completeEnd >= 0)
            {
                break;
            }
        }

        // The document closed on its own, so anything after it is trailing garbage.
        if (completeEnd >= 0)
        {
            var whole = text.Substring(start, completeEnd - start);
            if (validate(whole))
            {
                healed = whole;
                return true;
            }
        }

        // Longest candidate first: keep as much of the user's data as validates. A cut that
        // lands after a property NAME instead of a value produces malformed JSON and simply
        // fails validation, so the scanner doesn't need to tell the two apart.
        cuts.Sort();
        cuts.Reverse();

        var attempts = 0;
        foreach (var cut in cuts)
        {
            if (++attempts > MaxCandidates)
            {
                break;
            }

            var candidate = text.Substring(start, cut - start) + "\n}";
            if (validate(candidate))
            {
                healed = candidate;
                return true;
            }
        }

        return false;
    }
}
