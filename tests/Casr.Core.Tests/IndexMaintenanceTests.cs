using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Casr.Core.Models;
using Casr.Core.Providers;
using Casr.Core.Services;
using Casr.Core.Storage;
using Xunit;

namespace Casr.Core.Tests;

/// <summary>
/// The index used to grow forever: rows for providers deleted from the code, and rows whose
/// session files had been removed from disk, stayed listed but could never be opened. These
/// tests pin the maintenance pass that drops them, and the safety rule that an unreachable
/// session root (offline drive) must never cause deletions.
/// </summary>
public class IndexMaintenanceTests : IDisposable
{
    private readonly string _tempDir;

    public IndexMaintenanceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"casr_maint_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    /// <summary>Minimal provider stub: a slug plus session roots, nothing else.</summary>
    private sealed class StubProvider : IProvider
    {
        private readonly List<string> _roots;
        public StubProvider(string slug, params string[] roots)
        {
            Slug = slug;
            _roots = new List<string>(roots);
        }

        public string Name => Slug;
        public string Slug { get; }
        public string CliAlias => Slug;
        public DetectionResult Detect() => new() { Installed = true };
        public IReadOnlyList<string> SessionRoots() => _roots;

        /// <summary>Ids this stub still "owns" — anything else reports as no longer present.</summary>
        public HashSet<string> OwnedIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? OwnsSession(string sessionId) =>
            OwnedIds.Contains(sessionId) ? Path.Combine(Slug, sessionId) : null;
        public CanonicalSession ReadSession(string path) => new();
        public SessionSummary ReadSummary(string path) => new() { Provider = Slug, ProviderDisplayName = Slug };
        public WrittenSession WriteSession(CanonicalSession session, WriteOptions opts) => new();
        public string ResumeCommand(string sessionId, string? workspace = null) => $"stub {sessionId}";
        public IReadOnlyList<(string SessionId, string Path)>? ListSessions() => new List<(string, string)>();
    }

    private static void Seed(SessionDatabase db, string id, string provider, string sourcePath, int messages = 5)
    {
        db.UpsertSummary(new SessionSummary
        {
            SessionId = id,
            Provider = provider,
            ProviderDisplayName = provider,
            Title = $"session {id}",
            SourcePath = sourcePath,
            MessagesCount = messages,
            LastActiveAt = DateTime.Now
        });
    }

    private static HashSet<string> Ids(SessionDatabase db)
    {
        var set = new HashSet<string>();
        foreach (var (id, _, _) in db.GetAllSessionIdentities()) set.Add(id);
        return set;
    }

    [Fact]
    public void Prune_RemovesRowsForUnregisteredProviders()
    {
        var dbPath = Path.Combine(_tempDir, "index.db");
        using var db = new SessionDatabase(dbPath);

        var liveFile = Path.Combine(_tempDir, "live", "summary.json");
        Directory.CreateDirectory(Path.GetDirectoryName(liveFile)!);
        File.WriteAllText(liveFile, "{}");

        Seed(db, "keep-live", "grok", liveFile);
        Seed(db, "drop-dead-provider", "cline", liveFile);   // provider no longer exists in code

        var registry = new ProviderRegistry(new IProvider[] { new StubProvider("grok", _tempDir) });
        var discovery = new SessionDiscoveryService(registry, db);

        var removed = discovery.PruneStaleIndexEntries();

        Assert.Equal(1, removed);
        var remaining = Ids(db);
        Assert.Contains("keep-live", remaining);
        Assert.DoesNotContain("drop-dead-provider", remaining);
    }

