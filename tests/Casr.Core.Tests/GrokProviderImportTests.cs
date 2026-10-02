using System;
using System.IO;
using System.Text.Json;
using Casr.Core.Models;
using Casr.Core.Providers;
using Xunit;

namespace Casr.Core.Tests;

/// Regression: grok updates.jsonl chunks carry content as an OBJECT
/// ({"type":"text","text":"..."}), not a string. The old parser called
/// GetString() on it -> null -> every chunk dropped -> ReadSession returned
/// ZERO messages. Also: tool_call updates have no "name" (use title/kind)
/// and rawInput is an object, and a trailing chunk must flush at EOF.
public class GrokProviderImportTests : IDisposable
{
    private readonly string _tempDir;

    public GrokProviderImportTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"casr_grok_import_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Environment.SetEnvironmentVariable("GROK_HOME", null);
            Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", null);
            Environment.SetEnvironmentVariable("PI_CODING_AGENT_SESSION_DIR", null);
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    private void Chunk(string dir, string id, string variant, string kind, string content, string? toolTitle = null)
    {
        var line = JsonSerializer.Serialize(new
        {
            timestamp = 1789090367,
            method = "session/update",
            @params = new
            {
                sessionId = id,
                update = new
                {
                    sessionUpdate = kind,
                    content = new
                    {
                        type = "text",
                        text = content
                    },
                    title = toolTitle
                }
            }
        });
        File.AppendAllText(Path.Combine(dir, "updates.jsonl"), line + "\n");
    }

    [Fact]
    public void ReadSession_ParsesObjectContent_ToolCallsAndEofFlush()
    {
        var sessions = Path.Combine(_tempDir, "sessions");
        var dir = Path.Combine(sessions, "C%3A%5Ctest", "11111111-1111-4111-8111-111111111111");
        Directory.CreateDirectory(dir);
        var updatesPath = Path.Combine(dir, "updates.jsonl");

        // 1. user chunk with OBJECT content
        Chunk(dir, "11111111-1111-4111-8111-111111111111", "user_message_chunk", "user_message_chunk", "Build the high-speed resumer please");
        // 2. assistant chunk (role change -> flushes user)
        Chunk(dir, "11111111-1111-4111-8111-111111111111", "agent_message_chunk", "agent_message_chunk", "On it. Compiling now.");
        // 3. second user chunk (flushes assistant)
        Chunk(dir, "11111111-1111-4111-8111-111111111111", "user_message_chunk", "user_message_chunk", "Add a dark theme too");
        // 4. tool_call (title-based name + object rawInput)
        File.AppendAllText(updatesPath,
            "{\"method\":\"session/update\",\"params\":{\"sessionId\":\"11111111-1111-4111-8111-111111111111\",\"update\":{\"sessionUpdate\":\"tool_call\",\"title\":\"Code edit:\",\"kind\":\"edit\",\"rawInput\":{\"variant\":\"Edit\",\"path\":\"Main.cs\"}}}}\n");
        // 5. trailing assistant chunk -> must flush at EOF
        Chunk(dir, "11111111-1111-4111-8111-111111111111", "agent_message_chunk", "agent_message_chunk", "Done!");

        Environment.SetEnvironmentVariable("GROK_HOME", _tempDir);
        var provider = new GrokProvider();

        var session = provider.ReadSession(updatesPath);

        // 4 content-bearing messages + 1 tool message
        Assert.Equal(5, session.Messages.Count);

        Assert.Equal(MessageRole.User, session.Messages[0].Role);
        Assert.Equal("Build the high-speed resumer please", session.Messages[0].Content);

        Assert.Equal(MessageRole.Assistant, session.Messages[1].Role);
        Assert.Equal("On it. Compiling now.", session.Messages[1].Content);

        // tool_call lands before the pending user chunk flushes: name from title, args as JSON
        Assert.Equal(MessageRole.Tool, session.Messages[2].Role);
        Assert.Single(session.Messages[2].ToolCalls);
        Assert.Equal("Code edit:", session.Messages[2].ToolCalls[0].Name);
        Assert.Contains("Edit", session.Messages[2].ToolCalls[0].ArgumentsJson);

        // The pending user chunk (flushed when the trailing assistant chunk arrives)
        Assert.Equal(MessageRole.User, session.Messages[3].Role);
        Assert.Equal("Add a dark theme too", session.Messages[3].Content);

        // EOF flush of the trailing assistant chunk
        Assert.Equal(MessageRole.Assistant, session.Messages[4].Role);
        Assert.Equal("Done!", session.Messages[4].Content);

        // title derived from first user message
        Assert.Equal("Build the high-speed resumer please", session.Title);
    }

