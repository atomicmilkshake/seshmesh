using System;

namespace Casr.Core.Search;

/// <summary>
/// Float16 vector storage codec + model-tag compatibility. New embedding rows are
/// stored float16 (<see cref="Half"/>, 2 bytes/element) with dims unchanged and the
/// model tag suffixed <see cref="F16Suffix"/>; pre-upgrade float32 rows (plain model
/// tag) keep decoding with no data migration. Readers accept both widths
/// (<see cref="FromBytesAuto"/>/<see cref="DetectWidth"/>) and both tags
/// (<see cref="IsModelCompatible"/>). Provider-agnostic: works for any dims/model.
/// </summary>
public static class VectorCodecs
{
    /// <summary>Suffix marking float16 rows. Dims are unchanged — only the width halves.</summary>
    public const string F16Suffix = "|f16";

    /// <summary>Model tag written on new (float16) embedding rows.</summary>
    public static string F16ModelId(string baseModelId) => baseModelId + F16Suffix;

    /// <summary>
    /// Prefix match over stored model tags: accepts the plain base id (legacy float32
    /// rows) and any "<c>base|...</c>" variant (float16 today). A different model
    /// family mints a different base id and never matches, so cross-model scoring
    /// stays impossible.
    /// </summary>
    public static bool IsModelCompatible(string? storedModel, string baseModelId)
    {
        if (string.IsNullOrEmpty(storedModel) || string.IsNullOrEmpty(baseModelId)) return false;
        return storedModel.Equals(baseModelId, StringComparison.Ordinal)
            || storedModel.StartsWith(baseModelId + "|", StringComparison.Ordinal);
    }

    /// <summary>Stored width in bytes per element: 4 (float32), 2 (float16), else 0.</summary>
    public static int DetectWidth(byte[] bytes, int dims)
    {
        if (bytes.Length >= dims * 4) return 4;
        if (bytes.Length >= dims * 2) return 2;
        return 0;
    }

    /// <summary>Half-precision pack: 2 bytes per element, dims unchanged.</summary>
    public static byte[] ToBytesF16(float[] vec)
    {
        var bytes = new byte[vec.Length * 2];
        for (var i = 0; i < vec.Length; i++)
        {
            var hb = BitConverter.GetBytes((Half)vec[i]);
            bytes[i * 2] = hb[0];
            bytes[i * 2 + 1] = hb[1];
        }
        return bytes;
    }

    /// <summary>Half-precision unpack; short blobs decode to zeros (never throws).</summary>
    public static float[] FromBytesF16(byte[] bytes, int dims)
    {
        var vec = new float[dims];
        if (bytes.Length < dims * 2) return vec;
        for (var i = 0; i < dims; i++)
            vec[i] = (float)BitConverter.ToHalf(bytes, i * 2);
        return vec;
    }

    /// <summary>
    /// Width-detecting decode: float32 when the blob is long enough, else float16,
    /// else zeros. This is what lets pre-upgrade F32 rows and new F16 rows coexist.
    /// </summary>
    public static float[] FromBytesAuto(byte[] bytes, int dims)
    {
        if (bytes.Length >= dims * 4)
        {
            var vec = new float[dims];
            Buffer.BlockCopy(bytes, 0, vec, 0, dims * 4);
            return vec;
        }
        return FromBytesF16(bytes, dims);
    }
}
