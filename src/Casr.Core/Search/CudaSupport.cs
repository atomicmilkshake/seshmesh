using System;
using System.IO;
using System.Linq;
using Casr.Core.Logging;

namespace Casr.Core.Search;

/// <summary>
/// Makes the CUDA runtime + cuDNN 9 DLLs visible to onnxruntime's dynamically
/// loaded CUDA provider. Resolution is lazy and silent: the optional support
/// directory (<c>%LOCALAPPDATA%\Casr\cuda</c>, override <c>$CASR_CUDA_DIR</c>)
/// is prepended to the process PATH once, right before the execution provider is
/// created. Never throws; a missing directory is not an error — the CPU fallback
/// stays available.
/// </summary>
public static class CudaSupport
{
    public const string CudaDirEnvVar = "CASR_CUDA_DIR";

    private static readonly object _gate = new();
    private static bool _applied;
    private static string? _appliedDir;

    /// <summary>
    /// Support directory: <c>%LOCALAPPDATA%\Casr\cuda</c> or <c>$CASR_CUDA_DIR</c>.
    /// Resolving the path creates nothing on disk.
    /// </summary>
    public static string CudaDirectory
    {
        get
        {
            var overrideDir = Environment.GetEnvironmentVariable(CudaDirEnvVar);
            if (!string.IsNullOrWhiteSpace(overrideDir)) return overrideDir;
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local)) local = Path.GetTempPath();
            return Path.Combine(local, "Casr", "cuda");
        }
    }

    /// <summary>
    /// Prepends <see cref="CudaDirectory"/> to the process PATH (once) when it
    /// exists. Returns the applied directory, or null when no support dir is present.
    /// </summary>
    public static string? EnsureRuntimeSearchPath()
    {
        lock (_gate)
        {
            if (_applied) return _appliedDir;
            _applied = true;
            var dir = CudaDirectory;
            try
            {
                if (!Directory.Exists(dir)) return null;

                var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                var alreadyOnPath = path
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(p => string.Equals(p, dir, StringComparison.OrdinalIgnoreCase));
                if (!alreadyOnPath)
                {
                    Environment.SetEnvironmentVariable("PATH", dir + ";" + path);
                    CasrLogger.Info("ONNX", $"CUDA support directory added to the runtime search path: {dir}");
                }
                _appliedDir = dir;
                return dir;
            }
            catch (Exception ex)
            {
                CasrLogger.Debug("ONNX", $"CUDA support path setup failed (CPU fallback stays available): {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>Test hook: forget the applied state (the caller owns PATH restoration).</summary>
    internal static void ResetForTests()
    {
        lock (_gate)
        {
            _applied = false;
            _appliedDir = null;
        }
    }
}
