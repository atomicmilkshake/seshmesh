using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Casr.Core.Context.Capabilities;
using Casr.Core.Context.Pipeline;
using Casr.Core.Models;
using Casr.Core.Providers;
using Casr.Core.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Casr.Core.Tests;

public class CrossResumeMatrixTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _donorDb;
    private const string DonorCascadeId = "11111111-2222-3333-4444-555555555555";
    private const string DonorTrajectoryId = "66666666-7777-8888-9999-000000000000";

    public CrossResumeMatrixTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"casr_matrix_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // Setup mock environment variables for clean testing. Pi uses the canonical
        // sessions-dir variable (what PiProvider.SessionRoots() resolves) so every
        // Pi write in this class lands under _tempDir — never the live store.
        Environment.SetEnvironmentVariable("PI_CODING_AGENT_SESSION_DIR", Path.Combine(_tempDir, "pi", "sessions"));
        Environment.SetEnvironmentVariable("HERMES_HOME", Path.Combine(_tempDir, "hermes"));
        Environment.SetEnvironmentVariable("GROK_HOME", Path.Combine(_tempDir, "grok"));
        Environment.SetEnvironmentVariable("GEMINI_HOME", Path.Combine(_tempDir, "gemini"));

        Directory.CreateDirectory(Path.Combine(_tempDir, "pi", "sessions"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "hermes", "sessions", "saved"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "grok", "sessions"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "gemini", "antigravity-cli", "conversations"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "gemini", "antigravity-cli", "brain"));

        _donorDb = Path.Combine(_tempDir, "donor.db");
        CreateMinimalDonorDb(_donorDb);
        AntigravityProvider.SetWriteTemplateForTests(_donorDb, DonorCascadeId, DonorTrajectoryId);
        AntigravityProvider.InvalidateCache();
    }

    public void Dispose()
    {
        try
        {
            Environment.SetEnvironmentVariable("PI_CODING_AGENT_DIR", null);
            Environment.SetEnvironmentVariable("PI_CODING_AGENT_SESSION_DIR", null);
            Environment.SetEnvironmentVariable("HERMES_HOME", null);
            Environment.SetEnvironmentVariable("GROK_HOME", null);
            Environment.SetEnvironmentVariable("GEMINI_HOME", null);
            AntigravityProvider.ResetWriteTemplateForTests();
            AntigravityProvider.InvalidateCache();

            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    private static void CreateMinimalDonorDb(string path)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        using var conn = new SqliteConnection(cs);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE trajectory_meta (
                trajectory_id TEXT, cascade_id TEXT, session_id TEXT, parent_trajectory_id TEXT
            );
            INSERT INTO trajectory_meta VALUES ('66666666-7777-8888-9999-000000000000', '11111111-2222-3333-4444-555555555555', 'dummy', '');

            CREATE TABLE steps (
                idx INTEGER PRIMARY KEY,
                step_type INTEGER,
                step_payload BLOB
            );";
        cmd.ExecuteNonQuery();

        // 2 synthetic steps embedding the donor cascade ID in payload
        var donorBytes = System.Text.Encoding.UTF8.GetBytes("donor-step-content-11111111-2222-3333-4444-555555555555-run_command");
        using var ins = conn.CreateCommand();
        ins.CommandText = "INSERT INTO steps VALUES (0, 14, $p), (1, 15, $p)";
        ins.Parameters.AddWithValue("$p", donorBytes);
        ins.ExecuteNonQuery();
    }

    private static CanonicalSession CreateSampleSession(string providerSlug, string workspace)
    {
        return new CanonicalSession
        {
            SessionId = $"src-{Guid.NewGuid():N}",
            ProviderSlug = providerSlug,
            Workspace = workspace,
            Title = "Implement Clean Matrix",
            StartedAtEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Messages = new List<CanonicalMessage>
            {
                new()
                {
                    Index = 0,
                    Role = MessageRole.User,
                    Content = "Please implement feature X"
                },
                new()
                {
                    Index = 1,
                    Role = MessageRole.Assistant,
                    Content = "I am implementing feature X",
                    Extra = new Dictionary<string, object?> { ["thinking"] = "First let's check directory structure" },
                    ToolCalls = new List<ToolCall>
                    {
                        new() { Id = "tc1", Name = "list_dir", ArgumentsJson = "{\"path\":\".\"}" }
                    }
                },
                new()
                {
                    Index = 2,
                    Role = MessageRole.Tool,
                    Content = "file1.cs\nfile2.cs",
                    ToolResults = new List<ToolResult>
                    {
                        new() { CallId = "tc1", Content = "file1.cs\nfile2.cs" }
                    },
                    Extra = new Dictionary<string, object?> { ["tool_name"] = "list_dir" }
                }
            }
        };
    }

    [Fact]
    public void PiProvider_WriteSession_ContainsUsageObject_NoCrashOnTokenEstimate()
    {
        var provider = new PiProvider();
        var session = CreateSampleSession("pi", _tempDir);

        var written = provider.WriteSession(session, new WriteOptions { Force = true });
        Assert.Single(written.Paths);
        Assert.StartsWith(_tempDir, written.Paths[0], StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(written.Paths[0]));

        var lines = File.ReadAllLines(written.Paths[0]);
        Assert.True(lines.Length >= 3);

        // Find assistant message line
        var asstLine = lines.FirstOrDefault(l => l.Contains("\"role\":\"assistant\""));
        Assert.NotNull(asstLine);

        using var doc = JsonDocument.Parse(asstLine!);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("message", out var msg));
        Assert.True(msg.TryGetProperty("usage", out var usage));
        Assert.True(usage.TryGetProperty("totalTokens", out var totalTokens));
        Assert.Equal(0, totalTokens.GetInt32());

        // Check thinking part preserved
        Assert.True(msg.TryGetProperty("content", out var contentArr));
        var hasThinking = false;
        foreach (var item in contentArr.EnumerateArray())
        {
            if (item.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "thinking")
            {
                hasThinking = true;
                break;
            }
        }
        Assert.True(hasThinking, "Pi assistant message must preserve thinking block");
    }

    [Fact]
    public void PiProvider_OwnsSession_RejectsNonPiJsonlFiles()
    {
        var provider = new PiProvider();

        // Create a non-Pi jsonl file elsewhere (like VSCode chat history)
        var foreignJsonl = Path.Combine(_tempDir, "vscode_chat_random.jsonl");
        File.WriteAllLines(foreignJsonl, new[] { "{\"role\":\"user\",\"content\":\"hello\"}" });

        var claimed = provider.OwnsSession(foreignJsonl);
        Assert.Null(claimed);

        // Now create a genuine Pi session file with {"type":"session"} header
        var piJsonl = Path.Combine(_tempDir, "real_pi.jsonl");
        File.WriteAllLines(piJsonl, new[] { "{\"type\":\"session\",\"version\":3,\"id\":\"pi-123\"}" });

        var claimedPi = provider.OwnsSession(piJsonl);
        Assert.NotNull(claimedPi);
        Assert.Equal(Path.GetFullPath(piJsonl), claimedPi);
    }

    [Fact]
    public void HermesProvider_ReadSavedJson_MatchesHermesFormat()
    {
        var savedDir = HermesProvider.GetSavedSessionsDir();
        // Filename must carry the session id (as WriteSavedJsonSession's
        // hermes_conversation_{id}.json does) — OwnsSession content-verifies
        // only filename-plausible candidates instead of parsing every file.
        var jsonFile = Path.Combine(savedDir, "hermes_conversation_20260915_test123.json");

        var sampleSavedJson = @"{
            ""session_id"": ""20260915_test123"",
            ""model"": ""deepseek-v4.1-flash"",
            ""session_start"": ""2026-09-15T11:22:35.233923"",
            ""messages"": [
                {
                    ""role"": ""user"",
                    ""content"": ""Find the issue in module"",
                    ""timestamp"": 1789090000
                },
                {
                    ""role"": ""assistant"",
                    ""content"": ""Found the issue"",
                    ""reasoning_content"": ""Checking line 100"",
                    ""timestamp"": 1789090010
                }
            ]
        }";
        File.WriteAllText(jsonFile, sampleSavedJson);

        var provider = new HermesProvider();
        var owned = provider.OwnsSession("20260915_test123");
        Assert.NotNull(owned);
        Assert.Equal(jsonFile, owned);

        var canonical = provider.ReadSession(jsonFile);
        Assert.Equal("20260915_test123", canonical.SessionId);
        Assert.Equal("deepseek-v4.1-flash", canonical.ModelName);
        Assert.Equal(2, canonical.Messages.Count);
        Assert.Equal("Checking line 100", canonical.Messages[1].Extra["thinking"]?.ToString());
    }

    [Fact]
    public void HermesProvider_ReadSession_IgnoresInactiveMessages()
    {
        var dbPath = HermesProvider.GetStateDbPath();
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        using (var conn = new SqliteConnection(cs))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS sessions (
                    id TEXT PRIMARY KEY, source TEXT, display_name TEXT, title TEXT, cwd TEXT, model TEXT,
                    started_at REAL, ended_at REAL, last_activity_at REAL, message_count INTEGER,
                    tool_call_count INTEGER, parent_session_id TEXT, input_tokens INTEGER,
                    output_tokens INTEGER, estimated_cost_usd REAL, model_config TEXT
                );
                INSERT INTO sessions (id, title) VALUES ('s1', 'Test Active');

                CREATE TABLE IF NOT EXISTS messages (
                    id INTEGER PRIMARY KEY, session_id TEXT, role TEXT, content TEXT, tool_call_id TEXT,
                    tool_calls TEXT, tool_name TEXT, timestamp REAL, reasoning TEXT, reasoning_content TEXT,
                    active INTEGER
                );
                INSERT INTO messages (session_id, role, content, timestamp, active) VALUES
                    ('s1', 'user', 'Turn 1 active', 1000.0, 1),
                    ('s1', 'assistant', 'Turn 2 rewound zombie', 1001.0, 0),
                    ('s1', 'assistant', 'Turn 2 real branch', 1002.0, 1);
            ";
            cmd.ExecuteNonQuery();
        }

        var provider = new HermesProvider();
        var canonical = provider.ReadSession($"{dbPath}::s1");

        // The zombie message with active=0 should NOT be included!
        Assert.Equal(2, canonical.Messages.Count);
        Assert.Equal("Turn 1 active", canonical.Messages[0].Content);
        Assert.Equal("Turn 2 real branch", canonical.Messages[1].Content);
    }

    [Fact]
    public void GrokProvider_WriteSession_WritesUpdatesAndChatHistory()
    {
        var provider = new GrokProvider();
        var session = CreateSampleSession("grok", _tempDir);

        var written = provider.WriteSession(session, new WriteOptions { Force = true });
        Assert.Equal(3, written.Paths.Count);

        var updatesPath = written.Paths.First(p => p.EndsWith("updates.jsonl"));
        var chatHistoryPath = written.Paths.First(p => p.EndsWith("chat_history.jsonl"));
        var summaryPath = written.Paths.First(p => p.EndsWith("summary.json"));

        Assert.True(File.Exists(updatesPath));
        Assert.True(File.Exists(chatHistoryPath));
        Assert.True(File.Exists(summaryPath));

        // Check updates.jsonl has integer second timestamps
        var firstUpdate = File.ReadLines(updatesPath).First();
        using var doc = JsonDocument.Parse(firstUpdate);
        Assert.True(doc.RootElement.TryGetProperty("timestamp", out var tsProp));
        Assert.True(tsProp.GetInt64() < 10000000000L, "Top-level timestamp in updates.jsonl must be integer seconds");

        // Check summary.json fields
        using var sumDoc = JsonDocument.Parse(File.ReadAllText(summaryPath));
        Assert.True(sumDoc.RootElement.TryGetProperty("chat_format_version", out var cfv));
        Assert.Equal(1, cfv.GetInt32());
        Assert.True(sumDoc.RootElement.TryGetProperty("agent_name", out var agn));
        Assert.Equal("grok-build-plan", agn.GetString());
    }

    [Theory]
    [InlineData("grok", "pi")]
    [InlineData("grok", "hermes")]
    [InlineData("grok", "antigravity")]
    [InlineData("pi", "grok")]
    [InlineData("pi", "hermes")]
    [InlineData("pi", "antigravity")]
    [InlineData("hermes", "grok")]
    [InlineData("hermes", "pi")]
    [InlineData("hermes", "antigravity")]
    [InlineData("antigravity", "grok")]
    [InlineData("antigravity", "pi")]
    [InlineData("antigravity", "hermes")]
    [InlineData("grok", "grok")]
    [InlineData("pi", "pi")]
    [InlineData("hermes", "hermes")]
    [InlineData("antigravity", "antigravity")]
    public void CrossResume_Matrix_All16CombinationsProduceValidOutput(string sourceSlug, string targetSlug)
    {
        var sampleSession = CreateSampleSession(sourceSlug, _tempDir);

        // Write sample session into source provider first
        var registry = ProviderRegistry.Default;
        var srcProvider = registry.FindBySlug(sourceSlug)!;
        var targetProvider = registry.FindBySlug(targetSlug)!;

        var initialWrite = srcProvider.WriteSession(sampleSession, new WriteOptions { Force = true });
        var sourcePath = initialWrite.Paths[0];

        var summary = new SessionSummary
        {
            SessionId = initialWrite.SessionId,
            Provider = sourceSlug,
            ProviderDisplayName = srcProvider.Name,
            Workspace = _tempDir,
            SourcePath = sourcePath,
            Title = "Matrix Test"
        };

        // Ingress validation & intermediate provenance: source must be readable before cross-resume
        var ingressSession = srcProvider.ReadSession(sourcePath);
        Assert.NotNull(ingressSession);
        Assert.True(ingressSession.Messages.Count >= 2);
        Assert.Contains("Please implement feature X", ingressSession.Messages[0].Content);

        // Verify intermediate normalized representation preserves message semantics
        var normalizedIntermediate = ContextPackager.Default.Normalize(ingressSession);
        Assert.NotNull(normalizedIntermediate);
        Assert.Equal(ingressSession.Messages.Count, normalizedIntermediate.Messages.Count);

        var resumer = new SessionResumerService(registry, ContextPackager.Default);
        var result = resumer.CrossResume(summary, targetSlug, force: true);

        Assert.NotNull(result);
        Assert.False(string.IsNullOrWhiteSpace(result.SessionId));
        Assert.False(string.IsNullOrWhiteSpace(result.ResumeCommand));

        // Verify target resume command syntax against official CLI switch grammar
        Infrastructure.CliSwitchValidator.ValidateResumeCommand(targetSlug, result.ResumeCommand, result.SessionId, result.Workspace);

        // Verify files were produced, non-empty, and free of UTF-8 BOM
        Assert.NotEmpty(result.Paths);
        foreach (var p in result.Paths)
        {
            Assert.True(File.Exists(p), $"Output path {p} should exist on disk");
            var fi = new FileInfo(p);
            Assert.True(fi.Length > 0, $"Output path {p} should not be empty");

            // Verify no UTF-8 BOM
            var headerBytes = new byte[Math.Min(3, (int)fi.Length)];
            using (var stream = File.OpenRead(p))
            {
                stream.Read(headerBytes, 0, headerBytes.Length);
            }
            if (headerBytes.Length >= 3)
            {
                Assert.False(headerBytes[0] == 0xEF && headerBytes[1] == 0xBB && headerBytes[2] == 0xBF,
                    $"File {p} should not have a UTF-8 BOM");
            }
        }

        // Independently examine produced results via target provider reader
        CanonicalSession readBack;
        switch (targetSlug)
        {
            case "pi":
                readBack = targetProvider.ReadSession(result.Paths[0]);
                break;
            case "hermes":
                readBack = targetProvider.ReadSession(result.Paths[0]);
                break;
            case "grok":
                var grokSummary = result.Paths.First(p => p.EndsWith("summary.json"));
                readBack = targetProvider.ReadSession(grokSummary);
                break;
            case "antigravity":
                var agyPath = result.Paths.FirstOrDefault(p => p.EndsWith("transcript.jsonl")) ?? result.Paths[0];
                readBack = targetProvider.ReadSession(agyPath);
                break;
            default:
                throw new InvalidOperationException($"Unexpected target {targetSlug}");
        }

        Assert.NotNull(readBack);
        Assert.Equal(result.SessionId, readBack.SessionId);
        Assert.True(readBack.Messages.Count >= 2, $"Expected at least 2 messages in reconstructed session, got {readBack.Messages.Count}");
        Assert.Contains("Please implement feature X", readBack.Messages[0].Content);
    }
}
