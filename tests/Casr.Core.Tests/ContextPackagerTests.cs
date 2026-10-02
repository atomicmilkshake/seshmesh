using System;
using System.Collections.Generic;
using System.Linq;
using Casr.Core.Context.Capabilities;
using Casr.Core.Context.Models;
using Casr.Core.Context.Pipeline;
using Casr.Core.Models;
using Xunit;

namespace Casr.Core.Tests;

public class ContextPackagerTests
{
    [Fact]
    public void Normalize_ExtractsThinkingAndToolsIntoMessageParts()
    {
        var session = new CanonicalSession
        {
            SessionId = "test-session",
            ProviderSlug = "hermes",
            Messages = new List<CanonicalMessage>
            {
                new()
                {
                    Index = 0,
                    Role = MessageRole.User,
                    Content = "Please fix the bug in server.py"
                },
                new()
                {
                    Index = 1,
                    Role = MessageRole.Assistant,
                    Content = "I am looking at server.py",
                    Extra = new Dictionary<string, object?>
                    {
                        ["thinking"] = "I need to check line 42."
                    },
                    ToolCalls = new List<ToolCall>
                    {
                        new() { Id = "c1", Name = "read_file", ArgumentsJson = "{\"path\":\"server.py\"}" }
                    }
                },
                new()
                {
                    Index = 2,
                    Role = MessageRole.Tool,
                    Content = "def main(): pass",
                    ToolResults = new List<ToolResult>
                    {
                        new() { CallId = "c1", Content = "def main(): pass" }
                    },
                    Extra = new Dictionary<string, object?> { ["tool_name"] = "read_file" }
                }
            }
        };

        var context = ContextPackager.Default.Normalize(session);

        Assert.Equal("test-session", context.SessionId);
        Assert.Equal(3, context.Messages.Count);

        // Turn 0: User message
        var msg0 = context.Messages[0];
        Assert.Single(msg0.Parts);
        Assert.IsType<TextPart>(msg0.Parts[0]);

        // Turn 1: Assistant message with thinking, text, and tool call
        var msg1 = context.Messages[1];
        Assert.Equal(3, msg1.Parts.Count);
        Assert.IsType<ThinkingPart>(msg1.Parts[0]);
        Assert.Equal("I need to check line 42.", ((ThinkingPart)msg1.Parts[0]).ReasoningText);
        Assert.IsType<TextPart>(msg1.Parts[1]);
        Assert.IsType<ToolCallPart>(msg1.Parts[2]);

        // Turn 2: Tool message with tool result
        var msg2 = context.Messages[2];
        Assert.True(msg2.Parts.Count >= 1);
        var tr = Assert.Single(msg2.GetToolResults());
        Assert.Equal("read_file", tr.ToolName);
        Assert.Equal("def main(): pass", tr.Output);
    }

    [Fact]
    public void Package_ForGrok_SynthesizesToolsIntoMarkdown()
    {
        var session = new CanonicalSession
        {
            SessionId = "conv-123",
            ProviderSlug = "pi",
            Messages = new List<CanonicalMessage>
            {
                new() { Index = 0, Role = MessageRole.User, Content = "Run tests" },
                new()
                {
                    Index = 1,
                    Role = MessageRole.Assistant,
                    Content = "Running test suite",
                    ToolCalls = new List<ToolCall>
                    {
                        new() { Id = "c1", Name = "run_command", ArgumentsJson = "{\"cmd\":\"dotnet test\"}" }
                    }
                },
                new()
                {
                    Index = 2,
                    Role = MessageRole.Tool,
                    Content = "Passed: 98",
                    ToolResults = new List<ToolResult>
                    {
                        new() { CallId = "c1", Content = "Passed: 98" }
                    },
                    Extra = new Dictionary<string, object?> { ["tool_name"] = "run_command" }
                }
            }
        };

        var grokCaps = HarnessCapabilities.For("grok");
        var packaged = ContextPackager.Default.Package(session, grokCaps);

        Assert.Equal("grok", packaged.ProviderSlug);
        var asstMsg = packaged.Messages[1];
        Assert.Contains("[Tool Call: run_command]", asstMsg.Content);
        Assert.Contains("dotnet test", asstMsg.Content);

        var toolMsg = packaged.Messages[2];
        Assert.Contains("[Tool Result: run_command", toolMsg.Content);
        Assert.Contains("Passed: 98", toolMsg.Content);
    }

    [Fact]
    public void Package_ForPi_SetsRequiresUsageOnAssistant()
    {
        var session = new CanonicalSession
        {
            SessionId = "sess-pi",
            ProviderSlug = "hermes",
            Messages = new List<CanonicalMessage>
            {
                new() { Index = 0, Role = MessageRole.User, Content = "Hello" },
                new() { Index = 1, Role = MessageRole.Assistant, Content = "Hi there!" }
            }
        };

        var piCaps = HarnessCapabilities.For("pi");
        var packaged = ContextPackager.Default.Package(session, piCaps);

        var asst = packaged.Messages.First(m => m.Role == MessageRole.Assistant);
        Assert.True(asst.Extra.ContainsKey("requires_usage"));
        Assert.True((bool)asst.Extra["requires_usage"]!);
    }

    [Fact]
    public void Package_CompactLargeToolOutputs_OlderTurnsTruncatedGoalPreserved()
    {
        var session = new CanonicalSession
        {
            SessionId = "large-session",
            ProviderSlug = "antigravity",
            Messages = new List<CanonicalMessage>()
        };

        // Turn 0: Goal
        session.Messages.Add(new CanonicalMessage
        {
            Index = 0,
            Role = MessageRole.User,
            Content = "Goal: Build the system from scratch"
        });

        // 6 turns of heavy tool outputs
        for (int i = 1; i <= 6; i++)
        {
            session.Messages.Add(new CanonicalMessage
            {
                Index = session.Messages.Count,
                Role = MessageRole.User,
                Content = $"Step {i} instructions"
            });
            session.Messages.Add(new CanonicalMessage
            {
                Index = session.Messages.Count,
                Role = MessageRole.Assistant,
                Content = $"Working on step {i}"
            });
            session.Messages.Add(new CanonicalMessage
            {
                Index = session.Messages.Count,
                Role = MessageRole.Tool,
                Content = new string('X', 5000), // 5,000 chars per turn
                ToolResults = new List<ToolResult>
                {
                    new() { CallId = $"c_{i}", Content = new string('X', 5000) }
                },
                Extra = new Dictionary<string, object?> { ["tool_name"] = "heavy_scan" }
            });
        }

        // Target: small 2,000 token limit
        var tightCaps = new HarnessCapabilities
        {
            HarnessSlug = "tight",
            TokenContextLimit = 2_000,
            MaxToolResultCharsRecent = 2_000,
            MaxToolResultCharsOlder = 200,
            PreserveRecentTurnsCount = 2,
            ToolStyle = ToolPackagingStyle.Native
        };

        var packaged = ContextPackager.Default.Package(session, tightCaps);

        // Turn 0 goal should remain intact
        Assert.Equal("Goal: Build the system from scratch", packaged.Messages[0].Content);

        // An older tool turn (turn 1) should be compacted to <= 200 chars + notice
        var olderToolMsg = packaged.Messages.First(m => m.Role == MessageRole.Tool);
        var olderResult = olderToolMsg.ToolResults.First();
        Assert.True(olderResult.Content.Length < 1000);
        Assert.Contains("truncated", olderResult.Content);
    }
}
