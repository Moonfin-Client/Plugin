using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Moonfin.SmartSearch;

/// <summary>
/// The SentencePiece unigram tokenizer of XLM-R style models, read from a Hugging Face
/// tokenizer.json: NFKC, every word gets a leading "▁", then each word takes its most likely split.
/// </summary>
public sealed class UnigramTokenizer : ITokenizer
{
    private const char WordStart = '\u2581';
    private const double UnknownPenalty = 10;

    private readonly Dictionary<string, (int Id, double Score)> _pieces = new Dictionary<string, (int, double)>(StringComparer.Ordinal);
    private readonly int _maxPieceLength;
    private readonly double _unknownScore;
    private readonly int _unknown;
    private readonly int _start;
    private readonly int _end;

    // Read as a stream rather than a JsonDocument, whose pooled buffers for a 17 MB file would
    // stay in memory after it's disposed.
    public UnigramTokenizer(string tokenizerJsonPath)
    {
        var reader = new Utf8JsonReader(File.ReadAllBytes(tokenizerJsonPath));
        var id = 0;
        var minScore = 0.0;
        reader.Read();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isModel = reader.ValueTextEquals("model");
            reader.Read();
            if (!isModel)
            {
                reader.Skip();
                continue;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("unk_id"))
                {
                    reader.Read();
                    _unknown = reader.GetInt32();
                }
                else if (reader.ValueTextEquals("vocab"))
                {
                    reader.Read();
                    while (reader.Read() && reader.TokenType == JsonTokenType.StartArray)
                    {
                        reader.Read();
                        var piece = reader.GetString()!;
                        reader.Read();
                        var score = reader.GetDouble();
                        reader.Read();
                        _pieces[piece] = (id++, score);
                        _maxPieceLength = Math.Max(_maxPieceLength, piece.Length);
                        minScore = Math.Min(minScore, score);
                    }
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }
        }

        _unknownScore = minScore - UnknownPenalty;
        _start = _pieces["<s>"].Id;
        _end = _pieces["</s>"].Id;
    }

    public int[] Encode(string text, int maxTokens)
    {
        var ids = new List<int> { _start };
        var spaced = Normalize(text).Replace(' ', WordStart);
        if (spaced.Length > 0 && spaced[0] != WordStart)
        {
            spaced = WordStart + spaced;
        }

        for (var start = 0; start < spaced.Length && ids.Count < maxTokens - 1;)
        {
            var end = spaced.IndexOf(WordStart, start + 1);
            end = end < 0 ? spaced.Length : end;
            AddPieces(spaced.Substring(start, end - start), ids);
            start = end;
        }

        if (ids.Count > maxTokens - 1)
        {
            ids.RemoveRange(maxTokens - 1, ids.Count - (maxTokens - 1));
        }

        ids.Add(_end);
        return ids.ToArray();
    }

    // NFKC plus what SentencePiece's own table does on top: line breaks, tabs, zero-width and
    // direction marks become spaces, other control characters go, and runs of spaces become one.
    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormKC))
        {
            var space = c == '\t' || c == '\n' || c == '\f' || c == '\r' || c == WordStart || c == '\uFFFD' || c == '\uFEFF'
                || (c >= '\u200B' && c <= '\u200F') || (char.IsWhiteSpace(c) && !char.IsControl(c));
            if (space)
            {
                if (sb.Length == 0 || sb[sb.Length - 1] != ' ')
                {
                    sb.Append(' ');
                }
            }
            else if (!char.IsControl(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    // Viterbi over every split the vocabulary allows. A character no piece covers becomes an
    // unknown token, and unknowns in a row merge into one, as Hugging Face's tokenizer does.
    private void AddPieces(string word, List<int> ids)
    {
        var n = word.Length;
        var best = new double[n + 1];
        var from = new int[n + 1];
        var piece = new int[n + 1];
        for (var i = 1; i <= n; i++)
        {
            best[i] = double.NegativeInfinity;
        }

        for (var start = 0; start < n; start++)
        {
            if (double.IsNegativeInfinity(best[start]))
            {
                continue;
            }

            var charLength = char.IsHighSurrogate(word[start]) && start + 1 < n ? 2 : 1;
            var coversChar = false;
            for (var end = start + 1; end <= Math.Min(n, start + _maxPieceLength); end++)
            {
                if (!_pieces.TryGetValue(word.Substring(start, end - start), out var p))
                {
                    continue;
                }

                coversChar |= end - start == charLength;
                if (best[start] + p.Score > best[end])
                {
                    best[end] = best[start] + p.Score;
                    from[end] = start;
                    piece[end] = p.Id;
                }
            }

            if (!coversChar && best[start] + _unknownScore > best[start + charLength])
            {
                best[start + charLength] = best[start] + _unknownScore;
                from[start + charLength] = start;
                piece[start + charLength] = _unknown;
            }
        }

        var split = new List<int>();
        for (var i = n; i > 0; i = from[i])
        {
            split.Add(piece[i]);
        }

        var previous = -1;
        for (var i = split.Count - 1; i >= 0; i--)
        {
            if (split[i] != _unknown || previous != _unknown)
            {
                ids.Add(split[i]);
            }

            previous = split[i];
        }
    }
}
