using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Casr.Core.Configuration;
using Casr.Core.Models;
using Casr.Core.Providers;
using Casr.Core.Services;
using Xunit;

namespace Casr.Core.Tests;

public class PiProviderTests : IDisposable
{
    private readonly string _tempDir;

    public PiProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"casr_pi_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void PiProvider_ResumeCommand_AndSessionRoots_VerifyInvariants()
    {
        Environment.SetEnvironmentVariable("PI_CODING_AGENT_SESSION_DIR", _tempDir);
        try
        {
            var provider = new PiProvider();
            Assert.Equal("Pi", provider.Name);
            Assert.Equal("pi", provider.Slug);
            Assert.Equal("pi", provider.CliAlias);

            var resumeCmd = provider.ResumeCommand("session-uuid-999");
            Assert.Equal("pi --session session-uuid-999", resumeCmd);

            var roots = provider.SessionRoots();
            Assert.NotNull(roots);
            Assert.NotEmpty(roots);
            Assert.Equal(_tempDir, roots[0]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PI_CODING_AGENT_SESSION_DIR", null);
        }
    }

    [Fact]
    public void PiProvider_EncodeWorkspacePath_MatchesPiCliSpecification()
    {
        var encodedGpuZ = PiProvider.EncodeWorkspacePath(@"C:\Program Files (x86)\GPU-Z");
        Assert.Equal("--C--Program Files (x86)-GPU-Z--", encodedGpuZ);

        var encodedDriveD = PiProvider.EncodeWorkspacePath(@"D:\");
        Assert.Equal("--D----", encodedDriveD);

        var encodedUsers = PiProvider.EncodeWorkspacePath(@"C:\Users\testuser");
        Assert.Equal("--C--Users-testuser--", encodedUsers);
    }

    [Fact]
    public void PiProvider_ExtractSessionIdFromFileName_ExtractsUuid()
    {
        var fileName = "2026-08-11T17-27-40-744Z_019ff1dd-9dc8-7701-ba32-d32e8d4d233c";
        var id = PiProvider.ExtractSessionIdFromFileName(fileName);
        Assert.Equal("019ff1dd-9dc8-7701-ba32-d32e8d4d233c", id);

        var plainId = "custom-session-123";
        Assert.Equal("custom-session-123", PiProvider.ExtractSessionIdFromFileName(plainId));
    }

    [Trait("Category", "LiveSystem")]
    [Fact]
    public void PiProvider_Detect_FindsInstalledPiOnMachine()
    {
        var provider = new PiProvider();
        var result = provider.Detect();

        Assert.True(result.Installed);
        Assert.NotEmpty(result.Evidence);
        Assert.NotNull(result.Version);
        // Semver, not a frozen version: pinning the machine's installed version turns
        // ordinary CLI upgrades (0.85.1 -> 0.87.0) into environmental failures.
        Assert.Matches(@"^\d+\.\d+\.\d+", result.Version);
    }

    [Trait("Category", "LiveSystem")]
    [Fact]
    public void PiProvider_ListSessions_DiscoversLiveSessions()
    {
        var provider = new PiProvider();
        var sessions = provider.ListSessions();

        Assert.NotNull(sessions);
        Assert.NotEmpty(sessions);

        var first = sessions[0];
        Assert.False(string.IsNullOrWhiteSpace(first.SessionId));
        Assert.True(File.Exists(first.Path));

        // Test OwnsSession for this session
        var owned = provider.OwnsSession(first.SessionId);
        Assert.NotNull(owned);
        Assert.Equal(first.Path, owned);
    }

    [Fact]
    public void PiProvider_ReadSummaryAndSession_FromMockJsonl()
    {
        var provider = new PiProvider();
        var mockSessionDir = Path.Combine(_tempDir, "--C--Projects-MyPiApp--");
        Directory.CreateDirectory(mockSessionDir);

        var mockFile = Path.Combine(mockSessionDir, "2026-09-11T12-00-00-000Z_mock-pi-uuid-001.jsonl");

        var lines = new[]
        {
            JsonSerializer.Serialize(new
            {
                type = "session",
                version = 3,
                id = "mock-pi-uuid-001",
                timestamp = "2026-09-11T12:00:00.000Z",
                cwd = @"C:\Projects\MyPiApp"
            }),
            JsonSerializer.Serialize(new
            {
                type = "session_info",
                id = "inf00001",
                parentId = (string?)null,
                timestamp = "2026-09-11T12:00:01.000Z",
                name = "Optimize GPU Shader Pipeline"
            }),
            JsonSerializer.Serialize(new
            {
                type = "model_change",
                id = "mod00001",
                parentId = "inf00001",
                timestamp = "2026-09-11T12:00:02.000Z",
                provider = "openrouter",
                modelId = "deepseek/deepseek-v4-flash-0731"
            }),
            JsonSerializer.Serialize(new
            {
                type = "message",
                id = "msg00001",
                parentId = "mod00001",
                timestamp = "2026-09-11T12:00:03.000Z",
                message = new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = "/code Optimize the HLSL compute shader for matrix multiplication" }
                    },
                    timestamp = 1789128003000L
                }
            }),
            JsonSerializer.Serialize(new
            {
                type = "message",
                id = "msg00002",
                parentId = "msg00001",
                timestamp = "2026-09-11T12:00:05.000Z",
                message = new
                {
                    role = "assistant",
                    content = new object[]
                    {
                        new { type = "thinking", thinking = "We should use shared memory tiles to maximize throughput." },
                        new
                        {
                            type = "toolCall",
                            id = "call_bash_001",
                            name = "bash",
                            arguments = new { command = "ls -la shaders" }
                        },
                        new { type = "text", text = "Here is the optimized shader using LDS memory." }
                    },
                    model = "deepseek/deepseek-v4-flash-0731",
                    timestamp = 1789128005000L
                }
            }),
            JsonSerializer.Serialize(new
            {
                type = "message",
                id = "msg00003",
                parentId = "msg00002",
                timestamp = "2026-09-11T12:00:07.000Z",
                message = new
                {
                    role = "toolResult",
                    toolCallId = "call_bash_001",
                    toolName = "bash",
                    content = new object[]
                    {
                        new { type = "text", text = "shader.hlsl\nshader.spv" }
                    },
                    isError = false,
                    timestamp = 1789128007000L
                }
            })
        };

