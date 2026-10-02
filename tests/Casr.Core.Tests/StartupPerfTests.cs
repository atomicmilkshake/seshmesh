using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Casr.Core.Configuration;
using Casr.Core.Logging;
using Casr.Core.Models;
using Casr.Core.Providers;
using Casr.Core.Services;
using Casr.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Casr.Core.Tests;

/// <summary>
/// Startup-perf + log-hygiene workstream. All hermetic, temp-only I/O: every test builds
/// its own store dirs, index DB, settings file and scan-gate file under one temp root
/// (deleted in Dispose) and never touches ProviderRegistry.Default or %LOCALAPPDATA%.
/// </summary>
public class StartupPerfTests : IDisposable
{
    private readonly string _tempDir;

    public StartupPerfTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "casr_startup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    /// <summary>File-backed stub: ListSessions enumerates *.jsonl in StoreDir; every session
    /// file really exists so the prune pass keeps the rows between scans.</summary>
    private sealed class GateProvider : IProvider
    {
        private int _listCalls;
        private int _readCalls;
        public int ListCalls => _listCalls;
        public int ReadCalls => _readCalls;

        public string SlugValue { get; }
        public string StoreDir { get; }
        public Dictionary<string, SessionSummary> Overrides { get; } = new(StringComparer.OrdinalIgnoreCase);

        public GateProvider(string slug, string storeDir)
        {
            SlugValue = slug;
            StoreDir = storeDir;
            Directory.CreateDirectory(storeDir);
        }

        public string Name => SlugValue;
        public string Slug => SlugValue;
        public string CliAlias => SlugValue;
        public DetectionResult Detect() => new() { Installed = true };
        public IReadOnlyList<string> SessionRoots() => new[] { StoreDir };
        public string? OwnsSession(string sessionId) => null;

        public void WriteFile(string id, string content)
            => File.WriteAllText(Path.Combine(StoreDir, id + ".jsonl"), content);

        public IReadOnlyList<(string SessionId, string Path)>? ListSessions()
        {
            Interlocked.Increment(ref _listCalls);
            return Directory.GetFiles(StoreDir, "*.jsonl")
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => (Path.GetFileNameWithoutExtension(f), f))
                .ToList();
        }

        public SessionSummary ReadSummary(string path)
        {
            Interlocked.Increment(ref _readCalls);
            var id = Path.GetFileNameWithoutExtension(path);
            if (Overrides.TryGetValue(id, out var o))
            {
                return new SessionSummary
                {
                    SessionId = o.SessionId,
                    Provider = SlugValue,
                    ProviderDisplayName = SlugValue,
                    Title = o.Title,
                    Workspace = o.Workspace,
                    StartedAt = o.StartedAt,
                    LastActiveAt = o.LastActiveAt,
                    MessagesCount = o.MessagesCount,
                    FileSizeBytes = new FileInfo(path).Length,
                    SourcePath = path,
                };
            }
            var stamp = File.GetLastWriteTime(path);
            return new SessionSummary
            {
                SessionId = id,
                Provider = SlugValue,
                ProviderDisplayName = SlugValue,
                Title = "t-" + id,
                Workspace = StoreDir,
                StartedAt = stamp,
                LastActiveAt = stamp,
                MessagesCount = 1,
                FileSizeBytes = new FileInfo(path).Length,
                SourcePath = path,
            };
        }

        public CanonicalSession ReadSession(string path) => new()
        {
            SessionId = Path.GetFileNameWithoutExtension(path),
            ProviderSlug = SlugValue,
            SourcePath = path,
            Messages = new() { new CanonicalMessage { Index = 0, Role = MessageRole.User, Content = "hello" } },
        };

