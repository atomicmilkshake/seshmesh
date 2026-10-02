using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Casr.Core.Models;
using Casr.Core.Providers;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Casr.Core.Tests;

public class HermesImportTests
{
    private static CanonicalSession BuildSession(string workspace)
    {
        var baseMs = 1789128000000L; // fixed epoch ms for deterministic timestamps
        // Hermes session titles are UNIQUE-indexed, and the importer derives the title from the
        // first user message. A fixed fixture string therefore collides on the second live run
        // (the duplicate title is rejected and stored NULL). Keep the prompt unique per run so
        // the test is repeatable against a live state.db.
        var marker = $"casr-hermes-{Guid.NewGuid():N}";
        return new CanonicalSession
        {
            SessionId = Guid.NewGuid().ToString(),
            ProviderSlug = "pi",
            Workspace = workspace,
            Title = "Hermes Import Test Session",
            ModelName = "gpt-4o",
            Messages = new()
            {
                new CanonicalMessage
                {
                    Index = 0,
                    Role = MessageRole.User,
                    Content = $"How do I list files in bash? ({marker})",
                    TimestampEpochMs = baseMs
                },
                new CanonicalMessage
                {
                    Index = 1,
                    Role = MessageRole.Assistant,
                    Content = "Use the `ls` command.",
                    TimestampEpochMs = baseMs + 1000,
                    ToolCalls = new()
                    {
                        new ToolCall { Id = "call_1", Name = "bash", ArgumentsJson = "{\"cmd\":\"ls\"}" }
                    }
                },
                new CanonicalMessage
                {
                    Index = 2,
                    Role = MessageRole.Tool,
                    Content = "file1.txt",
                    TimestampEpochMs = baseMs + 2000,
                    ToolResults = new()
                    {
                        new ToolResult { CallId = "call_1", Content = "file1.txt", IsError = false }
                    }
                },
                new CanonicalMessage
                {
                    Index = 3,
                    Role = MessageRole.User,
                    Content = "Thanks!",
                    TimestampEpochMs = baseMs + 3000
                }
            }
        };
    }

    [Fact]
    public void BuildClaudeCodeJsonl_EmitsSpikeValidatedShape()
    {
        var workspace = Path.GetTempPath();
        var session = BuildSession(workspace);
        var sessionId = Guid.NewGuid().ToString();

        var (lines, droppedMessages, droppedToolArtifacts) =
            HermesProvider.BuildClaudeCodeJsonl(session, sessionId, workspace);

        // Tool message folded into the preceding assistant turn: 2 user + 1 assistant kept
        Assert.Equal(3, lines.Count);
        Assert.Equal(1, droppedMessages);
        Assert.Equal(0, droppedToolArtifacts); // tool call + result synthesized into markdown, none orphaned

        string? parent = null;
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            var type = root.GetProperty("type").GetString();
            Assert.True(type == "user" || type == "assistant");
            Assert.Equal(sessionId, root.GetProperty("sessionId").GetString());
            Assert.Equal(workspace, root.GetProperty("cwd").GetString());

            // ISO-8601 UTC timestamp
            var ts = root.GetProperty("timestamp").GetString();
            Assert.False(string.IsNullOrWhiteSpace(ts));
            Assert.True(DateTimeOffset.TryParse(ts, out var parsed));
            Assert.Equal(TimeSpan.Zero, parsed.Offset);

            // uuid/parentUuid DAG: first has null parent, rest chain
            var uuid = root.GetProperty("uuid").GetString();
            Assert.False(string.IsNullOrWhiteSpace(uuid));
            Assert.True(Guid.TryParse(uuid, out _));
            if (parent == null)
            {
                Assert.Equal(JsonValueKind.Null, root.GetProperty("parentUuid").ValueKind);
            }
            else
            {
                Assert.Equal(parent, root.GetProperty("parentUuid").GetString());
            }
            parent = uuid;

            // message{role, content} matches outer type and canonical content
            var msg = root.GetProperty("message");
            Assert.Equal(type, msg.GetProperty("role").GetString());
            Assert.False(string.IsNullOrWhiteSpace(msg.GetProperty("content").GetString()));
        }