        File.WriteAllLines(mockFile, lines);

        // 1. Verify ReadSummary
        var summary = provider.ReadSummary(mockFile);
        Assert.Equal("mock-pi-uuid-001", summary.SessionId);
        Assert.Equal("pi", summary.Provider);
        Assert.Equal("Optimize GPU Shader Pipeline", summary.Title);
        Assert.Equal(@"C:\Projects\MyPiApp", summary.Workspace);
        Assert.Equal(3, summary.MessagesCount);
        Assert.Equal(1, summary.ToolCallsCount);
        Assert.Equal("deepseek/deepseek-v4-flash-0731", summary.ModelName);
        Assert.False(summary.IsSubagent);

        // 2. Verify ReadSession
        var session = provider.ReadSession(mockFile);
        Assert.Equal("mock-pi-uuid-001", session.SessionId);
        Assert.Equal("pi", session.ProviderSlug);
        Assert.Equal("Optimize GPU Shader Pipeline", session.Title);
        Assert.Equal(@"C:\Projects\MyPiApp", session.Workspace);
        Assert.Equal(3, session.Messages.Count);

        // User message
        Assert.Equal(MessageRole.User, session.Messages[0].Role);
        Assert.Equal("/code Optimize the HLSL compute shader for matrix multiplication", session.Messages[0].Content);

        // Assistant message
        Assert.Equal(MessageRole.Assistant, session.Messages[1].Role);
        Assert.Equal("Here is the optimized shader using LDS memory.", session.Messages[1].Content);
        Assert.Single(session.Messages[1].ToolCalls);
        Assert.Equal("call_bash_001", session.Messages[1].ToolCalls[0].Id);
        Assert.Equal("bash", session.Messages[1].ToolCalls[0].Name);
        Assert.Equal("We should use shared memory tiles to maximize throughput.", session.Messages[1].Extra["thinking"]);

        // ToolResult message
        Assert.Equal(MessageRole.Tool, session.Messages[2].Role);
        Assert.Equal("shader.hlsl\nshader.spv", session.Messages[2].Content);
        Assert.Single(session.Messages[2].ToolResults);
        Assert.Equal("call_bash_001", session.Messages[2].ToolResults[0].CallId);
        Assert.False(session.Messages[2].ToolResults[0].IsError);

