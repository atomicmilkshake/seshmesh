using System;
using System.IO;
using System.Text.Json;
using Casr.Core.Models;
using Casr.Core.Providers;
using Xunit;

namespace Casr.Core.Tests;

/// <summary>
/// B2b. Live shape, with the user's words removed: a planner call, a MODEL
/// line whose type is the tool name, GENERIC prose, and a SYSTEM_MESSAGE
/// that must stay System. SYSTEM/TOOL_OUTPUT remains the writer contract.
/// </summary>
public class AntigravityToolResultTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _geminiHome;

    public AntigravityToolResultTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"casr_agy_tool_{Guid.NewGuid():N}");
        _geminiHome = Path.Combine(_tempDir, "mock_gemini");
        Directory.CreateDirectory(Path.Combine(_geminiHome, "antigravity-cli", "cache"));
        Directory.CreateDirectory(Path.Combine(_geminiHome, "antigravity-cli", "conversations"));
        Environment.SetEnvironmentVariable("GEMINI_HOME", _geminiHome);
        AntigravityProvider.InvalidateCache();
    }

    public void Dispose()
    {
        try
        {
            Environment.SetEnvironmentVariable("GEMINI_HOME", null);
            AntigravityProvider.InvalidateCache();
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    [Fact]
    public void ReadSession_MapsLiveToolLine_AndLeavesGenericAndSystemMessage()
    {
        var id = "agy-tool-fixture";
        var dir = Path.Combine(_geminiHome, "antigravity-cli", "brain", id, ".system_generated", "logs");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "transcript.jsonl");
        var lines = new object[]
        {
            new { step_index = 0, source = "USER_EXPLICIT", type = "USER_INPUT", created_at = "2026-09-01T10:01:00Z", content = "Please look at the fixture file." },
            new
            {
                step_index = 1,
                source = "MODEL",
                type = "PLANNER_RESPONSE",
                status = "DONE",
                created_at = "2026-09-01T10:02:00Z",
                tool_calls = new[] { new { name = "view_file", args = new { DirectoryPath = "C:\\fixture" } } }
            },
            new { step_index = 2, source = "MODEL", type = "VIEW_FILE", status = "DONE", created_at = "2026-09-01T10:03:00Z", content = "FIXTURE-TOOL-BODY" },
            new { step_index = 3, source = "MODEL", type = "GENERIC", status = "DONE", created_at = "2026-09-01T10:04:00Z", content = "The fixture file is in place." },
            new { step_index = 4, source = "SYSTEM", type = "SYSTEM_MESSAGE", status = "DONE", created_at = "2026-09-01T10:05:00Z", content = "The following is a <SYSTEM_MESSAGE> not actually sent by the user. Fixture notice." },
            new { step_index = 5, source = "SYSTEM", type = "TOOL_OUTPUT", status = "DONE", created_at = "2026-09-01T10:06:00Z", content = "WRITER-TOOL-BODY" }
        };
        File.WriteAllLines(path, lines.Select(line => JsonSerializer.Serialize(line)));

        var session = new AntigravityProvider().ReadSession(path);

        Assert.Equal(6, session.Messages.Count);
        Assert.Equal(MessageRole.User, session.Messages[0].Role);
        Assert.Equal(MessageRole.Assistant, session.Messages[1].Role);
        Assert.Equal("view_file", session.Messages[1].ToolCalls[0].Name);
        Assert.Empty(session.Messages[1].ToolResults);

        Assert.Equal(MessageRole.Tool, session.Messages[2].Role);
        Assert.Equal("VIEW_FILE", session.Messages[2].Author);
        Assert.Equal("FIXTURE-TOOL-BODY", session.Messages[2].Content);
        Assert.Equal("FIXTURE-TOOL-BODY", session.Messages[2].ToolResults[0].Content);

        Assert.Equal(MessageRole.Assistant, session.Messages[3].Role);
        Assert.Equal("The fixture file is in place.", session.Messages[3].Content);
        Assert.Empty(session.Messages[3].ToolResults);

        Assert.Equal(MessageRole.System, session.Messages[4].Role);
        Assert.Contains("<SYSTEM_MESSAGE>", session.Messages[4].Content);

        Assert.Equal(MessageRole.Tool, session.Messages[5].Role);
        Assert.Equal("WRITER-TOOL-BODY", session.Messages[5].Content);
    }
}
