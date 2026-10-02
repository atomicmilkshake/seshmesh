using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Casr.Core.Configuration;

namespace Casr.Core.Logging;

public static class CasrLogger
{
    private static readonly object _lock = new();
    private static readonly string _logDir;
    private static readonly string _logFile;
    private static readonly string _rotatedFile;
    private static readonly string? _initNote;

    /// <summary>Rotate the active log once it passes this size; the previous segment is kept as casr_debug.1.log.</summary>
    public const long MaxLogBytes = 10L * 1024 * 1024;

    static CasrLogger()
    {
        // %LOCALAPPDATA% can be unavailable (service/test-host contexts): fall back to
        // %TEMP% so logging degrades location, never silently dies.
        // CASR_LOG_DIR overrides everything (tests / diagnostics). Test hosts
        // (testhost/vstest/dotnet) are auto-redirected to %TEMP% so hermetic and
        // LiveSystem runs never pollute the production casr_debug.log the user
        // and Rule 4 triage depend on.
        string dir;
        try
        {
            var overrideDir = Environment.GetEnvironmentVariable("CASR_LOG_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDir))
            {
                dir = overrideDir;
                _initNote = $"CASR_LOG_DIR override: {dir}";
            }
            else if (IsTestHostProcess(out var hostName))
            {
                dir = Path.Combine(Path.GetTempPath(), "Casr-test-logs");
                _initNote = $"test host detected ({hostName}); using {dir}";
            }
            else
            {
                dir = CasrPaths.LogsDir;
            }
        }
        catch (Exception ex)
        {
            dir = Path.Combine(Path.GetTempPath(), "Casr", "logs");
            _initNote = $"primary log dir unavailable ({ex.Message}); using fallback {dir}";
        }

        _logDir = dir;
        _logFile = Path.Combine(_logDir, "casr_debug.log");
        _rotatedFile = Path.Combine(_logDir, "casr_debug.1.log");

        long existingBytes = 0;
        var rotatedAtStartup = false;
        try
        {
            Directory.CreateDirectory(_logDir);
            if (File.Exists(_logFile)) existingBytes = new FileInfo(_logFile).Length;
            // The pre-existing segment (including any historic test noise) is preserved as
            // casr_debug.1.log, never deleted — the notice below says exactly that, once.
            if (existingBytes >= MaxLogBytes) { Rotate(); rotatedAtStartup = true; }
            // Append session startup marker (process name distinguishes app runs
            // from testhost/vstest runs sharing this file pre-redirect).
            string processName;
            try { processName = System.Diagnostics.Process.GetCurrentProcess().ProcessName; }
            catch { processName = "?"; }
            Log("LOGGER", "INFO", "================ CASR SESSION STARTED ================" +
                $" [process={processName} log={_logFile}]" +
                (_initNote != null ? $" [{_initNote}]" : string.Empty));
            if (rotatedAtStartup)
                Log("LOGGER", "INFO", "Log rotated: previous segment preserved at casr_debug.1.log (history retained, not deleted).");
        }
        catch
        {
            // Logging must never crash the app; Log() below re-checks per write.
        }
        // A startup rotation already zeroed the counter and the marker/notice writes above
        // counted from there — restoring existingBytes would force one spurious re-rotation.
        if (!rotatedAtStartup) _bytesWritten = existingBytes;
    }

    private static long _bytesWritten;

    public static string LogFilePath => _logFile;

    /// <summary>True when running inside a test runner; callers use this to
    /// avoid production side effects. Test-only detection, never inverts app behavior.</summary>
    private static bool IsTestHostProcess(out string hostName)
    {
        hostName = string.Empty;
        try
        {
            hostName = System.Diagnostics.Process.GetCurrentProcess().ProcessName ?? string.Empty;
            return hostName.Contains("testhost", StringComparison.OrdinalIgnoreCase)
                || hostName.Contains("vstest", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void Debug(string component, string message) => Log(component, "DEBUG", message);
    public static void Info(string component, string message) => Log(component, "INFO", message);
    public static void Warn(string component, string message) => Log(component, "WARN", message);
    public static void Error(string component, string message, Exception? ex = null)
    {
        var full = ex != null ? $"{message} | Exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}" : message;
        Log(component, "ERROR", full);
    }

    /// <summary>Moves the full active log to casr_debug.1.log (overwriting the older segment) and starts fresh.</summary>
    private static void Rotate()
    {
        try
        {
            if (File.Exists(_rotatedFile)) File.Delete(_rotatedFile);
            if (File.Exists(_logFile)) File.Move(_logFile, _rotatedFile);
            _bytesWritten = 0;
        }
        catch { }
    }

    private static void Log(string component, string level, string message)
    {
        // Offset-bearing local timestamp: summer/winter (DST) log lines stay unambiguous.
        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
        var threadId = Thread.CurrentThread.ManagedThreadId;
        var line = $"[{timestamp}] [{level,-5}] [T{threadId:D2}] [{component}] {message}";

        Trace.WriteLine(line);

        lock (_lock)
        {
            try
            {
                var bytes = System.Text.Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
                var justRotated = false;
                if (_bytesWritten + bytes > MaxLogBytes) { Rotate(); justRotated = true; }
                File.AppendAllText(_logFile, line + Environment.NewLine);
                _bytesWritten += bytes;
                if (justRotated)
                {
                    // One-time notice per rotation so a fresh log is never mistaken for a
                    // wiped one: the previous segment survives as casr_debug.1.log.
                    var noteStamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz");
                    var noteThread = Thread.CurrentThread.ManagedThreadId;
                    var note = $"[{noteStamp}] [INFO ] [T{noteThread:D2}] [LOGGER] Log rotated: previous segment preserved at casr_debug.1.log (history retained, not deleted).";
                    Trace.WriteLine(note);
                    try
                    {
                        File.AppendAllText(_logFile, note + Environment.NewLine);
                        _bytesWritten += System.Text.Encoding.UTF8.GetByteCount(note) + Environment.NewLine.Length;
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
