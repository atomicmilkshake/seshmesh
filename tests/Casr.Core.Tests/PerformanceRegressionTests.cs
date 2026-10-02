using System;
using System.Collections.Concurrent;
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
using Microsoft.Data.Sqlite;
using Xunit;

namespace Casr.Core.Tests;

/// <summary>
/// Focused indexing regressions. Every provider store, database and settings path is
/// temporary; no live registry/store or production configuration is consulted.
/// </summary>
public sealed class PerformanceRegressionTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "casr_perf_" + Guid.NewGuid().ToString("N"));

    public PerformanceRegressionTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { SqliteConnection.ClearAllPools(); } catch { }
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private sealed class PerfProvider : IProvider, IDisposable
    {
        private readonly List<(string SessionId, string Path)> _sessions = new();
        private readonly ConcurrentDictionary<string, string> _bodies = new(StringComparer.Ordinal);
        private int _readCalls;
        private int _activeReads;
        private int _maxConcurrentReads;
        private ManualResetEventSlim? _twoReaders;
        private ManualResetEventSlim? _releaseReaders;

        public PerfProvider(string slug, string root, bool createRoot = true)
        {
            Slug = slug;
            Root = root;
            if (createRoot) Directory.CreateDirectory(root);
        }

        public string Slug { get; }
        public string Root { get; }
        public string Name => Slug;
        public string CliAlias => Slug;
        public int ReadCalls => Volatile.Read(ref _readCalls);
        public int MaxConcurrentReads => Volatile.Read(ref _maxConcurrentReads);
        public ManualResetEventSlim TwoReadersArrived => _twoReaders ?? throw new InvalidOperationException("Concurrency gate not enabled.");

        public void EnableConcurrencyGate()
        {
            _twoReaders = new ManualResetEventSlim(false);
            _releaseReaders = new ManualResetEventSlim(false);
        }

        public void ReleaseReaders() => _releaseReaders?.Set();

        public string AddSession(string id, string body)
        {
            var path = Path.Combine(Root, id + ".jsonl");
            File.WriteAllText(path, body);
            _bodies[id] = body;
            _sessions.Add((id, path));
            return path;
        }

        public void ReplaceBody(string id, string body)
        {
            var path = _sessions.Single(s => s.SessionId == id).Path;
            File.WriteAllText(path, body);
            _bodies[id] = body;
        }

        public DetectionResult Detect() => new() { Installed = true };
        public IReadOnlyList<string> SessionRoots() => new[] { Root };
        public IReadOnlyList<(string SessionId, string Path)>? ListSessions() => _sessions.ToList();
        public string? OwnsSession(string sessionId) => null;
        public SessionSummary ReadSummary(string path) => throw new NotSupportedException();
        public WrittenSession WriteSession(CanonicalSession session, WriteOptions opts) => throw new NotSupportedException();
        public string ResumeCommand(string sessionId, string? workspace = null) => $"perf {sessionId}";

        public CanonicalSession ReadSession(string path)
        {
            Interlocked.Increment(ref _readCalls);
            var active = Interlocked.Increment(ref _activeReads);
            int current;
            while (active > (current = Volatile.Read(ref _maxConcurrentReads)) &&
                   Interlocked.CompareExchange(ref _maxConcurrentReads, active, current) != current) { }
            try
            {
                if (active >= 2) _twoReaders?.Set();
                if (_releaseReaders != null && !_releaseReaders.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Test concurrency gate was not released.");

                var id = Path.GetFileNameWithoutExtension(path);
                return new CanonicalSession
                {
                    SessionId = id,
                    ProviderSlug = Slug,
                    SourcePath = path,
                    Messages = new List<CanonicalMessage>
                    {
                        new() { Index = 0, Role = MessageRole.User, Content = _bodies[id] },
                    },
                };
            }
            finally
            {
                Interlocked.Decrement(ref _activeReads);
            }
        }

        public void Dispose()
        {
            _twoReaders?.Dispose();
            _releaseReaders?.Dispose();
        }
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private ProviderRegistry Registry(string name, params IProvider[] providers)
    {
        var settings = UserSettings.Load(Path.Combine(_tempDir, name + "-settings.json"));
        settings.EnabledProviderSlugs = providers.Select(p => p.Slug).ToList();
        return new ProviderRegistry(providers, settings);
    }

    private SessionDatabase Database(string name) => new(Path.Combine(_tempDir, name + ".db"));

    private static SessionSummary Summary(PerfProvider provider, string id, string path, int messages = 1)
        => new()
        {
            SessionId = id,
            Provider = provider.Slug,
            ProviderDisplayName = provider.Name,
            Title = id,
            SourcePath = path,
            FileSizeBytes = new FileInfo(path).Length,
            MessagesCount = messages,
            LastActiveAt = DateTime.UtcNow,
        };

    [Fact]
    public async Task Indexing_UsesConcurrentReadersAndPersistsFinalPartialBatch()
    {
        using var provider = new PerfProvider("perf-concurrent", Path.Combine(_tempDir, "concurrent-store"));
        provider.EnableConcurrencyGate();
        var summaries = Enumerable.Range(0, 3)
            .Select(i =>
            {
                var id = "c" + i;
                return Summary(provider, id, provider.AddSession(id, "parallel-body-" + id));
            }).ToList();
        using var db = Database("concurrent-index");
        var service = new SessionDiscoveryService(Registry("concurrent", provider), db, Path.Combine(_tempDir, "concurrent-gate.json"));

        var indexing = service.EnsureContentIndexAsync(summaries);
        bool overlapped;
        try { overlapped = provider.TwoReadersArrived.Wait(TimeSpan.FromSeconds(8)); }
        finally { provider.ReleaseReaders(); }

        var outcome = await indexing.WaitAsync(TimeSpan.FromSeconds(12));
        Assert.True(overlapped, "the test safety timeout elapsed before two bounded readers overlapped");
        Assert.Equal(3, outcome.Indexed);
        Assert.Equal(0, outcome.Errors);
        Assert.True(provider.MaxConcurrentReads >= 2);
        Assert.Equal(3, db.GetIndexCoverage().IndexedSessions);
        Assert.Equal(3, db.SearchExact("parallel-body").Select(r => r.SessionId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Indexing_CancellationFlushesFinalPartialBatch()
    {
        using var provider = new PerfProvider("perf-cancel", Path.Combine(_tempDir, "cancel-store"));
        var paths = Enumerable.Range(0, 8)
            .Select(i => (Id: "x" + i, Path: provider.AddSession("x" + i, "cancel-body-" + i)))
            .ToList();
        var summaries = paths.Select(x => Summary(provider, x.Id, x.Path)).ToList();
        using var db = Database("cancel-index");
        var service = new SessionDiscoveryService(Registry("cancel", provider), db, Path.Combine(_tempDir, "cancel-gate.json"));
        using var cancellation = new CancellationTokenSource();
        var progress = new CallbackProgress<ScanProgress>(beat =>
        {
            if (beat.ProviderCompleted > 0) cancellation.Cancel();
        });

        var indexing = service.EnsureContentIndexAsync(summaries, progress, cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => indexing.WaitAsync(TimeSpan.FromSeconds(12)));

        Assert.Equal(1, db.GetIndexCoverage().IndexedSessions);
        Assert.Single(db.SearchExact("cancel-body"));
    }

    [Fact]
    public async Task ProgressFailure_CancelsAndJoinsReadersBeforeReturningFailure()
    {
        using var provider = new PerfProvider("perf-progress-fail", Path.Combine(_tempDir, "progress-fail-store"));
        var summaries = Enumerable.Range(0, 8)
            .Select(i =>
            {
                var id = "p" + i;
                return Summary(provider, id, provider.AddSession(id, "progress-body-" + id));
            }).ToList();
        using var db = Database("progress-fail-index");
        var service = new SessionDiscoveryService(Registry("progress-fail", provider), db, Path.Combine(_tempDir, "progress-fail-gate.json"));
        var progress = new CallbackProgress<ScanProgress>(beat =>
        {
            if (beat.ProviderCompleted > 0) throw new InvalidOperationException("progress sink failed");
        });

        var indexing = service.EnsureContentIndexAsync(summaries, progress);
        await Assert.ThrowsAsync<InvalidOperationException>(() => indexing.WaitAsync(TimeSpan.FromSeconds(12)));
        Assert.Equal(1, db.GetIndexCoverage().IndexedSessions);
    }

    [Fact]
    public async Task Reindex_WhenFingerprintChanges_ReplacesOldMessageContent()
    {
        using var provider = new PerfProvider("perf-fresh", Path.Combine(_tempDir, "fresh-store"));
        var path = provider.AddSession("fresh-session", "old-unique-token");
        using var db = Database("fresh-index");
        var service = new SessionDiscoveryService(Registry("fresh", provider), db, Path.Combine(_tempDir, "fresh-gate.json"));

        var first = await service.EnsureContentIndexAsync(new[] { Summary(provider, "fresh-session", path) });
        Assert.Equal(1, first.Indexed);
        provider.ReplaceBody("fresh-session", "replacement-unique-token-with-new-size");
        var updated = await service.EnsureContentIndexAsync(new[] { Summary(provider, "fresh-session", path) });

        Assert.Equal(1, updated.Indexed);
        Assert.Empty(db.SearchExact("old-unique-token"));
        Assert.Single(db.SearchExact("replacement-unique-token"));
    }

    [Fact]
    public void Fingerprint_DatabaseFileRootIncludesWalChanges()
    {
        var path = Path.Combine(_tempDir, "wal-root.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE payloads(value TEXT); INSERT INTO payloads VALUES ('first');";
            setup.ExecuteNonQuery();
        }
        var provider = new PerfProvider("perf-wal", path, createRoot: false);
        var before = SessionDiscoveryService.ComputeStoreFingerprint(provider);
        using (var write = connection.CreateCommand())
        {
            write.CommandText = "INSERT INTO payloads VALUES (@value);";
            write.Parameters.AddWithValue("@value", new string('w', 16000));
            write.ExecuteNonQuery();
        }
        var after = SessionDiscoveryService.ComputeStoreFingerprint(provider);

        Assert.NotEqual(before, after);
        connection.Close();
    }

    [Fact]
    public void PreCancelledSearchesThrowBeforeDoingWork()
    {
        using var db = Database("pre-cancel-search");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;

        Assert.Throws<OperationCanceledException>(() => db.SearchFts("needle", cancellationToken: token));
        Assert.Throws<OperationCanceledException>(() => db.SearchFtsRaw("needle", cancellationToken: token));
        Assert.Throws<OperationCanceledException>(() => db.SearchExact("needle", cancellationToken: token));
        Assert.Throws<OperationCanceledException>(() => db.SearchRegex("needle", cancellationToken: token));
        Assert.Throws<OperationCanceledException>(() => db.SearchSemantic("needle", cancellationToken: token));
        Assert.Throws<OperationCanceledException>(() => db.SearchHybrid("needle", cancellationToken: token));
    }

    [Fact]
    public void PrepareEmbeddings_HonorsPreCancelledToken()
    {
        var summary = new SessionSummary { SessionId = "cancelled-prepare" };
        var session = new CanonicalSession
        {
            SessionId = summary.SessionId,
            Messages = Enumerable.Range(0, 100).Select(i => new CanonicalMessage
            {
                Index = i,
                Content = "message " + i,
            }).ToList(),
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => SessionDatabase.PrepareEmbeddings(summary, session, cancellation.Token));
    }

    [Fact]
    public void UpsertConversationBatch_RejectsPrecomputedVectorsFromDifferentModelOrWidth()
    {
        using var provider = new PerfProvider("perf-model", Path.Combine(_tempDir, "model-store"));
        var firstPath = provider.AddSession("wrong-model", "model-validation-needle");
        var secondPath = provider.AddSession("wrong-width", "model-validation-second");
        using var db = Database("model-validation");
        var dims = TextEmbedder.Dims;
        var wrongModel = new SessionDatabase.EmbeddingBundle
        {
            SessionId = "wrong-model",
            ModelId = "not-the-active-model",
            Dims = dims,
            SessionVector = new float[dims],
        };
        var wrongWidth = new SessionDatabase.EmbeddingBundle
        {
            SessionId = "wrong-width",
            ModelId = TextEmbedder.ModelId,
            Dims = dims + 1,
            SessionVector = new float[dims + 1],
        };
        wrongModel.MessageVectors[0] = new float[dims];
        wrongWidth.MessageVectors[0] = new float[dims + 1];
        var batch = new[]
        {
            (Summary(provider, "wrong-model", firstPath), provider.ReadSession(firstPath)),
            (Summary(provider, "wrong-width", secondPath), provider.ReadSession(secondPath)),
        };

        var result = db.UpsertConversationBatch(batch, new Dictionary<string, SessionDatabase.EmbeddingBundle>
        {
            ["wrong-model"] = wrongModel,
            ["wrong-width"] = wrongWidth,
        });

        Assert.Equal(2, result.Written);
        Assert.Empty(result.FailedIds);
        Assert.Equal("wrong-model", db.SearchSemantic("model-validation-needle")[0].SessionId);
        // Inspect persisted bytes independently: invalid zero vectors must be replaced
        // by correctly sized, nonzero vectors tagged with the active model.
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_tempDir, "model-validation.db")};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT dims, model, embedding FROM message_embeddings ORDER BY session_id";
        using var rows = command.ExecuteReader();
        var rowCount = 0;
        while (rows.Read())
        {
            Assert.Equal(dims, rows.GetInt32(0));
            Assert.Equal(TextEmbedder.F16ModelId, rows.GetString(1));
            var bytes = (byte[])rows.GetValue(2);
            Assert.Equal(dims * 2, bytes.Length);
            Assert.Contains(bytes, value => value != 0);
            rowCount++;
        }
        Assert.Equal(2, rowCount);
    }

    [Fact]
    public void HashingEmbedder_IsDeterministicUnderConcurrentCalls()
    {
        var embedder = HashingEmbedder.Instance;
        var expected = embedder.Embed("same immutable hashing input");
        var outputs = new float[128][];
        Parallel.For(0, outputs.Length, i => outputs[i] = embedder.Embed("same immutable hashing input"));

        Assert.All(outputs, output => Assert.Equal(expected, output));
    }
}
