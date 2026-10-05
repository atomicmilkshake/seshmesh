using System;
using System.Collections.Generic;
using System.Linq;

namespace Casr.Core.Search;

/// <summary>
/// Embedding backend contract. The hashing provider is the default; the ONNX
/// MiniLM provider is opt-in (see <see cref="TextEmbedder"/>). Every provider
/// stamps its <see cref="ModelId"/> + <see cref="Dims"/> on each stored row so
/// the SQL prefilter never scores vectors across models.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>Stored in the model column; must be unique per model file.</summary>
    string ModelId { get; }
    /// <summary>Vector width; stored in the dims column.</summary>
    int Dims { get; }
    /// <summary>Honest UI-facing label (must never claim "semantic" unless neural).</summary>
    string DisplayLabel { get; }
    float[] Embed(string? text);
    float[] EmbedSession(IEnumerable<(string? Content, int WeightHint)> parts);

    /// <summary>
    /// Embeds many texts in one pass (same order as the input; blank inputs yield zero
    /// vectors). The default loops <see cref="Embed"/>; the ONNX provider overrides it
    /// to run one batched inference — the actual CPU/GPU throughput win.
    /// </summary>
    float[][] EmbedBatch(IReadOnlyList<string?> texts)
    {
        var result = new float[texts.Count][];
        for (var i = 0; i < texts.Count; i++) result[i] = Embed(texts[i]);
        return result;
    }
}

/// <summary>
/// Zero-dependency dense text embedder (hashing-trick word + char-trigram vectors).
/// Deterministic, offline, hermetic-testable. Produces L2-normalized float vectors
/// so cosine similarity is a dot product. This is keyword/trigram similarity,
/// NOT neural semantic search.
/// </summary>
public sealed class HashingEmbedder : IEmbeddingProvider
{
    public static readonly HashingEmbedder Instance = new();

    public string ModelId => "hashing-trigram-v1";
    public int Dims => 512;
    public string DisplayLabel => "Keyword similarity (offline trigram — not neural semantic search)";

    public float[] Embed(string? text)
    {
        var vec = new float[Dims];
        if (string.IsNullOrWhiteSpace(text)) return vec;

        var lower = text.ToLowerInvariant();
        // Token features (weight 1.0).
        var tokens = Tokenize(lower);
        foreach (var tok in tokens)
        {
            vec[Hash("t:" + tok)] += 1.0f;
        }
        // Char-trigram features per token (weight 0.3) — captures morphology/typos.
        foreach (var tok in tokens)
        {
            if (tok.Length < 3) continue;
            var padded = "#" + tok + "#";
            for (var i = 0; i + 3 <= padded.Length; i++)
            {
                vec[Hash("g:" + padded.Substring(i, 3))] += 0.3f;
            }
        }

        return EmbeddingVectors.L2Normalize(vec);
    }

    public float[] EmbedSession(IEnumerable<(string? Content, int WeightHint)> parts)
        => EmbeddingVectors.AverageSession(this, parts);

    /// <summary>
    /// Lowercase letter/digit/_ runs. Single-character tokens are dropped (length &lt; 2):
    /// with only 512 hashing buckets they add collisions without discrimination.
    /// </summary>
    internal static List<string> Tokenize(string lower)
    {
        var tokens = new List<string>();
        int start = -1;
        for (var i = 0; i <= lower.Length; i++)
        {
            var c = i < lower.Length ? lower[i] : ' ';
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
            {
                if (i - start >= 2) tokens.Add(lower.Substring(start, i - start));
                start = -1;
            }
        }
        return tokens;
    }

    // Stable FNV-1a 32-bit — GetHashCode is runtime-randomized, must not be used.
    private int Hash(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var c in s)
            {
                h ^= c;
                h *= 16777619;
            }
            return (int)(h % (uint)Dims);
        }
    }
}

