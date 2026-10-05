using System.Collections.Generic;

namespace Casr.Core.Models;

/// <summary>
/// Filesystem-derived git context for a session workspace — the GUI analogue of
/// casr's <c>--enrich-fs</c> output (repository name and branch).
/// </summary>
public class GitRepoInfo
{
    public string RepoRoot { get; set; } = string.Empty;
    public string RepoName { get; set; } = string.Empty;
    public string? Branch { get; set; }
    public string? HeadSha { get; set; }

    /// <summary>Human-readable summary, e.g. "casr @ main" or "casr @ detached 1a2b3c4d".</summary>
    public string Display
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(RepoName) ? RepoRoot : RepoName;
            if (string.IsNullOrWhiteSpace(Branch)) return name;
            return $"{name} @ {Branch}";
        }
    }
}

/// <summary>
/// Result of the read-back verification performed after a cross-provider write.
/// Message-count integrity is enforced (a mismatch is a failed verification and the
/// conversion is rolled back when the written artifacts are safe to remove); role and
/// content differences are recorded as format-fidelity warnings because several target
/// formats intentionally re-render tool history as text.
/// </summary>
public class WriteVerification
{
    public bool Attempted { get; set; }

    /// <summary>True when the provider is import-only (no readable artifact) so no claim is made either way.</summary>
    public bool Unverifiable { get; set; }

    public bool Passed { get; set; }
    public string Message { get; set; } = string.Empty;
    public int OriginalMessages { get; set; }
    public int ReadBackMessages { get; set; }
    public List<string> Warnings { get; set; } = new();
    public bool RolledBack { get; set; }
    public string? RollbackMessage { get; set; }
}
