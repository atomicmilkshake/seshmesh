using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Casr.Core.Models;
using Casr.Core.Providers;
using Xunit;

namespace Casr.Core.Tests;

/// <summary>
/// Tests for OpenCodeProvider.WriteSession — history injection via `opencode session import`.
/// Test 1 is hermetic (pure serialization shape). Test 2 is LiveSystem: writes a real
/// session into the user's OpenCode DB via the CLI and round-trips it back out.
/// </summary>
public class OpenCodeImportTests
{
    private static CanonicalSession SampleSession(string workspace)
    {
        return new CanonicalSession
        {
            SessionId = "src-" + Guid.NewGuid().ToString("N")[..8],
            ProviderSlug = "pi",
            Workspace = workspace,
            Title = "CASR import test session",
            ModelName = "test-model-1",
            StartedAtEpochMs = 1789390000000,
            EndedAtEpochMs = 1789390060000,
            Messages = new List<CanonicalMessage>
            {
                new CanonicalMessage { Index = 0, Role = MessageRole.User, Content = "hello from casr user 1", TimestampEpochMs = 1789390001000 },
                new CanonicalMessage { Index = 1, Role = MessageRole.Assistant, Content = "assistant reply 1", TimestampEpochMs = 1789390002000 },
                new CanonicalMessage { Index = 2, Role = MessageRole.User, Content = "casr user 2 followup", TimestampEpochMs = 1789390003000 },
                new CanonicalMessage
                {
                    Index = 3,
                    Role = MessageRole.Tool,
                    Content = "tool output body",
                    TimestampEpochMs = 1789390004000,
                    ToolResults = new List<ToolResult> { new ToolResult { CallId = "c1", Content = "tool output body" } }
                }
            }
        };
    }

    [Fact]
    public void WriteSession_Serialization_MatchesExportShape()
    {
        var warnings = new List<string>();
        var json = OpenCodeProvider.SerializeToExportJson(SampleSession(Path.GetTempPath()), Path.GetTempPath(), warnings);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // info block — the 2.x importer requires projectID, cost, tokens, time and location
        Assert.True(root.TryGetProperty("info", out var info));
        Assert.True(info.TryGetProperty("id", out var idProp));
        Assert.StartsWith("ses_", idProp.GetString());
        Assert.Equal("CASR import test session", info.GetProperty("title").GetString());
        Assert.Equal("global", info.GetProperty("projectID").GetString());
        Assert.Equal(Path.GetTempPath(), info.GetProperty("location").GetProperty("directory").GetString());
        Assert.True(info.TryGetProperty("cost", out _));
        Assert.True(info.TryGetProperty("tokens", out var tokens));
        Assert.True(tokens.TryGetProperty("cache", out _));
        Assert.True(info.TryGetProperty("time", out var infoTime));
        Assert.True(infoTime.TryGetProperty("created", out _));
        Assert.True(info.TryGetProperty("model", out var model));
        Assert.True(model.TryGetProperty("id", out _));
        Assert.True(model.TryGetProperty("providerID", out _));

        // messages are flat in 2.x — { id, time, type, text } / { id, time, type, agent,
        // model, content[] } — instead of the 1.x { info: { role }, parts: [...] } nesting.
        Assert.True(root.TryGetProperty("messages", out var messages));
        Assert.Equal(JsonValueKind.Array, messages.ValueKind);
        var msgs = messages.EnumerateArray().ToList();
        Assert.Equal(3, msgs.Count);
        Assert.Contains(warnings, w => w.Contains("Dropped 1"));

        var types = msgs.Select(m => m.GetProperty("type").GetString()).ToList();
        Assert.Equal(new[] { "user", "assistant", "user" }, types);

        foreach (var m in msgs)
        {
            Assert.StartsWith("msg_", m.GetProperty("id").GetString());
            Assert.True(m.GetProperty("time").TryGetProperty("created", out var tc));
            Assert.Equal(JsonValueKind.Number, tc.ValueKind);
        }

        var assistant = msgs[1];
        Assert.Equal("build", assistant.GetProperty("agent").GetString());
        Assert.True(assistant.TryGetProperty("model", out _));
        var assistantText = assistant.GetProperty("content").EnumerateArray()
            .First(c => c.GetProperty("type").GetString() == "text")
            .GetProperty("text").GetString();
        Assert.Equal("assistant reply 1", assistantText);

        Assert.Equal("hello from casr user 1", msgs[0].GetProperty("text").GetString());
        Assert.Equal("casr user 2 followup", msgs[2].GetProperty("text").GetString());
    }