        // Exact roles and contents in order (the first prompt carries a per-run uniqueness marker).
        // The assistant turn carries the tool call + result synthesized as markdown blocks.
        var roles = lines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("type").GetString()).ToArray();
        Assert.Equal(new[] { "user", "assistant", "user" }, roles);
        var contents = lines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("message").GetProperty("content").GetString()).ToArray();
        Assert.StartsWith("How do I list files in bash?", contents[0]);
        Assert.StartsWith("Use the `ls` command.", contents[1]);
        Assert.Contains("bash", contents[1]);
        Assert.Contains("file1.txt", contents[1]);
        Assert.Equal("Thanks!", contents[2]);
    }

    [Fact]
    public void ParseImportedSessionId_ParsesRealImporterOutput()
    {
        var sample = "✓ Imported Claude Code session as 20260914_084859_bcd314\r\n  Continue it with:  hermes --resume 20260914_084859_bcd314\r\n";
        Assert.Equal("20260914_084859_bcd314", HermesProvider.ParseImportedSessionId(sample));
        Assert.Equal("abc123", HermesProvider.ParseImportedSessionId("hermes --resume abc123"));
        Assert.Null(HermesProvider.ParseImportedSessionId("no id here"));
        Assert.Null(HermesProvider.ParseImportedSessionId(null));
    }

    [Trait("Category", "LiveSystem")]
    [Fact]
    public void HermesProvider_WriteSession_ImportsIntoLiveStateDb()
    {
        var provider = new HermesProvider();
        var workspace = Path.GetTempPath();
        var session = BuildSession(workspace);

        var written = provider.WriteSession(session, new WriteOptions { Force = true });

        try
        {
        Assert.False(string.IsNullOrWhiteSpace(written.SessionId));
        Assert.Equal($"hermes --resume {written.SessionId}", written.ResumeCommand);
        Assert.Equal(workspace, written.Workspace);
        Assert.NotEmpty(written.Warnings); // dropped tool artifacts warning

        // Verify the imported session is queryable in the real state.db
        var dbPath = HermesProvider.GetStateDbPath();
        Assert.True(File.Exists(dbPath));

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();

        using (var sCmd = conn.CreateCommand())
        {
            sCmd.CommandText = "SELECT id, title, source FROM sessions WHERE id = @id LIMIT 1;";
            sCmd.Parameters.AddWithValue("@id", written.SessionId);
            using var reader = sCmd.ExecuteReader();
            Assert.True(reader.Read(), "Imported session row not found in state.db");
            var title = reader.IsDBNull(1) ? null : reader.GetString(1);
            Assert.Contains("How do I list files in bash?", title ?? string.Empty);
        }

        using (var mCmd = conn.CreateCommand())
        {
            mCmd.CommandText = "SELECT role, content FROM messages WHERE session_id = @id ORDER BY timestamp ASC, id ASC;";
            mCmd.Parameters.AddWithValue("@id", written.SessionId);
            using var reader = mCmd.ExecuteReader();
            var rows = new System.Collections.Generic.List<(string Role, string Content)>();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1)));
            }
            Assert.Equal(3, rows.Count); // tool message folded into the assistant turn
            Assert.Equal("user", rows[0].Role);
            Assert.StartsWith("How do I list files in bash?", rows[0].Content);
            Assert.Equal("assistant", rows[1].Role);
            Assert.StartsWith("Use the `ls` command.", rows[1].Content);
            Assert.Contains("file1.txt", rows[1].Content); // synthesized tool result block
            Assert.Equal("user", rows[2].Role);
            Assert.Equal("Thanks!", rows[2].Content);
        }
        }
        finally
        {
            // The import lands in the user's real state.db; remove it so repeated runs do not
            // accumulate "Imported from Claude Code: ..." junk in their session list.
            LiveStoreCleanup.DeleteHermesSession(written.SessionId);
        }
    }
}
