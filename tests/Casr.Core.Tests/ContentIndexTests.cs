using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Casr.Core.Configuration;
using Casr.Core.Models;
using Casr.Core.Providers;
using Casr.Core.Search;
using Casr.Core.Services;
using Casr.Core.Storage;
using Xunit;

namespace Casr.Core.Tests;

public class ContentIndexTests : IDisposable
{
    private readonly string _tempDir;

    public ContentIndexTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "casr_content_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static (SessionSummary Summary, CanonicalSession Session) Fixture(
        string id, string title, (MessageRole Role, string Content)[] messages, long fileSize = 1000)
    {
        var summary = new SessionSummary
        {
            SessionId = id,
            Provider = "test",
            ProviderDisplayName = "Test",
            Title = title,
            Workspace = "/tmp",
            StartedAt = DateTime.Now.AddHours(-1),
            LastActiveAt = DateTime.Now,
            MessagesCount = messages.Length,
            FileSizeBytes = fileSize,
            SourcePath = "/tmp/" + id,
        };
        var session = new CanonicalSession
        {
            SessionId = id,
            ProviderSlug = "test",
            Title = title,
            Messages = messages.Select((m, i) => new CanonicalMessage
            {
                Index = i,
                Role = m.Role,
                Content = m.Content,
                TimestampEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }).ToList(),
        };
        return (summary, session);
    }

    [Fact]
    public void UpsertConversation_RoundTripsFullMessages()
    {
        using var db = new SessionDatabase(Path.Combine(_tempDir, "c1.db"));
        var (summary, session) = Fixture("s1", "GPU debug", new[]
        {
            (MessageRole.User, "Nvidia NVML linking error in C++?"),
            (MessageRole.Assistant, "Link with nvml.lib please."),
        });
        session.Messages[0].Author = "owen";
        session.Messages[0].ToolCalls.Add(new ToolCall { Id = "c1", Name = "bash", ArgumentsJson = "{}" });

        db.UpsertConversation(summary, session);

        var back = db.GetMessagesBySession("s1");
        Assert.Equal(2, back.Count);
        Assert.Equal("Nvidia NVML linking error in C++?", back[0].Content);
        Assert.Equal("owen", back[0].Author);
        Assert.Single(back[0].ToolCalls);
        Assert.Equal("bash", back[0].ToolCalls[0].Name);
        Assert.Equal(MessageRole.Assistant, back[1].Role);
    }

    [Fact]
    public void FtsAndMessages_StayInSync()
    {
        using var db = new SessionDatabase(Path.Combine(_tempDir, "c2.db"));
        var (summary, session) = Fixture("s1", "t", new[] { (MessageRole.User, "supercalifragilistic gpu driver") });
        db.UpsertConversation(summary, session);

        Assert.NotEmpty(db.SearchFts("supercalifragilistic"));
        Assert.NotEmpty(db.GetMessagesBySession("s1"));

        // Re-index with edited content: old terms must vanish from both stores.
        var (s2, edited) = Fixture("s1", "t", new[] { (MessageRole.User, "pasta recipe basil") }, fileSize: 2000);
        s2.MessagesCount = 1;
        db.UpsertConversation(s2, edited);

        Assert.Empty(db.SearchFts("supercalifragilistic"));
        Assert.NotEmpty(db.SearchFts("pasta"));
        var back = db.GetMessagesBySession("s1");
        Assert.Single(back);
        Assert.Contains("pasta", back[0].Content);
    }

    [Fact]
    public void SearchExact_FindsLiteralSubstring()
    {
        using var db = new SessionDatabase(Path.Combine(_tempDir, "c3.db"));
        var (summary, session) = Fixture("s1", "t", new[] { (MessageRole.User, "link with nvml.lib today") });
        db.UpsertConversation(summary, session);

        // Literal dots are NOT wildcards here (unlike FTS tokenization which strips them).
        var hits = db.SearchExact("nvml.lib");
        Assert.NotEmpty(hits);
        Assert.Equal("s1", hits[0].SessionId);
        Assert.Contains("<b>", hits[0].Snippet);

        Assert.Empty(db.SearchExact("no-such-phrase-xyz"));
    }

    [Fact]
    public void SearchExact_CaseSensitivityFlag()
    {
        using var db = new SessionDatabase(Path.Combine(_tempDir, "c9.db"));
        var (summary, session) = Fixture("s1", "t", new[] { (MessageRole.User, "NVML driver installed") });
        db.UpsertConversation(summary, session);

        var insensitive = db.SearchExact("nvml");
        Assert.NotEmpty(insensitive);
        Assert.Contains("<b>NVML</b>", insensitive[0].Snippet);

        Assert.Empty(db.SearchExact("nvml", caseSensitive: true));
        Assert.NotEmpty(db.SearchExact("NVML", caseSensitive: true));
    }

    [Fact]
    public void SearchRegex_ReportsMeasuredProgress()
    {
        using var db = new SessionDatabase(Path.Combine(_tempDir, "c10.db"));
        // 2500 rows > 2000-row chunk: forces at least two progress beats.
        var messages = Enumerable.Range(0, 2500)
            .Select(i => (MessageRole.User, $"message number {i} needle"))
            .ToArray();
        var (summary, session) = Fixture("big", "t", messages, fileSize: 99999);
        summary.MessagesCount = messages.Length;
        db.UpsertConversation(summary, session);

        var beats = new List<(int Scanned, int Total)>();
        void Handler((int Scanned, int Total) p) { lock (beats) beats.Add(p); }
        var hits = db.SearchRegex("needle", 10000, progress: new BeatReporter(Handler), cancellationToken: CancellationToken.None);

        Assert.True(hits.Count > 100);
        Assert.True(beats.Count >= 2);
        Assert.All(beats, b => Assert.Equal(2500, b.Total));
        Assert.Equal(2500, beats[^1].Scanned);
        for (var i = 1; i < beats.Count; i++)
            Assert.True(beats[i].Scanned >= beats[i - 1].Scanned);
        // Every beat derives an exact percent — no estimates.
        Assert.All(beats, b => Assert.Equal(b.Scanned * 100.0 / b.Total, b.Scanned * 100.0 / 2500));
    }

    private sealed class BeatReporter : IProgress<(int Scanned, int Total)>
    {
        private readonly Action<(int Scanned, int Total)> _handler;
        public BeatReporter(Action<(int Scanned, int Total)> handler) { _handler = handler; }
        public void Report((int Scanned, int Total) value) => _handler(value);
    }

    [Fact]
    public void SearchRegex_CancelledTokenThrows()
    {
        using var db = new SessionDatabase(Path.Combine(_tempDir, "c11.db"));
        var (summary, session) = Fixture("s1", "t", new[] { (MessageRole.User, "hello world") });
        db.UpsertConversation(summary, session);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            db.SearchRegex("hello", 100, cancellationToken: cts.Token));
    }

    [Fact]
    public void SearchRegex_FindsPattern_AndRejectsInvalid()
    {
        using var db = new SessionDatabase(Path.Combine(_tempDir, "c4.db"));
        var (summary, session) = Fixture("s1", "t", new[]
        {
            (MessageRole.User, "error code NVML-404 happened twice"),
            (MessageRole.Assistant, "all good here"),
        });
        db.UpsertConversation(summary, session);

        var hits = db.SearchRegex(@"NVML-\d+");
        Assert.NotEmpty(hits);
        Assert.Equal("s1", hits[0].SessionId);

        var invalid = db.SearchRegex("([unclosed");
        Assert.Empty(invalid);
        Assert.NotNull(db.LastError);
        Assert.Contains("Invalid regex", db.LastError);
    }

    [Fact]
    public void SearchSemantic_RanksLexicallyRelatedHigher()
    {
        using var db = new SessionDatabase(Path.Combine(_tempDir, "c5.db"));
        var (g, gpu) = Fixture("gpu", "driver", new[] { (MessageRole.User, "nvidia graphics driver install failed on windows") });
        var (p, pasta) = Fixture("pasta", "recipe", new[] { (MessageRole.User, "pasta recipe with basil and tomato sauce") });
        db.UpsertConversationBatch(new[] { (g, gpu), (p, pasta) });

        var hits = db.SearchSemantic("nvidia driver install");
        Assert.True(hits.Count >= 2);
        Assert.Equal("gpu", hits[0].SessionId);
    }

    [Fact]
    public void SearchHybrid_MergesWithoutDuplicates()
    {
        using var db = new SessionDatabase(Path.Combine(_tempDir, "c6.db"));
        var (g, gpu) = Fixture("gpu", "driver", new[] { (MessageRole.User, "nvidia graphics driver install failed") });
        var (p, pasta) = Fixture("pasta", "recipe", new[] { (MessageRole.User, "pasta recipe basil tomato") });
        db.UpsertConversationBatch(new[] { (g, gpu), (p, pasta) });

        var hits = db.SearchHybrid("nvidia driver");
        Assert.NotEmpty(hits);
        Assert.Equal(hits.Count, hits.Select(h => h.SessionId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("gpu", hits[0].SessionId);
    }

    [Fact]
    public void ContentIndexState_TracksFileSizeAndCount()
    {
        using var db = new SessionDatabase(Path.Combine(_tempDir, "c7.db"));
        var (summary, session) = Fixture("s1", "t", new[] { (MessageRole.User, "hello world") });
        db.UpsertSummary(summary);
        Assert.Empty(db.GetContentIndexStates());

        db.UpsertConversation(summary, session);
        var states = db.GetContentIndexStates();
        Assert.True(states.ContainsKey("s1"));
        Assert.Equal(summary.FileSizeBytes, states["s1"].FileSize);

        var (total, indexed, msgs, emb) = db.GetIndexCoverage();
        Assert.Equal(1, total);
        Assert.Equal(1, indexed);
        Assert.Equal(1, msgs);
        Assert.Equal(1, emb);
    }

    [Fact]
    public void Prune_ClearsMessagesFtsAndEmbeddings()
    {
        using var db = new SessionDatabase(Path.Combine(_tempDir, "c8.db"));
        var (summary, session) = Fixture("s1", "t", new[] { (MessageRole.User, "unique zebra phrase qq") });
        db.UpsertConversation(summary, session);
        Assert.NotEmpty(db.SearchFts("zebra"));

        db.DeleteSessionsByIds(new[] { "s1" });

        Assert.Empty(db.GetMessagesBySession("s1"));
        Assert.Empty(db.SearchFts("zebra"));
        Assert.Empty(db.SearchSemantic("zebra phrase"));
        var (total, indexed, msgs, _) = db.GetIndexCoverage();
        Assert.Equal(0, total);
        Assert.Equal(0, indexed);
        Assert.Equal(0, msgs);
    }

    private sealed class StubProvider : IProvider
    {
        public int ReadCalls;
        public string? ThrowOnPath;
        public bool EmitDuplicateIndex;
        public string Name => "Stub";
        public string Slug => "stub";
        public string CliAlias => "stub";
        public DetectionResult Detect() => new() { Installed = false };
        public IReadOnlyList<string> SessionRoots() => Array.Empty<string>();
        public string? OwnsSession(string sessionId) => null;
        public CanonicalSession ReadSession(string path)
        {
            ReadCalls++;
            if (path == ThrowOnPath) throw new InvalidOperationException("boom");
            var id = Path.GetFileNameWithoutExtension(path);
            if (EmitDuplicateIndex)
            {
                // Two messages sharing one index violate the (session_id, message_index) PK
                // mid-write: forces a deterministic write failure through the public API.
                return new CanonicalSession
                {
                    SessionId = id,
                    ProviderSlug = "stub",
                    SourcePath = path,
                    Messages = new()
                    {
                        new CanonicalMessage { Index = 0, Role = MessageRole.User, Content = "dup a " + id },
                        new CanonicalMessage { Index = 0, Role = MessageRole.Assistant, Content = "dup b " + id },
                    },
                };
            }
            return new CanonicalSession
            {
                SessionId = id,
                ProviderSlug = "stub",
                SourcePath = path,
                Messages = new() { new CanonicalMessage { Index = 0, Role = MessageRole.User, Content = "hello " + id } },
            };
        }
        public SessionSummary ReadSummary(string path) => throw new NotImplementedException();
        public WrittenSession WriteSession(CanonicalSession session, WriteOptions opts) => throw new NotImplementedException();
        public string ResumeCommand(string sessionId, string? workspace = null) => "stub";
        public IReadOnlyList<(string SessionId, string Path)>? ListSessions() => null;
    }

    /// <summary>Same offline math as the default provider, different ModelId — simulates a provider switch.</summary>
    private sealed class RenamedEmbedder : IEmbeddingProvider
    {
        public RenamedEmbedder(string modelId) { ModelId = modelId; }

        public string ModelId { get; }
        public int Dims => HashingEmbedder.Instance.Dims;
        public string DisplayLabel => "test-renamed";
        public float[] Embed(string? text) => HashingEmbedder.Instance.Embed(text);
        public float[] EmbedSession(IEnumerable<(string? Content, int WeightHint)> parts) => HashingEmbedder.Instance.EmbedSession(parts);
    }

    private sealed class SyncProgress : IProgress<ScanProgress>
    {
        public readonly List<ScanProgress> Beats = new();
        public void Report(ScanProgress p) { lock (Beats) Beats.Add(p); }
    }

    private SessionDiscoveryService StubService(SessionDatabase db, StubProvider stub, string settingsName)
    {
        var settings = UserSettings.Load(Path.Combine(_tempDir, settingsName));
        return new SessionDiscoveryService(new ProviderRegistry(new[] { stub }, settings), db);
    }

    private static List<SessionSummary> StubSessions(string dir, int n) =>
        Enumerable.Range(0, n).Select(i => new SessionSummary
        {
            SessionId = "s" + i,
            Provider = "stub",
            ProviderDisplayName = "Stub",
            MessagesCount = 1,
            FileSizeBytes = 100 + i,
            SourcePath = Path.Combine(dir, $"s{i}.jsonl"),
        }).ToList();

    [Fact]
    public async Task EnsureContentIndex_ReportsExactPerSessionProgress()
    {
        using var db = new SessionDatabase(Path.Combine(_tempDir, "p1.db"));
        var stub = new StubProvider();
        var svc = StubService(db, stub, "p1_settings.json");
        var sessions = StubSessions(_tempDir, 3);

        var progress = new SyncProgress();
        var (indexed, skipped, errors) = await svc.EnsureContentIndexAsync(sessions, progress);

        Assert.Equal(3, indexed);
        Assert.Equal(0, skipped);
        Assert.Equal(0, errors);
        Assert.Equal(3, stub.ReadCalls);

        // Opening beat (0/N) then one beat per finished session — denominator exact throughout.
        var beats = progress.Beats.Where(b => b.Phase == "indexing").ToList();
        Assert.True(beats.Count >= 4);
        Assert.All(beats, b => Assert.Equal(3, b.ProviderTotal));
        Assert.Equal(new[] { 0, 1, 2, 3 }, beats.Take(4).Select(b => b.ProviderCompleted));
        Assert.All(beats, b => Assert.Equal("Index", b.CurrentProvider));
    }

    [Fact]
    public async Task EnsureContentIndex_ModelSwitch_ReembedsAllSessions()
    {
        using var db = new SessionDatabase(Path.Combine(_tempDir, "p_model.db"));
        var stub = new StubProvider();
        var svc = StubService(db, stub, "p_model_settings.json");
        var sessions = StubSessions(_tempDir, 3);

        await svc.EnsureContentIndexAsync(sessions, new SyncProgress());
        Assert.Equal(3, stub.ReadCalls);
        Assert.False(db.NeedsModelReembed());

        // Simulate the user opting into a different embedding provider (hashing -> neural).
        TextEmbedder.SetProvider(new RenamedEmbedder("hashing-trigram-v1-test-renamed"));
        try
        {
            Assert.True(db.NeedsModelReembed());

            // Interrupted migration: one session is re-embedded before the cancel.
            var (firstPass, _, _) = await svc.EnsureContentIndexAsync(new[] { sessions[0] }, new SyncProgress());
            Assert.Equal(1, firstPass);
            Assert.Equal(4, stub.ReadCalls); // 3 initial + 1 migrated
            Assert.True(db.NeedsModelReembed());

            // The next pass resumes: already-migrated sessions are skipped, only the
            // two unfinished sessions are re-read despite unchanged fingerprints.
            var progress = new SyncProgress();
            var (indexed, skipped, errors) = await svc.EnsureContentIndexAsync(sessions, progress);

            Assert.Equal(2, indexed);
            Assert.Equal(1, skipped);
            Assert.Equal(0, errors);
            Assert.Equal(6, stub.ReadCalls);
            Assert.False(db.NeedsModelReembed());
            Assert.Contains(progress.Beats, b => b.Phase == "indexing");
        }
        finally
        {
            TextEmbedder.ResetToDefault();
        }

        // The post-switch rows carry the renamed model, so the default hashing
        // provider now needs its own one-time re-embed again.
        Assert.True(db.NeedsModelReembed());
    }

    [Fact]
    public async Task EnsureContentIndex_SecondRunSkipsWithoutReads()
    {
        using var db = new SessionDatabase(Path.Combine(_tempDir, "p2.db"));
        var stub = new StubProvider();
        var svc = StubService(db, stub, "p2_settings.json");
        var sessions = StubSessions(_tempDir, 3);

        await svc.EnsureContentIndexAsync(sessions, new SyncProgress());
        Assert.Equal(3, stub.ReadCalls);

        var progress = new SyncProgress();
        var (indexed, skipped, errors) = await svc.EnsureContentIndexAsync(sessions, progress);

        Assert.Equal(0, indexed);
        Assert.Equal(3, skipped);
        Assert.Equal(0, errors);
        Assert.Equal(3, stub.ReadCalls); // no new reads: everything served from the index
        var beats = progress.Beats.Where(b => b.Phase == "indexing").ToList();
        Assert.Single(beats);
        Assert.Equal(3, beats[0].ProviderCompleted);
    }

    [Fact]
    public async Task EnsureContentIndex_ErrorIsolatedAndCounted()
    {
        using var db = new SessionDatabase(Path.Combine(_tempDir, "p3.db"));
        var stub = new StubProvider();
        var svc = StubService(db, stub, "p3_settings.json");
        var sessions = StubSessions(_tempDir, 3);
        stub.ThrowOnPath = sessions[1].SourcePath;

        var progress = new SyncProgress();
        var (indexed, skipped, errors) = await svc.EnsureContentIndexAsync(sessions, progress);

        Assert.Equal(2, indexed);
        Assert.Equal(0, skipped);
        Assert.Equal(1, errors);
        Assert.NotEmpty(db.GetMessagesBySession("s0"));
        Assert.Empty(db.GetMessagesBySession("s1"));
        Assert.NotEmpty(db.GetMessagesBySession("s2"));
        Assert.Contains(progress.Beats, b => b.Phase == "indexing" && b.ProviderErrors == 1);
    }

    [Fact]
    public async Task EnsureContentIndex_SameSizeDifferentMtime_TriggersReindex()
    {
        using var db = new SessionDatabase(Path.Combine(_tempDir, "fp1.db"));
        var stub = new StubProvider();
        var svc = StubService(db, stub, "fp1_settings.json");
        var sessions = StubSessions(_tempDir, 2);

        var (first, _, _) = await svc.EnsureContentIndexAsync(sessions, new SyncProgress());
        Assert.Equal(2, first);
        Assert.Equal(2, stub.ReadCalls);

        // Same file size + same message count, but a newer mtime (a same-size edit):
        // the fingerprint must catch it and re-read exactly that session.
        var edited = StubSessions(_tempDir, 2);
        edited[0].LastActiveAt = DateTime.Now.AddHours(1);
        var (indexed, skipped, _) = await svc.EnsureContentIndexAsync(edited, new SyncProgress());

        Assert.Equal(1, indexed);
        Assert.Equal(1, skipped);
        Assert.Equal(3, stub.ReadCalls);
    }

    [Fact]
    public void UpsertConversationBatch_ReportsFailuresAndPreservesPriorState()
    {
        using var db = new SessionDatabase(Path.Combine(_tempDir, "w1.db"));
        var (good, goodSession) = Fixture("s1", "t", new[] { (MessageRole.User, "original content here") });
        db.UpsertConversation(good, goodSession);
        Assert.Single(db.GetMessagesBySession("s1"));

        var (okSummary, okSession) = Fixture("s2", "t", new[] { (MessageRole.User, "second session content") });
        var badSummary = new SessionSummary
        {
            SessionId = "s1",
            Provider = "test",
            ProviderDisplayName = "Test",
            Title = "t",
            Workspace = "/tmp",
            MessagesCount = 2,
            FileSizeBytes = 2000,
            SourcePath = "/tmp/s1",
        };
        var badSession = new CanonicalSession
        {
            SessionId = "s1",
            ProviderSlug = "test",
            Messages = new()
            {
                new CanonicalMessage { Index = 0, Role = MessageRole.User, Content = "new content a" },
                new CanonicalMessage { Index = 0, Role = MessageRole.Assistant, Content = "new content b" },
            },
        };

        var (written, failed) = db.UpsertConversationBatch(new[] { (okSummary, okSession), (badSummary, badSession) });

        Assert.Equal(1, written);
        Assert.Contains("s1", failed);
        Assert.NotNull(db.LastError);
        // SAVEPOINT rollback: the failed session keeps its prior rows, not a partial write.
        var back = db.GetMessagesBySession("s1");
        Assert.Single(back);
        Assert.Equal("original content here", back[0].Content);
        Assert.Single(db.GetMessagesBySession("s2"));
    }

    [Fact]
    public async Task EnsureContentIndex_CountsWriteFailuresAsErrors()
    {
        using var db = new SessionDatabase(Path.Combine(_tempDir, "w2.db"));
        var stub = new StubProvider { EmitDuplicateIndex = true };
        var svc = StubService(db, stub, "w2_settings.json");
        var sessions = StubSessions(_tempDir, 2);

        var (indexed, skipped, errors) = await svc.EnsureContentIndexAsync(sessions, new SyncProgress());

        Assert.Equal(0, indexed);
        Assert.Equal(0, skipped);
        Assert.Equal(2, errors);
        Assert.NotNull(db.LastError);
    }
}