    [Fact]
    [Trait("Category", "LiveSystem")]
    public void WriteSession_ImportExport_RoundTrips_ThroughRealCli()
    {
        var provider = new OpenCodeProvider();
        Assert.True(provider.CanWrite);
        Assert.NotNull(OpenCodeProvider.FindOpenCodeCli());

        var session = SampleSession(Path.GetTempPath());
        var written = provider.WriteSession(session, new WriteOptions { Force = true });

        Assert.False(string.IsNullOrWhiteSpace(written.SessionId));
        Assert.StartsWith("ses_", written.SessionId);
        Assert.Equal($"opencode -s {written.SessionId}", written.ResumeCommand);

        // Export the imported session back out via the real CLI.
        var exportPath = Path.Combine(Path.GetTempPath(), $"casr_oc_export_{Guid.NewGuid():N}.json");
        try
        {
            var cli = OpenCodeProvider.FindOpenCodeCli()!;
            var psi = new ProcessStartInfo
            {
                FileName = cli,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            // OpenCode 2.x: `export` moved under `session`.
            psi.ArgumentList.Add("session");
            psi.ArgumentList.Add("export");
            psi.ArgumentList.Add(written.SessionId);
            using var proc = Process.Start(psi)!;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            Assert.True(proc.WaitForExit(60_000), "opencode export timed out");
            Assert.True(proc.ExitCode == 0, $"opencode export failed: {stderr}");

            File.WriteAllText(exportPath, stdout);
            Assert.True(File.Exists(exportPath) && new FileInfo(exportPath).Length > 100,
                "export output was empty");

            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            Assert.Equal(written.SessionId, root.GetProperty("info").GetProperty("id").GetString());
            var msgs = root.GetProperty("messages").EnumerateArray().ToList();

            // 2.x export messages are flat: user carries `text`, assistant carries `content[]`.
            var texts = msgs
                .Select(m =>
                {
                    var type = m.GetProperty("type").GetString();
                    if (type == "user") return m.GetProperty("text").GetString();
                    if (type != "assistant") return null;
                    return m.GetProperty("content").EnumerateArray()
                        .Where(c => c.GetProperty("type").GetString() == "text")
                        .Select(c => c.GetProperty("text").GetString())
                        .FirstOrDefault();
                })
                .Where(t => t != null)
                .ToList();
            Assert.Contains("hello from casr user 1", texts);
            Assert.Contains("assistant reply 1", texts);
            Assert.Contains("casr user 2 followup", texts);

            Console.WriteLine($"ROUNDTRIP_OK session={written.SessionId} messages={msgs.Count}");
        }
        finally
        {
            try { if (File.Exists(exportPath)) File.Delete(exportPath); } catch { }

            // The import lives in the user's real OpenCode store; drop it so repeated runs do
            // not pile up duplicate "casr import test session" conversations.
            LiveStoreCleanup.DeleteOpenCodeSession(written.SessionId);
        }
    }

    /// <summary>
    /// Regression: OpenCode 2.x's importer zod-validates tool content items and rejects the
    /// whole import when they are malformed (verified against `opencode session import`).
    /// Every emitted tool item must carry id, name, time and a state holding
    /// status/input/content — and a successful call must not carry state.error.
    /// </summary>
    [Fact]
    public void SerializeToExportJson_ToolItems_CarryRequiredStateKeys()
    {
        var session = SampleSession(Path.GetTempPath());
        session.Messages.Insert(1, new CanonicalMessage
        {
            Index = 1,
            Role = MessageRole.Assistant,
            Content = "running a command",
            TimestampEpochMs = 1789390001500,
            ToolCalls = new List<ToolCall>
            {
                new ToolCall
                {
                    Id = "call-abc-1",
                    Name = "bash",
                    ArgumentsJson = "{\"command\":\"ls -la\",\"description\":\"List files\"}"
                }
            },
            ToolResults = new List<ToolResult>
            {
                new ToolResult { CallId = "call-abc-1", Content = "file1\nfile2", IsError = false }
            }
        });

        var warnings = new List<string>();
        var json = OpenCodeProvider.SerializeToExportJson(session, Path.GetTempPath(), warnings);

        using var doc = JsonDocument.Parse(json);
        var toolItems = doc.RootElement.GetProperty("messages")
            .EnumerateArray()
            .Where(m => m.GetProperty("type").GetString() == "assistant")
            .SelectMany(m => m.GetProperty("content").EnumerateArray())
            .Where(c => c.GetProperty("type").GetString() == "tool")
            .ToList();

        Assert.NotEmpty(toolItems);
        foreach (var item in toolItems)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("id").GetString()), "tool item missing id");
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("name").GetString()), "tool item missing name");
            Assert.True(item.GetProperty("time").TryGetProperty("created", out _), "tool item missing time.created");

            var state = item.GetProperty("state");
            Assert.Equal("completed", state.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Object, state.GetProperty("input").ValueKind);
            Assert.Equal(JsonValueKind.Array, state.GetProperty("content").ValueKind);
            Assert.False(state.TryGetProperty("error", out _), "a successful call must not carry state.error");
            Assert.True(state.TryGetProperty("metadata", out var meta), "tool item missing state.metadata");
            Assert.Equal(JsonValueKind.Object, meta.ValueKind);
        }

        var bash = toolItems.Single(i => i.GetProperty("name").GetString() == "bash");
        var bashState = bash.GetProperty("state");
        Assert.Equal("ls -la", bashState.GetProperty("input").GetProperty("command").GetString());
        Assert.Equal("file1\nfile2",
            bashState.GetProperty("content").EnumerateArray().First().GetProperty("text").GetString());
    }

    /// <summary>
    /// OpenCode 2.x requires state.error on a failed tool call, typed as a StructuredError
    /// ({ type, message }); a plain string — or no key at all — fails the import schema.
    /// </summary>
    [Fact]
    public void SerializeToExportJson_FailedToolCall_CarriesStructuredError()
    {
        var session = SampleSession(Path.GetTempPath());
        session.Messages.Insert(1, new CanonicalMessage
        {
            Index = 1,
            Role = MessageRole.Assistant,
            Content = "running a command",
            TimestampEpochMs = 1789390001500,
            ToolCalls = new List<ToolCall>
            {
                new ToolCall { Id = "call-err-1", Name = "shell", ArgumentsJson = "{\"command\":\"boom\"}" }
            },
            ToolResults = new List<ToolResult>
            {
                new ToolResult { CallId = "call-err-1", Content = "exit code 1", IsError = true }
            }
        });

        var json = OpenCodeProvider.SerializeToExportJson(session, Path.GetTempPath(), new List<string>());

        using var doc = JsonDocument.Parse(json);
        var tool = doc.RootElement.GetProperty("messages")
            .EnumerateArray()
            .Where(m => m.GetProperty("type").GetString() == "assistant")
            .SelectMany(m => m.GetProperty("content").EnumerateArray())
            .Single(c => c.GetProperty("type").GetString() == "tool");

        var state = tool.GetProperty("state");
        Assert.Equal("error", state.GetProperty("status").GetString());

        var error = state.GetProperty("error");
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("type").GetString()));
        Assert.Equal("exit code 1", error.GetProperty("message").GetString());
    }
}
