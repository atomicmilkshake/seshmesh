using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Casr.Core.Backup;
using Casr.Core.Models;
using Casr.Core.Providers;
using Casr.Core.Storage;
using Xunit;

namespace Casr.Core.Tests;

/// <summary>
/// Hermetic (temp-root only, no live stores) coverage for the backup /
/// disaster-recovery surface: collector scope, manifest shape + SHA-256,
/// canonical registration + sidecar restore, per-entry failure isolation,
/// inspect verification, live-database sidecars, and .bak hygiene.
/// </summary>
public class BackupTests : IDisposable
{
    private readonly string _tempDir;

    public BackupTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"casr_backup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        foreach (var key in new[] { "PI_CODING_AGENT_DIR", "HERMES_HOME", "OPENCODE_HOME", "OPENCLAUDE_CONFIG_DIR", "CASR_BACKUP_EXTRA_ROOTS" })
        {
            try { Environment.SetEnvironmentVariable(key, null); } catch { }
        }
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private static string Sha256Of(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }

    /// <summary>Builds the four env-overridden provider homes with marker files.</summary>
    private (string Pi, string Hermes, string OpenCode, string OpenClaude) MakeProviderHomes()
    {
        var pi = Path.Combine(_tempDir, "pi_home");
        Directory.CreateDirectory(Path.Combine(pi, "sessions", "ws1"));
        File.WriteAllText(Path.Combine(pi, "sessions", "ws1", "s1.jsonl"), "{\"t\":1}");

        var hermes = Path.Combine(_tempDir, "hermes_home");
        Directory.CreateDirectory(Path.Combine(hermes, "sessions", "saved"));
        File.WriteAllText(Path.Combine(hermes, "state.db"), "hermes-db-bytes");
        File.WriteAllText(Path.Combine(hermes, "sessions", "saved", "conv.json"), "{\"id\":\"c1\"}");

        var oc = Path.Combine(_tempDir, "oc_data");
        Directory.CreateDirectory(oc);
        File.WriteAllText(Path.Combine(oc, "opencode.db"), "opencode-db-bytes");

        var ocl = Path.Combine(_tempDir, "ocl_home", "projects");
        Directory.CreateDirectory(ocl);
        File.WriteAllText(Path.Combine(ocl, "sess.jsonl"), "{\"t\":2}");

        Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", pi);
        Environment.SetEnvironmentVariable("HERMES_HOME", hermes);
        Environment.SetEnvironmentVariable("OPENCODE_HOME", oc);
        Environment.SetEnvironmentVariable("OPENCLAUDE_CONFIG_DIR", Path.Combine(_tempDir, "ocl_home"));

        return (pi, hermes, oc, Path.Combine(_tempDir, "ocl_home"));
    }

    private static BackupScope FourOnlyScope() => new()
    {
        IncludeAntigravity = false,
        IncludeCursor = false,
        IncludeGrok = false,
        IncludePi = true,
        IncludeHermes = true,
        IncludeOpenCode = true,
        IncludeOpenClaude = true,
        IncludeCodex = false,
        IncludeCasrDatabase = false,
        IncludeCanonicalExport = false
    };

    [Fact]
    public void CollectItems_IncludesPiHermesOpenCodeOpenClaude_FromEnvOverrides()
    {
        MakeProviderHomes();
        var items = new BackupService().CollectItems(FourOnlyScope());

        Assert.NotEmpty(items);
        Assert.Contains(items, i => i.Provider == "pi" && i.ZipRelativePath == "raw/pi/sessions/ws1/s1.jsonl");
        Assert.Contains(items, i => i.Provider == "hermes" && i.ZipRelativePath == "raw/hermes/state.db");
        Assert.Contains(items, i => i.Provider == "hermes" && i.ZipRelativePath == "raw/hermes/sessions/saved/conv.json");
        Assert.Contains(items, i => i.Provider == "opencode" && i.ZipRelativePath == "raw/opencode/opencode.db");
        Assert.Contains(items, i => i.Provider == "openclaude" && i.ZipRelativePath == "raw/openclaude/projects/sess.jsonl");
        // Nothing from the disabled scopes leaked in.
        Assert.DoesNotContain(items, i => i.Provider is "antigravity" or "cursor" or "grok" or "casr");
    }

