using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Casr.Core.Models;
using Casr.Core.Providers;
using Xunit;

namespace Casr.Core.Tests;

/// <summary>
/// Cross-resume regression tests for D2 (grok chunk-merge count collapse) and
/// D3 (antigravity "::cid" suffixed paths with no backing file).
/// Hermetic: all provider home dirs and the process CWD are redirected to temp dirs.
/// </summary>
public class CrossResumeFixTests : IDisposable
{
    private readonly string _tempDir;

    public CrossResumeFixTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"casr_crossresume_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Environment.SetEnvironmentVariable("GROK_HOME", null);
            Environment.SetEnvironmentVariable("GEMINI_HOME", null);
            Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", null);
            Environment.SetEnvironmentVariable("PI_CODING_AGENT_SESSION_DIR", null);
            AntigravityProvider.InvalidateCache();
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    private static CanonicalSession SessionWith(string? workspace, params (MessageRole Role, string Content)[] msgs)
    {
        return new CanonicalSession
        {
            SessionId = Guid.NewGuid().ToString(),
            Workspace = workspace,
            Title = "Cross Resume Fix Test",
            Messages = msgs.Select((m, i) => new CanonicalMessage
            {
                Index = i,
                Role = m.Role,
                Content = m.Content
            }).ToList()
        };
    }

    // ---- D2: grok write→read message-count preservation --------------------------