    [Fact]
    public void Prune_RemovesRowsWhoseSessionFileIsGone_WhenRootIsReachable()
    {
        var dbPath = Path.Combine(_tempDir, "index.db");
        using var db = new SessionDatabase(dbPath);

        var root = Path.Combine(_tempDir, "store", "sessions");
        Directory.CreateDirectory(root);
        var existing = Path.Combine(root, "present", "summary.json");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllText(existing, "{}");
        var deleted = Path.Combine(root, "gone", "summary.json");   // never created

        Seed(db, "present", "grok", existing);
        Seed(db, "deleted-from-disk", "grok", deleted);

        var registry = new ProviderRegistry(new IProvider[] { new StubProvider("grok", root) });
        var discovery = new SessionDiscoveryService(registry, db);

        var removed = discovery.PruneStaleIndexEntries();

        Assert.Equal(1, removed);
        var remaining = Ids(db);
        Assert.Contains("present", remaining);
        Assert.DoesNotContain("deleted-from-disk", remaining);
    }

    [Fact]
    public void Prune_SkipsMissingFilesWhenTheSessionRootIsUnreachable()
    {
        var dbPath = Path.Combine(_tempDir, "index.db");
        using var db = new SessionDatabase(dbPath);

        // Provider points at a root that does not exist right now (unmounted drive / offline store)
        var offlineRoot = Path.Combine(_tempDir, "not-mounted", "sessions");
        var ghost = Path.Combine(offlineRoot, "ws", "summary.json");
        Seed(db, "on-offline-drive", "grok", ghost);

        var registry = new ProviderRegistry(new IProvider[] { new StubProvider("grok", offlineRoot) });
        var discovery = new SessionDiscoveryService(registry, db);

        var removed = discovery.PruneStaleIndexEntries();

        Assert.Equal(0, removed);
        Assert.Contains("on-offline-drive", Ids(db));   // index must survive an offline store
    }

    [Fact]
    public void Prune_KeepsSessionsOutsideTheProvidersRoots()
    {
        var dbPath = Path.Combine(_tempDir, "index.db");
        using var db = new SessionDatabase(dbPath);

        var root = Path.Combine(_tempDir, "store", "sessions");
        Directory.CreateDirectory(root);
        var elsewhere = Path.Combine(_tempDir, "somewhere-else", "summary.json");
        Seed(db, "outside-root", "grok", elsewhere);

        var registry = new ProviderRegistry(new IProvider[] { new StubProvider("grok", root) });
        var discovery = new SessionDiscoveryService(registry, db);

        Assert.Equal(0, discovery.PruneStaleIndexEntries());
        Assert.Contains("outside-root", Ids(db));
    }

    [Fact]
    public void DeleteSessionsByIds_RemovesOnlyTheNamedRows()
    {
        var dbPath = Path.Combine(_tempDir, "index.db");
        using var db = new SessionDatabase(dbPath);
        var f = Path.Combine(_tempDir, "x.json");
        File.WriteAllText(f, "{}");
        Seed(db, "a", "grok", f);
        Seed(db, "b", "grok", f);
        Seed(db, "c", "grok", f);

        var deleted = db.DeleteSessionsByIds(new[] { "a", "c", "a" });

        Assert.Equal(2, deleted);
        var remaining = Ids(db);
        Assert.Single(remaining);
        Assert.Contains("b", remaining);

        Assert.Equal(0, db.DeleteSessionsByIds(Array.Empty<string>()));
    }

    [Fact]
    public void Prune_RemovesStoreKeyedRowsTheProviderNoLongerOwns()
    {
        var dbPath = Path.Combine(_tempDir, "index.db");
        using var db = new SessionDatabase(dbPath);

        // A shared store file (Cursor state.vscdb / Hermes state.db shape) exists on disk, so
        // file existence cannot tell us whether an individual conversation is still inside it.
        var store = Path.Combine(_tempDir, "store", "state.db");
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        File.WriteAllText(store, "sqlite-ish");

        var stillThere = new StubProvider("stubstore");
        stillThere.OwnedIds.Add("own-yes");
        var provider = stillThere;

        Seed(db, "own-yes", "stubstore", $"{store}::own-yes");
        Seed(db, "own-no", "stubstore", $"{store}::own-no");

        var registry = new ProviderRegistry(new IProvider[] { provider });
        var discovery = new SessionDiscoveryService(registry, db);

        var removed = discovery.PruneStaleIndexEntries();

        Assert.Equal(1, removed);
        var remaining = Ids(db);
        Assert.Contains("own-yes", remaining);      // provider still owns it
        Assert.DoesNotContain("own-no", remaining); // deleted from the store -> unopenable row
    }

