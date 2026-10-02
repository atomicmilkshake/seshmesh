using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Casr.Core.Backup;
using Casr.Core.Configuration;
using Casr.Core.Logging;
using Casr.Core.Providers;
using Casr.Core.Storage;
using Microsoft.Win32;

namespace Casr.App.ViewModels;

public class BackupViewModel : ViewModelBase, IDisposable
{
    private readonly BackupService _backupService;
    private readonly SessionDatabase? _database;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _inspectCts;
    private bool _disposed;
    private int _inspectGeneration;

    private string _backupDirectory;
    private bool _includeAntigravity = true;
    private bool _includeCursor = true;
    private bool _includeGrok = true;
    private bool _includePi = true;
    private bool _includeHermes = true;
    private bool _includeOpenCode = true;
    private bool _includeOpenClaude = true;
    private bool _includeCodex = true;
    private bool _includeCasrDatabase = true;
    private bool _includeCanonicalExport = true;

    private string _restoreZipPath = string.Empty;
    private BackupManifest? _inspectedManifest;
    private bool _createRestoreBackupCopies = true;

    private string _customDbPath;

    private bool _isBusy;
    private bool _isInspecting;
    private double _progressPercent;
    private string _statusTitle = "Ready";
    private string _statusDetail = string.Empty;
    private string _operationResult = string.Empty;

    public BackupViewModel(SessionDatabase? database = null)
    {
        _database = database;
        _backupService = new BackupService(ProviderRegistry.Default, database);

        var defaultBackupDir = UserSettings.Default.BackupDirectory;
        if (string.IsNullOrWhiteSpace(defaultBackupDir))
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            defaultBackupDir = !string.IsNullOrWhiteSpace(docs)
                ? Path.Combine(docs, "SeshMesh", "backups")
                : Path.Combine(userProfile, "SeshMeshBackups");
        }
        _backupDirectory = defaultBackupDir;

        _customDbPath = UserSettings.Default.CustomDatabasePath ?? (_database?.DbPath ?? string.Empty);

