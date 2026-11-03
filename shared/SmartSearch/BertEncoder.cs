using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Moonfin.SmartSearch;

/// <summary>
/// A plain managed forward pass of a BERT sentence encoder, mean pooled and L2 normalized, so a
/// dot product of two embeddings is their cosine similarity. No native runtime, so it runs
/// wherever the server does.
/// </summary>
public sealed class BertEncoder
{
    private const string WordEmbeddings = "embeddings.word_embeddings.weight";

    private readonly ITokenizer _tokenizer;
    private readonly int _hidden;
    private readonly int _heads;
    private readonly int _intermediate;
    private readonly int _maxTokens;
    private readonly float _eps;
    // Held so the word table stays mapped for as long as the encoder is in use.
    private readonly MemoryMappedFile _modelFile;
    private readonly MemoryMappedViewAccessor _wordEmb;
    private readonly float[] _posEmb;
    private readonly float[] _typeEmb;
    private readonly float[] _embLnW;
    private readonly float[] _embLnB;
    private readonly Layer[] _layers;

    // Scratch buffers run to a few MB, so each call borrows a set instead of allocating its own.
    private readonly ConcurrentBag<Workspace> _workspaces = new ConcurrentBag<Workspace>();

    private BertEncoder(ITokenizer tokenizer, JsonElement config, Dictionary<string, float[]> w, MemoryMappedFile modelFile, MemoryMappedViewAccessor wordEmb, int maxTokens)
    {
        _tokenizer = tokenizer;
        _modelFile = modelFile;
        _wordEmb = wordEmb;
        _hidden = config.GetProperty("hidden_size").GetInt32();
        _heads = config.GetProperty("num_attention_heads").GetInt32();
        _intermediate = config.GetProperty("intermediate_size").GetInt32();
        _eps = (float)config.GetProperty("layer_norm_eps").GetDouble();
        _maxTokens = Math.Min(maxTokens, config.GetProperty("max_position_embeddings").GetInt32());

        _posEmb = w["embeddings.position_embeddings.weight"];
        _typeEmb = w["embeddings.token_type_embeddings.weight"];
        _embLnW = w["embeddings.LayerNorm.weight"];
        _embLnB = w["embeddings.LayerNorm.bias"];

        _layers = new Layer[config.GetProperty("num_hidden_layers").GetInt32()];
        for (var i = 0; i < _layers.Length; i++)
        {
            var p = $"encoder.layer.{i}.";
            _layers[i] = new Layer
            {
                Qw = w[p + "attention.self.query.weight"], Qb = w[p + "attention.self.query.bias"],
                Kw = w[p + "attention.self.key.weight"], Kb = w[p + "attention.self.key.bias"],
                Vw = w[p + "attention.self.value.weight"], Vb = w[p + "attention.self.value.bias"],
                Ow = w[p + "attention.output.dense.weight"], Ob = w[p + "attention.output.dense.bias"],
                Ln1W = w[p + "attention.output.LayerNorm.weight"], Ln1B = w[p + "attention.output.LayerNorm.bias"],
                Iw = w[p + "intermediate.dense.weight"], Ib = w[p + "intermediate.dense.bias"],
                O2w = w[p + "output.dense.weight"], O2b = w[p + "output.dense.bias"],
                Ln2W = w[p + "output.LayerNorm.weight"], Ln2B = w[p + "output.LayerNorm.bias"],
            };
        }
    }

