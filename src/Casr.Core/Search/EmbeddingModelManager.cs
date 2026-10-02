using System;
using System.IO;
using System.Security.Cryptography;
using Casr.Core.Logging;

namespace Casr.Core.Search;

/// <summary>
/// Locates and validates the neural embedding model file. Never touches the
/// network and never blocks startup: resolution is lazy (first opt-in neural
/// query), failures are reported via <see cref="LastError"/>, and the outcome
/// (success or failure) is logged exactly once per process.
/// </summary>
public static class EmbeddingModelManager
{
    /// <summary>Env override used by tests to redirect the models dir to temp.</summary>
    public const string ModelsDirEnvVar = "CASR_MODELS_DIR";
    /// <summary>Optional pinned SHA-256 (hex) the model file must match.</summary>
    public const string ExpectedShaEnvVar = "CASR_MODEL_SHA256";

    public const string ModelFileName = "minilm-l6-v2.onnx";
    public const string VocabFileName = "vocab.txt";
    public const string ShaSidecarFileName = "minilm-l6-v2.onnx.sha256";

    private static readonly object _gate = new();
    private static string? _lastError;
    private static bool _loggedOnce;

    /// <summary>Last resolution/verification failure, or null when healthy/absent-model.</summary>
    public static string? LastError
    {
        get { lock (_gate) return _lastError; }
    }

    /// <summary>
    /// Models directory: <c>%LOCALAPPDATA%\Casr\models</c>, or
    /// <c>$CASR_MODELS_DIR</c> when set (tests use this for isolation).
    /// Resolving the path creates nothing on disk.
    /// </summary>
    public static string ModelsDirectory
    {
        get
        {
            var overrideDir = Environment.GetEnvironmentVariable(ModelsDirEnvVar);
            if (!string.IsNullOrWhiteSpace(overrideDir)) return overrideDir;
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local))
                local = Path.GetTempPath(); // %LOCALAPPDATA% unavailable: temp fallback, never crash
            return Path.Combine(local, "Casr", "models");
        }
    }

    public static string ModelPath => Path.Combine(ModelsDirectory, ModelFileName);
    public static string VocabPath => Path.Combine(ModelsDirectory, VocabFileName);

    public static bool IsModelPresent => File.Exists(ModelPath) && File.Exists(VocabPath);

    /// <summary>
    /// Verifies the model file against its SHA-256 sidecar
    /// (<c>minilm-l6-v2.onnx.sha256</c>, written by
    /// <c>scripts/Download-EmbeddingModel.ps1</c>) or the
    /// <c>$CASR_MODEL_SHA256</c> pin when set. No sidecar and no pin means
    /// "presence only" — returns true with a logged notice, never a failure.
    /// Pure check: creates no files, writes no state except <see cref="LastError"/>.
    /// </summary>
    public static bool VerifyModelHash(out string? error)
    {
        error = null;
        if (!File.Exists(ModelPath))
        {
            error = $"Embedding model not downloaded: '{ModelPath}' is missing. " +
                    "Run scripts/Download-EmbeddingModel.ps1 to fetch it.";
            SetError(error);
            return false;
        }
        string? expected = Environment.GetEnvironmentVariable(ExpectedShaEnvVar)?.Trim();
        if (string.IsNullOrWhiteSpace(expected))
        {
            var sidecar = Path.Combine(ModelsDirectory, ShaSidecarFileName);
            if (File.Exists(sidecar))
            {
                try { expected = File.ReadAllText(sidecar).Trim().Split()[0]; }
                catch (Exception ex)
                {
                    error = $"Cannot read model SHA sidecar '{sidecar}': {ex.Message}";
                    SetError(error);
                    return false;
                }
            }
        }
        if (string.IsNullOrWhiteSpace(expected))
        {
            LogOnce("MODELS", $"No SHA pin for '{ModelFileName}': presence-only check (no sidecar, no {ExpectedShaEnvVar}).");
            ClearError();
            return true;
        }
        string actual;
        try
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(ModelPath);
            actual = Convert.ToHexString(sha.ComputeHash(stream));
        }
        catch (Exception ex)
        {
            error = $"Cannot hash embedding model '{ModelPath}': {ex.Message}";
            SetError(error);
            return false;
        }
        if (!string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            error = $"Embedding model SHA-256 mismatch for '{ModelPath}': " +
                    $"expected {expected.Trim()}, got {actual}. Delete it and re-run scripts/Download-EmbeddingModel.ps1.";
            SetError(error);
            return false;
        }
        ClearError();
        return true;
    }

    /// <summary>
    /// Full readiness gate used before constructing the ONNX session:
    /// presence + hash. Returns false with <see cref="LastError"/> set when
    /// the model is missing or corrupt. Never throws for missing files.
    /// </summary>
    public static bool TryEnsureReady(out string? error) => VerifyModelHash(out error);

    internal static void LogOnce(string component, string message)
    {
        lock (_gate)
        {
            if (_loggedOnce) return;
            _loggedOnce = true;
        }
        CasrLogger.Info(component, message);
    }

    internal static void SetError(string error)
    {
        lock (_gate) _lastError = error;
        LogOnce("MODELS", error);
    }

    private static void ClearError()
    {
        lock (_gate) _lastError = null;
    }

    /// <summary>Test hook: resets the log-once latch and last error.</summary>
    internal static void ResetForTests()
    {
        lock (_gate)
        {
            _lastError = null;
            _loggedOnce = false;
        }
    }
}