    [Fact]
    public async Task CreateBackupAsync_ManifestShapeShaAndInspectPasses()
    {
        MakeProviderHomes();
        Environment.SetEnvironmentVariable("CASR_BACKUP_EXTRA_ROOTS", _tempDir);
        var zipPath = Path.Combine(_tempDir, "out", "b.zip");

        var manifest = await new BackupService().CreateBackupAsync(zipPath, FourOnlyScope());

        Assert.True(File.Exists(zipPath));
        Assert.NotEmpty(manifest.Files);
        Assert.Equal(manifest.Files.Count, manifest.TotalFilesCount);
        Assert.True(manifest.TotalSizeBytes > 0);
        Assert.Empty(manifest.Warnings);
        foreach (var f in manifest.Files)
        {
            Assert.False(string.IsNullOrWhiteSpace(f.ZipPath));
            Assert.True(f.SizeBytes > 0, f.ZipPath);
            Assert.NotNull(f.Sha256);
            Assert.Equal(64, f.Sha256!.Length);
            Assert.True(f.Sha256.All(c => Uri.IsHexDigit(c)), f.ZipPath);
        }
        foreach (var slug in new[] { "pi", "hermes", "opencode", "openclaude" })
        {
            Assert.True(manifest.ProviderCounts.ContainsKey(slug), $"missing ProviderCounts[{slug}]");
        }
        Assert.Equal(manifest.Files.Count, manifest.ProviderCounts.Values.Sum());

        // The archive we just wrote must pass the restore-gate inspection (E6).
        var inspected = BackupService.InspectBackup(zipPath);
        Assert.NotNull(inspected);
        Assert.Equal(manifest.TotalFilesCount, inspected!.TotalFilesCount);
    }

    [Fact]
    public async Task CreateBackupAsync_CanonicalReadFailuresRecordedAsWarnings_NotSilent()
    {
        using var db = new SessionDatabase(Path.Combine(_tempDir, "idx.db"));
        var bogusId = $"bogus_{Guid.NewGuid():N}";
        db.UpsertSession(new SessionSummary
        {
            SessionId = bogusId,
            Provider = "openclaude", // ReadSession throws FileNotFoundException here
            ProviderDisplayName = "OpenClaude",
            SourcePath = Path.Combine(_tempDir, "does-not-exist.jsonl")
        });

        var scope = new BackupScope
        {
            IncludeAntigravity = false, IncludeCursor = false, IncludeGrok = false,
            IncludePi = false, IncludeHermes = false, IncludeOpenCode = false,
            IncludeOpenClaude = false, IncludeCodex = false, IncludeCasrDatabase = false,
            IncludeCanonicalExport = true
        };

        // No cap: the attempt happens (no Take(500)) and the skip is recorded.
        var manifest = await new BackupService(ProviderRegistry.Default, db)
            .CreateBackupAsync(Path.Combine(_tempDir, "canon.zip"), scope);

        Assert.Empty(manifest.CanonicalFiles);
        Assert.Equal(0, manifest.CanonicalFilesCount);
        var warning = Assert.Single(manifest.Warnings);
        Assert.Contains(bogusId, warning);
        Assert.True(File.Exists(Path.Combine(_tempDir, "canon.zip")));
    }