    [Fact]
    public void ReadSession_PrefersChatHistory_WhenAvailable()
    {
        var sessions = Path.Combine(_tempDir, "sessions");
        var dir = Path.Combine(sessions, "J%3A%5Ctest", "22222222-2222-4222-8222-222222222222");
        Directory.CreateDirectory(dir);

        // updates.jsonl has stale/noisy content
        File.WriteAllText(Path.Combine(dir, "updates.jsonl"), "{\"timestamp\":123}\n");

        // chat_history.jsonl has structured conversation
        var chatLines = new[]
        {
            "{\"type\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"<user_query>\\nPlease refresh this with code\\n</user_query>\"}]}",
            "{\"type\":\"reasoning\",\"id\":\"rs_1\",\"summary\":[{\"type\":\"summary_text\",\"text\":\"Checking codebase before editing\"}]}",
            "{\"type\":\"assistant\",\"content\":\"I'll check the files first.\",\"tool_calls\":[{\"id\":\"call-abc-1\",\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\\\"main.py\\\"}\"}]}",
            "{\"type\":\"tool_result\",\"tool_call_id\":\"call-abc-1\",\"content\":\"print('hello')\"}"
        };
        File.WriteAllLines(Path.Combine(dir, "chat_history.jsonl"), chatLines);

        Environment.SetEnvironmentVariable("GROK_HOME", _tempDir);
        var provider = new GrokProvider();
        var session = provider.ReadSession(Path.Combine(dir, "summary.json"));

        Assert.Equal(3, session.Messages.Count);

        // 1. User message with <user_query> unwrapped
        Assert.Equal(MessageRole.User, session.Messages[0].Role);
        Assert.Equal("Please refresh this with code", session.Messages[0].Content);

        // 2. Assistant message with attached reasoning and tool_calls
        Assert.Equal(MessageRole.Assistant, session.Messages[1].Role);
        Assert.Equal("I'll check the files first.", session.Messages[1].Content);
        Assert.True(session.Messages[1].Extra.ContainsKey("thinking"));
        Assert.Equal("Checking codebase before editing", session.Messages[1].Extra["thinking"]);
        Assert.Single(session.Messages[1].ToolCalls);
        Assert.Equal("call-abc-1", session.Messages[1].ToolCalls[0].Id);
        Assert.Equal("read_file", session.Messages[1].ToolCalls[0].Name);

        // 3. Tool result with matching CallId
        Assert.Equal(MessageRole.Tool, session.Messages[2].Role);
        Assert.Single(session.Messages[2].ToolResults);
        Assert.Equal("call-abc-1", session.Messages[2].ToolResults[0].CallId);
        Assert.Equal("print('hello')", session.Messages[2].ToolResults[0].Content);
    }

