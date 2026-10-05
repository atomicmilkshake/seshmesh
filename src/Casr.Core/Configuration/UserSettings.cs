using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Casr.Core.Logging;

namespace Casr.Core.Configuration;

public class UserSettings
{
    private static readonly object _fileLock = new();
    private static UserSettings? _instance;

    public static UserSettings Default => Load();

    /// <summary>
    /// Every provider slug the build knows about. Fresh installs enable all of them;
    /// an existing settings.json that predates a provider keeps that provider
    /// <em>disabled</em> until the user explicitly enables it (explicit choice —
    /// Load logs the missing slugs as a notice rather than silently opting in).
    /// </summary>
    public static readonly IReadOnlyList<string> KnownProviderSlugs = new List<string>
    {
        "antigravity",
        "grok",
        "cursor",
        "openclaude",
        "pi",
        "hermes",
        "opencode",
        "codex"
    };

    public List<string> EnabledProviderSlugs { get; set; } = new(KnownProviderSlugs);

    public string PreferredTerminal { get; set; } = "WindowsTerminal";
    public string SelectedTheme { get; set; } = "VS Code Dark Modern";

    /// <summary>
    /// Launch every started/resumed/cross-resumed session elevated (Windows "Run as
    /// administrator"). Persisted so the choice survives restarts.
    /// </summary>
    public bool RunAsAdmin { get; set; } = false;

    /// <summary>
    /// When true, resume / resume-with / copy-command insert that harness's
    /// approval-bypass flag. Default false. Pi and Cursor have no flag, so the
    /// switch does not change their commands. An older settings.json with no
    /// key deserializes as false.
    /// </summary>
    public bool BypassApprovals { get; set; } = false;

    /// <summary>
    /// Conversion dialog preferences (casr-parity defaults: reasoning dropped for
    /// cross-agent handoffs, ~200k-token history budget, 4k tool-output cap, and
    /// read-back verification on). Additive block: older settings.json files
    /// deserialize the defaults. <see cref="ConversionPreviewEnabled"/> controls
    /// whether Resume-With opens the preview dialog before converting.
    /// </summary>
    public bool ConversionPreviewEnabled { get; set; } = true;
    public bool ConversionEnrich { get; set; } = false;
    public bool ConversionKeepReasoning { get; set; } = false;
    public bool ConversionVerify { get; set; } = true;
    public int ConversionMaxContextTokens { get; set; } = 200_000;
    public int ConversionMaxToolOutput { get; set; } = 4_000;

    /// <summary>
    /// Opt-in ONNX MiniLM neural embeddings for deep search. Off by default: the
    /// bundled hashing/trigram embedder is the offline zero-dependency default.
    /// Toggling in the app re-embeds the transcript index for the new model
    /// (resumable across scans). Additive block; older settings.json defaults to off.
    /// </summary>
    public bool NeuralEmbeddings { get; set; } = false;

    public bool IncludeSubagents { get; set; } = false;
    public string? BackupDirectory { get; set; }
    public string? CustomDatabasePath { get; set; }

    /// <summary>
    /// Named saved searches (search-UX). Additive block: older settings.json files
    /// simply deserialize these as empty lists (null-guarded in Normalize).
    /// </summary>
    public List<SavedSearch> SavedSearches { get; set; } = new();

    /// <summary>
    /// Auto-recorded MRU search history, newest first, capped at
    /// <see cref="MaxSearchHistoryEntries"/>. Additive; null-guarded in Normalize.
    /// </summary>
    public List<SearchHistoryEntry> SearchHistory { get; set; } = new();

    /// <summary>MRU history cap: only the 20 most recent searches are kept.</summary>
    public const int MaxSearchHistoryEntries = 20;

    /// <summary>
    /// Records a search in MRU history (newest first, case-insensitive de-dupe on
    /// query+mode, capped at <see cref="MaxSearchHistoryEntries"/>). Empty queries
    /// are ignored. Callers must call <see cref="Save()"/> to persist.
    /// </summary>
    public void RecordSearchHistory(string query, string mode, bool caseSensitive)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        SearchHistory ??= new List<SearchHistoryEntry>();
        var q = query.Trim();
        var m = string.IsNullOrWhiteSpace(mode) ? "Hybrid" : mode.Trim();
        SearchHistory.RemoveAll(h =>
            string.Equals(h.Query, q, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(h.Mode, m, StringComparison.OrdinalIgnoreCase) &&
            h.CaseSensitive == caseSensitive);
        SearchHistory.Insert(0, new SearchHistoryEntry
        {
            Query = q,
            Mode = m,
            CaseSensitive = caseSensitive,
        });
        if (SearchHistory.Count > MaxSearchHistoryEntries)
            SearchHistory.RemoveRange(MaxSearchHistoryEntries, SearchHistory.Count - MaxSearchHistoryEntries);
    }

