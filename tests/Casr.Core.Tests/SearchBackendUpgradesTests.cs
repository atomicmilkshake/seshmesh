using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Casr.Core.Models;
using Casr.Core.Search;
using Casr.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Casr.Core.Tests;

/// <summary>
/// Search-backend upgrades: float16 vector storage, SQL filter pushdown, composite
/// ranking, raw FTS mode, and vacuum. 5-pillar discipline throughout: fixtures are
/// validated before execution, every test runs in a hermetic temp DB, and all
/// storage claims are verified with independent raw-SQLite egress (row counts,
/// blob widths, model tags) — never the SUT's own readers alone. Temp DBs only;
/// production %LOCALAPPDATA% paths are never touched.
/// </summary>
public class SearchBackendUpgradesTests : IDisposable
{
    private readonly string _tempDir;

    private const long Ts2020 = 1577836800000L; // 2020-01-01T00:00:00Z
    private const long Ts2023 = 1672531200000L; // 2023-01-01T00:00:00Z

    public SearchBackendUpgradesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "casr_searchup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        // Another suite owns the neural opt-in; pin the hashing default so dims/model
        // tags are deterministic here regardless of test parallelism.
        TextEmbedder.ResetToDefault();
    }

    public void Dispose()
    {
        try { TextEmbedder.ResetToDefault(); } catch { }
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string DbPath(string name) => Path.Combine(_tempDir, name);

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static (SessionSummary Summary, CanonicalSession Session) Fixture(
        string id, string provider, string workspace,
        (MessageRole Role, string Content, long TsMs)[] messages,
        DateTime? lastActive = null, long fileSize = 1000)
    {
        Assert.NotEmpty(messages); // P1: ingress must be non-trivial
        var la = lastActive ?? DateTime.UtcNow;
        var summary = new SessionSummary
        {
            SessionId = id,
            Provider = provider,
            ProviderDisplayName = provider,
            Title = "t-" + id,
            Workspace = workspace,
            StartedAt = la.AddHours(-1),
            LastActiveAt = la,
            MessagesCount = messages.Length,
            FileSizeBytes = fileSize,
            SourcePath = "/tmp/" + id,
        };
        var session = new CanonicalSession
        {
            SessionId = id,
            ProviderSlug = provider,
            Title = "t-" + id,
            Workspace = workspace,
            SourcePath = "/tmp/" + id,
            Messages = messages.Select((m, i) => new CanonicalMessage
            {
                Index = i,
                Role = m.Role,
                Content = m.Content,
                TimestampEpochMs = m.TsMs,
            }).ToList(),
        };
        return (summary, session);
    }

    /// <summary>Production-like write order: summary row first, then conversation.</summary>
    private static void Seed(SessionDatabase db, (SessionSummary Summary, CanonicalSession Session) fx)
    {
        db.UpsertSummary(fx.Summary);
        db.UpsertConversation(fx.Summary, fx.Session);
    }

    // ---------- Independent raw-SQLite egress (bypasses every SUT reader) ----------

    private static SqliteConnection OpenRaw(string dbPath)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
        }.ToString());
        conn.Open();
        return conn;
    }

    private static long RawCount(string dbPath, string table, string? where = null)
    {
        using var conn = OpenRaw(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}" + (where == null ? ";" : $" WHERE {where};");
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static List<(long Length, string Model, long Dims)> RawEmbeddingRows(string dbPath, string table)
    {
        var rows = new List<(long, string, long)>();
        using var conn = OpenRaw(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT LENGTH(embedding), model, dims FROM {table} ORDER BY rowid;";
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add((r.GetInt64(0), r.GetString(1), r.GetInt64(2)));
        return rows;
    }

    private static List<(int Idx, string Content, long? Ts)> RawMessages(string dbPath, string sid)
    {
        var rows = new List<(int, string, long?)>();
        using var conn = OpenRaw(dbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT message_index, content, timestamp_ms FROM messages WHERE session_id = @id ORDER BY message_index;";
        cmd.Parameters.AddWithValue("@id", sid);
        using var r = cmd.ExecuteReader();
        while (r.Read()) rows.Add((r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2)));
        return rows;
    }

    private static List<string> IdSet(IEnumerable<SearchResult> hits) =>
        hits.Select(h => h.SessionId).Distinct(StringComparer.Ordinal).OrderBy(s => s).ToList();

    // ---------- 1. Float16 codec ----------

    [Fact]
    public void F16Codec_RoundTrips_WithNegligibleError()
    {
        // P1: ingress is a real embedding, not zeros.
        var vec = TextEmbedder.Embed("nvidia driver install failed on windows");
        Assert.Equal(TextEmbedder.Dims, vec.Length);
        Assert.Contains(vec, v => v != 0);

        // P2: execute the width migration primitives.
        var f16 = TextEmbedder.ToBytesF16(vec);
        var f32 = TextEmbedder.ToBytes(vec);

        // P3/P4: exact byte widths + width detector truth table.
        Assert.Equal(TextEmbedder.Dims * 2, f16.Length);
        Assert.Equal(TextEmbedder.Dims * 4, f32.Length);
        Assert.Equal(2, TextEmbedder.DetectWidth(f16, TextEmbedder.Dims));
        Assert.Equal(4, TextEmbedder.DetectWidth(f32, TextEmbedder.Dims));
        Assert.Equal(0, TextEmbedder.DetectWidth(new byte[3], TextEmbedder.Dims));

        // P5: half-precision round-trip preserves direction (cosine > 0.999) and the
        // width-detecting decoder agrees with the explicit decoders bit-for-bit.
        var back = TextEmbedder.FromBytesF16(f16, TextEmbedder.Dims);
        Assert.True(TextEmbedder.Cosine(vec, back) > 0.999,
            $"F16 cosine drift too large: {TextEmbedder.Cosine(vec, back)}");
        Assert.Equal(back, TextEmbedder.FromBytesAuto(f16, TextEmbedder.Dims));
        Assert.Equal(vec, TextEmbedder.FromBytesAuto(f32, TextEmbedder.Dims));
        Assert.Equal(new float[TextEmbedder.Dims], TextEmbedder.FromBytesF16(new byte[10], TextEmbedder.Dims));

        // Model-tag compatibility: plain (legacy F32) + suffixed (F16) match, others don't.
        Assert.True(TextEmbedder.IsModelCompatible(TextEmbedder.ModelId));
        Assert.True(TextEmbedder.IsModelCompatible(TextEmbedder.F16ModelId));
        Assert.EndsWith(VectorCodecs.F16Suffix, TextEmbedder.F16ModelId);
        Assert.False(TextEmbedder.IsModelCompatible("some-other-model-v9"));
        Assert.False(TextEmbedder.IsModelCompatible(null));
    }

    [Fact]
    public void NewRows_WriteF16Vectors_ModelTagged_MessageCountsIntact()
    {
        var dbPath = DbPath("f16.db");
        var now = NowMs();
        using (var db = new SessionDatabase(dbPath))
        {
            var fx = Fixture("s1", "pi", "/w/a", new[]
            {
                (MessageRole.User, "nvidia driver install failed on windows", now),
                (MessageRole.Assistant, "try reinstalling the driver package", now),
                (MessageRole.User, "   ", now), // whitespace-only: no message row, no vector
            });
            Seed(db, fx);
            Assert.Null(db.LastError);
        }

        // P4: independent egress — counts, widths, tags, dims.
        Assert.Equal(2, RawMessages(dbPath, "s1").Count);
        Assert.Equal(2, RawCount(dbPath, "message_embeddings", "session_id = 's1'"));
        Assert.Equal(1, RawCount(dbPath, "session_embeddings", "session_id = 's1'"));
        foreach (var table in new[] { "message_embeddings", "session_embeddings" })
        {
            var rows = RawEmbeddingRows(dbPath, table);
            Assert.NotEmpty(rows);
            Assert.All(rows, r =>
            {
                Assert.Equal(TextEmbedder.Dims * 2, r.Length); // halved width, dims unchanged
                Assert.Equal(TextEmbedder.Dims, r.Dims);
                Assert.True(TextEmbedder.IsModelCompatible(r.Model));
                Assert.EndsWith(VectorCodecs.F16Suffix, r.Model);
            });
        }

        // P5: the F16 rows actually serve semantic search (round-trip through the SUT).
        using (var db = new SessionDatabase(dbPath))
        {
            Assert.False(db.NeedsMessageEmbeddingBackfill());
            var hits = db.SearchSemantic("nvidia driver install");
            Assert.Contains(hits, h => h.SessionId == "s1");
        }
    }

    [Fact]
    public void LegacyF32Rows_StillDecode_AlongsideF16()
    {
        var dbPath = DbPath("mig.db");
        var now = NowMs();
        using (var db = new SessionDatabase(dbPath))
        {
            var fx = Fixture("s_new", "pi", "/w/a", new[]
            {
                (MessageRole.User, "nvidia graphics driver install failed on windows", now),
            });
            Seed(db, fx);
        }

        // P2: craft a pre-upgrade session directly in SQL: F32 blobs + plain model tag.
        var legacyText = "nvidia display driver setup problem on linux";
        var legacyVec = TextEmbedder.Embed(legacyText);
        var legacySes = TextEmbedder.EmbedSession(new List<(string?, int)> { (legacyText, legacyText.Length) });
        using (var conn = OpenRaw(dbPath))
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO sessions (session_id, provider, provider_display_name, title, workspace, started_at, last_active_at, messages_count, file_size_bytes, source_path, last_indexed_at) VALUES ('s_old','pi','pi','t','/w/a',1,1,1,10,'/tmp/s_old',1);";
                cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO messages (session_id, message_index, role, content, timestamp_ms) VALUES ('s_old', 0, 'User', @c, @ts);";
                cmd.Parameters.AddWithValue("@c", legacyText);
                cmd.Parameters.AddWithValue("@ts", Ts2020);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO message_embeddings (session_id, message_index, chunk_ord, embedding, dims, model, updated_at) VALUES ('s_old', 0, 0, @e, @d, @m, @at);";
                cmd.Parameters.Add("@e", SqliteType.Blob).Value = TextEmbedder.ToBytes(legacyVec);
                cmd.Parameters.AddWithValue("@d", TextEmbedder.Dims);
                cmd.Parameters.AddWithValue("@m", TextEmbedder.ModelId); // plain tag: legacy F32
                cmd.Parameters.AddWithValue("@at", Ts2020);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO session_embeddings (session_id, embedding, dims, model, updated_at) VALUES ('s_old', @e, @d, @m, @at);";
                cmd.Parameters.Add("@e", SqliteType.Blob).Value = TextEmbedder.ToBytes(legacySes);
                cmd.Parameters.AddWithValue("@d", TextEmbedder.Dims);
                cmd.Parameters.AddWithValue("@m", TextEmbedder.ModelId);
                cmd.Parameters.AddWithValue("@at", Ts2020);
                cmd.ExecuteNonQuery();
            }
        }

        // P4: width truth table — legacy rows are F32, new rows F16, dims identical.
        var msgRows = RawEmbeddingRows(dbPath, "message_embeddings");
        Assert.Equal(2, msgRows.Count);
        Assert.Contains(msgRows, r => r.Length == TextEmbedder.Dims * 4 && r.Model == TextEmbedder.ModelId);
        Assert.Contains(msgRows, r => r.Length == TextEmbedder.Dims * 2 && r.Model == TextEmbedder.F16ModelId);
        var sesRows = RawEmbeddingRows(dbPath, "session_embeddings");
        Assert.Equal(2, sesRows.Count);

        // P5: semantic search scores BOTH widths — no migration, no data loss.
        using (var db = new SessionDatabase(dbPath))
        {
            var hits = db.SearchSemantic("nvidia driver install");
            var ids = IdSet(hits);
            Assert.Contains("s_old", ids);
            Assert.Contains("s_new", ids);
            Assert.All(hits, h => Assert.True(h.Score > 0));
        }
    }

    // ---------- 2. Filter pushdown ----------

    private static void SeedFilterCorpus(SessionDatabase db, long now)
    {
        Seed(db, Fixture("s_pi", "pi", "/w/a", new[]
        {
            (MessageRole.User, "needle supply chain logistics report", now),
        }, lastActive: DateTime.UtcNow));
        Seed(db, Fixture("s_grok", "grok", "/w/b", new[]
        {
            (MessageRole.User, "needle inventory status update", now),
        }, lastActive: DateTime.UtcNow));
        Seed(db, Fixture("s_old", "pi", "/w/a", new[]
        {
            (MessageRole.User, "needle procurement archive note", Ts2020),
        }, lastActive: new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Filter_Fts_MatchesUnfilteredSubset_NoFalseEmpty()
    {
        var dbPath = DbPath("flt_fts.db");
        var now = NowMs();
        using (var db = new SessionDatabase(dbPath))
        {
            SeedFilterCorpus(db, now);
        }
        // P1: ingress — all three sessions indexed with bodies.
        Assert.Equal(3, RawCount(dbPath, "messages"));

        using (var db = new SessionDatabase(dbPath))
        {
            // P2/P5: unfiltered baseline first, then every axis must return the exact
            // subset — non-empty (no false-empty) and contained in the baseline.
            var baseline = IdSet(db.SearchFts("needle", 100));
            Assert.Equal(new[] { "s_grok", "s_old", "s_pi" }, baseline);

            Assert.Equal(new[] { "s_old", "s_pi" }, IdSet(db.SearchFts("needle", 100, providerSlugs: new[] { "pi" })));
            Assert.Equal(new[] { "s_grok" }, IdSet(db.SearchFts("needle", 100, providerSlugs: new[] { "GROK" })));
            Assert.Equal(new[] { "s_grok" }, IdSet(db.SearchFts("needle", 100, workspace: "/w/b")));
            Assert.Equal(new[] { "s_grok", "s_pi" }, IdSet(db.SearchFts("needle", 100, sinceMs: Ts2023)));
            Assert.Equal(new[] { "s_pi" }, IdSet(db.SearchFts("needle", 100,
                providerSlugs: new[] { "pi" }, workspace: "/w/a", sinceMs: Ts2023)));
            // Empty provider list = no restriction, not "match nothing".
            Assert.Equal(baseline, IdSet(db.SearchFts("needle", 100, providerSlugs: Array.Empty<string>())));
        }
    }

    [Fact]
    public void Filter_Exact_MatchesUnfilteredSubset()
    {
        var dbPath = DbPath("flt_exact.db");
        var now = NowMs();
        using (var db = new SessionDatabase(dbPath)) SeedFilterCorpus(db, now);
        using (var db = new SessionDatabase(dbPath))
        {
            var baseline = IdSet(db.SearchExact("needle", 100));
            Assert.Equal(new[] { "s_grok", "s_old", "s_pi" }, baseline);
            Assert.Equal(new[] { "s_old", "s_pi" }, IdSet(db.SearchExact("needle", 100, providerSlugs: new[] { "pi" })));
            Assert.Equal(new[] { "s_grok" }, IdSet(db.SearchExact("needle", 100, workspace: "/w/b")));
            Assert.Equal(new[] { "s_grok", "s_pi" }, IdSet(db.SearchExact("needle", 100, sinceMs: Ts2023)));
        }
    }

    [Fact]
    public void Filter_RegexDetailed_MatchesUnfilteredSubset()
    {
        var dbPath = DbPath("flt_regex.db");
        var now = NowMs();
        using (var db = new SessionDatabase(dbPath)) SeedFilterCorpus(db, now);
        using (var db = new SessionDatabase(dbPath))
        {
            var (baseResults, baseTimedOut) = db.SearchRegexDetailed("needle", 100);
            Assert.False(baseTimedOut);
            Assert.Equal(new[] { "s_grok", "s_old", "s_pi" }, IdSet(baseResults));
            var (prov, timedOut1) = db.SearchRegexDetailed("needle", 100, providerSlugs: new[] { "pi" });
            Assert.False(timedOut1);
            Assert.Equal(new[] { "s_old", "s_pi" }, IdSet(prov));
            var (ws, timedOut2) = db.SearchRegexDetailed("needle", 100, workspace: "/w/b");
            Assert.False(timedOut2);
            Assert.Equal(new[] { "s_grok" }, IdSet(ws));
            var (since, timedOut3) = db.SearchRegexDetailed("needle", 100, sinceMs: Ts2023);
            Assert.False(timedOut3);
            Assert.Equal(new[] { "s_grok", "s_pi" }, IdSet(since));
        }
    }

    [Fact]
    public void Filter_Semantic_MatchesUnfilteredSubset()
    {
        var dbPath = DbPath("flt_sem.db");
        var now = NowMs();
        using (var db = new SessionDatabase(dbPath)) SeedFilterCorpus(db, now);
        using (var db = new SessionDatabase(dbPath))
        {
            var baseline = IdSet(db.SearchSemantic("needle", 100));
            Assert.Equal(new[] { "s_grok", "s_old", "s_pi" }, baseline);
            Assert.Equal(new[] { "s_old", "s_pi" }, IdSet(db.SearchSemantic("needle", 100, providerSlugs: new[] { "pi" })));
            Assert.Equal(new[] { "s_grok" }, IdSet(db.SearchSemantic("needle", 100, workspace: "/w/b")));
            Assert.Equal(new[] { "s_grok", "s_pi" }, IdSet(db.SearchSemantic("needle", 100, sinceMs: Ts2023)));
        }
    }

    [Fact]
    public void Filter_Hybrid_MatchesUnfilteredSubset()
    {
        var dbPath = DbPath("flt_hyb.db");
        var now = NowMs();
        using (var db = new SessionDatabase(dbPath)) SeedFilterCorpus(db, now);
        using (var db = new SessionDatabase(dbPath))
        {
            var baseline = IdSet(db.SearchHybrid("needle", 100));
            Assert.Equal(new[] { "s_grok", "s_old", "s_pi" }, baseline);
            Assert.Equal(new[] { "s_old", "s_pi" }, IdSet(db.SearchHybrid("needle", 100, providerSlugs: new[] { "pi" })));
            Assert.Equal(new[] { "s_grok" }, IdSet(db.SearchHybrid("needle", 100, workspace: "/w/b")));
            Assert.Equal(new[] { "s_grok", "s_pi" }, IdSet(db.SearchHybrid("needle", 100, sinceMs: Ts2023)));
        }
    }

    [Fact]
    public void Filter_NarrowFilter_BroadQuery_LimitOne_DoesNotFalseEmpty()
    {
        // The pushdown case: LIMIT applies AFTER the SQL filter, so a narrow filter
        // over a query matching every session still returns the filtered session.
        // (Post-hoc in-memory filtering under limit=1 could return nothing.)
        var dbPath = DbPath("flt_noempty.db");
        var now = NowMs();
        using (var db = new SessionDatabase(dbPath)) SeedFilterCorpus(db, now);
        using (var db = new SessionDatabase(dbPath))
        {
            var grok = new[] { "grok" };
            Assert.Equal(new[] { "s_grok" }, IdSet(db.SearchFts("needle", 1, providerSlugs: grok)));
            Assert.Equal(new[] { "s_grok" }, IdSet(db.SearchExact("needle", 1, providerSlugs: grok)));
            Assert.Equal(new[] { "s_grok" }, IdSet(db.SearchRegex("needle", 1, providerSlugs: grok)));
            Assert.Equal(new[] { "s_grok" }, IdSet(db.SearchSemantic("needle", 1, providerSlugs: grok)));
            Assert.Equal(new[] { "s_grok" }, IdSet(db.SearchHybrid("needle", 1, providerSlugs: grok)));
        }
    }

    // ---------- 3. Ranking ----------

    [Fact]
    public void Ranking_Exact_MatchCountBeatsEarlierOffset()
    {
        // Justification for the order change: raw-offset ranking put a single
        // incidental mention above the session that discusses the term throughout.
        var dbPath = DbPath("rank_exact.db");
        var now = NowMs();
        using (var db = new SessionDatabase(dbPath))
        {
            Seed(db, Fixture("s_few", "pi", "/w/a", new[]
            {
                (MessageRole.User, "alpha", now), // 1 occurrence at offset 0
            }));
            Seed(db, Fixture("s_many", "pi", "/w/a", new[]
            {
                (MessageRole.User, "zzz alpha zzz alpha zzz alpha", now), // 3 occurrences, offset 4
            }));
        }
        Assert.Equal(2, RawCount(dbPath, "messages"));

        using (var db = new SessionDatabase(dbPath))
        {
            var hits = db.SearchExact("alpha");
            Assert.Equal(2, hits.Count);
            Assert.Equal("s_many", hits[0].SessionId);
            Assert.Equal("s_few", hits[1].SessionId);
            Assert.True(hits[0].Score > hits[1].Score);
            Assert.Equal(-hits[0].Score, hits[0].Rank); // Rank = -Score convention kept
        }
    }

    [Fact]
    public void Ranking_Exact_ShorterThenEarlierThenNewerWinTies()
    {
        var dbPath = DbPath("rank_ties.db");
        var now = NowMs();
        using (var db = new SessionDatabase(dbPath))
        {
            // Length tiebreak: same count (1), same offset (0) -> shorter first.
            Seed(db, Fixture("s_long", "pi", "/w/a", new[]
            {
                (MessageRole.User, "alpha plus padding words here yes", now),
            }));
            Seed(db, Fixture("s_short", "pi", "/w/a", new[]
            {
                (MessageRole.User, "alpha", now),
            }));
            // Offset tiebreak: same count, same length (8) -> earlier offset first.
            Seed(db, Fixture("s_early", "pi", "/w/a", new[]
            {
                (MessageRole.User, "alpha xx", now),
            }));
            Seed(db, Fixture("s_late", "pi", "/w/a", new[]
            {
                (MessageRole.User, "xx alpha", now),
            }));
            // Recency tiebreak: byte-identical content, different timestamps -> newer first.
            Seed(db, Fixture("s_stale", "pi", "/w/a", new[]
            {
                (MessageRole.User, "alpha identical content here", Ts2020),
            }));
            Seed(db, Fixture("s_fresh", "pi", "/w/a", new[]
            {
                (MessageRole.User, "alpha identical content here", now),
            }));
        }

        using (var db = new SessionDatabase(dbPath))
        {
            var bySession = db.SearchExact("alpha", 100).ToDictionary(h => h.SessionId);
            Assert.True(bySession["s_short"].Score > bySession["s_long"].Score);
            Assert.True(bySession["s_early"].Score > bySession["s_late"].Score);
            Assert.True(bySession["s_fresh"].Score > bySession["s_stale"].Score);
            // Full ordering sanity: the two strongest signals top the list.
            var ordered = db.SearchExact("alpha", 100).Select(h => h.SessionId).ToList();
            Assert.Equal("s_short", ordered[0]);
        }
    }

    [Fact]
    public void Ranking_Fts_RecencyBreaksEqualRankTies()
    {
        var dbPath = DbPath("rank_fts.db");
        using (var db = new SessionDatabase(dbPath))
        {
            // Byte-identical rows -> identical BM25 ranks -> the recency tiebreak decides.
            Seed(db, Fixture("s_stale", "pi", "/w/a", new[]
            {
                (MessageRole.User, "quasar synchronizer zebra", Ts2020),
            }));
            Seed(db, Fixture("s_fresh", "pi", "/w/a", new[]
            {
                (MessageRole.User, "quasar synchronizer zebra", NowMs()),
            }));
        }
        Assert.Equal(2, RawCount(dbPath, "messages"));

        using (var db = new SessionDatabase(dbPath))
        {
            var hits = db.SearchFts("quasar synchronizer");
            Assert.Equal(2, hits.Count);
            Assert.Equal("s_fresh", hits[0].SessionId);
            Assert.Equal("s_stale", hits[1].SessionId);
        }
    }

    [Fact]
    public void Ranking_Hybrid_RecencyBoostIsSmall_NeverOverridesRank()
    {
        var dbPath = DbPath("rank_hyb.db");
        var now = NowMs();
        using (var db = new SessionDatabase(dbPath))
        {
            // Strong but stale: exact keyword + vector match on every query token.
            Seed(db, Fixture("s_strong_old", "pi", "/w/a", new[]
            {
                (MessageRole.User, "nvidia driver install failed on windows", Ts2020),
            }));
            // Weak but fresh: shares one query token only.
            Seed(db, Fixture("s_weak_new", "pi", "/w/a", new[]
            {
                (MessageRole.User, "driver safety briefing for kitchen staff", now),
            }));
            // Identical pair: exact RRF tie -> boost (default on) picks the newer.
            Seed(db, Fixture("s_dup_old", "pi", "/w/a", new[]
            {
                (MessageRole.User, "quasar synchronizer zebra", Ts2020),
            }));
            Seed(db, Fixture("s_dup_new", "pi", "/w/a", new[]
            {
                (MessageRole.User, "quasar synchronizer zebra", now),
            }));
        }

        using (var db = new SessionDatabase(dbPath))
        {
            // Small weight: a full rank step beats any recency gap.
            var hits = db.SearchHybrid("nvidia driver install");
            var bySession = hits.ToDictionary(h => h.SessionId);
            Assert.Contains("s_strong_old", bySession.Keys);
            Assert.Contains("s_weak_new", bySession.Keys);
            Assert.True(bySession["s_strong_old"].Score > bySession["s_weak_new"].Score);

            // Tie case: default (boost on) orders newer first with strictly greater score.
            var dupOn = db.SearchHybrid("quasar synchronizer").Where(h => h.SessionId.StartsWith("s_dup_")).ToList();
            Assert.Equal(2, dupOn.Count);
            Assert.Equal("s_dup_new", dupOn[0].SessionId);
            Assert.True(dupOn[0].Score > dupOn[1].Score);

            // Boost off: still returns both, and the newer session's score drops
            // (proving the boost was applied when on) without changing membership.
            var dupOff = db.SearchHybrid("quasar synchronizer", recencyBoost: false)
                .Where(h => h.SessionId.StartsWith("s_dup_")).ToList();
            Assert.Equal(2, dupOff.Count);
            var offNew = dupOff.First(h => h.SessionId == "s_dup_new");
            Assert.True(dupOn[0].Score > offNew.Score);
        }
    }

    // ---------- 4. FTS raw mode ----------

    [Fact]
    public void RawFts_PhraseQuery_MatchesAdjacencyOnly()
    {
        var dbPath = DbPath("raw_phrase.db");
        var now = NowMs();
        using (var db = new SessionDatabase(dbPath))
        {
            Seed(db, Fixture("s_adj", "pi", "/w/a", new[]
            {
                (MessageRole.User, "nvidia driver install failed", now),
            }));
            Seed(db, Fixture("s_sep", "pi", "/w/a", new[]
            {
                (MessageRole.User, "nvidia chef driver recipe", now),
            }));
        }

        using (var db = new SessionDatabase(dbPath))
        {
            // Raw phrase is adjacency- AND order-sensitive: only the adjacent session hits.
            var raw = db.SearchFtsRaw("\"driver install\"");
            Assert.Single(raw);
            Assert.Equal("s_adj", raw[0].SessionId);
            // Reversed phrase matches nothing raw...
            Assert.Empty(db.SearchFtsRaw("\"install driver\""));
            // ...while the sanitized token-AND is order-free and still finds it.
            var san = db.SearchFts("install driver");
            Assert.Single(san);
            Assert.Equal("s_adj", san[0].SessionId);
            Assert.Null(db.LastError);
        }
    }

    [Fact]
    public void RawFts_OrAndNearAndColumnFilters_Work()
    {
        var dbPath = DbPath("raw_ops.db");
        var now = NowMs();
        using (var db = new SessionDatabase(dbPath))
        {
            Seed(db, Fixture("s_pasta", "pi", "/w/a", new[]
            {
                (MessageRole.User, "pasta recipe with basil", now),
            }));
            Seed(db, Fixture("s_gpu", "pi", "/w/a", new[]
            {
                (MessageRole.User, "nvidia driver install", now),
            }));
        }

        using (var db = new SessionDatabase(dbPath))
        {
            Assert.Equal(new[] { "s_gpu", "s_pasta" }, IdSet(db.SearchFtsRaw("pasta OR nvidia")));
            Assert.Equal(new[] { "s_gpu" }, IdSet(db.SearchFtsRaw("NEAR(nvidia driver)")));
            Assert.Equal(new[] { "s_pasta" }, IdSet(db.SearchFtsRaw("content:pasta")));
            Assert.Null(db.LastError);
        }
    }

    [Fact]
    public void RawFts_SyntaxError_SetsLastError_NeverThrows()
    {
        var dbPath = DbPath("raw_err.db");
        var now = NowMs();
        using (var db = new SessionDatabase(dbPath))
        {
            Seed(db, Fixture("s1", "pi", "/w/a", new[]
            {
                (MessageRole.User, "hello world from the raw test", now),
            }));
            Assert.Null(db.LastError);

            // Unbalanced quote is an FTS5 syntax error: empty list + LastError, no throw.
            var bad = db.SearchFtsRaw("\"unclosed phrase");
            Assert.Empty(bad);
            Assert.NotNull(db.LastError);
            Assert.StartsWith("Invalid FTS query", db.LastError);

            // The sanitized path treats the same input as harmless text (never throws).
            // (LastError is sticky by design, so no Null assertion after the raw error.)
            var safe = db.SearchFts("\"unclosed phrase");
            Assert.NotNull(safe);
        }
    }

    // ---------- 5. Vacuum ----------

    [Fact]
    public void Vacuum_ReclaimsSpace_AndKeepsIndexSearchable()
    {
        var dbPath = DbPath("vac.db");
        var now = NowMs();
        using (var db = new SessionDatabase(dbPath))
        {
            Seed(db, Fixture("s_keep", "pi", "/w/a", new[]
            {
                (MessageRole.User, "unique zirconium keepme phrase", now),
            }));
            Seed(db, Fixture("s_drop", "pi", "/w/a", new[]
            {
                (MessageRole.User, "unique zirconium dropme phrase " + new string('x', 4000), now),
            }));
            Assert.Equal(2, RawCount(dbPath, "messages"));
            Assert.Equal(1, db.DeleteSessionsByIds(new[] { "s_drop" }));
            Assert.Equal(1, RawCount(dbPath, "messages"));

            var (before, after) = db.VacuumAndOptimize();
            // P4: file-level egress — vacuumed file exists and did not grow.
            Assert.True(before > 0);
            Assert.True(after > 0);
            Assert.True(after <= before, $"vacuum grew the file: {before} -> {after}");

            // P5: the index still serves every engine after the rebuild.
            Assert.Contains(db.SearchFts("zirconium"), h => h.SessionId == "s_keep");
            Assert.Contains(db.SearchExact("keepme"), h => h.SessionId == "s_keep");
            Assert.Contains(db.SearchSemantic("zirconium keepme"), h => h.SessionId == "s_keep");
            Assert.Contains(db.SearchHybrid("zirconium keepme"), h => h.SessionId == "s_keep");

            // Invalid page size is rejected without touching the file.
            var (b2, a2) = db.VacuumAndOptimize(pageSize: 1234);
            Assert.Equal((0L, 0L), (b2, a2));
            Assert.NotNull(db.LastError);
        }
    }
}