    private string CraftZip(BackupManifest manifest, Dictionary<string, string> rawEntries, Dictionary<string, string>? canonicalEntries = null)
    {
        var zipPath = Path.Combine(_tempDir, $"craft_{Guid.NewGuid():N}.zip");
        using (var fs = new FileStream(zipPath, FileMode.Create))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var (zipName, content) in rawEntries)
            {
                var e = archive.CreateEntry(zipName);
                using var w = new StreamWriter(e.Open());
                w.Write(content);
            }
            if (canonicalEntries != null)
            {
                foreach (var (zipName, content) in canonicalEntries)
                {
                    var e = archive.CreateEntry(zipName);
                    using var w = new StreamWriter(e.Open());
                    w.Write(content);
                }
            }
            var m = archive.CreateEntry("backup_manifest.json");
            using var mw = new StreamWriter(m.Open());
            mw.Write(JsonSerializer.Serialize(manifest));
        }
        return zipPath;
    }

    [Fact]
    public void InspectBackup_RejectsManifestReferencingMissingEntry()
    {
        var manifest = new BackupManifest
        {
            TotalFilesCount = 1,
            Files = { new BackupFileEntry { ZipPath = "raw/pi/ghost.jsonl", DestinationTokenPath = "x", Provider = "pi", SizeBytes = 3 } }
        };
        var zip = CraftZip(manifest, new Dictionary<string, string> { ["raw/pi/other.jsonl"] = "{}" });
        Assert.Null(BackupService.InspectBackup(zip));
    }

    [Fact]
    public void InspectBackup_RejectsUnsupportedSchemaVersion()
    {
        var manifest = new BackupManifest
        {
            SchemaVersion = BackupService.CurrentSchemaVersion + 1,
            TotalFilesCount = 0
        };
        var zip = CraftZip(manifest, new Dictionary<string, string>());
        Assert.Null(BackupService.InspectBackup(zip));
    }

    [Fact]
    public async Task RestoreDetails_ContinuesPastBadEntries_AndWrapperThrowsOnlyWhenNothingRestored()
    {
        Environment.SetEnvironmentVariable("CASR_BACKUP_EXTRA_ROOTS", _tempDir);
        var goodDest = Path.Combine(_tempDir, "good.txt");
        var outsideDest = Path.Combine(_tempDir + "_sibling", "evil.txt");
        var traversalDest = Path.Combine(_tempDir, "..", Path.GetFileName(_tempDir) + "_escape", "evil2.txt");

        var manifest = new BackupManifest
        {
            Files =
            {
                new BackupFileEntry { ZipPath = "raw/good.txt", DestinationTokenPath = goodDest, Provider = "pi", SizeBytes = 4 },
                new BackupFileEntry { ZipPath = "raw/evil.txt", DestinationTokenPath = outsideDest, Provider = "pi", SizeBytes = 4 },
                new BackupFileEntry { ZipPath = "raw/evil2.txt", DestinationTokenPath = traversalDest, Provider = "pi", SizeBytes = 5 },
            }
        };
        var zip = CraftZip(manifest, new Dictionary<string, string>
        {
            ["raw/good.txt"] = "good",
            ["raw/evil.txt"] = "evil",
            ["raw/evil2.txt"] = "evil2",
        });

        var svc = new BackupService();
        var details = await svc.RestoreBackupWithDetailsAsync(zip, new RestoreOptions());

        Assert.Equal(1, details.RestoredCount);
        Assert.Equal(3, details.TotalRequested);
        Assert.Equal(2, details.Failures.Count);
        Assert.Equal("good", await File.ReadAllTextAsync(goodDest));
        Assert.False(File.Exists(outsideDest));
        Assert.False(File.Exists(Path.GetFullPath(traversalDest)));

        // Partial success must NOT throw through the compat wrapper.
        Assert.Equal(1, await svc.RestoreBackupAsync(zip, new RestoreOptions()));

        // Total failure still throws a summary through the compat wrapper.
        var allBad = new BackupManifest
        {
            Files = { new BackupFileEntry { ZipPath = "raw/evil.txt", DestinationTokenPath = outsideDest, Provider = "pi", SizeBytes = 4 } }
        };
        var allBadZip = CraftZip(allBad, new Dictionary<string, string> { ["raw/evil.txt"] = "evil" });
        var allBadDetails = await svc.RestoreBackupWithDetailsAsync(allBadZip, new RestoreOptions());
        Assert.Equal(0, allBadDetails.RestoredCount);
        Assert.Single(allBadDetails.Failures);
        await Assert.ThrowsAsync<InvalidDataException>(() => svc.RestoreBackupAsync(allBadZip, new RestoreOptions()));
    }

    [Fact]
    public async Task RestoreDetails_LockedDatabaseStagesSidecar_LeavesOriginalIntact()
    {
        Environment.SetEnvironmentVariable("CASR_BACKUP_EXTRA_ROOTS", _tempDir);
        var target = Path.Combine(_tempDir, "live.db");
        await File.WriteAllTextAsync(target, "original-bytes");
        var payload = "new-db-bytes";
        var manifest = new BackupManifest
        {
            Files = { new BackupFileEntry { ZipPath = "raw/live.db", DestinationTokenPath = target, Provider = "hermes", SizeBytes = payload.Length, Sha256 = Sha256Of(System.Text.Encoding.UTF8.GetBytes(payload)) } }
        };
        var zip = CraftZip(manifest, new Dictionary<string, string> { ["raw/live.db"] = payload });

        // Hold the target open with no sharing: any in-place replace would die.
        using var locker = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var details = await new BackupService().RestoreBackupWithDetailsAsync(zip, new RestoreOptions());

        Assert.Equal(1, details.RestoredCount);
        Assert.Empty(details.Failures);
        var sidecar = Assert.Single(details.PendingSwaps);
        Assert.Contains(".pending_restore", sidecar);
        Assert.Equal(payload, await File.ReadAllTextAsync(target + ".pending_restore"));

        locker.Dispose();
        Assert.Equal("original-bytes", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task RestoreDetails_CanonicalEntriesRestoreToSidecarTree_WithHashCheck()
    {
        Environment.SetEnvironmentVariable("CASR_BACKUP_EXTRA_ROOTS", _tempDir);
        var canonicalJson = "{\"sessionId\":\"s1\",\"provider\":\"grok\"}";
        var manifest = new BackupManifest
        {
            CanonicalFiles =
            {
                new CanonicalFileEntry { ZipPath = "canonical/grok/s1.json", Provider = "grok", SessionId = "s1", SizeBytes = canonicalJson.Length, Sha256 = Sha256Of(System.Text.Encoding.UTF8.GetBytes(canonicalJson)) },
                new CanonicalFileEntry { ZipPath = "canonical/grok/tampered.json", Provider = "grok", SessionId = "tampered", SizeBytes = 2, Sha256 = new string('0', 64) },
            },
            CanonicalFilesCount = 2
        };
        var zip = CraftZip(manifest, new Dictionary<string, string>(),
            new Dictionary<string, string> { ["canonical/grok/s1.json"] = canonicalJson, ["canonical/grok/tampered.json"] = "{}" });

        var details = await new BackupService().RestoreBackupWithDetailsAsync(zip, new RestoreOptions());

        Assert.Equal(1, details.CanonicalRestoredCount);
        Assert.Single(details.Failures);
        var expected = Path.Combine(Path.GetDirectoryName(zip)!, "_restored", "canonical", "grok", "s1.json");
        Assert.Equal(canonicalJson, await File.ReadAllTextAsync(expected));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(zip)!, "_restored", "canonical", "grok", "tampered.json")));
    }

    [Fact]
    public async Task RestoreDetails_SafetyBackupsHashSkippedAndCapped()
    {
        Environment.SetEnvironmentVariable("CASR_BACKUP_EXTRA_ROOTS", _tempDir);
        var target = Path.Combine(_tempDir, "state.txt");
        await File.WriteAllTextAsync(target, "v0");
        var svc = new BackupService();

        async Task RestoreVersionAsync(string content)
        {
            var manifest = new BackupManifest
            {
                Files = { new BackupFileEntry { ZipPath = "raw/state.txt", DestinationTokenPath = target, Provider = "pi", SizeBytes = content.Length, Sha256 = Sha256Of(System.Text.Encoding.UTF8.GetBytes(content)) } }
            };
            var zip = CraftZip(manifest, new Dictionary<string, string> { ["raw/state.txt"] = content });
            var d = await svc.RestoreBackupWithDetailsAsync(zip, new RestoreOptions { CreateBackupCopies = true });
            Assert.Empty(d.Failures);
        }

        string[] Baks() => Directory.GetFiles(_tempDir, "state.txt.bak_*");

        await RestoreVersionAsync("v1"); // replaces v0 -> 1 .bak
        Assert.Single(Baks());

        await RestoreVersionAsync("v1"); // identical bytes -> hash-compare skips the .bak
        Assert.Single(Baks());

        for (int i = 2; i <= 8; i++) await RestoreVersionAsync($"v{i}"); // 7 more distinct versions
        Assert.Equal("v8", await File.ReadAllTextAsync(target));
        Assert.True(Baks().Length <= BackupService.MaxSafetyBackupsPerFile);
    }
}