    /// <summary>
    /// Upserts a named saved search (name match is case-insensitive). Callers must
    /// call <see cref="Save()"/> to persist.
    /// </summary>
    public void SaveSearch(SavedSearch entry)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.Name)) return;
        SavedSearches ??= new List<SavedSearch>();
        SavedSearches.RemoveAll(s => string.Equals(s.Name, entry.Name.Trim(), StringComparison.OrdinalIgnoreCase));
        entry.Name = entry.Name.Trim();
        SavedSearches.Add(entry);
        SavedSearches.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Deletes a saved search by name (case-insensitive). Returns true when removed.</summary>
    public bool DeleteSavedSearch(string name)
    {
        if (SavedSearches == null || string.IsNullOrWhiteSpace(name)) return false;
        return SavedSearches.RemoveAll(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) > 0;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? FilePath { get; set; }

    /// <summary>Canonical production settings location (%LOCALAPPDATA%\Casr\settings.json).</summary>
    public static string SettingsFilePath => CasrPaths.SettingsFilePath;

    /// <summary>
    /// One-line startup summary for the app log: version + settings path + enabled
    /// slugs + resolved index-db path. Log this once at startup (App.OnStartup).
    /// </summary>
    public string ToStartupLogLine()
    {
        var version = typeof(UserSettings).Assembly.GetName().Version?.ToString() ?? "unknown";
        var dbPath = !string.IsNullOrWhiteSpace(CustomDatabasePath) ? CustomDatabasePath : CasrPaths.DefaultDbPath;
        return $"version={version} settings={FilePath ?? SettingsFilePath} " +
               $"providers=[{string.Join(",", EnabledProviderSlugs ?? new List<string>())}] db={dbPath}";
    }

    private static string GetSettingsFilePath() => CasrPaths.SettingsFilePath;

    public static UserSettings Load(string? customPath = null)
    {
        // The fast-path null check lives INSIDE the lock: two threads racing Load()
        // must not both observe _instance == null and both write defaults to disk.
        lock (_fileLock)
        {
            if (customPath == null && _instance != null) return _instance;

            var path = customPath ?? GetSettingsFilePath();
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var settings = JsonSerializer.Deserialize<UserSettings>(json);
                    if (settings != null)
                    {
                        settings.FilePath = path;
                        Normalize(settings);
                        if (customPath == null) _instance = settings;
                        CasrLogger.Info("SETTINGS", $"Loaded user settings from {path}. Enabled providers: {string.Join(", ", settings.EnabledProviderSlugs)}");
                        LogNewProviderNotice(settings);
                        return settings;
                    }
                }
                catch (Exception ex)
                {
                    // A corrupt settings.json must never be silently overwritten: quarantine
                    // the broken file next to the original, then fall through to defaults.
                    try
                    {
                        var backup = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                        File.Move(path, backup);
                        CasrLogger.Warn("SETTINGS", $"Corrupt settings.json renamed to {backup} ({ex.Message}); writing defaults to {path}.");
                    }
                    catch (Exception moveEx)
                    {
                        CasrLogger.Warn("SETTINGS", $"Corrupt settings.json at {path} could not be quarantined ({moveEx.Message}); defaults will overwrite it: {ex.Message}");
                    }
                }
            }

            var newSettings = new UserSettings { FilePath = path };
            newSettings.Save(customPath);
            if (customPath == null) _instance = newSettings;
            else CasrLogger.Info("SETTINGS", $"Wrote default settings to {path}.");
            return newSettings;
        }
    }

    /// <summary>
    /// Guards every deserialized instance: the list itself may be null (hand-edited
    /// JSON), entries may carry any casing, and duplicates accumulate across edits.
    /// </summary>
    private static void Normalize(UserSettings settings)
    {
        settings.EnabledProviderSlugs ??= new List<string>();
        settings.EnabledProviderSlugs = settings.EnabledProviderSlugs
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToLowerInvariant())
            .Where(s => s.Length > 0)
            .Distinct()
            .ToList();
        // Additive search-UX blocks: hand-edited or older files may carry nulls.
        settings.SavedSearches ??= new List<SavedSearch>();
        settings.SavedSearches = settings.SavedSearches
            .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Name))
            .GroupBy(s => s.Name!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var s in settings.SavedSearches) s.Normalize();
        settings.SearchHistory ??= new List<SearchHistoryEntry>();
        settings.SearchHistory = settings.SearchHistory
            .Where(h => h != null && !string.IsNullOrWhiteSpace(h.Query))
            .Take(MaxSearchHistoryEntries)
            .ToList();
        foreach (var h in settings.SearchHistory) h.Normalize();

        // Conversion preferences: clamp hand-edited or corrupted numeric values.
        if (settings.ConversionMaxContextTokens < 0) settings.ConversionMaxContextTokens = 0;
        if (settings.ConversionMaxToolOutput < 0) settings.ConversionMaxToolOutput = 0;
        if (settings.ConversionMaxContextTokens > 2_000_000) settings.ConversionMaxContextTokens = 2_000_000;
        if (settings.ConversionMaxToolOutput > 1_000_000) settings.ConversionMaxToolOutput = 1_000_000;
    }

    /// <summary>
    /// Explicit-choice notice: slugs this build knows about but the loaded file does
    /// not mention stay disabled until the user opts in via the Providers dropdown.
    /// </summary>
    private static void LogNewProviderNotice(UserSettings settings)
    {
        var missing = KnownProviderSlugs
            .Where(k => !settings.EnabledProviderSlugs.Contains(k, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (missing.Count > 0)
        {
            CasrLogger.Info("SETTINGS", $"Providers disabled until explicitly enabled: {string.Join(", ", missing)}. " +
                "Enable them in the Providers dropdown; defaults apply to fresh installs only.");
        }
    }

    /// <summary>
    /// Persists this instance. Returns false (and writes nothing) when the instance
    /// was never bound to a file and no path was supplied — an unbound
    /// `new UserSettings().Save()` used to silently overwrite the user's real
    /// configuration with code defaults (2026-09-22: hid every OpenCode session).
    /// </summary>
    public bool Save(string? customPath = null)
    {
        lock (_fileLock)
        {
            var path = customPath ?? FilePath;
            if (path == null)
            {
                // Production writes always come from UserSettings.Default, which is
                // bound to a file by Load(). Anything else reaching here is a bug.
                CasrLogger.Warn("SETTINGS", "Save() ignored: this settings instance has no bound file and no path was supplied.");
                return false;
            }

            string? temporaryPath = null;
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                // Publish a complete file in one rename so interrupted writes cannot
                // truncate the user's provider choices and saved searches.
                temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
                File.WriteAllText(temporaryPath, json);
                File.Move(temporaryPath, path, overwrite: true);
                CasrLogger.Info("SETTINGS", $"Saved settings to {path}");
                return true;
            }
            catch (Exception ex)
            {
                CasrLogger.Error("SETTINGS", "Failed to save settings.json", ex);
                return false;
            }
            finally
            {
                if (temporaryPath != null)
                {
                    try { File.Delete(temporaryPath); } catch { }
                }
            }
        }
    }

    public bool IsProviderEnabled(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug)) return false;
        var list = EnabledProviderSlugs;
        if (list == null) return false;
        return list.Contains(slug, StringComparer.OrdinalIgnoreCase);
    }

    public void SetProviderEnabled(string slug, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(slug)) return;
        EnabledProviderSlugs ??= new List<string>();
        var normalized = slug.ToLowerInvariant();
        if (enabled && !EnabledProviderSlugs.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            EnabledProviderSlugs.Add(normalized);
            Save();
        }
        else if (!enabled && EnabledProviderSlugs.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            EnabledProviderSlugs.RemoveAll(s => s.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            Save();
        }
    }
}

