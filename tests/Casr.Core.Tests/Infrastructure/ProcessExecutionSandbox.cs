using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Casr.Core.Tests.Infrastructure;

public record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Duration);

/// <summary>
/// Hermetic subprocess execution sandbox with asynchronous stream capture,
/// strict timeout enforcement, and entire process tree termination on timeout.
/// </summary>
public static class ProcessExecutionSandbox
{
    private static string? _cachedPowerShellExe;

    /// <summary>
    /// Finds a usable PowerShell executable on Windows (pwsh.exe preferred, powershell.exe fallback).
    /// </summary>
    public static string FindPowerShellExe()
    {
        if (_cachedPowerShellExe != null && File.Exists(_cachedPowerShellExe))
        {
            return _cachedPowerShellExe;
        }

        // 1. Check standard PowerShell 7 paths
        var pwshCandidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7-preview", "pwsh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "pwsh.exe")
        };

        foreach (var path in pwshCandidates)
        {
            if (File.Exists(path))
            {
                _cachedPowerShellExe = path;
                return path;
            }
        }

        // 2. Check Windows PowerShell (guaranteed on all Windows platforms)
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var winPowerShell = Path.Combine(system32, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(winPowerShell))
        {
            _cachedPowerShellExe = winPowerShell;
            return winPowerShell;
        }

        // Fallback to "powershell.exe" via PATH resolution
        _cachedPowerShellExe = "powershell.exe";
        return _cachedPowerShellExe;
    }

    /// <summary>
    /// Executes a process with timeout and full stream capture.
    /// </summary>
    public static async Task<ProcessRunResult> RunAsync(
        string executable,
        string arguments,
        string? workingDirectory = null,
        int timeoutMs = 10_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var sw = Stopwatch.StartNew();
        using var proc = new Process { StartInfo = psi };
        using var cts = new CancellationTokenSource(timeoutMs);

        try
        {
            if (!proc.Start())
            {
                throw new InvalidOperationException($"Failed to start process: {executable}");
            }

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            var waitTask = proc.WaitForExitAsync(cts.Token);

            await Task.WhenAll(waitTask, stdoutTask, stderrTask);
            sw.Stop();

            return new ProcessRunResult(proc.ExitCode, await stdoutTask, await stderrTask, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch { }

            throw new TimeoutException($"Process '{executable}' exceeded timeout of {timeoutMs}ms and was killed.");
        }
    }
}
