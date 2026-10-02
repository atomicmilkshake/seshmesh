using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Casr.Core.Configuration;
using Xunit;

namespace Casr.Core.Tests;

/// <summary>
/// Saved-search + MRU history persistence tests. All I/O goes through a temp
/// customPath (never prod %LOCALAPPDATA%); every file is re-read from disk.
/// NOTE on scope: a VM-level ClearFilters-keeps-engine test is deliberately NOT
/// included — MainViewModel's constructor is not headless-safe (DispatcherTimer,
/// SessionDatabase on the prod path, fire-and-forget live scan), so the engine
/// preservation is verified by Release-build compile + code review instead.
/// </summary>
public class SavedSearchesPersistenceTests
{
    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "casr_savedsearch_" + Guid.NewGuid().ToString("N"), "settings.json");

    [Fact]
    public void SavedSearches_RoundTripThroughTempFile()
    {
        var path = TempSettingsPath();
        try
        {
            // Ingress: fresh temp settings start empty.
            var settings = UserSettings.Load(path);
            Assert.NotNull(settings.SavedSearches);
            Assert.Empty(settings.SavedSearches);

            settings.SaveSearch(new SavedSearch
            {
                Name = "Auth bugs",
                Query = "authentication",
                Mode = "Hybrid",
                CaseSensitive = true,
                ProviderFilter = "OpenCode",
                WorkspaceFilter = @"C:\Projects\MyRepo",
                DateFilter = "Past 7 Days",
            });
            settings.SaveSearch(new SavedSearch
            {
                Name = "Second",
                Query = "token",
                Mode = "Keyword",
            });
            Assert.True(settings.Save(path), "Save to temp path must succeed");

            // Egress: independent re-read from disk.
            Assert.True(File.Exists(path), "settings file missing from temp destination");
            var rawBytes = File.ReadAllBytes(path);
            Assert.True(rawBytes.Length > 0, "settings file is 0 bytes");
            using var doc = JsonDocument.Parse(rawBytes);
            Assert.True(doc.RootElement.TryGetProperty("SavedSearches", out _), "SavedSearches block missing in JSON");

            var reloaded = UserSettings.Load(path);
            Assert.Equal(2, reloaded.SavedSearches.Count);
            var auth = reloaded.SavedSearches.First(s => s.Name == "Auth bugs");
            Assert.Equal("authentication", auth.Query);
            Assert.Equal("Hybrid", auth.Mode);
            Assert.True(auth.CaseSensitive);
            Assert.Equal("OpenCode", auth.ProviderFilter);
            Assert.Equal(@"C:\Projects\MyRepo", auth.WorkspaceFilter);
            Assert.Equal("Past 7 Days", auth.DateFilter);

            // Upsert is case-insensitive on name; delete removes.
            reloaded.SaveSearch(new SavedSearch { Name = "auth BUGS", Query = "auth2", Mode = "Exact" });
            Assert.Equal(2, reloaded.SavedSearches.Count);
            Assert.Equal("auth2", reloaded.SavedSearches.First(s => s.Name.Equals("Auth bugs", StringComparison.OrdinalIgnoreCase)).Query);
            Assert.True(reloaded.DeleteSavedSearch("SECOND"));
            Assert.Single(reloaded.SavedSearches);
            Assert.False(reloaded.DeleteSavedSearch("nope"));
        }
        finally
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void SearchHistory_MruCappedAtTwentyWithDedupe()
    {
        var path = TempSettingsPath();
        try
        {
            var settings = UserSettings.Load(path);
            Assert.NotNull(settings.SearchHistory);

            for (var i = 0; i < 25; i++)
                settings.RecordSearchHistory($"query-{i:D2}", i % 2 == 0 ? "Hybrid" : "Keyword", false);
            Assert.True(settings.Save(path), "Save to temp path must succeed");

            var reloaded = UserSettings.Load(path);
            Assert.Equal(UserSettings.MaxSearchHistoryEntries, reloaded.SearchHistory.Count);
            Assert.Equal(20, reloaded.SearchHistory.Count);
            // Newest first: last recorded wins the head.
            Assert.Equal("query-24", reloaded.SearchHistory[0].Query);
            Assert.Equal("query-05", reloaded.SearchHistory[^1].Query);

            // Re-recording an old query moves it to the front without growing the list.
            reloaded.RecordSearchHistory("query-05", "Keyword", false);
            Assert.Equal(20, reloaded.SearchHistory.Count);
            Assert.Equal("query-05", reloaded.SearchHistory[0].Query);
            Assert.Single(reloaded.SearchHistory, h => h.Query == "query-05");

            // Empty queries are ignored.
            reloaded.RecordSearchHistory("   ", "Hybrid", false);
            Assert.Equal(20, reloaded.SearchHistory.Count);
        }
        finally
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Normalize_NullSearchBlocksBecomeEmptyLists()
    {
        var path = TempSettingsPath();
        try
        {
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, "{\"EnabledProviderSlugs\":[],\"SavedSearches\":null,\"SearchHistory\":null}");

            var settings = UserSettings.Load(path);
            Assert.NotNull(settings.SavedSearches);
            Assert.NotNull(settings.SearchHistory);
            Assert.Empty(settings.SavedSearches);
            Assert.Empty(settings.SearchHistory);

            // Blank-name rows are dropped; blank modes default.
            File.WriteAllText(path,
                "{\"EnabledProviderSlugs\":[],\"SavedSearches\":[{\"Name\":\"  \",\"Query\":\"x\"},{\"Name\":\"ok\",\"Query\":\"y\",\"Mode\":\"\"}],\"SearchHistory\":[{\"Query\":\"  \"},{\"Query\":\"z\",\"Mode\":null}]}");
            var cleaned = UserSettings.Load(path);
            Assert.Single(cleaned.SavedSearches);
            Assert.Equal("ok", cleaned.SavedSearches[0].Name);
            Assert.Equal("Hybrid", cleaned.SavedSearches[0].Mode);
            Assert.Single(cleaned.SearchHistory);
            Assert.Equal("z", cleaned.SearchHistory[0].Query);
            Assert.Equal("Hybrid", cleaned.SearchHistory[0].Mode);
        }
        finally
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