/// <summary>
/// A named, re-runnable deep-search configuration (search-UX). Persisted inside
/// settings.json under "SavedSearches"; unknown/blank fields are tolerated.
/// </summary>
public class SavedSearch
{
    public string Name { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public string Mode { get; set; } = "Hybrid";
    public bool CaseSensitive { get; set; }
    public string ProviderFilter { get; set; } = "All Providers";
    public string WorkspaceFilter { get; set; } = "All Workspaces";
    public string DateFilter { get; set; } = "All Time";

    internal void Normalize()
    {
        Name = Name?.Trim() ?? string.Empty;
        Query = Query?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(Mode)) Mode = "Hybrid";
        if (string.IsNullOrWhiteSpace(ProviderFilter)) ProviderFilter = "All Providers";
        if (string.IsNullOrWhiteSpace(WorkspaceFilter)) WorkspaceFilter = "All Workspaces";
        if (string.IsNullOrWhiteSpace(DateFilter)) DateFilter = "All Time";
    }
}

/// <summary>
/// One auto-recorded MRU history row (search-UX). Newest first in
/// <see cref="UserSettings.SearchHistory"/>.
/// </summary>
public class SearchHistoryEntry
{
    public string Query { get; set; } = string.Empty;
    public string Mode { get; set; } = "Hybrid";
    public bool CaseSensitive { get; set; }

    internal void Normalize()
    {
        Query = Query?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(Mode)) Mode = "Hybrid";
    }
}