    /// <summary>
    /// Loads config.json, model.safetensors and either a WordPiece vocab.txt or a SentencePiece
    /// tokenizer.json from one folder. The word table is most of a model's size, so it stays on
    /// disk and only the rows a text uses are read.
    /// </summary>
    public static BertEncoder Load(string folder, int maxTokens = 256)
    {
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "config.json")));
        var sentencePiece = Path.Combine(folder, "tokenizer.json");
        ITokenizer tokenizer = File.Exists(sentencePiece)
            ? new UnigramTokenizer(sentencePiece)
            : new WordPieceTokenizer(File.ReadAllLines(Path.Combine(folder, "vocab.txt")));
        var modelPath = Path.Combine(folder, "model.safetensors");
        var weights = ReadSafetensors(modelPath, out var wordsOffset, out var wordsLength);
        var modelFile = MemoryMappedFile.CreateFromFile(modelPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        var words = modelFile.CreateViewAccessor(wordsOffset, wordsLength, MemoryMappedFileAccess.Read);
        return new BertEncoder(tokenizer, config.RootElement.Clone(), weights, modelFile, words, maxTokens);
    }

    /// <summary>The unit-length sentence embedding of the text. Safe to call from several threads.</summary>
    public float[] Embed(string text)
    {
        var ids = _tokenizer.Encode(text, _maxTokens);
        var work = _workspaces.TryTake(out var spare) ? spare : new Workspace(_maxTokens, _hidden, _intermediate);
        try
        {
            return Embed(ids, work);
        }
        finally
        {
            _workspaces.Add(work);
        }
    }

    private float[] Embed(int[] ids, Workspace work)
    {
        int t = ids.Length, h = _hidden;
        var x = work.X;
        var word = new float[h];
        for (var i = 0; i < t; i++)
        {
            _wordEmb.ReadArray((long)ids[i] * h * sizeof(float), word, 0, h);
            for (var j = 0; j < h; j++)
            {
                x[i * h + j] = word[j] + _posEmb[i * h + j] + _typeEmb[j];
            }
        }

        LayerNorm(x, t, h, _embLnW, _embLnB, _eps);

        var q = work.Q;
        var k = work.K;
        var v = work.V;
        var ctx = work.Ctx;
        var attn = work.Attn;
        var inter = work.Inter;
        var scores = work.Scores;

        foreach (var L in _layers)
        {
            Linear(x, t, h, L.Qw, L.Qb, h, q);
            Linear(x, t, h, L.Kw, L.Kb, h, k);
            Linear(x, t, h, L.Vw, L.Vb, h, v);
            Attention(q, k, v, t, h, _heads, scores, ctx);

            Linear(ctx, t, h, L.Ow, L.Ob, h, attn);
            Add(attn, x, t * h);
            LayerNorm(attn, t, h, L.Ln1W, L.Ln1B, _eps);

            Linear(attn, t, h, L.Iw, L.Ib, _intermediate, inter);
            for (var i = 0; i < t * _intermediate; i++)
            {
                inter[i] = Gelu(inter[i]);
            }

            Linear(inter, t, _intermediate, L.O2w, L.O2b, h, x);
            Add(x, attn, t * h);
            LayerNorm(x, t, h, L.Ln2W, L.Ln2B, _eps);
        }

        var pooled = new float[h];
        for (var i = 0; i < t; i++)
        {
            for (var j = 0; j < h; j++)
            {
                pooled[j] += x[i * h + j];
            }
        }

        Normalize(pooled);
        return pooled;
    }

    private static void Normalize(float[] vector)
    {
        var norm = MathF.Sqrt(Dot(vector, vector));
        if (norm > 0)
        {
            for (var j = 0; j < vector.Length; j++)
            {
                vector[j] /= norm;
            }
        }
    }

    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
        var acc = Vector<float>.Zero;
        for (var i = 0; i < va.Length; i++)
        {
            acc += va[i] * vb[i];
        }

        var sum = Vector.Dot(acc, Vector<float>.One);
        for (var i = va.Length * Vector<float>.Count; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    // y[rows, outDim] = x[rows, inDim] * W^T + b, with W stored [outDim, inDim] as PyTorch saves it.
    // Two rows and four outputs at a time share their loads and keep eight sums going at once,
    // which runs about twice as fast as one dot product per output.
    private static void Linear(float[] x, int rows, int inDim, float[] w, float[] b, int outDim, float[] y)
    {
        var o = 0;
        if (inDim % Vector<float>.Count == 0)
        {
            for (; o + 4 <= outDim; o += 4)
            {
                var w0 = MemoryMarshal.Cast<float, Vector<float>>(new ReadOnlySpan<float>(w, o * inDim, inDim));
                var w1 = MemoryMarshal.Cast<float, Vector<float>>(new ReadOnlySpan<float>(w, (o + 1) * inDim, inDim));
                var w2 = MemoryMarshal.Cast<float, Vector<float>>(new ReadOnlySpan<float>(w, (o + 2) * inDim, inDim));
                var w3 = MemoryMarshal.Cast<float, Vector<float>>(new ReadOnlySpan<float>(w, (o + 3) * inDim, inDim));
                var r = 0;
                for (; r + 2 <= rows; r += 2)
                {
                    var xa = MemoryMarshal.Cast<float, Vector<float>>(new ReadOnlySpan<float>(x, r * inDim, inDim));
                    var xb = MemoryMarshal.Cast<float, Vector<float>>(new ReadOnlySpan<float>(x, (r + 1) * inDim, inDim));
                    Vector<float> a0 = Vector<float>.Zero, a1 = Vector<float>.Zero, a2 = Vector<float>.Zero, a3 = Vector<float>.Zero;
                    Vector<float> b0 = Vector<float>.Zero, b1 = Vector<float>.Zero, b2 = Vector<float>.Zero, b3 = Vector<float>.Zero;
                    for (var i = 0; i < xa.Length; i++)
                    {
                        Vector<float> va = xa[i], vb = xb[i], v0 = w0[i], v1 = w1[i], v2 = w2[i], v3 = w3[i];
                        a0 += va * v0;
                        a1 += va * v1;
                        a2 += va * v2;
                        a3 += va * v3;
                        b0 += vb * v0;
                        b1 += vb * v1;
                        b2 += vb * v2;
                        b3 += vb * v3;
                    }

                    Store(y, r * outDim + o, b, o, a0, a1, a2, a3);
                    Store(y, (r + 1) * outDim + o, b, o, b0, b1, b2, b3);
                }

                for (; r < rows; r++)
                {
                    var xr = MemoryMarshal.Cast<float, Vector<float>>(new ReadOnlySpan<float>(x, r * inDim, inDim));
                    Vector<float> s0 = Vector<float>.Zero, s1 = Vector<float>.Zero, s2 = Vector<float>.Zero, s3 = Vector<float>.Zero;
                    for (var i = 0; i < xr.Length; i++)
                    {
                        var xv = xr[i];
                        s0 += xv * w0[i];
                        s1 += xv * w1[i];
                        s2 += xv * w2[i];
                        s3 += xv * w3[i];
                    }

                    Store(y, r * outDim + o, b, o, s0, s1, s2, s3);
                }
            }
        }

        for (; o < outDim; o++)
        {
            var row = new ReadOnlySpan<float>(w, o * inDim, inDim);
            for (var r = 0; r < rows; r++)
            {
                y[r * outDim + o] = Dot(new ReadOnlySpan<float>(x, r * inDim, inDim), row) + b[o];
            }
        }
    }

    private static void Store(float[] y, int at, float[] b, int o, Vector<float> s0, Vector<float> s1, Vector<float> s2, Vector<float> s3)
    {
        y[at] = Vector.Dot(s0, Vector<float>.One) + b[o];
        y[at + 1] = Vector.Dot(s1, Vector<float>.One) + b[o + 1];
        y[at + 2] = Vector.Dot(s2, Vector<float>.One) + b[o + 2];
        y[at + 3] = Vector.Dot(s3, Vector<float>.One) + b[o + 3];
    }

    private static void Attention(float[] q, float[] k, float[] v, int t, int h, int heads, float[] scores, float[] ctx)
    {
        var hd = h / heads;
        var scale = 1f / MathF.Sqrt(hd);
        Array.Clear(ctx, 0, t * h);
        for (var head = 0; head < heads; head++)
        {
            var off = head * hd;
            for (var i = 0; i < t; i++)
            {
                var qi = new ReadOnlySpan<float>(q, i * h + off, hd);
                var max = float.NegativeInfinity;
                for (var j = 0; j < t; j++)
                {
                    scores[j] = Dot(qi, new ReadOnlySpan<float>(k, j * h + off, hd)) * scale;
                    max = MathF.Max(max, scores[j]);
                }

                var sum = 0f;
                for (var j = 0; j < t; j++)
                {
                    scores[j] = MathF.Exp(scores[j] - max);
                    sum += scores[j];
                }

                var outRow = new Span<float>(ctx, i * h + off, hd);
                for (var j = 0; j < t; j++)
                {
                    var p = scores[j] / sum;
                    var vj = new ReadOnlySpan<float>(v, j * h + off, hd);
                    for (var d = 0; d < hd; d++)
                    {
                        outRow[d] += p * vj[d];
                    }
                }
            }
        }
    }

    private static void Add(float[] target, float[] other, int count)
    {
        for (var i = 0; i < count; i++)
        {
            target[i] += other[i];
        }
    }

    private static void LayerNorm(float[] x, int rows, int h, float[] w, float[] b, float eps)
    {
        for (var r = 0; r < rows; r++)
        {
            var row = new Span<float>(x, r * h, h);
            var mean = 0f;
            foreach (var value in row)
            {
                mean += value;
            }

            mean /= h;
            var variance = 0f;
            foreach (var value in row)
            {
                variance += (value - mean) * (value - mean);
            }

            var inv = 1f / MathF.Sqrt(variance / h + eps);
            for (var j = 0; j < h; j++)
            {
                row[j] = (row[j] - mean) * inv * w[j] + b[j];
            }
        }
    }

    private static float Gelu(float x) => 0.5f * x * (1f + Erf(x / MathF.Sqrt(2f)));

    // Abramowitz and Stegun 7.1.26, within 1.5e-7 of the true erf.
    private static float Erf(float x)
    {
        var sign = x < 0 ? -1f : 1f;
        x = MathF.Abs(x);
        var t = 1f / (1f + 0.3275911f * x);
        var y = 1f - (((((1.061405429f * t - 1.453152027f) * t) + 1.421413741f) * t - 0.284496736f) * t + 0.254829592f) * t * MathF.Exp(-x * x);
        return sign * y;
    }

    private static Dictionary<string, float[]> ReadSafetensors(string path, out long wordsOffset, out long wordsLength)
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new NotSupportedException("Smart search needs a little-endian CPU.");
        }

        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        var headerLength = (long)reader.ReadUInt64();
        using var header = JsonDocument.Parse(reader.ReadBytes(checked((int)headerLength)));
        var dataStart = 8 + headerLength;
        var tensors = new Dictionary<string, float[]>(StringComparer.Ordinal);
        wordsOffset = wordsLength = 0;
        foreach (var tensor in header.RootElement.EnumerateObject())
        {
            if (tensor.Name == "__metadata__")
            {
                continue;
            }

            // The forward pass only reads float weights. Integer buffers like position_ids are skipped.
            if (tensor.Value.GetProperty("dtype").GetString() != "F32")
            {
                continue;
            }

            var offsets = tensor.Value.GetProperty("data_offsets");
            var start = offsets[0].GetInt64();
            if (tensor.Name == WordEmbeddings)
            {
                wordsOffset = dataStart + start;
                wordsLength = offsets[1].GetInt64() - start;
                continue;
            }

            var values = new float[(offsets[1].GetInt64() - start) / 4];
            stream.Position = dataStart + start;
            var bytes = MemoryMarshal.AsBytes(values.AsSpan());
            while (bytes.Length > 0)
            {
                var read = stream.Read(bytes);
                if (read <= 0)
                {
                    throw new EndOfStreamException(tensor.Name);
                }

                bytes = bytes.Slice(read);
            }

            tensors[tensor.Name] = values;
        }

        return tensors;
    }

    private sealed class Workspace
    {
        public Workspace(int tokens, int hidden, int intermediate)
        {
            X = new float[tokens * hidden];
            Q = new float[tokens * hidden];
            K = new float[tokens * hidden];
            V = new float[tokens * hidden];
            Ctx = new float[tokens * hidden];
            Attn = new float[tokens * hidden];
            Inter = new float[tokens * intermediate];
            Scores = new float[tokens];
        }

        public float[] X { get; }

        public float[] Q { get; }

        public float[] K { get; }

        public float[] V { get; }

        public float[] Ctx { get; }

        public float[] Attn { get; }

        public float[] Inter { get; }

        public float[] Scores { get; }
    }

    private sealed class Layer
    {
        public float[] Qw = Array.Empty<float>(), Qb = Array.Empty<float>(), Kw = Array.Empty<float>(), Kb = Array.Empty<float>();
        public float[] Vw = Array.Empty<float>(), Vb = Array.Empty<float>(), Ow = Array.Empty<float>(), Ob = Array.Empty<float>();
        public float[] Ln1W = Array.Empty<float>(), Ln1B = Array.Empty<float>(), Iw = Array.Empty<float>(), Ib = Array.Empty<float>();
        public float[] O2w = Array.Empty<float>(), O2b = Array.Empty<float>(), Ln2W = Array.Empty<float>(), Ln2B = Array.Empty<float>();
    }
}

public interface ITokenizer
{
    /// <summary>Token ids wrapped in the model's start and end tokens, cut to maxTokens.</summary>
    int[] Encode(string text, int maxTokens);
}