    [Fact]
    public void ReadSession_ChatHistory_TimestampWhenPresent_AndToolAuthor()
    {
        var dir = Path.Combine(_tempDir, "sessions", "J%3A%5Ctest", "33333333-3333-4333-8333-333333333333");
        Directory.CreateDirectory(dir);
        var lines = new[]
        {
            "{\"type\":\"user\",\"timestamp\":1700000000,\"content\":\"Hello from the fixture\"}",
            "{\"type\":\"assistant\",\"content\":\"No clock on this line.\"}",
            "{\"type\":\"tool_call\",\"name\":\"read_file\",\"timestamp\":1700000002,\"arguments\":\"{}\"}",
            "{\"type\":\"tool_result\",\"name\":\"read_file\",\"tool_call_id\":\"call-1\",\"timestamp\":1700000003000,\"content\":\"file body\"}",
            "{\"type\":\"tool_result\",\"tool_call_id\":\"call-2\",\"content\":\"unnamed result\"}"
        };
        File.WriteAllLines(Path.Combine(dir, "chat_history.jsonl"), lines);

        Environment.SetEnvironmentVariable("GROK_HOME", _tempDir);
        var session = new GrokProvider().ReadSession(Path.Combine(dir, "summary.json"));

        Assert.Equal(1700000000000L, session.Messages[0].TimestampEpochMs);
        Assert.Equal("user", session.Messages[0].Author);
        Assert.Null(session.Messages[1].TimestampEpochMs);
        Assert.Equal("read_file", session.Messages[2].Author);
        Assert.Equal(1700000002000L, session.Messages[2].TimestampEpochMs);
        Assert.Equal("read_file", session.Messages[3].Author);
        Assert.Equal(1700000003000L, session.Messages[3].TimestampEpochMs);
        Assert.Equal("tool", session.Messages[4].Author);
        Assert.Null(session.Messages[4].TimestampEpochMs);
    }

    [Fact]
    public void ReadSession_Updates_ToolAuthorAndSecondTimestamp()
    {
        var dir = Path.Combine(_tempDir, "sessions", "C%3A%5Ctest", "44444444-4444-4444-8444-444444444444");
        Directory.CreateDirectory(dir);
        Chunk(dir, "44444444-4444-4444-8444-444444444444", "user_message_chunk", "user_message_chunk", "Count the crates");
        File.AppendAllText(Path.Combine(dir, "updates.jsonl"),
            "{\"timestamp\":1787364708,\"method\":\"session/update\",\"params\":{\"update\":{\"sessionUpdate\":\"tool_call\",\"title\":\"Code edit:\",\"kind\":\"edit\",\"rawInput\":{}}}}\n");

        Environment.SetEnvironmentVariable("GROK_HOME", _tempDir);
        var session = new GrokProvider().ReadSession(Path.Combine(dir, "updates.jsonl"));

        var user = session.Messages.Single(m => m.Role == MessageRole.User);
        Assert.Equal(1789090367000L, user.TimestampEpochMs);
        var tool = session.Messages.Single(m => m.Role == MessageRole.Tool);
        Assert.Equal("Code edit:", tool.Author);
        Assert.Equal(1787364708000L, tool.TimestampEpochMs);
    }

    [Fact]
    public void PiProvider_WriteSession_EmitsNoUtf8Bom()
    {
        // Canonical sessions-dir variable (what PiProvider.SessionRoots() resolves):
        // never PI_CODING_AGENT_DIR here, so writes cannot escape _tempDir.
        var piSessions = Path.Combine(_tempDir, "pi_home", "sessions");
        Directory.CreateDirectory(piSessions);
        Environment.SetEnvironmentVariable("PI_CODING_AGENT_SESSION_DIR", piSessions);
        try
        {
            var provider = new PiProvider();
            var sample = new CanonicalSession
            {
                SessionId = Guid.NewGuid().ToString(),
                Title = "No BOM Test",
                Workspace = _tempDir,
                Messages = new List<CanonicalMessage>
                {
                    new CanonicalMessage { Role = MessageRole.User, Content = "Hello" },
                    new CanonicalMessage { Role = MessageRole.Assistant, Content = "World" }
                }
            };

            var written = provider.WriteSession(sample, new WriteOptions { Force = true });
            Assert.NotEmpty(written.Paths);

            var filePath = written.Paths[0];
            Assert.StartsWith(_tempDir, filePath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(filePath));

            var bytes = File.ReadAllBytes(filePath);
            Assert.True(bytes.Length >= 3);

            // UTF-8 BOM is 0xEF, 0xBB, 0xBF. First character of JSON must be '{' (0x7B)
            Assert.False(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "Pi session file must not contain a UTF-8 BOM");
            Assert.Equal((byte)'{', bytes[0]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PI_CODING_AGENT_SESSION_DIR", null);
        }
    }
}