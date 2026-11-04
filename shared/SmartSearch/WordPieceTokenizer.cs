using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Moonfin.SmartSearch;

/// <summary>
/// BERT's uncased WordPiece tokenizer, the same steps Hugging Face's BertTokenizer runs for the
/// MiniLM vocab: clean, space out CJK, lowercase, strip accents, split punctuation, then greedy
/// longest-match pieces.
/// </summary>
public sealed class WordPieceTokenizer : ITokenizer
{
    private const int MaxWordChars = 100;

    private readonly Dictionary<string, int> _vocab = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly int _unk;
    private readonly int _cls;
    private readonly int _sep;

    public WordPieceTokenizer(IEnumerable<string> vocabLines)
    {
        var index = 0;
        foreach (var line in vocabLines)
        {
            _vocab[line.TrimEnd('\r', '\n')] = index++;
        }

        _unk = _vocab["[UNK]"];
        _cls = _vocab["[CLS]"];
        _sep = _vocab["[SEP]"];
    }

    public int[] Encode(string text, int maxTokens)
    {
        var ids = new List<int> { _cls };
        foreach (var word in BasicTokens(text))
        {
            if (ids.Count >= maxTokens - 1)
            {
                break;
            }

            AddPieces(word, ids);
        }

        if (ids.Count > maxTokens - 1)
        {
            ids.RemoveRange(maxTokens - 1, ids.Count - (maxTokens - 1));
        }

        ids.Add(_sep);
        return ids.ToArray();
    }

    private void AddPieces(string word, List<int> ids)
    {
        if (word.Length > MaxWordChars)
        {
            ids.Add(_unk);
            return;
        }

        var pieces = new List<int>();
        var start = 0;
        while (start < word.Length)
        {
            var end = word.Length;
            var found = -1;
            while (start < end)
            {
                var piece = start > 0 ? "##" + word.Substring(start, end - start) : word.Substring(start, end - start);
                if (_vocab.TryGetValue(piece, out var id))
                {
                    found = id;
                    break;
                }

                end--;
            }

            if (found < 0)
            {
                ids.Add(_unk);
                return;
            }

            pieces.Add(found);
            start = end;
        }

        ids.AddRange(pieces);
    }

    private static List<string> BasicTokens(string text)
    {
        var cleaned = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == 0 || c == '\uFFFD' || IsControl(text, i))
            {
                continue;
            }

            if (IsWhitespace(text, i))
            {
                cleaned.Append(' ');
            }
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length)
            {
                var cp = char.ConvertToUtf32(c, text[i + 1]);
                if (IsCjk(cp))
                {
                    cleaned.Append(' ').Append(c).Append(text[i + 1]).Append(' ');
                }
                else
                {
                    cleaned.Append(c).Append(text[i + 1]);
                }

                i++;
            }
            else if (IsCjk(c))
            {
                cleaned.Append(' ').Append(c).Append(' ');
            }
            else
            {
                cleaned.Append(c);
            }
        }

        var tokens = new List<string>();
        foreach (var raw in cleaned.ToString().Normalize(NormalizationForm.FormC).Split(' '))
        {
            if (raw.Length == 0)
            {
                continue;
            }

            var word = StripAccents(raw.ToLowerInvariant());
            var current = new StringBuilder();
            for (var i = 0; i < word.Length; i++)
            {
                if (IsPunctuation(word, i))
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }

                    tokens.Add(word.Substring(i, char.IsHighSurrogate(word[i]) && i + 1 < word.Length ? 2 : 1));
                    if (char.IsHighSurrogate(word[i]))
                    {
                        i++;
                    }
                }
                else
                {
                    current.Append(word[i]);
                }
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
            }
        }

        return tokens;
    }

    private static string StripAccents(string word)
    {
        var decomposed = word.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static bool IsWhitespace(string s, int i)
    {
        var c = s[i];
        return c == ' ' || c == '\t' || c == '\n' || c == '\r' ||
            CharUnicodeInfo.GetUnicodeCategory(s, i) == UnicodeCategory.SpaceSeparator;
    }

    private static bool IsControl(string s, int i)
    {
        var c = s[i];
        if (c == '\t' || c == '\n' || c == '\r')
        {
            return false;
        }

        var category = CharUnicodeInfo.GetUnicodeCategory(s, i);
        return category == UnicodeCategory.Control || category == UnicodeCategory.Format;
    }

    private static bool IsPunctuation(string s, int i)
    {
        int cp = s[i];
        if ((cp >= 33 && cp <= 47) || (cp >= 58 && cp <= 64) || (cp >= 91 && cp <= 96) || (cp >= 123 && cp <= 126))
        {
            return true;
        }

        switch (CharUnicodeInfo.GetUnicodeCategory(s, i))
        {
            case UnicodeCategory.ConnectorPunctuation:
            case UnicodeCategory.DashPunctuation:
            case UnicodeCategory.OpenPunctuation:
            case UnicodeCategory.ClosePunctuation:
            case UnicodeCategory.InitialQuotePunctuation:
            case UnicodeCategory.FinalQuotePunctuation:
            case UnicodeCategory.OtherPunctuation:
                return true;
            default:
                return false;
        }
    }

    private static bool IsCjk(int cp) =>
        (cp >= 0x4E00 && cp <= 0x9FFF) || (cp >= 0x3400 && cp <= 0x4DBF) || (cp >= 0x20000 && cp <= 0x2A6DF) ||
        (cp >= 0x2A700 && cp <= 0x2B73F) || (cp >= 0x2B740 && cp <= 0x2B81F) || (cp >= 0x2B820 && cp <= 0x2CEAF) ||
        (cp >= 0xF900 && cp <= 0xFAFF) || (cp >= 0x2F800 && cp <= 0x2FA1F);
}
