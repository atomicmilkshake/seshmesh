using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Casr.Core.Logging;
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
    private readonly string _providerLabel;
    private bool _disposed;

    private static bool? _runtimeAvailable;

    /// <summary>
    /// Execution provider actually in use: <c>CUDA (GPU)</c> when the native
    /// runtime was built with CUDA support and the CUDA 13 + cuDNN 9 DLLs
    /// resolved, otherwise <c>CPU</c> (fallback — never an error).
    /// </summary>
    public string ProviderLabel => _providerLabel;

    private OnnxEmbedder(string modelPath, string vocabPath)
    {
        _tokenizer = MiniLmTokenizer.Load(vocabPath);
        CudaSupport.EnsureRuntimeSearchPath();
        _session = CreateSession(modelPath, out _providerLabel);
        _inputNames = new HashSet<string>(_session.InputMetadata.Keys, StringComparer.Ordinal);
    }

    private static InferenceSession CreateSession(string modelPath, out string providerLabel)
    {
        // Optional CUDA: append the EP when the native runtime supports it and the
        // CUDA/cuDNN DLLs resolve; otherwise (CPU build, no GPU, missing cuDNN) fall
        // back to the CPU provider without failing the whole neural feature.
        // CASR_ONNX_CPU=1 forces the CPU provider (escape hatch for broken drivers).
        var forceCpu = string.Equals(Environment.GetEnvironmentVariable("CASR_ONNX_CPU"), "1", StringComparison.Ordinal);
        if (forceCpu)
        {
            providerLabel = "CPU";
            CasrLogger.Info("ONNX", "MiniLM execution provider: CPU (forced by CASR_ONNX_CPU=1)");
            return new InferenceSession(modelPath);
        }

        try
        {
            var options = new SessionOptions();
            try
            {
                options.AppendExecutionProvider_CUDA(0);
                var session = new InferenceSession(modelPath, options);
                providerLabel = "CUDA (GPU)";
                CasrLogger.Info("ONNX", $"MiniLM execution provider: CUDA device 0 ({Path.GetFileName(modelPath)})");
                return session;
            }
            catch (Exception ex)
            {
                CasrLogger.Info("ONNX", $"CUDA execution provider unavailable ({ex.Message}); using the CPU provider.");
            }
            finally
            {
                options.Dispose();
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("ONNX", $"CUDA setup failed before provider append: {ex.Message}");
        }

        providerLabel = "CPU";
        CasrLogger.Info("ONNX", "MiniLM execution provider: CPU");
        return new InferenceSession(modelPath);
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

    /// <summary>Texts per ONNX Run call; bounds activation memory on CPU and GPU.</summary>
    public const int MaxBatchSize = 32;

    public float[] Embed(string? text) => EmbedBatch(new[] { text })[0];

    /// <summary>
    /// Batched inference: tokenizes every text, pads to the batch's longest sequence,
    /// and runs one ONNX call per <see cref="MaxBatchSize"/> chunk. Blank inputs yield
    /// zero vectors in place. Output is element-wise identical to single-text calls.
    /// </summary>
    public float[][] EmbedBatch(IReadOnlyList<string?> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var result = new float[texts.Count][];
        var pending = new List<int>(texts.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(texts[i]))
                result[i] = new float[Dims]; // blank input: zero vector, same contract as Embed
            else
                pending.Add(i);
        }

        for (var start = 0; start < pending.Count; start += MaxBatchSize)
        {
            var count = Math.Min(MaxBatchSize, pending.Count - start);
            var encoded = new (long[] Ids, long[] Mask, long[] TypeIds)[count];
            var maxLength = 0;
            for (var j = 0; j < count; j++)
            {
                encoded[j] = _tokenizer.Encode(texts[pending[start + j]]!, MaxSequenceLength);
                if (encoded[j].Ids.Length > maxLength) maxLength = encoded[j].Ids.Length;
            }

            var ids = new long[count * maxLength];
            var mask = new long[count * maxLength];
            var typeIds = new long[count * maxLength];
            for (var j = 0; j < count; j++)
            {
                Array.Copy(encoded[j].Ids, 0, ids, j * maxLength, encoded[j].Ids.Length);
                Array.Copy(encoded[j].Mask, 0, mask, j * maxLength, encoded[j].Mask.Length);
                // token_type_ids stay zero (single-segment input), matching Encode's output.
            }

            var inputs = new List<NamedOnnxValue>(3);
            try
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, new[] { count, maxLength })));
                inputs.Add(NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(mask, new[] { count, maxLength })));
                if (_inputNames.Contains("token_type_ids"))
                    inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(typeIds, new[] { count, maxLength })));

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
                    var (flat, seq, hidden) = DecodeHiddenStatesBatch(results[0].Value, count, Dims);
                    for (var j = 0; j < count; j++)
                    {
                        var pooled = MeanPoolRow(flat, j * seq * hidden, mask, j * maxLength, seq, hidden);
                        result[pending[start + j]] = EmbeddingVectors.L2Normalize(pooled);
                    }
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

        return result;
    }

    public float[] EmbedSession(IEnumerable<(string? Content, int WeightHint)> parts)
        => EmbeddingVectors.AverageSession(this, parts);

    /// <summary>
    /// f32/f16-agnostic decode of a batched last-hidden-state tensor
    /// ([batch, seq, hidden]) into flat storage + its real seq/hidden.
    /// </summary>
    internal static (float[] Flat, int Seq, int Hidden) DecodeHiddenStatesBatch(object? raw, int expectedBatch, int expectedHidden)
    {
        float[] flat;
        int batch, seq, hidden;
        if (raw is Tensor<float> f32)
        {
            if (f32.Dimensions.Length != 3 || f32.Dimensions[0] != expectedBatch)
                throw new InvalidOperationException(
                    $"Unexpected embedding tensor shape [{string.Join(",", f32.Dimensions.ToArray())}]: expected [{expectedBatch}, seq, hidden].");
            batch = f32.Dimensions[0];
            seq = f32.Dimensions[1];
            hidden = f32.Dimensions[2];
            flat = f32.ToArray();
        }
        else if (raw is Tensor<Half> f16)
        {
            if (f16.Dimensions.Length != 3 || f16.Dimensions[0] != expectedBatch)
                throw new InvalidOperationException(
                    $"Unexpected embedding tensor shape [{string.Join(",", f16.Dimensions.ToArray())}]: expected [{expectedBatch}, seq, hidden].");
            batch = f16.Dimensions[0];
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
        if (flat.Length != batch * seq * hidden)
            throw new InvalidOperationException("Embedding tensor storage length does not match its shape.");
        return (flat, seq, hidden);
    }

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

    /// <summary>Attention-masked mean pooling over one row of [batch, seq, hidden] storage.</summary>
    internal static float[] MeanPoolRow(float[] flat, int flatOffset, long[] mask, int maskOffset, int seq, int hidden)
    {
        var pooled = new float[hidden];
        double count = 0;
        for (var s = 0; s < seq; s++)
        {
            if (mask[maskOffset + s] == 0) continue;
            count += 1;
            var idx = flatOffset + s * hidden;
            for (var h = 0; h < hidden; h++) pooled[h] += flat[idx + h];
        }
        if (count < 1) return pooled;
        for (var h = 0; h < hidden; h++) pooled[h] = (float)(pooled[h] / count);
        return pooled;
    }

    /// <summary>Attention-masked mean pooling of a single [seq, hidden] block.</summary>
    internal static float[] MeanPool(float[] flat, long[] mask, int hidden)
        => MeanPoolRow(flat, 0, mask, 0, mask.Length, hidden);

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
