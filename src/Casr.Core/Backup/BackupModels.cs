using System;
using System.Collections.Generic;

namespace Casr.Core.Backup;

public class BackupFileEntry
{
    public string ZipPath { get; set; } = string.Empty;
    public string DestinationTokenPath { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string? Sha256 { get; set; }
}

/// <summary>
/// A portable canonical session snapshot stored in the archive under
/// <c>canonical/&lt;provider&gt;/&lt;sessionId&gt;.json</c>. Unlike raw provider
/// files (which restore to their original absolute location), canonical files
/// restore into a <c>_restored/canonical/</c> sidecar directory next to the
/// archive, so they are always recoverable even on a machine where the
/// original provider stores no longer exist.
/// </summary>
public class CanonicalFileEntry
{
    public string ZipPath { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string? Sha256 { get; set; }
}

public class BackupManifest
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string MachineName { get; set; } = Environment.MachineName;
    public string UserName { get; set; } = Environment.UserName;
    public string CasrVersion { get; set; } = "1.0.0";
    public BackupScope Scope { get; set; } = new();
    public Dictionary<string, int> ProviderCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public long TotalSizeBytes { get; set; }
    public int TotalFilesCount { get; set; }
    public List<BackupFileEntry> Files { get; set; } = new();

    /// <summary>
    /// Human-readable reasons for every item that was skipped during backup
    /// (unreadable file, unknown provider, failed canonical read, ...).
    /// A non-empty list means partial success, never silent loss.
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>Canonical session snapshots registered in this archive.</summary>
    public List<CanonicalFileEntry> CanonicalFiles { get; set; } = new();
    public Dictionary<string, int> CanonicalCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int CanonicalFilesCount { get; set; }
    public long CanonicalSizeBytes { get; set; }
}

public class BackupScope
{
    public bool IncludeAntigravity { get; set; } = true;
    public bool IncludeCursor { get; set; } = true;
    public bool IncludeGrok { get; set; } = true;
    public bool IncludePi { get; set; } = true;
    public bool IncludeHermes { get; set; } = true;
    public bool IncludeOpenCode { get; set; } = true;
    public bool IncludeOpenClaude { get; set; } = true;
    public bool IncludeCodex { get; set; } = true;
    public bool IncludeCasrDatabase { get; set; } = true;
    public bool IncludeCanonicalExport { get; set; } = true;
}

public class BackupProgress
{
    public string CurrentItem { get; set; } = string.Empty;
    public int FilesProcessed { get; set; }
    public int TotalFiles { get; set; }
    public long BytesProcessed { get; set; }
    public long TotalBytes { get; set; }
    public double PercentComplete => TotalBytes > 0 ? (double)BytesProcessed / TotalBytes * 100.0 : 0.0;
    public string StatusMessage { get; set; } = string.Empty;
}

public class RestoreOptions
{
    public bool CreateBackupCopies { get; set; } = true;
    public HashSet<string> ProvidersToRestore { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class RestoreProgress
{
    public string CurrentFile { get; set; } = string.Empty;
    public int FilesRestored { get; set; }
    public int TotalFiles { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
}

/// <summary>
/// Per-entry outcome of a restore. A restore is partial-success aware:
/// individual entries may fail (missing zip entry, zip-slip refusal, hash
/// mismatch, locked live database) while the rest still land on disk.
/// </summary>
public class RestoreResult
{
    public int RestoredCount { get; set; }
    public int CanonicalRestoredCount { get; set; }
    public int TotalRequested { get; set; }
    public List<string> Failures { get; set; } = new();
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// Live databases that could not be replaced while their owner process
    /// holds them open. Each entry is <c>"target &lt;= sidecar"</c>; the bytes
    /// are safe in the sidecar <c>.pending_restore</c> file and the swap can
    /// be completed after restarting CASR / the owning agent.
    /// </summary>
    public List<string> PendingSwaps { get; set; } = new();

    public int SkippedCount => Failures.Count;
    public bool HasFailures => Failures.Count > 0;
}