    [Fact]
    public void AntigravityCatalogOnlyRow_ReportsZeroMessages_SoItIsNotListed()
    {
        var geminiHome = Path.Combine(_tempDir, "mock_gemini");
        var cliDir = Path.Combine(geminiHome, "antigravity-cli");
        Directory.CreateDirectory(cliDir);
        var dbPath = Path.Combine(cliDir, "conversation_summaries.db");

        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE conversation_summaries (
                    conversation_id TEXT PRIMARY KEY, title TEXT, preview TEXT, step_count INTEGER,
                    last_modified_time TEXT, workspace_uris TEXT, parent_conversation_id TEXT, nesting_depth INTEGER
                );
                INSERT INTO conversation_summaries VALUES
                    ('catalog-only-1', 'Reply with exactly: casr-template-probe-deadbeef', 'p', 5,
                     '2026-09-14T12:00:00Z', NULL, NULL, 0);";
            cmd.ExecuteNonQuery();
        }

        Environment.SetEnvironmentVariable("GEMINI_HOME", geminiHome);
        AntigravityProvider.InvalidateCache();
        try
        {
            var provider = new AntigravityProvider();
            var summary = provider.ReadSummary($"{dbPath}::catalog-only-1");

            // The CLI index still names it (so the title survives), but there is no transcript
            // or conversation db: CASR must not claim 5 turns it cannot actually open.
            Assert.Equal(0, summary.MessagesCount);
            Assert.Null(provider.OwnsSession("catalog-only-1"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEMINI_HOME", null);
            AntigravityProvider.InvalidateCache();
        }
    }

    [Fact]
    public void GrokReadSummary_FlagsAgentPromptsAsSubagents()
    {
        Environment.SetEnvironmentVariable("GROK_HOME", _tempDir);
        try
        {
            var provider = new GrokProvider();
            var dir = Path.Combine(_tempDir, "sessions", "ws", "sess-agent-1");
            Directory.CreateDirectory(dir);
            var summaryFile = Path.Combine(dir, "summary.json");
            File.WriteAllText(summaryFile, JsonSerializer.Serialize(new
            {
                info = new { id = "sess-agent-1", cwd = "C:\\Work" },
                session_summary = "You are auditing the GROK import/connect path of the CASR",
                created_at = "2026-09-14T10:00:00Z",
                num_messages = 5
            }));

            var summary = provider.ReadSummary(summaryFile);

            // Grok never classified its sessions before, so audit prompts stayed in the main list
            Assert.True(summary.IsSubagent, "agent-prompt grok session must be flagged as a subagent");
            Assert.True(provider.ReadSession(summaryFile).IsSubagent);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GROK_HOME", null);
        }
    }

    [Fact]
    public void GrokReadSummary_LeavesRealConversationsUnflagged()
    {
        Environment.SetEnvironmentVariable("GROK_HOME", _tempDir);
        try
        {
            var provider = new GrokProvider();
            var dir = Path.Combine(_tempDir, "sessions", "ws", "sess-real-1");
            Directory.CreateDirectory(dir);
            var summaryFile = Path.Combine(dir, "summary.json");
            File.WriteAllText(summaryFile, JsonSerializer.Serialize(new
            {
                info = new { id = "sess-real-1", cwd = "C:\\Work" },
                generated_title = "Optimize Watchdog UI Responsiveness",
                created_at = "2026-09-14T10:00:00Z",
                num_messages = 40
            }));

            var summary = provider.ReadSummary(summaryFile);

            Assert.False(summary.IsSubagent);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GROK_HOME", null);
        }
    }
}