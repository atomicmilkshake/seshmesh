using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Casr.Core.Search;

/// <summary>
/// Neural embedding provider: MiniLM-class transformer exported to ONNX,
/// run offline via Microsoft.ML.OnnxRuntime. 384-dim L2-normalized vectors
/// with mean-pooling over the attention-masked token states.
/// Opt-in only — construction throws a clear "model not downloaded" error
/// when the model file is absent (see <see cref="EmbeddingModelManager"/>).
/// Vectors are real model outputs; this class never fabricates embeddings.
/// </summary>
public sealed class OnnxEmbedder : IEmbeddingProvider, IDisposable
{
    public string ModelId => "minilm-l6-v2-onnx";
    public int Dims => 384;
    public string DisplayLabel => "Neural semantic search (MiniLM, offline ONNX)";

    /// <summary>Token cap per text; MiniLM supports 512, 256 is ample for messages.</summary>
    public const int MaxSequenceLength = 256;

    private readonly InferenceSession _session;
    private readonly MiniLmTokenizer _tokenizer;
    private readonly object _runGate = new();
    private readonly HashSet<string> _inputNames;
    private bool _disposed;

    private static bool? _runtimeAvailable;

    private OnnxEmbedder(string modelPath, string vocabPath)
    {
        _tokenizer = MiniLmTokenizer.Load(vocabPath);
        _session = new InferenceSession(modelPath);
        _inputNames = new HashSet<string>(_session.InputMetadata.Keys, StringComparer.Ordinal);
    }

    /// <summary>
    /// True when the ONNX native runtime loads in this process. Probes once
    /// and caches; never throws.
    /// </summary>
    public static bool IsRuntimeAvailable
    {
        get
        {
            if (_runtimeAvailable.HasValue) return _runtimeAvailable.Value;
            try
            {
                _ = OrtEnv.Instance();
                _runtimeAvailable = true;
            }
            catch (Exception)
            {
                _runtimeAvailable = false;
            }
            return _runtimeAvailable.Value;
        }
    }

    /// <summary>
    /// Creates the provider from the managed models dir. Returns false (never
    /// throws for missing files) with a human-readable error when the runtime
    /// or model is unavailable.
    /// </summary>
    public static bool TryCreate(out OnnxEmbedder? embedder, out string? error)
    {
        embedder = null;
        if (!IsRuntimeAvailable)
        {
            error = "ONNX runtime unavailable: the native onnxruntime library could not be loaded " +
                    "in this process. Semantic search stays on the hashing provider.";
            EmbeddingModelManager.SetError(error);
            return false;
        }
        if (!EmbeddingModelManager.TryEnsureReady(out error))
        {
            return false;
        }
        try
        {
            embedder = new OnnxEmbedder(EmbeddingModelManager.ModelPath, EmbeddingModelManager.VocabPath);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Cannot load embedding model '{EmbeddingModelManager.ModelPath}': {ex.Message}";
            EmbeddingModelManager.SetError(error);
            return false;
        }
    }

    public float[] Embed(string? text)
    {
        var vec = new float[Dims];
        if (string.IsNullOrWhiteSpace(text)) return vec;
        ObjectDisposedException.ThrowIf(_disposed, this);

        var (ids, mask, typeIds) = _tokenizer.Encode(text, MaxSequenceLength);
        var inputs = new List<NamedOnnxValue>(3);
        try
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("input_ids", ToInt64Tensor(ids)));
            inputs.Add(NamedOnnxValue.CreateFromTensor("attention_mask", ToInt64Tensor(mask)));
            if (_inputNames.Contains("token_type_ids"))
                inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", ToInt64Tensor(typeIds)));

            List<DisposableNamedOnnxValue> results;
            lock (_runGate)
            {
                using var collection = _session.Run(inputs);
                results = collection.ToList();
            }
            try
            {
                if (results.Count == 0)
                    throw new InvalidOperationException("ONNX model returned no outputs.");
                var hidden = DecodeHiddenStates(results[0].Value, Dims);
                var pooled = MeanPool(hidden, mask, Dims);
                return EmbeddingVectors.L2Normalize(pooled);
            }
            finally
            {
                foreach (var r in results) r.Dispose();
            }
        }
        finally
        {
            inputs.Clear();
        }
    }

    public float[] EmbedSession(IEnumerable<(string? Content, int WeightHint)> parts)
        => EmbeddingVectors.AverageSession(this, parts);

    private static Tensor<long> ToInt64Tensor(long[] values)
        => new DenseTensor<long>(values, new[] { 1, values.Length });

    /// <summary>
    /// f32/f16-agnostic decode of the model's last-hidden-state tensor
    /// ([1, seq, hidden]) into a flat float array. Throws a descriptive error
    /// on rank/shape/type mismatch instead of silently misshaping.
    /// </summary>
    internal static float[] DecodeHiddenStates(object? raw, int expectedHidden)
    {
        float[] flat;
        int seq, hidden;
        if (raw is Tensor<float> f32)
        {
            if (f32.Dimensions.Length != 3 || f32.Dimensions[0] != 1)
                throw new InvalidOperationException(
                    $"Unexpected embedding tensor shape [{string.Join(",", f32.Dimensions.ToArray())}]: expected [1, seq, hidden].");
            seq = f32.Dimensions[1];
            hidden = f32.Dimensions[2];
            flat = f32.ToArray();
        }
        else if (raw is Tensor<Half> f16)
        {
            if (f16.Dimensions.Length != 3 || f16.Dimensions[0] != 1)
                throw new InvalidOperationException(
                    $"Unexpected embedding tensor shape [{string.Join(",", f16.Dimensions.ToArray())}]: expected [1, seq, hidden].");
            seq = f16.Dimensions[1];
            hidden = f16.Dimensions[2];
            var halves = f16.ToArray();
            flat = new float[halves.Length];
            for (var i = 0; i < halves.Length; i++) flat[i] = (float)halves[i];
        }
        else
        {
            throw new InvalidOperationException(
                $"Unsupported embedding tensor type '{raw?.GetType().FullName ?? "null"}': expected float32 or float16.");
        }
        if (hidden != expectedHidden)
            throw new InvalidOperationException(
                $"Model hidden size {hidden} != provider dims {expectedHidden}: refusing to store misshaped vectors.");
        if (flat.Length != seq * hidden)
            throw new InvalidOperationException("Embedding tensor storage length does not match its shape.");
        return flat;
    }

    /// <summary>Attention-masked mean pooling over [seq, hidden] storage.</summary>
    internal static float[] MeanPool(float[] flat, long[] mask, int hidden)
    {
        var seq = mask.Length;
        var pooled = new float[hidden];
        double count = 0;
        for (var s = 0; s < seq; s++)
        {
            if (mask[s] == 0) continue;
            count += 1;
            for (var h = 0; h < hidden; h++) pooled[h] += flat[s * hidden + h];
        }
        if (count < 1) return pooled;
        for (var h = 0; h < hidden; h++) pooled[h] = (float)(pooled[h] / count);
        return pooled;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_runGate) _session.Dispose();
    }

    /// <summary>Test hook: resets the cached runtime probe.</summary>
    internal static void ResetRuntimeProbe() => _runtimeAvailable = null;
}

