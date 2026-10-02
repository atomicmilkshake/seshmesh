using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Casr.Core.Export.Formatters;
using Casr.Core.Logging;
using Casr.Core.Models;
using Casr.Core.Providers;

namespace Casr.Core.Export;

public class SessionExportService
{
    private static readonly Lazy<SessionExportService> _lazyDefault = new(() => new SessionExportService());
    public static SessionExportService Default => _lazyDefault.Value;

    public async Task ExportSessionAsync(
        CanonicalSession session,
        string destinationPath,
        ExportFormat format,
        ExportOptions? options = null,
        CancellationToken ct = default)
    {
        if (session == null) throw new ArgumentNullException(nameof(session));
        if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("Destination path is required.", nameof(destinationPath));
        if (session.Messages == null || session.Messages.Count == 0)
        {
            // Mirror CrossResume's empty guard: writing an empty transcript produces
            // a dead file the user mistakes for a real export.
            throw new InvalidOperationException(
                $"Cannot export session '{session.SessionId}': the session contains no messages. " +
                "Export of an empty transcript was aborted.");
        }

        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string content;
        switch (format)
        {
            case ExportFormat.Markdown:
                content = MarkdownSessionFormatter.Format(session, options);
                break;
            case ExportFormat.Html:
                content = HtmlSessionFormatter.Format(session, options);
                break;
            case ExportFormat.Json:
                content = JsonSessionFormatter.Format(session, options);
                break;
            case ExportFormat.Native:
                throw new InvalidOperationException("Use ExportNativeAsync for native harness exports.");
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported export format.");
        }

        await File.WriteAllTextAsync(destinationPath, content, Encoding.UTF8, ct);
        CasrLogger.Info("EXPORT", $"Successfully exported session {session.SessionId} to {destinationPath} (Format: {format})");
    }

    public string GenerateDefaultFileName(SessionSummary summary, ExportFormat format)
    {
        if (summary == null) throw new ArgumentNullException(nameof(summary));

        var slug = string.IsNullOrWhiteSpace(summary.Provider) ? "session" : summary.Provider.ToLowerInvariant();
        var ts = summary.LastActiveAt ?? summary.StartedAt ?? DateTime.Now;
        var date = ts.ToString("yyyyMMdd");

        var rawTitle = !string.IsNullOrWhiteSpace(summary.Title) ? summary.Title : summary.SessionId;
        var sanitizedTitle = SanitizeFileNamePart(rawTitle);

        if (sanitizedTitle.Length > 50)
        {
            sanitizedTitle = sanitizedTitle.Substring(0, 50).TrimEnd('-', '_');
        }

        var ext = format switch
        {
            ExportFormat.Markdown => ".md",
            ExportFormat.Html => ".html",
            ExportFormat.Json => ".json",
            ExportFormat.Native => GetNativeExtension(slug),
            _ => ".txt"
        };

        var suffix = BuildUniquenessSuffix(summary.SessionId, ts);
        return $"{slug}_{date}_{sanitizedTitle}{suffix}{ext}";
    }

    public string GenerateDefaultFileName(CanonicalSession session, ExportFormat format)
    {
        if (session == null) throw new ArgumentNullException(nameof(session));

        var slug = string.IsNullOrWhiteSpace(session.ProviderSlug) ? "session" : session.ProviderSlug.ToLowerInvariant();
        var ts = session.EndedAt ?? session.StartedAt ?? DateTime.Now;
        var date = ts.ToString("yyyyMMdd");

        var rawTitle = !string.IsNullOrWhiteSpace(session.Title) ? session.Title : session.SessionId;
        var sanitizedTitle = SanitizeFileNamePart(rawTitle);

        if (sanitizedTitle.Length > 50)
        {
            sanitizedTitle = sanitizedTitle.Substring(0, 50).TrimEnd('-', '_');
        }

        var ext = format switch
        {
            ExportFormat.Markdown => ".md",
            ExportFormat.Html => ".html",
            ExportFormat.Json => ".json",
            ExportFormat.Native => GetNativeExtension(slug),
            _ => ".txt"
        };

        var suffix = BuildUniquenessSuffix(session.SessionId, ts);
        return $"{slug}_{date}_{sanitizedTitle}{suffix}{ext}";
    }

    internal static string BuildUniquenessSuffix(string? sessionId, DateTime ts)
    {
        var timePart = ts.ToString("HHmmss");
        var idChars = new string((sessionId ?? string.Empty).Where(char.IsLetterOrDigit).Take(6).ToArray());
        if (string.IsNullOrEmpty(idChars))
        {
            idChars = Guid.NewGuid().ToString("N").Substring(0, 6);
        }
        return $"_{timePart}_{idChars}";
    }

    public static string SanitizeFileNamePart(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "untitled";

        // Strip Windows-invalid characters only; keep Unicode letters/numbers (\p{L}\p{N}).
        // Invalid on Windows: < > : " / \ | ? * plus control chars 0-31. Whitespace
        // runs become hyphens for readability; everything else (including unicode,
        // '-', '_', '.') passes through untouched.
        var sb = new StringBuilder(name.Trim().Length);
        foreach (var c in name.Trim())
        {
            if (c == '<' || c == '>' || c == ':' || c == '"' || c == '/' || c == '\\'
                || c == '|' || c == '?' || c == '*'
                || char.IsControl(c))
            {
                sb.Append('-');
            }
            else if (char.IsWhiteSpace(c))
            {
                sb.Append('-');
            }
            else
            {
                sb.Append(c);
            }
        }

        var clean = Regex.Replace(sb.ToString(), @"-+", "-");
        clean = clean.Trim('-', '_', '.', ' ');

        return string.IsNullOrWhiteSpace(clean) ? "untitled" : clean;
    }