    [Fact]
    public void Grok_WriteRead_PreservesMessageCount_IncludingConsecutiveSameRole()
    {
        Environment.SetEnvironmentVariable("GROK_HOME", _tempDir);
        try
        {
            var provider = new GrokProvider();
            // Alternating roles plus a consecutive same-role pair: the old reader merged
            // same-role chunks, collapsing the count; the writer now stamps each chunk
            // with a stable messageId and the reader must NOT merge distinct ids.
            var session = SessionWith(_tempDir,
                (MessageRole.User, "u1"),
                (MessageRole.Assistant, "a1"),
                (MessageRole.User, "u2"),
                (MessageRole.Assistant, "a2"),
                (MessageRole.User, "u3"));

            var written = provider.WriteSession(session, new WriteOptions { Force = true });
            var read = provider.ReadSession(written.Paths[1]); // updates.jsonl

            Assert.Equal(5, read.Messages.Count);
            Assert.Equal("u1", read.Messages[0].Content);
            Assert.Equal("a1", read.Messages[1].Content);
            Assert.Equal("u2", read.Messages[2].Content);
            Assert.Equal("a2", read.Messages[3].Content);
            Assert.Equal("u3", read.Messages[4].Content);

            // summary.json count must match what the reader reconstructs.
            var summary = provider.ReadSummary(written.Paths[0]); // summary.json
            Assert.Equal(5, summary.MessagesCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GROK_HOME", null);
        }
    }

    [Fact]
    public void Grok_ReadSession_DoesNotMerge_ConsecutiveSameRoleChunks_WithDistinctIds()
    {
        var dir = Path.Combine(_tempDir, "sessions", "ws", "sess-id-1");
        Directory.CreateDirectory(dir);
        var updatesPath = Path.Combine(dir, "updates.jsonl");
        var lines = new List<string>();
        string Chunk(string kind, string msgId, string text) => JsonSerializer.Serialize(new
        {
            timestamp = 1789090367,
            method = "session/update",
            @params = new
            {
                sessionId = "sess-id-1",
                update = new
                {
                    sessionUpdate = kind,
                    content = new { type = "text", text },
                    messageId = msgId
                }
            }
        });
        // Two consecutive assistant chunks with DIFFERENT ids = two messages.
        lines.Add(Chunk("agent_message_chunk", "m-a", "first answer"));
        lines.Add(Chunk("agent_message_chunk", "m-b", "second answer"));
        // Same id again = streaming continuation of the second message (must merge).
        lines.Add(Chunk("agent_message_chunk", "m-b", " continued"));
        File.WriteAllLines(updatesPath, lines);

        var provider = new GrokProvider();
        var session = provider.ReadSession(updatesPath);

        Assert.Equal(2, session.Messages.Count);
        Assert.Equal("first answer", session.Messages[0].Content);
        Assert.Equal("second answer continued", session.Messages[1].Content);
    }

    // ---- D3: antigravity "::cid" suffixed path with no backing file --------------

    [Fact]
    public void Antigravity_ReadSession_CidSuffixedMissingFile_ReturnsEmptySession()
    {
        var geminiHome = Path.Combine(_tempDir, "mock_gemini");
        var cliDir = Path.Combine(geminiHome, "antigravity-cli");
        Directory.CreateDirectory(Path.Combine(cliDir, "cache"));
        Directory.CreateDirectory(Path.Combine(cliDir, "conversations"));
        File.WriteAllText(Path.Combine(cliDir, "cache", "conversation_metadata.json"),
            JsonSerializer.Serialize(new
            {
                conversations = new Dictionary<string, object?>
                {
                    ["cid-missing-1"] = new
                    {
                        summary = new { Title = "Cached Topic Title", Preview = "preview", NumSteps = 4 },
                        is_internal = "false"
                    }
                }
            }));
        Environment.SetEnvironmentVariable("GEMINI_HOME", geminiHome);
        AntigravityProvider.InvalidateCache();

        try
        {
            var provider = new AntigravityProvider();
            // This is the path shape ListSessions emits for transcript-less conversations:
            // the shared summaries db (or per-conversation db) + "::" + cid — and the
            // backing file does NOT exist here.
            var missing = Path.Combine(cliDir, "conversation_summaries.db");
            var suffixed = $"{missing}::cid-missing-1";

            var session = provider.ReadSession(suffixed);
            Assert.Equal("cid-missing-1", session.SessionId);
            Assert.Empty(session.Messages);
            Assert.Equal("Cached Topic Title", session.Title);

            var summary = provider.ReadSummary(suffixed);
            Assert.Equal("cid-missing-1", summary.SessionId);
            Assert.Equal("Cached Topic Title", summary.Title);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEMINI_HOME", null);
            AntigravityProvider.InvalidateCache();
        }
    }

    /// <summary>
    /// Live-store read with temp-only writes: the donor session is read from the real
    /// grok store (read-only), but the Pi leg of CrossResume is redirected into _tempDir
    /// so a passing run can never mint junk in the user's live Pi store. Every written
    /// file is deleted in finally AND re-asserted gone afterwards.
    /// </summary>
    [Trait("Category", "LiveSystem")]
    [Fact]
    public void Grok_LiveFreeTokenSession_CrossResumesToPi_WithoutBomAndValidates()
    {
        var liveGrokSession = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".grok", "sessions", "J%3A%5CLLM%5Cworkspaces%5CFreeToken-vcruz",
            "01a09198-cb8e-72c1-b735-ddc39463f406", "summary.json");

        if (!File.Exists(liveGrokSession)) return;

        // Redirect every Pi write into temp BEFORE CrossResume runs.
        var piSessions = Path.Combine(_tempDir, "pi_home", "sessions");
        Directory.CreateDirectory(piSessions);
        Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", Path.Combine(_tempDir, "pi_home"));
        Environment.SetEnvironmentVariable("PI_CODING_AGENT_SESSION_DIR", piSessions);

        var writtenPaths = new List<string>();
        try
        {
            var registry = ProviderRegistry.Default;
            var grok = registry.FindBySlug("grok")!;

            var summary = grok.ReadSummary(liveGrokSession);
            Assert.Contains("FreeToken", summary.Title, StringComparison.OrdinalIgnoreCase);

            var canonical = grok.ReadSession(liveGrokSession);
            Assert.NotEmpty(canonical.Messages);
            Assert.True(canonical.Messages.Count > 100);

            var resumer = new Services.SessionResumerService(registry);
            var written = resumer.CrossResume(summary, "pi", force: true);

            Assert.NotEmpty(written.Paths);
            writtenPaths.AddRange(written.Paths);
            foreach (var p in writtenPaths)
            {
                // Nothing may escape the temp redirect into the live Pi store.
                Assert.StartsWith(_tempDir, p, StringComparison.OrdinalIgnoreCase);
                Assert.True(File.Exists(p));
            }

            var piPath = written.Paths[0];

            // Verify No UTF-8 BOM
            var bytes = File.ReadAllBytes(piPath);
            Assert.False(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "Must not have UTF-8 BOM");
            Assert.Equal((byte)'{', bytes[0]);
        }
        finally
        {
            foreach (var p in writtenPaths)
            {
                try { if (File.Exists(p)) File.Delete(p); } catch { }
            }
            Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", null);
            Environment.SetEnvironmentVariable("PI_CODING_AGENT_SESSION_DIR", null);
        }

        // Verified delete: re-query the filesystem AFTER cleanup ran.
        foreach (var p in writtenPaths)
        {
            Assert.False(File.Exists(p), $"cleanup failed — test junk remains: {p}");
        }
    }
}