        public WrittenSession WriteSession(CanonicalSession session, WriteOptions opts) => throw new NotSupportedException();
        public string ResumeCommand(string sessionId, string? workspace = null) => $"stub {sessionId}";
    }

    private sealed class CaptureListener : TraceListener
    {
        public readonly List<string> Lines = new();
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { lock (Lines) Lines.Add(message ?? string.Empty); }
    }

    private SessionDiscoveryService BuildService(string name, ProviderRegistry registry, out SessionDatabase db, out string gatePath)
    {
        db = new SessionDatabase(Path.Combine(_tempDir, name + ".db"));
        gatePath = Path.Combine(_tempDir, name + "-gate.json");
        return new SessionDiscoveryService(registry, db, gatePath);
    }

    private static ProviderRegistry BuildRegistry(string settingsPath, params IProvider[] providers)
    {
        var settings = UserSettings.Load(settingsPath);
        settings.EnabledProviderSlugs = providers.Select(p => p.Slug.ToLowerInvariant()).ToList();
        Assert.True(settings.Save(), "test settings must persist to the temp path");
        return new ProviderRegistry(providers, settings);
    }

    private sealed class BeatSink : IProgress<ScanProgress>
    {
        public readonly List<ScanProgress> Beats = new();
        public void Report(ScanProgress p) { lock (Beats) Beats.Add(p); }
    }

    [Fact]
    public void Gate_UnchangedStore_SkippedOnSecondScan()
    {
        // P1 ingress: a store with 2 real session files; gate file absent (first run).
        var store = Path.Combine(_tempDir, "storeA");
        var prov = new GateProvider("gatea", store);
        prov.WriteFile("s0", """{"a":1}""");
        prov.WriteFile("s1", """{"b":2}""");
        Assert.Equal(2, Directory.GetFiles(store, "*.jsonl").Length);
        var registry = BuildRegistry(Path.Combine(_tempDir, "s1-settings.json"), prov);
        var svc = BuildService("s1", registry, out var db, out var gatePath);
        Assert.False(File.Exists(gatePath), "gate must not exist before the first scan");

        // P2 execution: first scan is full.
        var first = svc.DiscoverAllSessionsSync();
        Assert.Equal(2, first.Count);
        Assert.Equal(1, prov.ListCalls);
        Assert.True(svc.LastScanWasForcedFull);

        // P4 independent egress: the gate file exists on disk with this provider's print,
        // and the index DB holds both rows (queried back, not via the service return).
        Assert.True(File.Exists(gatePath));
        using (var gateDoc = JsonDocument.Parse(File.ReadAllText(gatePath)))
        {
            Assert.True(gateDoc.RootElement.GetProperty("Providers").TryGetProperty("gatea", out var print));
            Assert.False(string.IsNullOrWhiteSpace(print.GetString()));
        }
        Assert.Equal(2, db.GetRecentSessions(100).Count);

        // P2 second execution under log capture: nothing touched.
        var beats = new BeatSink();
        var listener = new CaptureListener();
        Trace.Listeners.Add(listener);
        List<SessionSummary> second;
        try { second = svc.DiscoverAllSessionsSync(beats); }
        finally { Trace.Listeners.Remove(listener); }

        // P5 verification: skipped, not re-listed; the skip beat carries no denominator.
        Assert.Equal(1, prov.ListCalls);
        Assert.Equal(2, second.Count);
        Assert.Equal(new[] { "gatea" }, svc.LastSkippedProviders);
        Assert.False(svc.LastScanWasForcedFull);
        var skips = beats.Beats.Where(b => b.Phase == "skipped").ToList();
        Assert.Single(skips);
        Assert.True(skips[0].SkippedUnchanged);
        Assert.Equal(0, skips[0].ProviderTotal);
        Assert.Contains(listener.Lines, l => l.Contains("skipped unchanged"));
        db.Dispose();
    }

    [Fact]
    public void Gate_TouchedStore_Rescanned_MtimeAndSizeLegs()
    {
        // P1 ingress: one scan to establish the gate, then a provably-skipped baseline.
        var store = Path.Combine(_tempDir, "storeB");
        var prov = new GateProvider("gateb", store);
        prov.WriteFile("s0", """{"a":1}""");
        var registry = BuildRegistry(Path.Combine(_tempDir, "s2-settings.json"), prov);
        var svc = BuildService("s2", registry, out var db, out _);
        Assert.Single(svc.DiscoverAllSessionsSync());
        Assert.Single(svc.DiscoverAllSessionsSync().Select(s => s.SessionId));
        Assert.Equal(1, prov.ListCalls);
        Assert.Equal(new[] { "gateb" }, svc.LastSkippedProviders);

        // P2 mtime-only touch (same byte size): the mtime leg must force a rescan.
        var target = Path.Combine(store, "s0.jsonl");
        var beforeLen = new FileInfo(target).Length;
        File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(target).AddHours(2));
        Assert.Equal(beforeLen, new FileInfo(target).Length);

        var afterTouch = svc.DiscoverAllSessionsSync();
        Assert.Equal(2, prov.ListCalls);
        Assert.Empty(svc.LastSkippedProviders);
        Assert.Single(afterTouch);

        // P2 size touch: appending bytes changes size+mtime and rescans again.
        File.AppendAllText(target, """{"more":true}""");
        Assert.NotEqual(beforeLen, new FileInfo(target).Length);
        var afterAppend = svc.DiscoverAllSessionsSync();
        Assert.Equal(3, prov.ListCalls);
        Assert.Empty(svc.LastSkippedProviders);
        Assert.Single(afterAppend);
        db.Dispose();
    }

    [Fact]
    public void Gate_ProviderToggle_ForcesFullRescan()
    {
        // P1 ingress: two providers, both enabled, both scanned once then both skipped.
        var provA = new GateProvider("gatea", Path.Combine(_tempDir, "storeTA"));
        var provB = new GateProvider("gateb", Path.Combine(_tempDir, "storeTB"));
        provA.WriteFile("a0", "{}");
        provB.WriteFile("b0", "{}");
        var settingsPath = Path.Combine(_tempDir, "s3-settings.json");
        var registry = BuildRegistry(settingsPath, provA, provB);
        var svc = BuildService("s3", registry, out var db, out var gatePath);
        Assert.Equal(2, svc.DiscoverAllSessionsSync().Count);
        Assert.Equal(1, provA.ListCalls);
        Assert.Equal(1, provB.ListCalls);
        Assert.Equal(2, svc.DiscoverAllSessionsSync().Count);
        Assert.Equal(1, provA.ListCalls);

        // P2 toggle off B: the enabled set changed, so A is rescanned despite being unchanged.
        registry.Settings.SetProviderEnabled("gateb", false);
        var afterToggle = svc.DiscoverAllSessionsSync();
        Assert.True(svc.LastScanWasForcedFull);
        Assert.Equal(2, provA.ListCalls);
        Assert.Equal(1, provB.ListCalls);
        Assert.Single(afterToggle);

        // P4 gate file on disk now records the reduced enabled set.
        using (var gateDoc = JsonDocument.Parse(File.ReadAllText(gatePath)))
        {
            var slugs = gateDoc.RootElement.GetProperty("EnabledSlugs").EnumerateArray()
                .Select(e => e.GetString()).ToList();
            Assert.Equal(new[] { "gatea" }, slugs);
        }

        // P2 toggle back on: forced again, both providers scanned.
        registry.Settings.SetProviderEnabled("gateb", true);
        Assert.Equal(2, svc.DiscoverAllSessionsSync().Count);
        Assert.True(svc.LastScanWasForcedFull);
        Assert.Equal(3, provA.ListCalls);
        Assert.Equal(2, provB.ListCalls);
        db.Dispose();
    }

    [Fact]
    public void CachedFirst_ReturnsRecencyDescOrder()
    {
        // P1 ingress: three summaries inserted out of recency order.
        using var db = new SessionDatabase(Path.Combine(_tempDir, "s4.db"));
        var base_time = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Local);
        SessionSummary Row(string id, DateTime active) => new()
        {
            SessionId = id,
            Provider = "p",
            ProviderDisplayName = "P",
            Title = id,
            Workspace = "/tmp",
            StartedAt = active.AddHours(-1),
            LastActiveAt = active,
            MessagesCount = 1,
            SourcePath = "/tmp/" + id,
        };
        db.UpsertSummary(Row("oldest", base_time.AddHours(-3)));
        db.UpsertSummary(Row("newest", base_time));
        db.UpsertSummary(Row("mid", base_time.AddHours(-2)));

        // P2/P4 cached read + independent raw-SQL order check (not via GetRecentSessions).
        var cached = db.GetRecentSessions(100);
        Assert.Equal(3, cached.Count);
        Assert.Equal(new[] { "newest", "mid", "oldest" }, cached.Select(s => s.SessionId));
        var rawOrder = new List<string>();
        using (var conn = new SqliteConnection($"Data Source={db.DbPath};Mode=ReadOnly"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT session_id FROM sessions ORDER BY COALESCE(last_active_at, started_at, 0) DESC;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) rawOrder.Add(reader.GetString(0));
        }
        Assert.Equal(new[] { "newest", "mid", "oldest" }, rawOrder);

        // P2/P5 discovery also returns recency-desc even when ListSessions order differs.
        var store = Path.Combine(_tempDir, "storeC");
        var prov = new GateProvider("gatec", store);
        prov.WriteFile("s0", "{}");
        prov.WriteFile("s1", "{}");
        prov.WriteFile("s2", "{}");
        prov.Overrides["s0"] = Row("s0", base_time.AddHours(-2));
        prov.Overrides["s1"] = Row("s1", base_time);
        prov.Overrides["s2"] = Row("s2", base_time.AddHours(-1));
        var registry = BuildRegistry(Path.Combine(_tempDir, "s4-settings.json"), prov);
        var svc = BuildService("s4b", registry, out var db2, out _);
        var found = svc.DiscoverAllSessionsSync();
        Assert.Equal(new[] { "s1", "s2", "s0" }, found.Select(s => s.SessionId));
        db2.Dispose();
    }

    [Fact]
    public void StartupLine_ContainsRequiredFields()
    {
        // P1/P2 pure builder: version, DB path, user_version, all four counts, slugs.
        var line = StartupDiagnostics.BuildStartupLine(
            "9.9.9-test", @"C:\idx\casr_index.db", 3, 1343, 70000, 1343, 78263,
            new[] { "pi", "opencode" });
        Assert.Contains("9.9.9-test", line);
        Assert.Contains(@"C:\idx\casr_index.db", line);
        Assert.Contains("user_version=3", line);
        Assert.Contains("sessions=1343", line);
        Assert.Contains("messages=70000", line);
        Assert.Contains("session_emb=1343", line);
        Assert.Contains("message_emb=78263", line);
        Assert.Contains("providers=[pi,opencode]", line);

        // P5 first-run shape: unknown user_version renders n/a, never a bare -1.
        var fresh = StartupDiagnostics.BuildStartupLine("1.0", "X", -1, 0, 0, 0, 0, Array.Empty<string>());
        Assert.Contains("user_version=n/a", fresh);
        Assert.DoesNotContain("-1", fresh);
    }

    [Fact]
    public void ReadDbHealth_ReflectsLiveCounts()
    {
        // P1 ingress: missing DB probes as unknown/zeros without creating the file.
        var missing = Path.Combine(_tempDir, "nope.db");
        var absent = StartupDiagnostics.ReadDbHealth(missing);
        Assert.Equal(-1, absent.UserVersion);
        Assert.Equal(0, absent.Sessions);
        Assert.False(File.Exists(missing), "health probe must never create the DB file");

        // P2 summary-only write: sessions counted, message/vector stores empty.
        using var db = new SessionDatabase(Path.Combine(_tempDir, "s5.db"));
        db.UpsertSummary(new SessionSummary
        {
            SessionId = "h1",
            Provider = "p",
            ProviderDisplayName = "P",
            Title = "t",
            Workspace = "/tmp",
            StartedAt = DateTime.Now.AddHours(-1),
            LastActiveAt = DateTime.Now,
            MessagesCount = 1,
            SourcePath = "/tmp/h1",
        });
        var afterSummary = StartupDiagnostics.ReadDbHealth(db.DbPath);
        Assert.True(afterSummary.UserVersion >= 0);
        Assert.Equal(1, afterSummary.Sessions);
        Assert.Equal(0, afterSummary.Messages);

        // P2 conversation write: bodies + session + per-message vectors all counted.
        db.UpsertConversation(
            new SessionSummary
            {
                SessionId = "h1",
                Provider = "p",
                ProviderDisplayName = "P",
                Title = "t",
                Workspace = "/tmp",
                MessagesCount = 2,
                FileSizeBytes = 64,
                SourcePath = "/tmp/h1",
            },
            new CanonicalSession
            {
                SessionId = "h1",
                ProviderSlug = "p",
                Messages = new()
                {
                    new CanonicalMessage { Index = 0, Role = MessageRole.User, Content = "health probe alpha" },
                    new CanonicalMessage { Index = 1, Role = MessageRole.Assistant, Content = "health probe beta" },
                },
            });
        var full = StartupDiagnostics.ReadDbHealth(db.DbPath);
        Assert.Equal(1, full.Sessions);
        Assert.Equal(2, full.Messages);
        Assert.Equal(1, full.SessionEmb);
        Assert.Equal(2, full.MessageEmb);
    }

    [Fact]
    public void Logger_StaysOutOfProductionLog_AndTraceCaptureWorks()
    {
        // P4 temp-only I/O: under the test host the log must redirect away from production.
        Assert.Contains("Casr-test-logs", CasrLogger.LogFilePath);
        Assert.False(CasrLogger.LogFilePath.StartsWith(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Casr"),
            StringComparison.OrdinalIgnoreCase));

        // P2/P5 TraceListener capture path the startup-line tests rely on.
        var probe = "startup-perf-probe-" + Guid.NewGuid().ToString("N");
        var listener = new CaptureListener();
        Trace.Listeners.Add(listener);
        try { CasrLogger.Info("TEST", probe); }
        finally { Trace.Listeners.Remove(listener); }
        Assert.Contains(listener.Lines, l => l.Contains(probe) && l.Contains("[INFO") && l.Contains("[TEST]"));
    }
}