        BrowseBackupDirCommand = new RelayCommand(BrowseBackupDir, () => !IsBusy && !IsInspecting);
        BrowseRestoreZipCommand = new RelayCommand(BrowseRestoreZip, () => !IsBusy && !IsInspecting);
        StartBackupCommand = new AsyncRelayCommand(async () => await StartBackupAsync(), () => !IsBusy && !IsInspecting && !string.IsNullOrWhiteSpace(BackupDirectory), "backup");
        StartRestoreCommand = new AsyncRelayCommand(async () => await StartRestoreAsync(), () => !IsBusy && !IsInspecting && InspectedManifest != null, "restore");
        CancelCommand = new RelayCommand(CancelOperation, () => IsBusy);
        SaveStorageSettingsCommand = new AsyncRelayCommand(async () => await SaveStorageSettingsAsync(), () => !IsBusy && !IsInspecting, "save-storage");
    }

    public string BackupDirectory
    {
        get => _backupDirectory;
        set => SetProperty(ref _backupDirectory, value);
    }

    public bool IncludeAntigravity
    {
        get => _includeAntigravity;
        set => SetProperty(ref _includeAntigravity, value);
    }

    public bool IncludeCursor
    {
        get => _includeCursor;
        set => SetProperty(ref _includeCursor, value);
    }

    public bool IncludeGrok
    {
        get => _includeGrok;
        set => SetProperty(ref _includeGrok, value);
    }

    public bool IncludePi
    {
        get => _includePi;
        set => SetProperty(ref _includePi, value);
    }

    public bool IncludeHermes
    {
        get => _includeHermes;
        set => SetProperty(ref _includeHermes, value);
    }

    public bool IncludeOpenCode
    {
        get => _includeOpenCode;
        set => SetProperty(ref _includeOpenCode, value);
    }

    public bool IncludeOpenClaude
    {
        get => _includeOpenClaude;
        set => SetProperty(ref _includeOpenClaude, value);
    }

    public bool IncludeCodex
    {
        get => _includeCodex;
        set => SetProperty(ref _includeCodex, value);
    }

    public bool IncludeCasrDatabase
    {
        get => _includeCasrDatabase;
        set => SetProperty(ref _includeCasrDatabase, value);
    }

    public bool IncludeCanonicalExport
    {
        get => _includeCanonicalExport;
        set => SetProperty(ref _includeCanonicalExport, value);
    }

    public string RestoreZipPath
    {
        get => _restoreZipPath;
        set
        {
            if (SetProperty(ref _restoreZipPath, value))
            {
                InspectRestoreZip();
            }
        }
    }

    public BackupManifest? InspectedManifest
    {
        get => _inspectedManifest;
        set
        {
            if (SetProperty(ref _inspectedManifest, value))
            {
                // The Restore command's CanExecute depends on this: requery now so
                // the button enables/disables the moment inspection lands.
                SafeInvalidateRequerySuggested();
            }
        }
    }

    public bool CreateRestoreBackupCopies
    {
        get => _createRestoreBackupCopies;
        set => SetProperty(ref _createRestoreBackupCopies, value);
    }

    public string CustomDbPath
    {
        get => _customDbPath;
        set => SetProperty(ref _customDbPath, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsIndeterminateProgress));
                SafeInvalidateRequerySuggested();
            }
        }
    }

    public bool IsInspecting
    {
        get => _isInspecting;
        set
        {
            if (SetProperty(ref _isInspecting, value))
            {
                SafeInvalidateRequerySuggested();
            }
        }
    }

    private static void SafeInvalidateRequerySuggested()
    {
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                _ = dispatcher.InvokeAsync(CommandManager.InvalidateRequerySuggested);
            }
            else if (Application.Current != null)
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Warn("BACKUP_VM", $"InvalidateRequerySuggested failed: {ex.Message}");
        }
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        set
        {
            if (SetProperty(ref _progressPercent, value))
            {
                OnPropertyChanged(nameof(IsIndeterminateProgress));
            }
        }
    }

    public bool IsIndeterminateProgress => IsBusy && ProgressPercent <= 0;

    public string StatusTitle
    {
        get => _statusTitle;
        set => SetProperty(ref _statusTitle, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        set => SetProperty(ref _statusDetail, value);
    }

    public string OperationResult
    {
        get => _operationResult;
        set => SetProperty(ref _operationResult, value);
    }

    public ICommand BrowseBackupDirCommand { get; }
    public ICommand BrowseRestoreZipCommand { get; }
    public ICommand StartBackupCommand { get; }
    public ICommand StartRestoreCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand SaveStorageSettingsCommand { get; }

    private void BrowseBackupDir()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select Destination Folder for Agent Backups",
            InitialDirectory = Directory.Exists(BackupDirectory) ? BackupDirectory : null
        };

        if (dlg.ShowDialog() == true)
        {
            BackupDirectory = dlg.FolderName;
            UserSettings.Default.BackupDirectory = dlg.FolderName;
            UserSettings.Default.Save();
        }
    }

    private void BrowseRestoreZip()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select a SeshMesh backup archive (.zip)",
            Filter = "SeshMesh backup archives (*.zip)|*.zip|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(BackupDirectory) ? BackupDirectory : null
        };

        if (dlg.ShowDialog() == true)
        {
            RestoreZipPath = dlg.FileName;
        }
    }

    private async void InspectRestoreZip()
    {
        // Generation guard: only the latest inspection may publish StatusTitle/Detail
        // or clear IsInspecting — a stale task must never overwrite newer text.
        var generation = Interlocked.Increment(ref _inspectGeneration);
        bool IsCurrent() => generation == Volatile.Read(ref _inspectGeneration);

        // Debounce: typing a path fires per keystroke, so wait 400ms for the user
        // to settle before touching disk. The CTS cancels a pending wait when a
        // newer keystroke arrives; the generation guard covers the Task.Run tail.
        var prevCts = Interlocked.Exchange(ref _inspectCts, new CancellationTokenSource());
        try { prevCts?.Cancel(); } catch { }
        prevCts?.Dispose();
        var debounceToken = _inspectCts?.Token ?? CancellationToken.None;

        InspectedManifest = null;
        if (!File.Exists(RestoreZipPath))
        {
            // No stale "Inspecting…" or previous-manifest text may linger.
            if (IsCurrent())
            {
                StatusTitle = string.IsNullOrWhiteSpace(RestoreZipPath) ? "Ready" : "Archive not found";
                StatusDetail = string.IsNullOrWhiteSpace(RestoreZipPath)
                    ? string.Empty
                    : $"No file exists at {RestoreZipPath}.";
            }
            return;
        }

        try
        {
            await Task.Delay(400, debounceToken);
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer keystroke
        }
        if (!IsCurrent()) return;

        var targetPath = RestoreZipPath;
        IsInspecting = true;
        StatusTitle = "Inspecting backup archive...";
        StatusDetail = "Reading manifest and verifying archive contents...";

        try
        {
            var manifest = await Task.Run(() => BackupService.InspectBackup(targetPath));
            if (!IsCurrent() || targetPath != RestoreZipPath) return; // Stale check

            InspectedManifest = manifest;
            if (manifest != null)
            {
                var sizeMb = manifest.TotalSizeBytes / (1024 * 1024);
                StatusTitle = $"Backup verified: {manifest.TotalFilesCount} files ({sizeMb:N1} MB)";
                var detail = $"Created on {manifest.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} from machine '{manifest.MachineName}'";
                if (manifest.CanonicalFilesCount > 0)
                {
                    detail += $" + {manifest.CanonicalFilesCount} portable canonical sessions";
                }
                if (manifest.Warnings.Count > 0)
                {
                    detail += $"; note: backup was partial ({manifest.Warnings.Count} skipped item(s))";
                }
                StatusDetail = detail;
            }
            else
            {
                StatusTitle = "Invalid archive";
                StatusDetail = "Archive is not restorable: backup_manifest.json is missing/corrupt, uses an unsupported schema, or lists files absent from the archive.";
            }
        }
        catch (Exception ex)
        {
            if (!IsCurrent() || targetPath != RestoreZipPath) return; // Stale check
            StatusTitle = "Inspection error";
            StatusDetail = ex.Message;
            CasrLogger.Error("BACKUP_VM", $"Failed to inspect archive '{targetPath}'", ex);
        }
        finally
        {
            if (IsCurrent() && targetPath == RestoreZipPath)
            {
                IsInspecting = false;
            }
            // A stale task leaves IsInspecting alone: the current inspection owns it.
        }
    }

    public async Task StartBackupAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ProgressPercent = 0;
        StatusTitle = "Creating Agent State Backup...";
        StatusDetail = "Collecting agent databases, skills, and configuration...";
        OperationResult = string.Empty;

        _cts = new CancellationTokenSource();

        try
        {
            Directory.CreateDirectory(BackupDirectory);
            // Millisecond resolution plus a guid fallback: two backups started in the
            // same second (or same millisecond) must never overwrite each other.
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var targetZip = Path.Combine(BackupDirectory, $"casr_agents_backup_{timestamp}.zip");
            if (File.Exists(targetZip) || File.Exists(targetZip + ".tmp"))
            {
                targetZip = Path.Combine(BackupDirectory, $"casr_agents_backup_{timestamp}_{Guid.NewGuid():N}.zip");
            }
            // Build to a temp sibling and atomically move into place, so a cancelled
            // or crashed run (or a window close mid-backup) can never leave a partial
            // zip behind at the final name.
            var tempZip = targetZip + ".tmp";

            var scope = new BackupScope
            {
                IncludeAntigravity = IncludeAntigravity,
                IncludeCursor = IncludeCursor,
                IncludeGrok = IncludeGrok,
                IncludePi = IncludePi,
                IncludeHermes = IncludeHermes,
                IncludeOpenCode = IncludeOpenCode,
                IncludeOpenClaude = IncludeOpenClaude,
                IncludeCodex = IncludeCodex,
                IncludeCasrDatabase = IncludeCasrDatabase,
                IncludeCanonicalExport = IncludeCanonicalExport
            };

            var progress = new Progress<BackupProgress>(p =>
            {
                ProgressPercent = p.PercentComplete;
                StatusTitle = p.StatusMessage;
                StatusDetail = $"Processing {p.FilesProcessed} of {p.TotalFiles} items ({p.BytesProcessed / (1024 * 1024):N1} MB / {p.TotalBytes / (1024 * 1024):N1} MB)";
            });

            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { /* best effort */ }
            var manifest = await Task.Run(() => _backupService.CreateBackupAsync(tempZip, scope, progress, _cts.Token));

            File.Move(tempZip, targetZip, overwrite: false);

            ProgressPercent = 100;
            var sizeText = $"{manifest.TotalFilesCount} files ({manifest.TotalSizeBytes / (1024 * 1024):N1} MB)";
            if (manifest.CanonicalFilesCount > 0)
            {
                sizeText += $" + {manifest.CanonicalFilesCount} canonical sessions";
            }
            if (manifest.Warnings.Count == 0)
            {
                StatusTitle = "Backup Completed Successfully!";
                StatusDetail = $"Saved {sizeText} to {targetZip}";
                OperationResult = $"SUCCESS: Archive created at {targetZip}";
            }
            else
            {
                // Partial success: the archive is valid, but N items were skipped.
                // Surface the count in the title and the list in the detail.
                StatusTitle = $"Backup Completed with {manifest.Warnings.Count} Skipped Item(s)";
                StatusDetail = $"Saved {sizeText} to {targetZip}. Skipped {manifest.Warnings.Count}: {FormatList(manifest.Warnings, 8)}";
                OperationResult = $"PARTIAL: Archive created at {targetZip} with {manifest.Warnings.Count} skipped: {FormatList(manifest.Warnings, 20)}";
                CasrLogger.Warn("BACKUP_VM", $"Backup partial: {manifest.Warnings.Count} skipped. {string.Join(" | ", manifest.Warnings)}");
            }
        }
        catch (OperationCanceledException)
        {
            StatusTitle = "Backup Cancelled";
            StatusDetail = "Operation was cancelled by user.";
            OperationResult = "Backup cancelled.";
            TryDeleteTempBackups();
        }
        catch (Exception ex)
        {
            StatusTitle = "Backup Failed";
            StatusDetail = ex.Message;
            OperationResult = $"ERROR: {ex.Message}";
            CasrLogger.Error("BACKUP_VM", "Backup failed", ex);
            TryDeleteTempBackups();
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public async Task StartRestoreAsync()
    {
        if (IsBusy || InspectedManifest == null) return;
        IsBusy = true;
        ProgressPercent = 0;
        StatusTitle = "Restoring Agent State...";
        StatusDetail = "Extracting files to machine user profile...";
        OperationResult = string.Empty;

        _cts = new CancellationTokenSource();

        try
        {
            var options = new RestoreOptions
            {
                CreateBackupCopies = CreateRestoreBackupCopies
            };

            var progress = new Progress<RestoreProgress>(p =>
            {
                ProgressPercent = p.TotalFiles > 0 ? (double)p.FilesRestored / p.TotalFiles * 100.0 : 0;
                StatusTitle = p.StatusMessage;
                StatusDetail = $"Restored {p.FilesRestored} of {p.TotalFiles} files";
            });

            var result = await Task.Run(() => _backupService.RestoreBackupWithDetailsAsync(RestoreZipPath, options, progress, _cts.Token));

            ProgressPercent = 100;
            if (!result.HasFailures && result.PendingSwaps.Count == 0)
            {
                StatusTitle = "Restoration Completed Successfully!";
                var okDetail = $"Restored {result.RestoredCount} files to user profile and agent directories.";
                if (result.CanonicalRestoredCount > 0)
                {
                    okDetail += $" Plus {result.CanonicalRestoredCount} portable canonical sessions under _restored/canonical/.";
                }
                StatusDetail = okDetail;
                OperationResult = $"SUCCESS: {result.RestoredCount} files restored{(result.CanonicalRestoredCount > 0 ? $" + {result.CanonicalRestoredCount} canonical" : string.Empty)}. Restart or rescan SeshMesh to index restored sessions.";
            }
            else
            {
                // Partial success: report restored vs skipped + the skip list, and any
                // live-database sidecars that still need a restart to swap in.
                var parts = new List<string>();
                if (result.HasFailures) parts.Add($"{result.SkippedCount} skipped");
                if (result.PendingSwaps.Count > 0) parts.Add($"{result.PendingSwaps.Count} pending restart");
                StatusTitle = $"Restoration Completed with {string.Join(" + ", parts)}";
                var detail = $"Restored {result.RestoredCount} of {result.TotalRequested} files";
                if (result.CanonicalRestoredCount > 0) detail += $" (+{result.CanonicalRestoredCount} canonical)";
                detail += ".";
                if (result.HasFailures) detail += $" Skipped: {FormatList(result.Failures, 8)}";
                if (result.PendingSwaps.Count > 0) detail += $" Pending restart swap: {FormatList(result.PendingSwaps, 5)} Close SeshMesh and the owning agents, then replace each target with its .pending_restore sidecar.";
                StatusDetail = detail;
                OperationResult = $"PARTIAL: {detail}";
                CasrLogger.Warn("BACKUP_VM", $"Restore partial: {result.RestoredCount}/{result.TotalRequested} restored. Failures: {string.Join(" | ", result.Failures)}. Pending: {string.Join(" | ", result.PendingSwaps)}");
            }
        }
        catch (OperationCanceledException)
        {
            StatusTitle = "Restore Cancelled";
            StatusDetail = "Operation was cancelled by user.";
            OperationResult = "Restore cancelled.";
        }
        catch (Exception ex)
        {
            StatusTitle = "Restore Failed";
            StatusDetail = ex.Message;
            OperationResult = $"ERROR: {ex.Message}";
            CasrLogger.Error("BACKUP_VM", "Restore failed", ex);
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void CancelOperation()
    {
        try
        {
            _cts?.Cancel();
            StatusDetail = "Cancellation requested — stopping safely...";
        }
        catch (Exception ex)
        {
            CasrLogger.Warn("BACKUP_VM", $"Cancel failed: {ex.Message}");
        }
    }

    /// <summary>Caps a potentially long skip/failure list for UI text.</summary>
    private static string FormatList(IReadOnlyList<string> items, int maxShown)
    {
        var shown = items.Take(Math.Max(1, maxShown));
        var text = string.Join(" | ", shown);
        if (items.Count > maxShown) text += $" | …and {items.Count - maxShown} more (see log)";
        return text;
    }

    /// <summary>Best-effort removal of orphaned .tmp partial zips from cancelled/failed runs.</summary>
    private void TryDeleteTempBackups()    {
        try
        {
            if (!Directory.Exists(BackupDirectory)) return;
            foreach (var tmp in Directory.EnumerateFiles(BackupDirectory, "casr_agents_backup_*.zip.tmp"))
            {
                try { File.Delete(tmp); } catch { /* best effort */ }
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Warn("BACKUP_VM", $"Temp backup cleanup failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
        try
        {
            var cts = Interlocked.Exchange(ref _cts, null);
            try { cts?.Cancel(); } catch { /* already cancelled */ }
            cts?.Dispose();
            var inspectCts = Interlocked.Exchange(ref _inspectCts, null);
            try { inspectCts?.Cancel(); } catch { /* already cancelled */ }
            inspectCts?.Dispose();
        }
        catch (Exception ex)
        {
            CasrLogger.Warn("BACKUP_VM", $"Dispose failed: {ex.Message}");
        }
    }

    private async Task SaveStorageSettingsAsync()
    {
        if (string.IsNullOrWhiteSpace(CustomDbPath)) return;
        try
        {
            OperationResult = "Saving storage configuration...";
            var targetPath = CustomDbPath;
            await Task.Run(() =>
            {
                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            });
            UserSettings.Default.CustomDatabasePath = targetPath;
            UserSettings.Default.Save();
            OperationResult = "Index database path updated. Please restart SeshMesh to open the database at the new location.";
        }
        catch (Exception ex)
        {
            OperationResult = $"Error saving path: {ex.Message}";
        }
    }
}