/// <summary>
/// BERT WordPiece tokenizer (uncased) over a <c>vocab.txt</c> file.
/// Pure managed code — unit-testable without the native runtime or model.
/// </summary>
internal sealed class MiniLmTokenizer
{
    private readonly Dictionary<string, int> _vocab;
    private readonly int _unkId;
    private readonly int _clsId;
    private readonly int _sepId;

    private MiniLmTokenizer(Dictionary<string, int> vocab)
    {
        _vocab = vocab;
        if (!vocab.TryGetValue("[UNK]", out _unkId)) _unkId = 100;
        if (!vocab.TryGetValue("[CLS]", out _clsId)) _clsId = 101;
        if (!vocab.TryGetValue("[SEP]", out _sepId)) _sepId = 102;
    }

    public static MiniLmTokenizer Load(string vocabPath)
    {
        if (!File.Exists(vocabPath))
            throw new FileNotFoundException(
                $"Tokenizer vocabulary not downloaded: '{vocabPath}' is missing. " +
                "Run scripts/Download-EmbeddingModel.ps1 to fetch it.", vocabPath);
        var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        var lines = File.ReadAllLines(vocabPath);
        for (var i = 0; i < lines.Length; i++)
        {
            var tok = lines[i].TrimEnd('\r', '\n');
            if (!vocab.ContainsKey(tok)) vocab[tok] = i;
        }
        if (vocab.Count == 0)
            throw new InvalidDataException($"Tokenizer vocabulary is empty: '{vocabPath}'.");
        return new MiniLmTokenizer(vocab);
    }

    public int VocabSize => _vocab.Count;

    /// <summary>Encodes text to (inputIds, attentionMask, tokenTypeIds).</summary>
    public (long[] Ids, long[] Mask, long[] TypeIds) Encode(string text, int maxLength)
    {
        var ids = new List<long> { _clsId };
        foreach (var word in BasicTokenize(text.ToLowerInvariant()))
        {
            foreach (var piece in WordPiece(word))
            {
                ids.Add(piece);
                if (ids.Count >= maxLength - 1) break;
            }
            if (ids.Count >= maxLength - 1) break;
        }
        ids.Add(_sepId);
        var mask = Enumerable.Repeat(1L, ids.Count).ToArray();
        return (ids.ToArray(), mask, new long[ids.Count]);
    }

    internal static IEnumerable<string> BasicTokenize(string lower)
    {
        var cur = new System.Text.StringBuilder();
        foreach (var c in lower)
        {
            if (char.IsWhiteSpace(c))
            {
                if (cur.Length > 0) { yield return cur.ToString(); cur.Clear(); }
            }
            else if (char.IsPunctuation(c) || char.IsSymbol(c))
            {
                if (cur.Length > 0) { yield return cur.ToString(); cur.Clear(); }
                yield return c.ToString();
            }
            else
            {
                cur.Append(c);
            }
        }
        if (cur.Length > 0) yield return cur.ToString();
    }

    private IEnumerable<int> WordPiece(string word)
    {
        if (word.Length > 200) { yield return _unkId; yield break; } // BERT long-word rule
        var start = 0;
        var pieces = new List<int>();
        while (start < word.Length)
        {
            var end = word.Length;
            int? found = null;
            while (end > start)
            {
                var sub = start == 0 ? word.Substring(start, end - start) : "##" + word.Substring(start, end - start);
                if (_vocab.TryGetValue(sub, out var id)) { found = id; break; }
                end--;
            }
            if (!found.HasValue) { yield return _unkId; yield break; }
            pieces.Add(found.Value);
            start = end;
        }
        foreach (var p in pieces) yield return p;
    }
}
