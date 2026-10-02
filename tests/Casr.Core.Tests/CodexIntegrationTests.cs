using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using Casr.Core.Backup;
using Casr.Core.Configuration;
using Casr.Core.Providers;
using Xunit;

namespace Casr.Core.Tests;

public sealed class CodexIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "casr_codex_integration_" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousCodexRoot = Environment.GetEnvironmentVariable("CODEX_HOME");

    public CodexIntegrationTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("CODEX_HOME", _root);
    }

    [Fact]
    public async Task BackupAndRestore_CodexRolloutsAndTitles_PreserveBytesAndExcludeCredentials()
    {
        var active = Path.Combine(_root, "sessions", "2026", "09", "29", "rollout.jsonl");
        var archived = Path.Combine(_root, "archived_sessions", "old.jsonl");
        var titleIndex = Path.Combine(_root, "session_index.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(active)!);
        Directory.CreateDirectory(Path.GetDirectoryName(archived)!);
        await File.WriteAllTextAsync(active, "active transcript\n");
        await File.WriteAllTextAsync(archived, "archived transcript\n");
        await File.WriteAllTextAsync(titleIndex, "title index\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "auth.json"), "private credential fixture");
        var expected = new[] { active, archived, titleIndex }.ToDictionary(p => p, File.ReadAllBytes);
        var scope = new BackupScope
        {
            IncludeAntigravity = false, IncludeCursor = false, IncludeGrok = false,
            IncludePi = false, IncludeHermes = false, IncludeOpenCode = false,
            IncludeOpenClaude = false, IncludeCodex = true, IncludeCasrDatabase = false,
            IncludeCanonicalExport = false
        };
        var backup = new BackupService(new ProviderRegistry(Array.Empty<IProvider>(), new UserSettings()));
        var archive = Path.Combine(_root, "codex.zip");
        var manifest = await backup.CreateBackupAsync(archive, scope);
        Assert.Equal(3, manifest.TotalFilesCount);
        Assert.All(manifest.Files, f => Assert.Equal("codex", f.Provider));
        using (var zip = ZipFile.OpenRead(archive))
        {
            Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("auth.json", StringComparison.Ordinal));
            Assert.Contains(zip.Entries, e => e.FullName == "raw/codex/archived_sessions/old.jsonl");
        }
        foreach (var path in expected.Keys) File.Delete(path);
        var restored = await backup.RestoreBackupWithDetailsAsync(archive, new RestoreOptions());
        Assert.Equal(3, restored.RestoredCount);
        Assert.Empty(restored.Failures);
        foreach (var (path, bytes) in expected) Assert.Equal(bytes, File.ReadAllBytes(path));

        scope.IncludeCodex = false;
        Assert.Empty(backup.CollectItems(scope));
    }

    [Fact]
    public void Settings_SavePublishesCompleteJsonAndCleansTemporaryFilesOnFailure()
    {
        var path = Path.Combine(_root, "settings.json");
        var settings = UserSettings.Load(path);
        settings.SetProviderEnabled("codex", false);
        settings.SaveSearch(new SavedSearch { Name = "Unicode", Query = "café 日本語", Mode = "Literal" });
        Assert.True(settings.Save());
        var reloaded = UserSettings.Load(path);
        Assert.False(reloaded.IsProviderEnabled("codex"));
        Assert.Equal("café 日本語", Assert.Single(reloaded.SavedSearches).Query);

        var previous = File.ReadAllBytes(path);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            settings.SelectedTheme = "Changed during blocked rename";
            Assert.False(settings.Save());
        }
        Assert.Equal(previous, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(_root, "settings.json.tmp-*"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CODEX_HOME", _previousCodexRoot);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