    public bool SupportsNativeExport(string? providerSlug)
    {
        if (string.IsNullOrWhiteSpace(providerSlug)) return false;
        var slug = providerSlug.Trim().ToLowerInvariant();
        if (slug == "opencode")
        {
            return OpenCodeProvider.FindOpenCodeCli() != null;
        }
        if (slug == "pi")
        {
            return PiProvider.FindPiCli() != null;
        }
        return false;
    }

    public string GetNativeExtension(string? providerSlug)
    {
        if (string.IsNullOrWhiteSpace(providerSlug)) return ".txt";
        var slug = providerSlug.Trim().ToLowerInvariant();
        if (slug == "opencode") return ".json";
        if (slug == "pi") return ".html";
        return ".txt";
    }

    public async Task ExportNativeAsync(SessionSummary summary, string destinationPath, CancellationToken ct = default)
    {
        if (summary == null) throw new ArgumentNullException(nameof(summary));
        if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("Destination path is required.", nameof(destinationPath));

        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var slug = string.IsNullOrWhiteSpace(summary.Provider) ? string.Empty : summary.Provider.Trim().ToLowerInvariant();
        if (slug == "opencode")
        {
            var cli = OpenCodeProvider.FindOpenCodeCli()
                ?? throw new InvalidOperationException("OpenCode CLI not found.");

            // Stream to .tmp first so a failed/cancelled export never leaves a partial
            // file at the destination the user mistakes for a complete export.
            var tmpPath = destinationPath + ".tmp";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = cli,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("session");
                psi.ArgumentList.Add("export");
                psi.ArgumentList.Add(summary.SessionId);

                using var proc = Process.Start(psi)
                    ?? throw new InvalidOperationException("Failed to launch opencode session export process.");

                try
                {
                    await using var fileStream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    var copyTask = proc.StandardOutput.BaseStream.CopyToAsync(fileStream, ct);
                    var stderrTask = proc.StandardError.ReadToEndAsync(ct);

                    await Task.WhenAll(copyTask, stderrTask);
                    await proc.WaitForExitAsync(ct);
                    await fileStream.FlushAsync(ct);

                    if (proc.ExitCode != 0)
                    {
                        var err = await stderrTask;
                        throw new InvalidOperationException($"OpenCode session export failed (exit {proc.ExitCode}): {err.Trim()}");
                    }
                }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    throw;
                }
                catch
                {
                    try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                    throw;
                }

                var tmpInfo = new FileInfo(tmpPath);
                if (!tmpInfo.Exists || tmpInfo.Length == 0)
                {
                    throw new InvalidOperationException("OpenCode session export produced an empty file; export was aborted.");
                }

                // Verify the payload is JSON before promoting it to the destination.
                try
                {
                    await using var verifyStream = File.OpenRead(tmpPath);
                    using var doc = await JsonDocument.ParseAsync(verifyStream, cancellationToken: ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"OpenCode session export produced invalid JSON: {ex.Message}");
                }

                File.Move(tmpPath, destinationPath, overwrite: true);
            }
            catch
            {
                TryDeleteTemp(tmpPath);
                throw;
            }
            CasrLogger.Info("EXPORT", $"Successfully exported native OpenCode session to {destinationPath}");
            return;
        }

        if (slug == "pi")
        {
            var cli = PiProvider.FindPiCli()
                ?? throw new InvalidOperationException("Pi CLI not found.");

            if (!File.Exists(summary.SourcePath))
            {
                throw new FileNotFoundException($"Pi session source file not found at: {summary.SourcePath}");
            }

            var tmpPath = destinationPath + ".tmp";
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = cli,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("--export");
                psi.ArgumentList.Add(summary.SourcePath);
                psi.ArgumentList.Add(tmpPath);

                using var proc = Process.Start(psi)
                    ?? throw new InvalidOperationException("Failed to launch pi export process.");

                string stderr;
                try
                {
                    var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
                    var stderrTask = proc.StandardError.ReadToEndAsync(ct);

                    await proc.WaitForExitAsync(ct);
                    stderr = await stderrTask;
                    _ = await stdoutTask;
                }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    throw;
                }
                catch
                {
                    try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                    throw;
                }

                if (proc.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Pi export failed (exit {proc.ExitCode}): {stderr.Trim()}");
                }
                if (!File.Exists(tmpPath) || new FileInfo(tmpPath).Length == 0)
                {
                    throw new InvalidOperationException("Pi export produced no output file; export was aborted.");
                }

                File.Move(tmpPath, destinationPath, overwrite: true);
            }
            catch
            {
                TryDeleteTemp(tmpPath);
                throw;
            }
            CasrLogger.Info("EXPORT", $"Successfully exported native Pi session to {destinationPath}");
            return;
        }

        // Fallback for providers without dedicated CLI export: copy raw source file if it exists
        if (File.Exists(summary.SourcePath))
        {
            File.Copy(summary.SourcePath, destinationPath, overwrite: true);
            CasrLogger.Info("EXPORT", $"Exported raw session file from {summary.SourcePath} to {destinationPath}");
            return;
        }

        throw new NotSupportedException($"Native export is not supported for provider '{summary.Provider}'.");
    }

    private static void TryDeleteTemp(string tmpPath)
    {
        try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
    }
}
