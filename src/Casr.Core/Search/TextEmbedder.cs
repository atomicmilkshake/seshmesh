using System;
using System.Collections.Generic;

namespace Casr.Core.Search;

/// <summary>
/// Static embedding facade: every existing caller (indexing, semantic search)
/// keeps working unchanged. Delegates to the active <see cref="IEmbeddingProvider"/>
/// — <see cref="HashingEmbedder"/> by default, <see cref="OnnxEmbedder"/> after a
/// successful <see cref="TryEnableNeural"/>. Model/dims always reflect the active
/// provider so the SQL prefilter never cross-scores models.
/// NOTE on labels: the hashing default is keyword/trigram similarity, NOT neural
/// semantic search — any UI label built from this class must use
/// <see cref="DisplayLabel"/>, never "semantic", unless the neural provider is active.
/// </summary>
public static class TextEmbedder
{
    private static readonly object _gate = new();
    private static IEmbeddingProvider _active = HashingEmbedder.Instance;
    private static string? _lastError;

    /// <summary>Model id of the ACTIVE provider (stored in the model column).</summary>
    public static string ModelId => _active.ModelId;
    /// <summary>Honest UI-facing label of the ACTIVE provider.</summary>
    public static string DisplayLabel => _active.DisplayLabel;
    /// <summary>Execution device of the ACTIVE provider: "CUDA (GPU)", "CPU", or "offline".</summary>
    public static string DeviceLabel => _active is OnnxEmbedder onnx ? onnx.ProviderLabel : "offline";
    /// <summary>Vector width of the ACTIVE provider (stored in the dims column).</summary>
    public static int Dims => _active.Dims;

    /// <summary>Last provider-switch failure, or null when healthy.</summary>
    public static string? LastError
    {
        get { lock (_gate) return _lastError; }
    }

    /// <summary>True when the neural provider is the active one.</summary>
    public static bool IsNeuralActive => _active is OnnxEmbedder;

    public static float[] Embed(string? text) => _active.Embed(text);

    /// <summary>Embeds many texts in one provider pass (batched on the neural provider).</summary>
    public static float[][] EmbedBatch(IReadOnlyList<string?> texts) => _active.EmbedBatch(texts);

    /// <summary>
    /// Average message vectors (length-weighted, capped) into one session vector.
    /// Weighting is provider-agnostic (see <see cref="EmbeddingVectors"/>); only
    /// the per-text embedding differs per provider.
    /// </summary>
    public static float[] EmbedSession(IEnumerable<(string? Content, int WeightHint)> parts)
        => _active.EmbedSession(parts);

    public static double Cosine(float[] a, float[] b) => EmbeddingVectors.Cosine(a, b);

    public static byte[] ToBytes(float[] vec) => EmbeddingVectors.ToBytes(vec);

    public static float[] FromBytes(byte[] bytes, int dims) => EmbeddingVectors.FromBytes(bytes, dims);

    /// <summary>Model tag written on new (float16) embedding rows; dims unchanged.</summary>
    public static string F16ModelId => VectorCodecs.F16ModelId(ModelId);

    /// <summary>
    /// Accepts the plain active-model tag (legacy float32 rows) and any
    /// "<c>ModelId|...</c>" variant (float16 today); other models never match.
    /// </summary>
    public static bool IsModelCompatible(string? storedModel) => VectorCodecs.IsModelCompatible(storedModel, ModelId);

    /// <summary>Half-precision pack: 2 bytes per element, dims unchanged.</summary>
    public static byte[] ToBytesF16(float[] vec) => VectorCodecs.ToBytesF16(vec);

    /// <summary>Half-precision unpack; short blobs decode to zeros (never throws).</summary>
    public static float[] FromBytesF16(byte[] bytes, int dims) => VectorCodecs.FromBytesF16(bytes, dims);

    /// <summary>
    /// Width-detecting decode: legacy float32 rows and new float16 rows both read,
    /// so the width migration needs no data rewrite.
    /// </summary>
    public static float[] FromBytesAuto(byte[] bytes, int dims) => VectorCodecs.FromBytesAuto(bytes, dims);

    /// <summary>Stored width in bytes per element: 4 (float32), 2 (float16), else 0.</summary>
    public static int DetectWidth(byte[] bytes, int dims) => VectorCodecs.DetectWidth(bytes, dims);

    /// <summary>
    /// Opts into neural embeddings: loads the ONNX model lazily (first call, never
    /// at startup) and swaps the active provider. On failure the hashing provider
    /// stays active and the error is available via <see cref="LastError"/>.
    /// The caller owns the returned provider's lifetime only until the next switch;
    /// prefer <see cref="ResetToDefault"/> (which disposes it) over manual disposal.
    /// </summary>
    public static bool TryEnableNeural(out string? error)
    {
        lock (_gate)
        {
            if (_active is OnnxEmbedder)
            {
                error = null;
                return true;
            }
            if (!OnnxEmbedder.TryCreate(out var neural, out error))
            {
                _lastError = error;
                return false;
            }
            _active = neural!;
            _lastError = null;
            error = null;
            return true;
        }
    }

    /// <summary>
    /// Replaces the active provider (tests and future settings paths). Disposes a
    /// previously active <see cref="OnnxEmbedder"/>.
    /// </summary>
    public static void SetProvider(IEmbeddingProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        lock (_gate)
        {
            if (ReferenceEquals(_active, provider)) return;
            if (_active is IDisposable d) d.Dispose();
            _active = provider;
            _lastError = null;
        }
    }

    /// <summary>Restores the hashing default (disposes the neural provider if active).</summary>
    public static void ResetToDefault() => SetProvider(HashingEmbedder.Instance);
}