        // Resume Command
        var resumeCmd = provider.ResumeCommand(session.SessionId);
        Assert.Equal("pi --session mock-pi-uuid-001", resumeCmd);
    }

    [Fact]
    public void PiProvider_WriteSession_RoundTripsAccurately()
    {
        var provider = new PiProvider();
        var session = new CanonicalSession
        {
            SessionId = Guid.NewGuid().ToString(),
            ProviderSlug = "pi",
            Workspace = _tempDir,
            Title = "Exported Pi Test Session",
            ModelName = "gpt-4o",
            Messages = new()
            {
                new CanonicalMessage
                {
                    Index = 0,
                    Role = MessageRole.User,
                    Content = "Please analyze the algorithm complexity."
                },
                new CanonicalMessage
                {
                    Index = 1,
                    Role = MessageRole.Assistant,
                    Content = "The time complexity is O(N log N) and space complexity is O(1).",
                    ToolCalls = new()
                    {
                        new ToolCall { Id = "call_bench_99", Name = "bash", ArgumentsJson = "{\"cmd\":\"cargo bench\"}" }
                    }
                },
                new CanonicalMessage
                {
                    Index = 2,
                    Role = MessageRole.Tool,
                    Content = "Benchmark completed in 1.42s",
                    ToolResults = new()
                    {
                        new ToolResult { CallId = "call_bench_99", Content = "Benchmark completed in 1.42s", IsError = false }
                    }
                }
            }
        };

        Environment.SetEnvironmentVariable("PI_CODING_AGENT_SESSION_DIR", _tempDir);
        try
        {
            var written = provider.WriteSession(session, new WriteOptions { Force = true });
            Assert.NotEmpty(written.Paths);
            Assert.StartsWith(_tempDir, written.Paths[0], StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(written.Paths[0]));
            Assert.Equal($"pi --session {written.SessionId}", written.ResumeCommand);

            // Verify lines in the created file
            var fileLines = File.ReadAllLines(written.Paths[0]);
            Assert.True(fileLines.Length >= 4);

            // Line 0: session header
            using var doc0 = JsonDocument.Parse(fileLines[0]);
            Assert.Equal("session", doc0.RootElement.GetProperty("type").GetString());
            Assert.Equal(3, doc0.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(written.SessionId, doc0.RootElement.GetProperty("id").GetString());

            // Line 1: session_info
            using var doc1 = JsonDocument.Parse(fileLines[1]);
            Assert.Equal("session_info", doc1.RootElement.GetProperty("type").GetString());
            Assert.Equal("Exported Pi Test Session", doc1.RootElement.GetProperty("name").GetString());

            // Read the session back through PiProvider
            var readBack = provider.ReadSession(written.Paths[0]);
            Assert.Equal(written.SessionId, readBack.SessionId);
            Assert.Equal("Exported Pi Test Session", readBack.Title);
            Assert.Equal(3, readBack.Messages.Count);
            Assert.Equal("Please analyze the algorithm complexity.", readBack.Messages[0].Content);
            Assert.Equal(MessageRole.User, readBack.Messages[0].Role);
            Assert.Equal("The time complexity is O(N log N) and space complexity is O(1).", readBack.Messages[1].Content);
            Assert.Equal(MessageRole.Assistant, readBack.Messages[1].Role);
            Assert.Single(readBack.Messages[1].ToolCalls);
            Assert.Equal(MessageRole.Tool, readBack.Messages[2].Role);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PI_CODING_AGENT_SESSION_DIR", null);
        }
    }

    [Fact]
    public void SessionResumerService_CrossResume_AntigravityToPi()
    {
        var resumer = new SessionResumerService();
        var mockConvDir = Path.Combine(_tempDir, "mock_brain_pi", "conv-pi-001", ".system_generated", "logs");
        Directory.CreateDirectory(mockConvDir);
        var transcriptPath = Path.Combine(mockConvDir, "transcript.jsonl");

        var step0 = new
        {
            step_index = 0,
            source = "USER_EXPLICIT",
            type = "USER_INPUT",
            created_at = "2026-09-11T10:00:00Z",
            content = "<USER_REQUEST>\nBuild a high speed streaming pipeline\n</USER_REQUEST>"
        };
        var step1 = new
        {
            step_index = 1,
            source = "MODEL",
            type = "PLANNER_RESPONSE",
            created_at = "2026-09-11T10:00:05Z",
            content = "Here is the streaming pipeline implementation."
        };

        File.WriteAllLines(transcriptPath, new[]
        {
            JsonSerializer.Serialize(step0),
            JsonSerializer.Serialize(step1)
        });

        var summary = new SessionSummary
        {
            SessionId = "conv-pi-001",
            Provider = "antigravity",
            SourcePath = transcriptPath,
            Workspace = _tempDir
        };

        Environment.SetEnvironmentVariable("PI_CODING_AGENT_SESSION_DIR", _tempDir);
        try
        {
            var written = resumer.CrossResume(summary, "pi", force: true);
            Assert.NotEmpty(written.Paths);
            Assert.True(File.Exists(written.Paths[0]));
            Assert.StartsWith("pi --session", written.ResumeCommand);

            var piProvider = new PiProvider();
            var readBack = piProvider.ReadSession(written.Paths[0]);
            Assert.Equal(2, readBack.Messages.Count);
            Assert.Equal("Build a high speed streaming pipeline", readBack.Title);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PI_CODING_AGENT_SESSION_DIR", null);
        }
    }

    [Fact]
    public void ProviderRegistry_IncludesPiProvider_WithDualResolution()
    {
        var registry = ProviderRegistry.Default;
        var pi = registry.FindBySlug("pi");
        Assert.NotNull(pi);
        Assert.IsType<PiProvider>(pi);
        Assert.Equal("Pi", pi.Name);
        Assert.Equal("pi", pi.CliAlias);

        var byAlias = registry.FindByAlias("pi");
        Assert.Same(pi, byAlias);
    }
}
