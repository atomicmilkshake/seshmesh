using System;
using System.IO;
using System.Linq;
using Casr.Core.Models;
using Casr.Core.Storage;
using Xunit;

namespace Casr.Core.Tests;

/// <summary>
/// B1 keyword text, B3 stored-row count, B8 Cursor catalog label, B9 missing folder.
/// </summary>
public class IndexFidelityTests : IDisposable
{
    private readonly string _tempDir;

    public IndexFidelityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "casr_fidelity_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void Fts_IndexesToolResultNeedle_Once_AndLeavesMessageContent()
    {
        using var db = new SessionDatabase(Path.Combine(_tempDir, "fts.db"));
        const string needle = "NEEDLEB1ZEBRA9941";
        var summary = new SessionSummary
        {
            SessionId = "s-fts",
            Provider = "opencode",
            Title = "tool search",
            MessagesCount = 2,
            FileSizeBytes = 40,
            SourcePath = "mem",
            LastActiveAt = DateTime.Now
        };
        var session = new CanonicalSession
        {
            SessionId = "s-fts",
            Messages = new()
            {
                new CanonicalMessage
                {
                    Index = 0,
                    Role = MessageRole.Assistant,
                    Content = "[Called tool: read]",
                    ToolResults = new() { new ToolResult { Content = "prefix " + needle + " suffix" } }
                },
                new CanonicalMessage
                {
                    Index = 1,
                    Role = MessageRole.Assistant,
                    Content = "already has " + needle,
                    ToolResults = new() { new ToolResult { Content = needle } }
                }
            }
        };
        db.UpsertConversation(summary, session);

        var hits = db.SearchFts(needle);
        Assert.Equal(2, hits.Count);
        var stored = db.GetMessagesBySession("s-fts");
        Assert.Equal("[Called tool: read]", stored[0].Content);
        Assert.DoesNotContain(needle, stored[0].Content);
        Assert.Equal("already has " + needle, stored[1].Content);
        Assert.Equal("already has " + needle, SessionDatabase.FtsContent(stored[1].Content, new[]
        {
            new ToolResult { Content = needle }
        }));
    }

    [Fact]
    public void FtsContent_CapsEachResultAt2000_AndSkipsTextAlreadyInContent()
    {
        var head = "HEADTOKENB1 " + new string('x', 50);
        var tail = "TAILTOKENB1";
        var result = head + new string('y', 2000) + tail;
        var doc = SessionDatabase.FtsContent("[Called tool: read]", new[] { new ToolResult { Content = result } });
        Assert.Contains("HEADTOKENB1", doc);
        Assert.DoesNotContain(tail, doc);
        Assert.True(doc.Length <= "[Called tool: read]".Length + 1 + 2000);

        var already = SessionDatabase.FtsContent("see HEADTOKENB1 here", new[]
        {
            new ToolResult { Content = "HEADTOKENB1" }
        });
        Assert.Equal("see HEADTOKENB1 here", already);
    }

    [Fact]
    public void IndexedSession_DisplaysStoredRowCount_SkipGateKeepsProviderCount()
    {
        using var db = new SessionDatabase(Path.Combine(_tempDir, "count.db"));
        var when = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Local);
        var summary = new SessionSummary
        {
            SessionId = "s-count",
            Provider = "antigravity",
            Title = "steps vs rows",
            MessagesCount = 100,
            FileSizeBytes = 500,
            SourcePath = "mem",
            LastActiveAt = when
        };
        var session = new CanonicalSession
        {
            SessionId = "s-count",
            Messages = Enumerable.Range(0, 4).Select(i => new CanonicalMessage
            {
                Index = i,
                Role = MessageRole.User,
                Content = "row " + i
            }).ToList()
        };
        db.UpsertConversation(summary, session);

        var row = Assert.Single(db.GetRecentSessions());
        Assert.Equal(100, row.MessagesCount);
        Assert.Equal(4, row.StoredMessageCount);
        Assert.True(row.ContentIndexed);
        Assert.Equal("4", row.TurnsDisplay);
        Assert.Equal(4, row.DisplayedMessageCount);

        // EnsureContentIndexCore skips when content_messages_count equals the live summary count.
        var state = db.GetContentIndexStates()["s-count"];
        Assert.Equal(100, state.MessagesCount);
        Assert.Equal(state.MessagesCount, summary.MessagesCount);
    }

    [Fact]
    public void CursorWithoutContentStamp_IsCatalogOnly()
    {
        using var db = new SessionDatabase(Path.Combine(_tempDir, "cursor.db"));
        db.UpsertSummary(new SessionSummary
        {
            SessionId = "cur-1",
            Provider = "cursor",
            Title = "catalog",
            MessagesCount = 77,
            SourcePath = "cursor-db"
        });
        db.UpsertSummary(new SessionSummary
        {
            SessionId = "agy-1",
            Provider = "antigravity",
            Title = "not cursor",
            MessagesCount = 77,
            SourcePath = "agy"
        });

        var rows = db.GetRecentSessions().ToDictionary(s => s.SessionId);
        Assert.Equal("77 not indexed", rows["cur-1"].TurnsDisplay);
        Assert.False(rows["cur-1"].ContentIndexed);
        Assert.Equal("77", rows["agy-1"].TurnsDisplay);
    }

    [Fact]
    public void WorkspaceMissing_MarksOnlyANonEmptyPathThatIsNotADirectory()
    {
        var missingPath = Path.Combine(_tempDir, "no-such-folder");
        var missing = new SessionSummary { Workspace = missingPath };
        Assert.True(missing.WorkspaceMissing);
        Assert.Equal(missingPath + " (missing folder)", missing.WorkspaceDisplay);

        var present = new SessionSummary { Workspace = _tempDir };
        Assert.False(present.WorkspaceMissing);
        Assert.Equal(_tempDir, present.WorkspaceDisplay);

        var empty = new SessionSummary { Workspace = "   " };
        Assert.False(empty.WorkspaceMissing);
        Assert.Equal("—", empty.WorkspaceDisplay);
    }
}
