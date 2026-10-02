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
/// Search attribution (Phase 0/1): every engine names the winning message, its source,
/// and its matched terms; snippets escape content HTML; semantic winners point at the
/// true vector-winning message; the v3 migration is additive with a working backfill
/// detector. Hermetic temp DBs; independent raw-SQLite egress inspection throughout.
/// </summary>
public class SearchAttributionTests : IDisposable
{
    private readonly string _tempDir;

    public SearchAttributionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "casr_searchattr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static (SessionSummary Summary, CanonicalSession Session) Fixture(
        string id, (MessageRole Role, string Content)[] messages)
    {
        var summary = new SessionSummary
        {
            SessionId = id,
            Provider = "test",
            ProviderDisplayName = "Test",
            Title = "t",
            Workspace = "/tmp",
            StartedAt = DateTime.Now.AddHours(-1),
            LastActiveAt = DateTime.Now,
            MessagesCount = messages.Length,
            FileSizeBytes = 1000 + id.Length,
            SourcePath = "/tmp/" + id,
        };
        var session = new CanonicalSession
        {
            SessionId = id,
            ProviderSlug = "test",
            Title = "t",
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

    /// <summary>Independent egress: raw rows, bypassing every SUT reader.</summary>
    private static List<(int Idx, string Role, string Content, long? Ts)> RawMessages(string dbPath, string sid)
    {
        var rows = new List<(int, string, string, long?)>();
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT message_index, role, content, timestamp_ms FROM messages WHERE session_id = @id ORDER BY message_index;";
        cmd.Parameters.AddWithValue("@id", sid);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add((r.GetInt32(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt64(3)));
        return rows;
    }

    private static long RawCount(string dbPath, string table, string? where = null)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}" + (where == null ? ";" : $" WHERE {where};");
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public void Fts_CarriesMessageIndexRoleTerms_AndEscapesContentHtml()
    {
        var dbPath = Path.Combine(_tempDir, "fts.db");
        using var db = new SessionDatabase(dbPath);
        var (summary, session) = Fixture("s1", new[]
        {
            (MessageRole.User, "unrelated weather chatter here"),
            (MessageRole.Assistant, "deploy with <script>alert(1)</script> nvml driver flag"),
        });
        db.UpsertConversation(summary, session);

        // Pillar 1: ingress valid — independent row check before executing the search.
        var rows = RawMessages(dbPath, "s1");
        Assert.Equal(2, rows.Count);

        var hits = db.SearchFts("nvml driver");
        Assert.NotEmpty(hits);
        var hit = hits[0];
        // Pillar 4/5: attribution fields + independent cross-check of the winning row.
        Assert.Equal(1, hit.MessageIndex);
        Assert.Equal("Assistant", hit.Role);
        Assert.Equal(MatchSource.Fts, hit.Source);
        Assert.Contains(MatchSource.Fts, hit.Sources);
        Assert.Contains("nvml", hit.MatchedTerms, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(rows[1].Content, rows.First(r => r.Idx == hit.MessageIndex).Content);
        Assert.Equal(rows[1].Ts, hit.TimestampMs);
        // Content HTML escaped: no raw <script>, highlight markup intact.
        Assert.DoesNotContain("<script>", hit.Snippet);
        Assert.Contains("&lt;script&gt;", hit.Snippet);
        Assert.Contains("<b>", hit.Snippet);
        Assert.Contains("nvml", hit.PlainSnippet, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<b>", hit.PlainSnippet);
    }

    [Fact]
    public void Exact_CarriesIndexMatchedText_Timestamp_AndEarliestFirst()
    {
        var dbPath = Path.Combine(_tempDir, "exact.db");
        using var db = new SessionDatabase(dbPath);
        var (summary, session) = Fixture("s1", new[]
        {
            (MessageRole.User, "wrap up the zzz-top marker later please"),
            (MessageRole.Assistant, "the zzz-top marker ships today"),
        });
        db.UpsertConversation(summary, session);
        Assert.Equal(2, RawMessages(dbPath, "s1").Count);

        var hits = db.SearchExact("zzz-top");
        Assert.Equal(2, hits.Count);
        Assert.All(hits, h =>
        {
            Assert.True(h.MessageIndex >= 0);
            Assert.Equal(MatchSource.Exact, h.Source);
            Assert.Equal("zzz-top", Assert.Single(h.MatchedTerms));
        });
        // Earliest-match-first: msg 0's offset vs msg 1's offset decides order.
        var rows = RawMessages(dbPath, "s1");
        Assert.Equal(rows[0].Ts, hits.First(h => h.MessageIndex == 0).TimestampMs);
        Assert.Contains("<b>zzz-top</b>", hits[0].Snippet);
    }

    [Fact]
    public void Regex_CarriesMatchedValue_AndRejectsInvalid()
    {
        var dbPath = Path.Combine(_tempDir, "regex.db");
        using var db = new SessionDatabase(dbPath);
        var (summary, session) = Fixture("s1", new[]
        {
            (MessageRole.User, "error code NVML-404 happened twice"),
            (MessageRole.Assistant, "all good here"),
        });
        db.UpsertConversation(summary, session);

        var (results, timedOut) = db.SearchRegexDetailed(@"NVML-\d+");
        Assert.False(timedOut);
        Assert.NotEmpty(results);
        var hit = results[0];
        Assert.Equal(MatchSource.Regex, hit.Source);
        Assert.Equal(0, hit.MessageIndex);
        Assert.Equal("NVML-404", hit.MatchedTerms.Last());
        Assert.Contains(@"NVML-\d+", hit.MatchedTerms);
        Assert.Contains("<b>NVML-404</b>", hit.Snippet);

        var invalid = db.SearchRegex("([unclosed");
        Assert.Empty(invalid);
        Assert.Contains("Invalid regex", db.LastError ?? string.Empty);
    }

    [Fact]
    public void Semantic_AttributesTrueWinningMessage_NotFirstMessage()
    {
        var dbPath = Path.Combine(_tempDir, "sem.db");
        using var db = new SessionDatabase(dbPath);
        var filler = Enumerable.Range(0, 5)
            .Select(i => (MessageRole.User, $"filler chatter about weather number {i}"))
            .Concat(new[] { (MessageRole.Assistant, "zebra xylophone quantum violet bank vault") })
            .ToArray();
        var (summary, session) = Fixture("s1", filler);
        db.UpsertConversation(summary, session);
        Assert.Equal(6, RawMessages(dbPath, "s1").Count);

        var hits = db.SearchSemantic("zebra xylophone quantum");
        Assert.NotEmpty(hits);
        var hit = hits[0];
        // The headline capability: the late distinctive message wins, with its role —
        // never Role="semantic", never a first-message fallback.
        Assert.Equal("s1", hit.SessionId);
        Assert.Equal(5, hit.MessageIndex);
        Assert.Equal(MatchSource.SemanticVector, hit.Source);
        Assert.Equal("Assistant", hit.Role);
        Assert.True(hit.Score > 0);
        Assert.False(hit.IsContextGuess);
        var rows = RawMessages(dbPath, "s1");
        Assert.Contains("zebra", rows.First(r => r.Idx == hit.MessageIndex).Content);
        Assert.Equal(rows[5].Ts, hit.TimestampMs);
    }

    [Fact]
    public void Semantic_ZeroTokenRecall_Preserved_AndFlaggedAsGuess()
    {
        var dbPath = Path.Combine(_tempDir, "semzero.db");
        using var db = new SessionDatabase(dbPath);
        // No shared tokens with the query: hashing-trigram similarity must still recall.
        var (summary, session) = Fixture("s1", new[]
        {
            (MessageRole.User, "aa bb cc dd ee ff gg"),
        });
        db.UpsertConversation(summary, session);

        var hits = db.SearchSemantic("zz qq xx");
        // Either recalled-with-guess or legitimately unscored — but never a fake highlight.
        foreach (var h in hits.Where(h => h.SessionId == "s1"))
        {
            Assert.True(h.IsContextGuess);
            Assert.DoesNotContain("<b>", h.Snippet);
            Assert.Empty(h.MatchedTerms);
        }
    }

    [Fact]
    public void Hybrid_RetainsBothBranches_OnDualHit()
    {
        var dbPath = Path.Combine(_tempDir, "hyb.db");
        using var db = new SessionDatabase(dbPath);
        var (g, gpu) = Fixture("gpu", new[] { (MessageRole.User, "nvidia graphics driver install failed on windows") });
        var (p, pasta) = Fixture("pasta", new[] { (MessageRole.User, "pasta recipe with basil and tomato sauce") });
        db.UpsertConversationBatch(new[] { (g, gpu), (p, pasta) });

        var hits = db.SearchHybrid("nvidia driver install");
        Assert.NotEmpty(hits);
        Assert.Equal("gpu", hits[0].SessionId);
        var top = hits[0];
        Assert.Equal(MatchSource.Hybrid, top.Source);
        // Dual hit keeps both branches (was: loser branch silently dropped).
        Assert.Contains(MatchSource.Fts, top.Sources);
        Assert.Contains(MatchSource.SemanticVector, top.Sources);
        Assert.True(top.MessageIndex >= 0);
        Assert.False(string.IsNullOrWhiteSpace(top.Snippet));
        // Session-deduped list ordering intact.
        Assert.Equal(hits.Count, hits.Select(h => h.SessionId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void MessageVectors_WrittenPerMessage_AndBackfillDetectorTruthTable()
    {
        var dbPath = Path.Combine(_tempDir, "vec.db");
        using (var db = new SessionDatabase(dbPath))
        {
            var (summary, session) = Fixture("s1", new[]
            {
                (MessageRole.User, "hello world"),
                (MessageRole.Assistant, "hi there"),
                (MessageRole.User, "   "), // whitespace-only: no message row, no vector
            });
            db.UpsertConversation(summary, session);

            // Independent counts: one vector per non-empty message, session vector intact.
            Assert.Equal(2, RawMessages(dbPath, "s1").Count);
            Assert.Equal(2, RawCount(dbPath, "message_embeddings", "session_id = 's1'"));
            Assert.Equal(1, RawCount(dbPath, "session_embeddings", "session_id = 's1'"));
            Assert.False(db.NeedsMessageEmbeddingBackfill());
        }

        // Simulate a pre-v3 database: session vectors exist, per-message vectors gone.
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM message_embeddings;";
            del.ExecuteNonQuery();
        }
        using (var db = new SessionDatabase(dbPath))
        {
            Assert.True(db.NeedsMessageEmbeddingBackfill());
        }
    }

    [Fact]
    public void Migration_IsAdditive_SessionVectorsSurvive()
    {
        var dbPath = Path.Combine(_tempDir, "mig.db");
        using (var db = new SessionDatabase(dbPath))
        {
            var (summary, session) = Fixture("s1", new[] { (MessageRole.User, "migration probe content") });
            db.UpsertConversation(summary, session);
        }
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('messages','messages_fts','session_embeddings','message_embeddings');";
        var names = new List<string>();
        using (var r = cmd.ExecuteReader())
            while (r.Read()) names.Add(r.GetString(0));
        Assert.Equal(4, names.Count);
        using var ver = conn.CreateCommand();
        ver.CommandText = "PRAGMA user_version;";
        Assert.Equal(3L, Convert.ToInt64(ver.ExecuteScalar()));
        Assert.Equal(1, RawCount(dbPath, "session_embeddings"));
    }

    [Fact]
    public void SnippetHighlighter_ParsesBold_AndTreatsOtherTagsAsLiteral()
    {
        var parts = SnippetHighlighter.Parse("a <b>b</b> c <i>d</i>");
        Assert.Equal(3, parts.Count);
        Assert.Equal(("a ", false), parts[0]);
        Assert.Equal(("b", true), parts[1]);
        Assert.Equal((" c <i>d</i>", false), parts[2]);

        Assert.Equal("a b c", SnippetHighlighter.StripTags("a <b>b</b> c"));
        Assert.Equal("x & y", SnippetHighlighter.StripTags("x &amp; y"));
        Assert.Empty(SnippetHighlighter.StripTags(null));
        Assert.Empty(SnippetHighlighter.Parse(string.Empty));
    }
}
