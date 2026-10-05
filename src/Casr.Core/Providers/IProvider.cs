using System.Collections.Generic;
using Casr.Core.Models;

namespace Casr.Core.Providers;

public class DetectionResult
{
    public bool Installed { get; set; }
    public string? Version { get; set; }
    public List<string> Evidence { get; set; } = new();
}

public class WriteOptions
{
    public bool Force { get; set; }
}

public class WrittenSession
{
    public List<string> Paths { get; set; } = new();
    public string SessionId { get; set; } = string.Empty;
    public string ResumeCommand { get; set; } = string.Empty;
    public string? BackupPath { get; set; }
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// True when the target provider cannot host injected history and the "written"
    /// session is just a launch of the target CLI in the source workspace.
    /// </summary>
    public bool IsFallbackLaunch { get; set; }

    /// <summary>The workspace the resumed session was launched/written into, when known.</summary>
    public string? Workspace { get; set; }

    /// <summary>
    /// Read-back verification result, populated by SessionResumerService after a
    /// cross-provider write. Null for same-provider resumes and fallback launches.
    /// </summary>
    public WriteVerification? Verification { get; set; }
}

/// <summary>
/// Thrown by WriteSession when the source session has no resolvable workspace directory,
/// i.e. the write would land in the caller's current working directory.
/// </summary>
public class UnresolvableWorkspaceException : InvalidOperationException
{
    public UnresolvableWorkspaceException(string message) : base(message) { }
}

public interface IProvider
{
    string Name { get; }
    string Slug { get; }
    string CliAlias { get; }

    DetectionResult Detect();
    IReadOnlyList<string> SessionRoots();

    /// <summary>True when WriteSession actually injects history; false means fallback launch.</summary>
    /// <remarks>
    /// Contract: when <see cref="CanWrite"/> is false, <see cref="WriteSession"/>
    /// MUST throw <see cref="NotSupportedException"/> (Cursor is the reference
    /// read-only provider). Callers check <see cref="CanWrite"/> to decide whether
    /// a cross-resume target can host injected history or will only open a workspace.
    /// </remarks>
    bool CanWrite => false;

    string? OwnsSession(string sessionId);
    CanonicalSession ReadSession(string path);
    SessionSummary ReadSummary(string path);
    /// <summary>
    /// Injects <paramref name="session"/> history into this provider's store.
    /// Throws <see cref="NotSupportedException"/> when <see cref="CanWrite"/> is false.
    /// </summary>
    WrittenSession WriteSession(CanonicalSession session, WriteOptions opts);
    string ResumeCommand(string sessionId, string? workspace = null);
    IReadOnlyList<(string SessionId, string Path)>? ListSessions();

    /// <summary>
    /// Path that <see cref="ReadSession"/> should re-read to verify a freshly written
    /// session, or null when the provider writes through an import-only path (the
    /// caller then falls back to <see cref="OwnsSession"/>).
    /// </summary>
    string? ReadBackPath(WrittenSession written)
        => written.Paths.Count > 0 ? written.Paths[0] : null;

    /// <summary>
    /// True when the provider's native format intentionally folds or drops message rows
    /// (e.g. OpenCode tool messages become assistant tool items), so a read-back count
    /// difference is reported as a fidelity warning instead of failing verification.
    /// </summary>
    bool TolerantVerification => false;
}