/// <summary>
/// Provider-agnostic vector math shared by all <see cref="IEmbeddingProvider"/>
/// implementations: session averaging (length-weighted, first/last emphasis) and
/// cosine / byte-codec helpers. Lives here so every provider's
/// <c>EmbedSession</c> applies identical weighting — only the per-text
/// <c>Embed</c> differs.
/// </summary>
public static class EmbeddingVectors
{
    /// <summary>
    /// Average message vectors (length-weighted, capped) into one session vector.
    /// Per-message text is capped at 5000 chars; the first and last non-empty messages
    /// (usually the task statement and the outcome) carry 1.5x weight so long middle
    /// context cannot drown them out.
    /// </summary>
    public static float[] AverageSession(
        IEmbeddingProvider provider, IEnumerable<(string? Content, int WeightHint)> parts)
    {
        var list = parts.ToList();
        var dims = provider.Dims;
        var firstIdx = list.FindIndex(p => !string.IsNullOrWhiteSpace(p.Content));
        var lastIdx = list.FindLastIndex(p => !string.IsNullOrWhiteSpace(p.Content));

        // Collect the non-empty parts and embed them in bounded batches (one inference
        // per batch on the neural provider). Weighting below is unchanged.
        var eligible = new List<(int Index, string Text, int Hint)>(list.Count);
        for (var pi = 0; pi < list.Count; pi++)
        {
            var (content, hint) = list[pi];
            if (string.IsNullOrWhiteSpace(content)) continue;
            var text = content.Length > 5000 ? content.Substring(0, 5000) : content;
            eligible.Add((pi, text, hint));
        }

        const int embedBatchSize = 64;
        var vectors = new float[eligible.Count][];
        for (var start = 0; start < eligible.Count; start += embedBatchSize)
        {
            var count = Math.Min(embedBatchSize, eligible.Count - start);
            var chunk = new List<string?>(count);
            for (var j = 0; j < count; j++) chunk.Add(eligible[start + j].Text);
            var batch = provider.EmbedBatch(chunk);
            for (var j = 0; j < count && j < batch.Length; j++) vectors[start + j] = batch[j];
        }

        var acc = new double[dims];
        double totalWeight = 0;
        for (var i = 0; i < eligible.Count; i++)
        {
            var (index, text, hint) = eligible[i];
            var v = vectors[i];
            if (v == null || v.Length != dims) continue; // provider contract violation: skip, don't corrupt
            var w = Math.Max(1, Math.Min(500, hint > 0 ? hint : text.Length));
            var weight = Math.Log(1 + w);
            if (index == firstIdx || index == lastIdx) weight *= 1.5;
            for (var d = 0; d < dims; d++) acc[d] += v[d] * weight;
            totalWeight += weight;
        }
        var outVec = new float[dims];
        if (totalWeight <= 0) return outVec;
        double norm = 0;
        for (var i = 0; i < dims; i++) norm += acc[i] * acc[i];
        norm = Math.Sqrt(norm);
        if (norm < 1e-9) return outVec;
        for (var i = 0; i < dims; i++) outVec[i] = (float)(acc[i] / norm);
        return outVec;
    }

    public static float[] L2Normalize(float[] vec)
    {
        double norm = 0;
        for (var i = 0; i < vec.Length; i++) norm += (double)vec[i] * vec[i];
        norm = Math.Sqrt(norm);
        if (norm > 1e-9)
        {
            for (var i = 0; i < vec.Length; i++) vec[i] = (float)(vec[i] / norm);
        }
        return vec;
    }

    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        if (na < 1e-12 || nb < 1e-12) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    public static byte[] ToBytes(float[] vec)
    {
        var bytes = new byte[vec.Length * 4];
        Buffer.BlockCopy(vec, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] FromBytes(byte[] bytes, int dims)
    {
        var vec = new float[dims];
        if (bytes.Length < dims * 4) return vec;
        Buffer.BlockCopy(bytes, 0, vec, 0, dims * 4);
        return vec;
    }
}
