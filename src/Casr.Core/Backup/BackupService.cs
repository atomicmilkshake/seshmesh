using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Casr.Core.Logging;
using Casr.Core.Models;
using Casr.Core.Providers;
using Casr.Core.Storage;

namespace Casr.Core.Backup;

public class BackupService
{
    /// <summary>Manifest schema this build writes and accepts on restore.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>How many <c>.bak_*</c> safety copies are kept per restored file.</summary>
    public const int MaxSafetyBackupsPerFile = 5;

    private readonly ProviderRegistry _registry;
    private readonly SessionDatabase? _database;

    public BackupService(ProviderRegistry? registry = null, SessionDatabase? database = null)
    {
        _registry = registry ?? ProviderRegistry.Default;
        _database = database;
    }

    public static string TokenizePath(string fullPath)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrEmpty(localAppData) && fullPath.StartsWith(localAppData, StringComparison.OrdinalIgnoreCase))
        {
            return "%LOCALAPPDATA%" + fullPath.Substring(localAppData.Length);
        }
        if (!string.IsNullOrEmpty(appData) && fullPath.StartsWith(appData, StringComparison.OrdinalIgnoreCase))
        {
            return "%APPDATA%" + fullPath.Substring(appData.Length);
        }
        if (!string.IsNullOrEmpty(userProfile) && fullPath.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
        {
            return "%USERPROFILE%" + fullPath.Substring(userProfile.Length);
        }

        return fullPath;
    }

    public static string ExpandPath(string tokenPath)
    {
        return Environment.ExpandEnvironmentVariables(tokenPath);
    }

    /// Root directories a restore is allowed to write into. Mirrors the roots CollectItems
    /// reads from; anything outside this set is rejected as a potential zip-slip.
    internal static List<string> GetAllowedRestoreRoots()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var roots = new List<string>
        {
            Path.Combine(userProfile, ".gemini"),
            Path.Combine(userProfile, ".grok"),
            Path.Combine(userProfile, ".pi"),
            Path.Combine(userProfile, ".openclaude"),
            Path.Combine(appData, "Cursor"),
            Path.Combine(appData, "Code"),
            Path.Combine(localAppData, "Casr"),
            Path.Combine(localAppData, "hermes"),
            Path.Combine(userProfile, ".local", "share", "opencode"),
        };

        // Provider home overrides via environment (PI_CODING_AGENT_DIR, HERMES_HOME,
        // OPENCODE_HOME/OPENCODE_DATA_DIR/XDG_DATA_HOME, OPENCLAUDE_CONFIG_DIR/HOME):
        // CollectItems reads from these resolved locations, so restores must allow
        // writing back to them as well.
        try { roots.Add(PiProvider.GetHomeDir()); } catch { }
        try { roots.Add(HermesProvider.GetHomeDir()); } catch { }
        try { roots.Add(OpenCodeProvider.GetDataDir()); } catch { }
        try { roots.Add(OpenClaudeProvider.GetHomeDir()); } catch { }
        roots.Add(GetCodexBackupRoot());

        // Test hook: extra roots (semicolon-separated) under which restores are allowed,
        // so hermetic tests can restore into temp dirs without weakening the guard.
        var extra = Environment.GetEnvironmentVariable("CASR_BACKUP_EXTRA_ROOTS");
        if (!string.IsNullOrWhiteSpace(extra))
        {
            foreach (var r in extra.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                roots.Add(r);
            }
        }

        return roots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => Path.GetFullPath(r))
            .ToList();
    }

    internal static bool IsPathUnderAllowedRoots(string fullPath)
    {
        foreach (var root in GetAllowedRestoreRoots())
        {
            var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string ComputeSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    public List<(string SourcePath, string ZipRelativePath, string TokenPath, string Provider)> CollectItems(BackupScope scope)
    {
        var items = new List<(string SourcePath, string ZipRelativePath, string TokenPath, string Provider)>();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // 1. Antigravity
        if (scope.IncludeAntigravity)
        {
            var geminiDir = Path.Combine(userProfile, ".gemini");
            if (Directory.Exists(geminiDir))
            {
                // Skills & Plugins
                var pluginsDir = Path.Combine(geminiDir, "config", "plugins");
                AddDirectoryRecursive(pluginsDir, "raw/antigravity/plugins", "antigravity", items);

                // Global databases & summaries
                var cliDir = Path.Combine(geminiDir, "antigravity-cli");
                AddFileIfExists(Path.Combine(cliDir, "conversation_summaries.db"), "raw/antigravity/conversation_summaries.db", "antigravity", items);
                AddFileIfExists(Path.Combine(cliDir, "history.jsonl"), "raw/antigravity/history.jsonl", "antigravity", items);

                // Brain sessions (transcripts & subagents)
                var brainDir = Path.Combine(cliDir, "brain");
                AddDirectoryRecursive(brainDir, "raw/antigravity/brain", "antigravity", items);
            }
        }

        // 2. Cursor
        if (scope.IncludeCursor)
        {
            var cursorDir = Path.Combine(appData, "Cursor", "User");
            if (Directory.Exists(cursorDir))
            {
                // Global storage DB (state.vscdb has composerHeaders & KV bubbles)
                var globalDb = Path.Combine(cursorDir, "globalStorage", "state.vscdb");
                AddFileIfExists(globalDb, "raw/cursor/globalStorage/state.vscdb", "cursor", items);
                AddFileIfExists(globalDb + "-wal", "raw/cursor/globalStorage/state.vscdb-wal", "cursor", items);
                AddFileIfExists(globalDb + "-shm", "raw/cursor/globalStorage/state.vscdb-shm", "cursor", items);

                // Workspace storage (all workspace.json bindings)
                var wsStorage = Path.Combine(cursorDir, "workspaceStorage");
                AddDirectoryRecursive(wsStorage, "raw/cursor/workspaceStorage", "cursor", items);

                // Settings & Keybindings
                AddFileIfExists(Path.Combine(cursorDir, "settings.json"), "raw/cursor/settings.json", "cursor", items);
                AddFileIfExists(Path.Combine(cursorDir, "keybindings.json"), "raw/cursor/keybindings.json", "cursor", items);
            }
        }

        // 3. Grok CLI
        if (scope.IncludeGrok)
        {
            var grokDir = Path.Combine(userProfile, ".grok");
            if (Directory.Exists(grokDir))
            {
                AddDirectoryRecursive(grokDir, "raw/grok", "grok", items);
            }
        }

        // 4. Pi coding agent (PI_CODING_AGENT_DIR override or ~/.pi/agent)
        if (scope.IncludePi)
        {
            try
            {
                var piHome = PiProvider.GetHomeDir();
                if (Directory.Exists(piHome))
                {
                    AddDirectoryRecursive(piHome, "raw/pi", "pi", items);
                }

                // When the env override points elsewhere, ~/.pi may still hold config.
                var legacyPi = Path.Combine(userProfile, ".pi");
                if (Directory.Exists(legacyPi) &&
                    !string.Equals(Path.GetFullPath(legacyPi), Path.GetFullPath(piHome), StringComparison.OrdinalIgnoreCase) &&
                    !Path.GetFullPath(piHome).StartsWith(Path.GetFullPath(legacyPi) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    AddDirectoryRecursive(legacyPi, "raw/pi_home", "pi", items);
                }
            }
            catch (Exception ex)
            {
                CasrLogger.Warn("BACKUP", $"Failed collecting Pi sessions: {ex.Message}");
            }
        }

        // 5. Hermes (HERMES_HOME override or %LOCALAPPDATA%\hermes):
        // state.db (+ journals) plus the saved-sessions dir and top-level config
        // files. The bin/ runtime is deliberately excluded.
        if (scope.IncludeHermes)
        {
            try
            {
                var hermesHome = HermesProvider.GetHomeDir();
                if (Directory.Exists(hermesHome))
                {
                    var stateDb = HermesProvider.GetStateDbPath();
                    AddFileIfExists(stateDb, "raw/hermes/state.db", "hermes", items);
                    AddFileIfExists(stateDb + "-wal", "raw/hermes/state.db-wal", "hermes", items);
                    AddFileIfExists(stateDb + "-shm", "raw/hermes/state.db-shm", "hermes", items);

                    var savedDir = HermesProvider.GetSavedSessionsDir();
                    AddDirectoryRecursive(savedDir, "raw/hermes/sessions/saved", "hermes", items);

                    foreach (var topFile in Directory.EnumerateFiles(hermesHome, "*", SearchOption.TopDirectoryOnly))
                    {
                        var name = Path.GetFileName(topFile);
                        if (name.Equals("state.db", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("state.db-", StringComparison.OrdinalIgnoreCase))
                        {
                            continue; // already added above
                        }
                        AddFileIfExists(topFile, "raw/hermes/" + name.Replace('\\', '/'), "hermes", items);
                    }
                }
            }
            catch (Exception ex)
            {
                CasrLogger.Warn("BACKUP", $"Failed collecting Hermes sessions: {ex.Message}");
            }
        }

        // 6. OpenCode (OPENCODE_HOME / OPENCODE_DATA_DIR / XDG_DATA_HOME override
        // or ~/.local/share/opencode): whole data dir incl. opencode.db.
        if (scope.IncludeOpenCode)
        {
            try
            {
                var ocDir = OpenCodeProvider.GetDataDir();
                if (Directory.Exists(ocDir))
                {
                    AddDirectoryRecursive(ocDir, "raw/opencode", "opencode", items);
                }
            }
            catch (Exception ex)
            {
                CasrLogger.Warn("BACKUP", $"Failed collecting OpenCode sessions: {ex.Message}");
            }
        }

        // 7. OpenClaude (OPENCLAUDE_CONFIG_DIR / OPENCLAUDE_HOME override or ~/.openclaude)
        if (scope.IncludeOpenClaude)
        {
            try
            {
                var ocHome = OpenClaudeProvider.GetHomeDir();
                if (Directory.Exists(ocHome))
                {
                    AddDirectoryRecursive(ocHome, "raw/openclaude", "openclaude", items);
                }
            }
            catch (Exception ex)
            {
                CasrLogger.Warn("BACKUP", $"Failed collecting OpenClaude sessions: {ex.Message}");
            }
        }

        // Codex rollouts are self-contained transcripts. Back up active and archived
        // sessions plus the title index; credentials and caches are outside this scope.
        if (scope.IncludeCodex)
        {
            var codexRoot = GetCodexBackupRoot();
            AddDirectoryRecursive(Path.Combine(codexRoot, "sessions"), "raw/codex/sessions", "codex", items);
            AddDirectoryRecursive(Path.Combine(codexRoot, "archived_sessions"), "raw/codex/archived_sessions", "codex", items);
            AddFileIfExists(Path.Combine(codexRoot, "session_index.jsonl"), "raw/codex/session_index.jsonl", "codex", items);
        }

        // CASR Index & Settings
        if (scope.IncludeCasrDatabase)
        {
            var casrDir = Path.Combine(localAppData, "Casr");
            if (Directory.Exists(casrDir))
            {
                var dbFile = Path.Combine(casrDir, "casr_index.db");
                AddFileIfExists(dbFile, "raw/casr/casr_index.db", "casr", items);
                AddFileIfExists(dbFile + "-wal", "raw/casr/casr_index.db-wal", "casr", items);
                AddFileIfExists(dbFile + "-shm", "raw/casr/casr_index.db-shm", "casr", items);
                AddFileIfExists(Path.Combine(casrDir, "settings.json"), "raw/casr/settings.json", "casr", items);
            }
        }

        return items;
    }

    private static void AddFileIfExists(string filePath, string zipRelativePath, string provider, List<(string, string, string, string)> items)
    {
        if (File.Exists(filePath))
        {
            items.Add((filePath, zipRelativePath, TokenizePath(filePath), provider));
        }
    }

    private static string GetCodexBackupRoot() =>
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } codexRoot && !string.IsNullOrWhiteSpace(codexRoot)
            ? Path.GetFullPath(codexRoot)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    private static void AddDirectoryRecursive(string dirPath, string zipRelativePrefix, string provider, List<(string, string, string, string)> items)
    {
        if (!Directory.Exists(dirPath)) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(dirPath, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(dirPath, file).Replace('\\', '/');
                var zipPath = $"{zipRelativePrefix}/{rel}";
                items.Add((file, zipPath, TokenizePath(file), provider));
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Warn("BACKUP", $"Failed scanning directory {dirPath}: {ex.Message}");
        }
    }

    public async Task<BackupManifest> CreateBackupAsync(
        string targetZipPath,
        BackupScope scope,
        IProgress<BackupProgress>? progress = null,
        CancellationToken ct = default)
    {
        var targetDir = Path.GetDirectoryName(targetZipPath);
        if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

        var manifest = new BackupManifest
        {
            CreatedAtUtc = DateTime.UtcNow,
            Scope = scope,
            MachineName = Environment.MachineName,
            UserName = Environment.UserName
        };

        var items = CollectItems(scope);
        long totalBytes = 0;
        foreach (var item in items)
        {
            try { totalBytes += new FileInfo(item.SourcePath).Length; } catch { }
        }

        long processedBytes = 0;
        int processedFiles = 0;

        CasrLogger.Info("BACKUP", $"Starting backup to {targetZipPath}. Total items: {items.Count}, Total bytes: {totalBytes}");

        // Use temporary file while writing
        var tempZip = targetZipPath + ".tmp";
        if (File.Exists(tempZip)) File.Delete(tempZip);

        try
        {
        using (var zipStream = new FileStream(tempZip, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, false))
        {
            foreach (var (src, zipPath, tokenPath, provider) in items)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var fi = new FileInfo(src);
                    var entry = archive.CreateEntry(zipPath, CompressionLevel.Optimal);
                    entry.LastWriteTime = fi.LastWriteTime;

                    using (var srcStream = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var entryStream = entry.Open())
                    {
                        await srcStream.CopyToAsync(entryStream, 81920, ct);
                    }

                    string? sha256 = null;
                    try { sha256 = ComputeSha256(src); } catch { /* hashing is best-effort */ }

                    manifest.Files.Add(new BackupFileEntry
                    {
                        ZipPath = zipPath,
                        DestinationTokenPath = tokenPath,
                        Provider = provider,
                        SizeBytes = fi.Length,
                        Sha256 = sha256
                    });

                    if (!manifest.ProviderCounts.ContainsKey(provider)) manifest.ProviderCounts[provider] = 0;
                    manifest.ProviderCounts[provider]++;

                    processedBytes += fi.Length;
                    processedFiles++;

                    progress?.Report(new BackupProgress
                    {
                        CurrentItem = Path.GetFileName(src),
                        FilesProcessed = processedFiles,
                        TotalFiles = items.Count,
                        BytesProcessed = processedBytes,
                        TotalBytes = totalBytes,
                        StatusMessage = $"Archiving {Path.GetFileName(src)}..."
                    });
                }
                catch (Exception ex)
                {
                    var reason = $"Skipped {src}: {ex.GetType().Name}: {ex.Message}";
                    manifest.Warnings.Add(reason);
                    CasrLogger.Warn("BACKUP", $"Could not backup {src}: {ex.Message}");
                }
            }

            // Optional Canonical JSON Export: every indexed session is re-read through
            // its provider into the portable CanonicalSession shape and registered in
            // the manifest (zip path, provider, session id, size, SHA-256) so the
            // restore path can verify and re-materialize each file. No cap: every
            // session is attempted, and every skip is recorded in manifest.Warnings
            // with its reason instead of being silently dropped.
            if (scope.IncludeCanonicalExport && _database != null)
            {
                try
                {
                    progress?.Report(new BackupProgress
                    {
                        CurrentItem = "canonical_export.json",
                        FilesProcessed = processedFiles,
                        TotalFiles = items.Count,
                        BytesProcessed = processedBytes,
                        TotalBytes = totalBytes,
                        StatusMessage = "Synthesizing portable canonical sessions..."
                    });

                    var recent = _database.GetRecentSessions(int.MaxValue);
                    foreach (var s in recent)
                    {
                        ct.ThrowIfCancellationRequested();
                        var p = _registry.FindBySlug(s.Provider);
                        if (p == null)
                        {
                            manifest.Warnings.Add($"Canonical export skipped {s.SessionId}: unknown provider '{s.Provider}'.");
                            continue;
                        }
                        try
                        {
                            var canonical = p.ReadSession(s.SourcePath);
                            var json = JsonSerializer.Serialize(canonical, new JsonSerializerOptions { WriteIndented = true });
                            var payload = Encoding.UTF8.GetBytes(json);
                            var zipPath = $"canonical/{s.Provider}/{s.SessionId}.json";
                            var cEntry = archive.CreateEntry(zipPath, CompressionLevel.Optimal);
                            using (var entryStream = cEntry.Open())
                            {
                                await entryStream.WriteAsync(payload, ct);
                            }

                            string sha;
                            using (var hasher = SHA256.Create())
                            {
                                sha = Convert.ToHexString(hasher.ComputeHash(payload)).ToLowerInvariant();
                            }

                            manifest.CanonicalFiles.Add(new CanonicalFileEntry
                            {
                                ZipPath = zipPath,
                                Provider = s.Provider,
                                SessionId = s.SessionId,
                                SizeBytes = payload.Length,
                                Sha256 = sha
                            });
                            if (!manifest.CanonicalCounts.ContainsKey(s.Provider)) manifest.CanonicalCounts[s.Provider] = 0;
                            manifest.CanonicalCounts[s.Provider]++;
                            manifest.CanonicalSizeBytes += payload.Length;
                        }
                        catch (Exception ex)
                        {
                            manifest.Warnings.Add($"Canonical export skipped {s.Provider}/{s.SessionId}: {ex.GetType().Name}: {ex.Message}");
                            CasrLogger.Warn("BACKUP", $"Canonical export skipped {s.Provider}/{s.SessionId}: {ex.Message}");
                        }
                    }
                    manifest.CanonicalFilesCount = manifest.CanonicalFiles.Count;
                }
                catch (Exception ex)
                {
                    manifest.Warnings.Add($"Canonical export aborted: {ex.GetType().Name}: {ex.Message}");
                    CasrLogger.Warn("BACKUP", $"Failed canonical export: {ex.Message}");
                }
            }

            manifest.TotalFilesCount = manifest.Files.Count;
            manifest.TotalSizeBytes = processedBytes;

            // Write Manifest into zip root
            var manifestEntry = archive.CreateEntry("backup_manifest.json", CompressionLevel.Optimal);
            using (var mWriter = new StreamWriter(manifestEntry.Open()))
            {
                var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
                await mWriter.WriteAsync(manifestJson);
            }
        }

        if (File.Exists(targetZipPath)) File.Delete(targetZipPath);
        File.Move(tempZip, targetZipPath);

        CasrLogger.Info("BACKUP", $"Backup completed successfully: {targetZipPath} ({new FileInfo(targetZipPath).Length} bytes)");
        return manifest;
        }
        catch
        {
            // Cancelled or failed mid-write: never leave the orphaned temp zip behind.
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Reads the manifest and verifies the archive can actually be restored:
    /// the manifest must parse, its <see cref="BackupManifest.SchemaVersion"/>
    /// must be supported, and every <c>Files</c> / <c>CanonicalFiles</c> zip
    /// path must exist in the archive. Returns null when any check fails, so
    /// callers can keep Restore disabled on archives that would only
    /// partially restore. (Deliberately no full hash pre-check here: hashing
    /// every entry would re-read multi-GB archives on each inspection; hashes
    /// are verified per entry during restore instead.)
    /// </summary>
    public static BackupManifest? InspectBackup(string zipPath)
    {
        if (!File.Exists(zipPath)) return null;

        try
        {
            using var zipStream = File.OpenRead(zipPath);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
            var entry = archive.GetEntry("backup_manifest.json");
            if (entry == null) return null;

            BackupManifest? manifest;
            using (var reader = new StreamReader(entry.Open()))
            {
                var json = reader.ReadToEnd();
                manifest = JsonSerializer.Deserialize<BackupManifest>(json);
            }
            if (manifest == null) return null;

            if (manifest.SchemaVersion < 1 || manifest.SchemaVersion > CurrentSchemaVersion)
            {
                CasrLogger.Warn("BACKUP", $"Backup at {zipPath} has unsupported schema version {manifest.SchemaVersion} (supported: 1-{CurrentSchemaVersion}).");
                return null;
            }

            var names = new HashSet<string>(
                archive.Entries.Select(e => e.FullName),
                StringComparer.Ordinal);
            foreach (var f in manifest.Files)
            {
                if (!names.Contains(f.ZipPath))
                {
                    CasrLogger.Warn("BACKUP", $"Backup at {zipPath} is incomplete: manifest entry '{f.ZipPath}' is missing from the archive.");
                    return null;
                }
            }
            foreach (var c in manifest.CanonicalFiles)
            {
                if (!names.Contains(c.ZipPath))
                {
                    CasrLogger.Warn("BACKUP", $"Backup at {zipPath} is incomplete: canonical entry '{c.ZipPath}' is missing from the archive.");
                    return null;
                }
            }

            return manifest;
        }
        catch (Exception ex)
        {
            CasrLogger.Error("BACKUP", $"Failed inspecting backup at {zipPath}", ex);
            return null;
        }
    }

    /// <summary>
    /// Restores a backup archive. Back-compatible wrapper: returns the number of
    /// raw files restored, and throws a summary <see cref="InvalidDataException"/>
    /// only when nothing at all could be restored. Prefer
    /// <see cref="RestoreBackupWithDetailsAsync"/> for partial-success detail.
    /// </summary>
    public async Task<int> RestoreBackupAsync(
        string zipPath,
        RestoreOptions options,
        IProgress<RestoreProgress>? progress = null,
        CancellationToken ct = default)
    {
        var result = await RestoreBackupWithDetailsAsync(zipPath, options, progress, ct);
        if (result.RestoredCount == 0 && result.CanonicalRestoredCount == 0 && result.Failures.Count > 0)
        {
            var shown = string.Join("; ", result.Failures.Take(5));
            var more = result.Failures.Count > 5 ? $" (+{result.Failures.Count - 5} more)" : string.Empty;
            throw new InvalidDataException($"Restore failed: no files could be restored. {shown}{more}");
        }
        return result.RestoredCount;
    }

    /// <summary>
    /// Restores a backup archive with per-entry isolation: any single entry may
    /// fail (missing from the zip, zip-slip refusal, SHA-256 mismatch, locked
    /// live database) and is recorded in <see cref="RestoreResult.Failures"/>
    /// while the remaining entries still restore. Never throws for per-entry
    /// failures — only for a missing/corrupt manifest or cancellation.
    /// </summary>
    public async Task<RestoreResult> RestoreBackupWithDetailsAsync(
        string zipPath,
        RestoreOptions options,
        IProgress<RestoreProgress>? progress = null,
        CancellationToken ct = default)
    {
        var manifest = InspectBackup(zipPath);
        if (manifest == null)
        {
            throw new InvalidOperationException("Invalid backup archive: backup_manifest.json missing, corrupted, unsupported schema, or referencing files absent from the archive.");
        }

        var result = new RestoreResult();
        CasrLogger.Info("BACKUP", $"Starting restoration from {zipPath}. Total files in manifest: {manifest.Files.Count}, canonical: {manifest.CanonicalFiles.Count}");

        using var zipStream = File.OpenRead(zipPath);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        var filter = options.ProvidersToRestore;
        var filesToRestore = manifest.Files.Where(f => filter.Count == 0 || filter.Contains(f.Provider)).ToList();
        var canonicalToRestore = manifest.CanonicalFiles.Where(c => filter.Count == 0 || filter.Contains(c.Provider)).ToList();
        result.TotalRequested = filesToRestore.Count + canonicalToRestore.Count;

        int restoredCount = 0;
        int totalUnits = Math.Max(1, result.TotalRequested);
        // Millisecond resolution: two restores in the same second must not share a .bak name.
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");

        void Beat(string fileName, string status)
        {
            progress?.Report(new RestoreProgress
            {
                CurrentFile = fileName,
                FilesRestored = restoredCount + result.CanonicalRestoredCount,
                TotalFiles = totalUnits,
                StatusMessage = status
            });
        }

        foreach (var file in filesToRestore)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var entry = archive.GetEntry(file.ZipPath);
                if (entry == null)
                {
                    result.Failures.Add($"'{file.ZipPath}': listed in manifest but missing from the archive.");
                    continue;
                }

                var targetPath = Path.GetFullPath(ExpandPath(file.DestinationTokenPath));

                // Zip-slip guard: manifest paths come from an untrusted archive. Refuse to write
                // anywhere outside the roots CollectItems could have produced.
                if (!IsPathUnderAllowedRoots(targetPath))
                {
                    result.Failures.Add(
                        $"Refusing to restore '{file.ZipPath}': destination '{file.DestinationTokenPath}' " +
                        $"resolves to '{targetPath}', which is outside the allowed restore roots.");
                    continue;
                }

                // Hash verification: entries created by current builds carry a SHA-256; a mismatch
                // means the archive was tampered with or corrupted after the backup was taken.
                if (!string.IsNullOrEmpty(file.Sha256))
                {
                    string actualHash;
                    using (var verifyStream = entry.Open())
                    using (var sha = SHA256.Create())
                    {
                        actualHash = Convert.ToHexString(sha.ComputeHash(verifyStream)).ToLowerInvariant();
                    }
                    if (!string.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Failures.Add(
                            $"SHA-256 mismatch for '{file.ZipPath}' (expected {file.Sha256}, got {actualHash}). " +
                            "The archive entry is corrupted or was modified after the backup was created.");
                        continue;
                    }
                }

                var targetDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

                // Live-database guard: a SQLite store held open by its owner (CASR index,
                // Hermes state.db, OpenCode opencode.db, Cursor state.vscdb, ...) cannot be
                // replaced in place — the write would die with a sharing violation. Land the
                // bytes in a sidecar instead and offer the swap on restart.
                if (ShouldRestoreToSidecar(targetPath))
                {
                    var sidecar = targetPath + ".pending_restore";
                    try
                    {
                        using (var entryStream = entry.Open())
                        using (var outStream = new FileStream(sidecar, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            await entryStream.CopyToAsync(outStream, 81920, ct);
                        }
                        result.PendingSwaps.Add($"{targetPath} <= {sidecar}");
                        result.Warnings.Add($"'{file.ZipPath}': target database is open; restored to sidecar '{sidecar}' — restart SeshMesh and the owning agent to complete the swap.");
                        restoredCount++;
                        Beat(Path.GetFileName(targetPath), $"Staged {Path.GetFileName(targetPath)} (pending restart)");
                    }
                    catch (Exception ex)
                    {
                        result.Failures.Add($"'{file.ZipPath}': sidecar restore to '{sidecar}' failed: {ex.GetType().Name}: {ex.Message}");
                    }
                    continue;
                }

                // Safety backup of existing target (hash-compared, aged-out; never aborts).
                if (File.Exists(targetPath) && options.CreateBackupCopies)
                {
                    CreateSafetyBackup(targetPath, file.Sha256, timestamp);
                }

                try
                {
                    using (var entryStream = entry.Open())
                    using (var outStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await entryStream.CopyToAsync(outStream, 81920, ct);
                    }

                    restoredCount++;
                    Beat(Path.GetFileName(targetPath), $"Restored {Path.GetFileName(targetPath)}");
                }
                catch (IOException ex) when (IsSharingViolation(ex))
                {
                    // Lost a race with the owning process between the lock probe and the
                    // write: same sidecar fallback as the proactive guard above.
                    try
                    {
                        var sidecar = targetPath + ".pending_restore";
                        using (var entryStream = entry.Open())
                        using (var outStream = new FileStream(sidecar, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            await entryStream.CopyToAsync(outStream, 81920, ct);
                        }
                        result.PendingSwaps.Add($"{targetPath} <= {sidecar}");
                        result.Warnings.Add($"'{file.ZipPath}': target locked during restore; staged to sidecar '{sidecar}' — restart to complete the swap.");
                        restoredCount++;
                        Beat(Path.GetFileName(targetPath), $"Staged {Path.GetFileName(targetPath)} (pending restart)");
                    }
                    catch (Exception sideEx)
                    {
                        result.Failures.Add($"'{file.ZipPath}': target locked and sidecar restore failed: {sideEx.GetType().Name}: {sideEx.Message}");
                    }
                }
                catch (Exception ex)
                {
                    result.Failures.Add($"'{file.ZipPath}': failed restoring '{targetPath}': {ex.GetType().Name}: {ex.Message}");
                    CasrLogger.Warn("BACKUP", $"Failed restoring {targetPath}: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                // Per-entry isolation: one bad row must never abort the whole restore.
                result.Failures.Add($"'{file.ZipPath}': {ex.GetType().Name}: {ex.Message}");
                CasrLogger.Warn("BACKUP", $"Failed restoring manifest entry {file.ZipPath}: {ex.Message}");
            }
        }

        // Canonical snapshots restore into a sidecar tree next to the archive, so they
        // are recoverable even where the original provider stores no longer exist.
        if (canonicalToRestore.Count > 0)
        {
            var zipDir = Path.GetDirectoryName(Path.GetFullPath(zipPath)) ?? Directory.GetCurrentDirectory();
            var canonicalRoot = Path.GetFullPath(Path.Combine(zipDir, "_restored", "canonical"));
            foreach (var c in canonicalToRestore)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var entry = archive.GetEntry(c.ZipPath);
                    if (entry == null)
                    {
                        result.Failures.Add($"'{c.ZipPath}': listed in manifest but missing from the archive.");
                        continue;
                    }

                    var targetPath = Path.GetFullPath(Path.Combine(canonicalRoot, c.Provider, c.SessionId + ".json"));
                    var prefix = canonicalRoot.EndsWith(Path.DirectorySeparatorChar) ? canonicalRoot : canonicalRoot + Path.DirectorySeparatorChar;
                    if (!targetPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Failures.Add($"Refusing to restore '{c.ZipPath}': session id escapes the canonical restore root.");
                        continue;
                    }

                    var cDir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(cDir)) Directory.CreateDirectory(cDir);

                    byte[] payload;
                    using (var entryStream = entry.Open())
                    using (var ms = new MemoryStream())
                    {
                        await entryStream.CopyToAsync(ms, 81920, ct);
                        payload = ms.ToArray();
                    }

                    if (!string.IsNullOrEmpty(c.Sha256))
                    {
                        string actual;
                        using (var sha = SHA256.Create())
                        {
                            actual = Convert.ToHexString(sha.ComputeHash(payload)).ToLowerInvariant();
                        }
                        if (!string.Equals(actual, c.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            result.Failures.Add($"SHA-256 mismatch for '{c.ZipPath}' (expected {c.Sha256}, got {actual}).");
                            continue;
                        }
                    }

                    await File.WriteAllBytesAsync(targetPath, payload, ct);
                    result.CanonicalRestoredCount++;
                    Beat(c.SessionId + ".json", $"Restored canonical {c.Provider}/{c.SessionId}");
                }
                catch (Exception ex)
                {
                    result.Failures.Add($"'{c.ZipPath}': {ex.GetType().Name}: {ex.Message}");
                    CasrLogger.Warn("BACKUP", $"Failed restoring canonical entry {c.ZipPath}: {ex.Message}");
                }
            }
        }

        result.RestoredCount = restoredCount;
        CasrLogger.Info("BACKUP", $"Restoration completed: {restoredCount} raw + {result.CanonicalRestoredCount} canonical restored, {result.Failures.Count} failed, {result.PendingSwaps.Count} pending restart, out of {result.TotalRequested} requested.");
        return result;
    }

    private static bool IsSharingViolation(IOException ex)
    {
        var code = ex.HResult & 0xFFFF;
        return code == 32 || code == 33; // ERROR_SHARING_VIOLATION / ERROR_LOCK_VIOLATION
    }

    private static bool IsFileLocked(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static readonly string[] LiveDatabaseExtensions =
        { ".db", ".vscdb", ".sqlite", ".sqlite3" };

    /// <summary>
    /// True when the target is a live database file that is currently held open,
    /// i.e. an in-place replace would die with a sharing violation.
    /// </summary>
    private bool ShouldRestoreToSidecar(string targetPath)
    {
        if (!File.Exists(targetPath)) return false;

        var ext = Path.GetExtension(targetPath);
        var looksLikeDb = LiveDatabaseExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)
            || IsKnownLiveDatabasePath(targetPath);
        return looksLikeDb && IsFileLocked(targetPath);
    }

    private bool IsKnownLiveDatabasePath(string targetPath)
    {
        var candidates = new List<string?>();
        try
        {
            if (_database != null) candidates.Add(_database.DbPath);
            candidates.Add(HermesProvider.GetStateDbPath());
            candidates.Add(OpenCodeProvider.GetDatabasePath());
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(Path.Combine(appData, "Cursor", "User", "globalStorage", "state.vscdb"));
            candidates.Add(Path.Combine(userProfile, ".gemini", "antigravity-cli", "conversation_summaries.db"));
            candidates.Add(Path.Combine(localAppData, "Casr", "casr_index.db"));
        }
        catch { }

        string full;
        try { full = Path.GetFullPath(targetPath); }
        catch { return false; }

        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            try
            {
                if (string.Equals(Path.GetFullPath(c), full, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }
        }
        return false;
    }

    /// <summary>
    /// Copies the about-to-be-replaced file to <c>.bak_yyyyMMdd_HHmmss_fff</c>.
    /// Skips the copy when the existing bytes already match the incoming entry
    /// hash, and ages out older backups beyond <see cref="MaxSafetyBackupsPerFile"/>.
    /// Never throws: safety must not abort a restore.
    /// </summary>
    private static void CreateSafetyBackup(string targetPath, string? incomingSha256, string timestamp)
    {
        try
        {
            if (!string.IsNullOrEmpty(incomingSha256))
            {
                try
                {
                    var existingSha = ComputeSha256(targetPath);
                    if (string.Equals(existingSha, incomingSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        return; // identical bytes: no .bak needed
                    }
                }
                catch { /* fall through to copy */ }
            }

            var bakPath = $"{targetPath}.bak_{timestamp}";
            if (File.Exists(bakPath))
            {
                bakPath += "_" + Guid.NewGuid().ToString("N")[..8];
            }
            File.Copy(targetPath, bakPath, false);
            AgeOutSafetyBackups(targetPath);
        }
        catch (Exception ex)
        {
            CasrLogger.Warn("BACKUP", $"Could not create safety backup of {targetPath}: {ex.Message}");
        }
    }

    private static void AgeOutSafetyBackups(string targetPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(targetPath);
            var name = Path.GetFileName(targetPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return;
            var olds = Directory.EnumerateFiles(dir, name + ".bak_*", SearchOption.TopDirectoryOnly)
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.CreationTimeUtc)
                .ThenByDescending(f => f.Name, StringComparer.Ordinal)
                .Skip(MaxSafetyBackupsPerFile)
                .ToList();
            foreach (var stale in olds)
            {
                try { stale.Delete(); } catch { }
            }
        }
        catch { }
    }
}
