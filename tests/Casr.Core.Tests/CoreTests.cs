using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Casr.Core.Backup;
using Casr.Core.Configuration;
using Casr.Core.Models;
using Casr.Core.Providers;
using Casr.Core.Services;
using Casr.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Casr.Core.Tests;

public class CoreTests : IDisposable
{
    private readonly string _tempDir;

    public CoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"casr_tests_{Guid.NewGuid():N}");
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
    public void ModelHelpers_TruncateTitle_HandlesLongAndMultiline()
    {
        var longTitle = "Line 1\r\nLine 2 with lots of text that goes on and on and exceeds the maximum limit for titles";
        var truncated = ModelHelpers.TruncateTitle(longTitle, 30);

        Assert.True(truncated.Length <= 30);
        Assert.EndsWith("...", truncated);
        Assert.DoesNotContain("\r", truncated);
        Assert.DoesNotContain("\n", truncated);
    }

    [Fact]
    public void OpenClaudeProvider_ProjectDirKey_EncodesWorkspacePath()
    {
        var key1 = OpenClaudeProvider.ProjectDirKey("C:\\Users\\testuser");
        Assert.Equal("C--Users-testuser", key1);

        var key2 = OpenClaudeProvider.ProjectDirKey("D:\\_PROJECTS\\SampleProject");
        Assert.Equal("D---PROJECTS-SampleProject", key2);
    }

    [Fact]
    public void OpenClaudeProvider_WriteAndReadSession_RoundTripsWithToolCalls()
    {
        var provider = new OpenClaudeProvider();
        var session = new CanonicalSession
        {
            SessionId = Guid.NewGuid().ToString(),
            ProviderSlug = "openclaude",
            Workspace = _tempDir,
            Title = "Test OpenClaude Session",
            ModelName = "gpt-4o",
            Messages = new()
            {
                new CanonicalMessage
                {
                    Index = 0,
                    Role = MessageRole.User,
                    Content = "<USER_REQUEST>\nSearch Bitcoin price\n</USER_REQUEST>"
                },
                new CanonicalMessage
                {
                    Index = 1,
                    Role = MessageRole.Assistant,
                    Content = "I will check Bitcoin price using web search.",
                    ToolCalls = new()
                    {
                        new ToolCall { Id = "call_123", Name = "web_search", ArgumentsJson = "{\"query\":\"bitcoin price\"}" }
                    }
                },
                new CanonicalMessage
                {
                    Index = 2,
                    Role = MessageRole.Tool,
                    ToolResults = new()
                    {
                        new ToolResult { CallId = "call_123", Content = "$95,000 USD", IsError = false }
                    }
                },
                new CanonicalMessage
                {
                    Index = 3,
                    Role = MessageRole.Assistant,
                    Content = "The current price of Bitcoin is $95,000 USD."
                }
            }
        };

        Environment.SetEnvironmentVariable("OPENCLAUDE_CONFIG_DIR", _tempDir);
        try
        {
            var written = provider.WriteSession(session, new WriteOptions { Force = true });
            Assert.NotEmpty(written.Paths);
            Assert.True(File.Exists(written.Paths[0]));
            Assert.Contains("openclaude --resume", written.ResumeCommand);

            // Verify raw JSONL lines structure (mode, custom-title, uuid DAG)
            var lines = File.ReadAllLines(written.Paths[0]);
            Assert.True(lines.Length >= 5);
            using var modeDoc = JsonDocument.Parse(lines[0]);
            Assert.Equal("mode", modeDoc.RootElement.GetProperty("type").GetString());

            using var titleDoc = JsonDocument.Parse(lines[1]);
            Assert.Equal("custom-title", titleDoc.RootElement.GetProperty("type").GetString());
            Assert.Equal("Test OpenClaude Session", titleDoc.RootElement.GetProperty("customTitle").GetString());

            // Check ReadSession
            var read = provider.ReadSession(written.Paths[0]);
            Assert.Equal(4, read.Messages.Count);
            Assert.Equal("Test OpenClaude Session", read.Title);
            Assert.Equal("gpt-4o", read.ModelName);
            Assert.Equal(MessageRole.User, read.Messages[0].Role);
            Assert.Equal(MessageRole.Assistant, read.Messages[1].Role);
            Assert.Single(read.Messages[1].ToolCalls);
            Assert.Equal("web_search", read.Messages[1].ToolCalls[0].Name);
            Assert.Equal(MessageRole.Tool, read.Messages[2].Role);
            Assert.Single(read.Messages[2].ToolResults);
            Assert.Equal("$95,000 USD", read.Messages[2].ToolResults[0].Content);

            // Check ReadSummary
            var summary = provider.ReadSummary(written.Paths[0]);
            Assert.Equal("openclaude", summary.Provider);
            Assert.Equal("OpenClaude", summary.ProviderDisplayName);
            Assert.Equal("Test OpenClaude Session", summary.Title);
            Assert.Equal(4, summary.MessagesCount);
            Assert.Equal(1, summary.ToolCallsCount);
            Assert.Equal("gpt-4o", summary.ModelName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAUDE_CONFIG_DIR", null);
        }
    }

    [Trait("Category", "LiveSystem")]
    [Fact]
    public void OpenClaudeProvider_RealSystem_DetectsAndReadsLiveSessions()
    {
        var provider = new OpenClaudeProvider();
        var detect = provider.Detect();
        Assert.True(detect.Installed);
        Assert.NotEmpty(detect.Evidence);

        var sessions = provider.ListSessions();
        Assert.NotNull(sessions);
        Assert.NotEmpty(sessions);

        var first = sessions[0];
        var summary = provider.ReadSummary(first.Path);
        Assert.NotNull(summary.Title);
        Assert.Equal("openclaude", summary.Provider);

        var canonical = provider.ReadSession(first.Path);
        Assert.NotNull(canonical.Title);
        Assert.Equal("openclaude", canonical.ProviderSlug);

        var resumeCmd = provider.ResumeCommand(canonical.SessionId);
        Assert.Equal($"openclaude --resume {canonical.SessionId}", resumeCmd);
    }

    [Fact]
    public void OpenClaudeProvider_SubagentSession_IdentifiedAsSubagent()
    {
        var provider = new OpenClaudeProvider();
        var subagentFile = Path.Combine(_tempDir, "subagents", "agent-12345.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(subagentFile)!);

        var line1 = new
        {
            type = "user",
            isSidechain = true,
            message = new { role = "user", content = "Investigate the crash in database module" }
        };
        var line2 = new
        {
            type = "assistant",
            isSidechain = true,
            message = new { role = "assistant", content = "Looking into stacktrace" }
        };

        File.WriteAllLines(subagentFile, new[]
        {
            JsonSerializer.Serialize(line1),
            JsonSerializer.Serialize(line2)
        });

        var summary = provider.ReadSummary(subagentFile);
        Assert.True(summary.IsSubagent);
        Assert.Equal("Investigate the crash in database module", summary.Title);

        var session = provider.ReadSession(subagentFile);
        Assert.True(session.IsSubagent);
        Assert.Equal(2, session.Messages.Count);
    }

    [Fact]
    public void SessionResumerService_CrossResume_AntigravityToOpenClaude()
    {
        var resumer = new SessionResumerService();
        var convDir = Path.Combine(_tempDir, "mock_brain_oc", "99887766-5544-3322-1100-aabbccddeeff", ".system_generated", "logs");
        Directory.CreateDirectory(convDir);
        var transcriptPath = Path.Combine(convDir, "transcript.jsonl");

        var step0 = new
        {
            step_index = 0,
            source = "USER_EXPLICIT",
            type = "USER_INPUT",
            created_at = "2026-09-01T12:00:00Z",
            content = "Build an OpenClaude bridge"
        };
        var step1 = new
        {
            step_index = 1,
            source = "MODEL",
            type = "PLANNER_RESPONSE",
            created_at = "2026-09-01T12:00:05Z",
            content = "Bridge designed and operational"
        };

        File.WriteAllLines(transcriptPath, new[]
        {
            JsonSerializer.Serialize(step0),
            JsonSerializer.Serialize(step1)
        });

        var summary = new SessionSummary
        {
            SessionId = "99887766-5544-3322-1100-aabbccddeeff",
            Provider = "antigravity",
            SourcePath = transcriptPath,
            Workspace = _tempDir
        };

        Environment.SetEnvironmentVariable("OPENCLAUDE_CONFIG_DIR", _tempDir);
        try
        {
            var written = resumer.CrossResume(summary, "openclaude", force: true);
            Assert.NotEmpty(written.Paths);
            Assert.True(File.Exists(written.Paths[0]));
            Assert.Contains("openclaude --resume", written.ResumeCommand);

            var read = new OpenClaudeProvider().ReadSession(written.Paths[0]);
            Assert.Equal(2, read.Messages.Count);
            Assert.Equal("Build an OpenClaude bridge", read.Messages[0].Content);
            Assert.Equal("Bridge designed and operational", read.Messages[1].Content);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCLAUDE_CONFIG_DIR", null);
        }
    }



    [Fact]
    public void SessionDatabase_FtsSearch_FindsIndexedContent()
    {
        var dbPath = Path.Combine(_tempDir, "test_index.db");
        using var db = new SessionDatabase(dbPath);

        var summary = new SessionSummary
        {
            SessionId = "sess-12345",
            Provider = "antigravity",
            ProviderDisplayName = "Antigravity",
            Title = "Debugging CoreTempGPU and NVML",
            Workspace = _tempDir,
            StartedAt = DateTime.Now.AddHours(-1),
            LastActiveAt = DateTime.Now,
            MessagesCount = 2,
            SourcePath = Path.Combine(_tempDir, "sample.jsonl")
        };

        var session = new CanonicalSession
        {
            SessionId = summary.SessionId,
            ProviderSlug = summary.Provider,
            Title = summary.Title,
            Messages = new()
            {
                new CanonicalMessage
                {
                    Index = 0,
                    Role = MessageRole.User,
                    Content = "Can you help me fix the Nvidia NVML library linking error in C++?"
                },
                new CanonicalMessage
                {
                    Index = 1,
                    Role = MessageRole.Assistant,
                    Content = "Sure, make sure you link with nvml.lib and include nvml.h in your include path."
                }
            }
        };

        db.UpsertSession(summary, session);

        var recent = db.GetRecentSessions();
        Assert.Single(recent);
        Assert.Equal("sess-12345", recent[0].SessionId);

        // Search for NVML
        var searchResults = db.SearchFts("NVML");
        Assert.NotEmpty(searchResults);
        Assert.Equal("sess-12345", searchResults[0].SessionId);
        Assert.Contains("NVML", searchResults[0].Snippet, StringComparison.OrdinalIgnoreCase);

        // Search for non-existent word
        var emptyResults = db.SearchFts("supercalifragilistic");
        Assert.Empty(emptyResults);
    }

    [Fact]
    public void ProviderRegistry_AllProviders_AdhereToInterfaceInvariants()
    {
        var registry = ProviderRegistry.Default;
        var requiredSlugs = new[] { "antigravity", "openclaude", "cursor", "grok", "pi", "hermes", "opencode" };

        var allProviders = registry.AllProviders;
        Assert.True(allProviders.Count >= requiredSlugs.Length);

        var seenSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in allProviders)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name), "Provider Name must not be empty");
            Assert.False(string.IsNullOrWhiteSpace(p.Slug), "Provider Slug must not be empty");
            Assert.False(string.IsNullOrWhiteSpace(p.CliAlias), "Provider CliAlias must not be empty");

            // Slug and CliAlias must be alphanumeric/dashes (valid command line token)
            Assert.Matches("^[a-z0-9_-]+$", p.Slug);
            Assert.Matches("^[a-z0-9_-]+$", p.CliAlias);

            // Slugs must be strictly unique
            Assert.True(seenSlugs.Add(p.Slug), $"Duplicate provider slug: {p.Slug}");

            // Lookup invariants
            var bySlug = registry.FindBySlug(p.Slug);
            Assert.Same(p, bySlug);

            var byAlias = registry.FindByAlias(p.CliAlias);
            Assert.Same(p, byAlias);

            // SessionRoots must return non-null
            var roots = p.SessionRoots();
            Assert.NotNull(roots);

            // Unknown session ownership must safely return null without throwing
            var nonExistentOwnership = p.OwnsSession($"non_existent_session_{Guid.NewGuid():N}");
            Assert.Null(nonExistentOwnership);

            // ResumeCommand must include the CLI binary (slug or alias) and either the session ID or workspace
            var dummyCmd = p.ResumeCommand("test_id_xyz", @"C:\work");
            Assert.False(string.IsNullOrWhiteSpace(dummyCmd));
            Assert.True(dummyCmd.Contains(p.CliAlias, StringComparison.OrdinalIgnoreCase) ||
                        dummyCmd.Contains(p.Slug, StringComparison.OrdinalIgnoreCase),
                        $"ResumeCommand '{dummyCmd}' should contain either slug '{p.Slug}' or alias '{p.CliAlias}'");
            if (!string.Equals(p.Slug, "cursor", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Contains("test_id_xyz", dummyCmd);
            }
        }

        foreach (var slug in requiredSlugs)
        {
            Assert.True(seenSlugs.Contains(slug), $"Required provider slug '{slug}' is missing from registry");
        }
    }

    [Fact]
    public void ProviderRegistry_AllProviders_ResumeCommands_ComplyWithCliSpecifications()
    {
        var registry = ProviderRegistry.Default;
        var testCases = new[]
        {
            ("ses_019ff1dd-9dc8-7701-ba32-d32e8d4d233c", @"C:\Users\John Doe\Workspaces\App One"),
            ("custom-session-9912", @"D:\"),
            ("conv_abc-xyz_123", @"F:\Repositories\Deep Project\src\")
        };

        foreach (var p in registry.AllProviders)
        {
            foreach (var (sessionId, workspace) in testCases)
            {
                var cmd = p.ResumeCommand(sessionId, workspace);
                Assert.False(string.IsNullOrWhiteSpace(cmd), $"ResumeCommand for {p.Slug} returned empty");

                // Parse and validate syntax grammar via CliSwitchValidator
                Infrastructure.CliSwitchValidator.ValidateResumeCommand(p.Slug, cmd, sessionId, workspace);

                // Verify command line can be parsed into non-empty tokens
                var parsed = Infrastructure.CliSwitchValidator.Parse(cmd);
                Assert.NotEmpty(parsed.Binary);
                Assert.NotEmpty(parsed.Arguments);
            }
        }
    }

    [Fact]
    public void AntigravityProvider_ParseMockTranscript_ExtractsTitleAndMessages()
    {
        var provider = new AntigravityProvider();
        var convDir = Path.Combine(_tempDir, "mock_brain", "00112233-4455-6677-8899-aabbccddeeff", ".system_generated", "logs");
        Directory.CreateDirectory(convDir);
        var transcriptPath = Path.Combine(convDir, "transcript.jsonl");

        var step0 = new
        {
            step_index = 0,
            source = "USER_EXPLICIT",
            type = "USER_INPUT",
            created_at = "2026-09-01T12:00:00Z",
            content = "<USER_REQUEST>\nBuild a high speed session resumer in .NET\n</USER_REQUEST>"
        };

        var step1 = new
        {
            step_index = 1,
            source = "MODEL",
            type = "PLANNER_RESPONSE",
            created_at = "2026-09-01T12:00:05Z",
            thinking = "Analyzing user request to build resumer",
            content = "I will create a WPF application for Windows."
        };

        File.WriteAllLines(transcriptPath, new[]
        {
            JsonSerializer.Serialize(step0),
            JsonSerializer.Serialize(step1)
        });

        var session = provider.ReadSession(transcriptPath);
        Assert.Equal("00112233-4455-6677-8899-aabbccddeeff", session.SessionId);
        Assert.Equal("Build a high speed session resumer in .NET", session.Title);
        Assert.Equal(2, session.Messages.Count);
        Assert.Equal(MessageRole.User, session.Messages[0].Role);
        Assert.Equal(MessageRole.Assistant, session.Messages[1].Role);

        var resumeCmd = provider.ResumeCommand(session.SessionId);
        Assert.Contains("agy --conversation 00112233-4455-6677-8899-aabbccddeeff", resumeCmd);
        Assert.Contains("Gemini 3.1 Pro (High)", resumeCmd);
    }

    [Trait("Category", "LiveSystem")]
    [Fact]
    public void RealSystem_Antigravity_DetectsAndListsLiveConversations()
    {
        var provider = new AntigravityProvider();
        var detect = provider.Detect();
        if (!detect.Installed) return; // Skip if not on environment with Antigravity

        var sessions = provider.ListSessions();
        Assert.NotNull(sessions);
        Assert.NotEmpty(sessions);

        // Read the first session
        var first = sessions[0];
        var canonical = provider.ReadSession(first.Path);
        Assert.NotNull(canonical);
        Assert.False(string.IsNullOrWhiteSpace(canonical.SessionId));
        Assert.Equal("antigravity", canonical.ProviderSlug);

        var resumeCmd = provider.ResumeCommand(canonical.SessionId);
        Assert.StartsWith("agy --conversation", resumeCmd);
    }

    [Fact]
    public void GrokProvider_ReadSession_ExtractsInfoAndResumeCommand()
    {
        var provider = new GrokProvider();
        var mockSessionDir = Path.Combine(_tempDir, "grok_sessions", "C%3A%2FProjects%2FMyApp", "sess-grok-999");
        Directory.CreateDirectory(mockSessionDir);

        var summaryFile = Path.Combine(mockSessionDir, "summary.json");
        var updatesFile = Path.Combine(mockSessionDir, "updates.jsonl");

        var summaryRecord = new
        {
            info = new { id = "sess-grok-999", cwd = "C:\\Projects\\MyApp" },
            generated_title = "Building a fast CLI tool",
            session_summary = "Building a fast CLI tool",
            created_at = "2026-09-02T10:00:00Z",
            last_active_at = "2026-09-02T10:30:00Z",
            num_messages = 2,
            current_model_id = "grok-code"
        };
        File.WriteAllText(summaryFile, JsonSerializer.Serialize(summaryRecord));

        var update1 = new
        {
            method = "session/update",
            @params = new
            {
                sessionId = "sess-grok-999",
                update = new { sessionUpdate = "user_message_chunk", content = "Can you create a fast file parser?" }
            }
        };
        var update2 = new
        {
            method = "session/update",
            @params = new
            {
                sessionId = "sess-grok-999",
                update = new { sessionUpdate = "agent_message_chunk", content = "Yes, here is a zero-allocation streaming parser." }
            }
        };
        File.WriteAllLines(updatesFile, new[]
        {
            JsonSerializer.Serialize(update1),
            JsonSerializer.Serialize(update2)
        });

        // 1. Test ReadSummary
        var summary = provider.ReadSummary(summaryFile);
        Assert.Equal("sess-grok-999", summary.SessionId);
        Assert.Equal("grok", summary.Provider);
        Assert.Equal("Building a fast CLI tool", summary.Title);
        Assert.Equal("C:\\Projects\\MyApp", summary.Workspace);
        Assert.Equal(2, summary.MessagesCount);

        // 2. Test ReadSession
        var session = provider.ReadSession(summaryFile);
        Assert.Equal("sess-grok-999", session.SessionId);
        Assert.Equal("grok", session.ProviderSlug);
        Assert.Equal(2, session.Messages.Count);
        Assert.Equal("Can you create a fast file parser?", session.Messages[0].Content);
        Assert.Equal(MessageRole.User, session.Messages[0].Role);
        Assert.Equal("Yes, here is a zero-allocation streaming parser.", session.Messages[1].Content);
        Assert.Equal(MessageRole.Assistant, session.Messages[1].Role);

        // 3. Test Resume Command
        var resumeCmd = provider.ResumeCommand(session.SessionId);
        Assert.Equal("grok --resume sess-grok-999", resumeCmd);
    }

    [Fact]
    public void UserSettings_DefaultProviders_AndTogglePersistence()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var settings = UserSettings.Load(settingsPath);

        // Verify default enabled set: fresh installs enable every known provider.
        // This is an explicit-choice contract — if a new provider is added to
        // KnownProviderSlugs without updating this list, the test fails loudly.
        Assert.Equal(
            new[] { "antigravity", "grok", "cursor", "openclaude", "pi", "hermes", "opencode", "codex" },
            settings.EnabledProviderSlugs);
        Assert.True(settings.IsProviderEnabled("antigravity"));
        Assert.True(settings.IsProviderEnabled("grok"));
        Assert.True(settings.IsProviderEnabled("cursor"));
        Assert.True(settings.IsProviderEnabled("openclaude"));
        Assert.True(settings.IsProviderEnabled("pi"));
        Assert.True(settings.IsProviderEnabled("hermes"));
        Assert.True(settings.IsProviderEnabled("opencode"));
        Assert.True(settings.IsProviderEnabled("codex"));

        // Toggle and persist
        settings.SetProviderEnabled("pi", false);
        settings.SetProviderEnabled("grok", false);
        settings.Save(settingsPath);

        var reloaded = UserSettings.Load(settingsPath);
        Assert.False(reloaded.IsProviderEnabled("pi"));
        Assert.False(reloaded.IsProviderEnabled("grok"));
        Assert.True(reloaded.IsProviderEnabled("antigravity"));
    }

    [Fact]
    public void UserSettings_OldSettingsFile_KeepsNewProvidersDisabledUntilExplicitlyEnabled()
    {
        // A settings.json written before openclaude/opencode existed must NOT gain
        // them silently on load: new providers stay disabled until the user opts in
        // via the Providers dropdown (Load logs the missing slugs as a notice).
        var settingsPath = Path.Combine(_tempDir, "legacy_settings.json");
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new
        {
            EnabledProviderSlugs = new[] { "antigravity", "grok", "cursor", "pi", "hermes" },
            PreferredTerminal = "WindowsTerminal",
            SelectedTheme = "VS Code Dark Modern",
            RunAsAdmin = false,
            IncludeSubagents = false
        }));

        var settings = UserSettings.Load(settingsPath);

        Assert.True(settings.IsProviderEnabled("antigravity"));
        Assert.False(settings.IsProviderEnabled("openclaude"));
        Assert.False(settings.IsProviderEnabled("opencode"));
        Assert.False(settings.IsProviderEnabled("codex"));

        // Explicit opt-in works and persists.
        settings.SetProviderEnabled("openclaude", true);
        Assert.True(settings.IsProviderEnabled("OpenClaude"));
        var reloaded = UserSettings.Load(settingsPath);
        Assert.True(reloaded.IsProviderEnabled("openclaude"));
    }

    [Fact]
    public void UserSettings_ProviderMatching_IsCaseInsensitiveAndNullSafe()
    {
        var settingsPath = Path.Combine(_tempDir, "case_settings.json");
        var settings = UserSettings.Load(settingsPath);

        Assert.True(settings.IsProviderEnabled("GROK"));
        Assert.True(settings.IsProviderEnabled("GroK"));
        Assert.False(settings.IsProviderEnabled(""));
        Assert.False(settings.IsProviderEnabled("   "));
        Assert.False(settings.IsProviderEnabled("no-such-provider"));

        settings.SetProviderEnabled("PI", false);
        Assert.False(settings.IsProviderEnabled("pi"));
        settings.SetProviderEnabled("PI", true);
        Assert.True(settings.IsProviderEnabled("pi"));
        // No case-duplicate rows accumulate.
        Assert.Single(settings.EnabledProviderSlugs, s => s.Equals("pi", StringComparison.OrdinalIgnoreCase));

        // Hand-edited JSON with a null list or mixed case normalizes on load.
        var messyPath = Path.Combine(_tempDir, "messy_settings.json");
        File.WriteAllText(messyPath, "{\"EnabledProviderSlugs\":null}");
        var nulled = UserSettings.Load(messyPath);
        Assert.NotNull(nulled.EnabledProviderSlugs);
        Assert.Empty(nulled.EnabledProviderSlugs);

        var mixedPath = Path.Combine(_tempDir, "mixed_settings.json");
        File.WriteAllText(mixedPath, "{\"EnabledProviderSlugs\":[\"GROK\",\"grok\",\" Pi \",\"\"]}");
        var mixed = UserSettings.Load(mixedPath);
        Assert.Equal(new[] { "grok", "pi" }, mixed.EnabledProviderSlugs);
        Assert.True(mixed.IsProviderEnabled("GROK"));
    }

    [Fact]
    public void UserSettings_CorruptFile_IsQuarantinedAndDefaultsWritten()
    {
        var settingsPath = Path.Combine(_tempDir, "corrupt_settings.json");
        File.WriteAllText(settingsPath, "{ this is not valid json !!!");

        var settings = UserSettings.Load(settingsPath);

        // Defaults come back, and the broken file is preserved next to the original
        // (never silently overwritten).
        Assert.True(settings.IsProviderEnabled("grok"));
        var quarantined = Directory.GetFiles(_tempDir, "corrupt_settings.json.corrupt-*");
        Assert.Single(quarantined);

        // The path now holds valid defaults that reload cleanly (no second quarantine).
        var reloaded = UserSettings.Load(settingsPath);
        Assert.True(reloaded.IsProviderEnabled("grok"));
        Assert.Single(Directory.GetFiles(_tempDir, "corrupt_settings.json.corrupt-*"));
    }

    [Fact]
    public void UserSettings_UnboundSave_ReturnsFalseAndWritesNothing()
    {
        // Regression for 2026-09-22: `new UserSettings().Save()` overwrote the
        // production settings.json with code defaults. It must now refuse.
        var unbound = new UserSettings();
        Assert.Null(unbound.FilePath);
        Assert.False(unbound.Save());

        Assert.False(unbound.Save(null));
    }

    [Fact]
    public void UserSettings_RunAsAdmin_DefaultFalseAndRoundTrips()
    {
        Assert.False(new UserSettings().RunAsAdmin);

        var path = Path.Combine(_tempDir, "admin_settings.json");
        var settings = UserSettings.Load(path);
        settings.RunAsAdmin = true;
        Assert.True(settings.Save(path));

        var reloaded = UserSettings.Load(path);
        Assert.True(reloaded.RunAsAdmin);
    }

    [Fact]
    public void UserSettings_BypassApprovals_MissingKeyStaysFalse_AndRoundTrips()
    {
        Assert.False(new UserSettings().BypassApprovals);

        var path = Path.Combine(_tempDir, "bypass_settings.json");
        File.WriteAllText(path, """{"EnabledProviderSlugs":["grok"],"RunAsAdmin":false}""");
        var loaded = UserSettings.Load(path);
        Assert.False(loaded.BypassApprovals);

        loaded.BypassApprovals = true;
        Assert.True(loaded.Save(path));
        var reloaded = UserSettings.Load(path);
        Assert.True(reloaded.BypassApprovals);
        Assert.Contains("BypassApprovals", File.ReadAllText(path));
    }

    [Fact]
    public void CasrPaths_SettingsLogAndDb_ShareOneLocalAppRoot()
    {
        var appDir = CasrPaths.AppDir;
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            appDir, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(appDir, CasrPaths.SettingsFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(appDir, CasrPaths.LogFilePath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(appDir, CasrPaths.DefaultDbPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CasrPaths.SettingsFilePath, UserSettings.SettingsFilePath);
    }

    [Fact]
    public void ProviderRegistry_ActiveProviders_FilteredBySettings()
    {
        // Bind to a file: SetProviderEnabled() persists via Save(), and an unbound
        // instance would write the user's REAL settings.json with code defaults.
        var settings = UserSettings.Load(Path.Combine(_tempDir, "registry_settings.json"));
        settings.SetProviderEnabled("antigravity", true);
        settings.SetProviderEnabled("grok", true);
        settings.SetProviderEnabled("cursor", false);

        var registry = new ProviderRegistry(settings: settings);
        Assert.True(registry.IsProviderEnabled("antigravity"));
        Assert.True(registry.IsProviderEnabled("grok"));
        Assert.False(registry.IsProviderEnabled("cursor"));

        var active = registry.ActiveProviders;
        Assert.DoesNotContain(active, p => p.Slug == "cursor");
    }

    [Trait("Category", "LiveSystem")]
    [Fact]
    public void Benchmark_Discovery_IdentifiesBottleneck()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var registry = ProviderRegistry.Default;
        var installed = registry.InstalledProviders;
        
        Assert.NotNull(installed);
        Assert.NotEmpty(installed);
        System.Diagnostics.Debug.WriteLine($"Installed providers: {string.Join(", ", installed.Select(p => p.Name))}");

        foreach (var p in installed)
        {
            var pSw = System.Diagnostics.Stopwatch.StartNew();
            var sessions = p.ListSessions();
            pSw.Stop();
            Assert.NotNull(sessions);
            System.Diagnostics.Debug.WriteLine($"Provider {p.Name}: ListSessions found {sessions.Count} in {pSw.ElapsedMilliseconds} ms");

            if (sessions.Count > 0)
            {
                var rSw = System.Diagnostics.Stopwatch.StartNew();
                var sample = sessions.Take(5).ToList();
                foreach (var s in sample)
                {
                    var c = p.ReadSession(s.Path);
                    Assert.NotNull(c);
                    Assert.False(string.IsNullOrWhiteSpace(c.SessionId));
                }
                rSw.Stop();
                System.Diagnostics.Debug.WriteLine($"Provider {p.Name}: Read 5 sessions in {rSw.ElapsedMilliseconds} ms");
            }
        }
    }

    [Trait("Category", "LiveSystem")]
    [Fact]
    public async Task Benchmark_FullDiscoveryWithDatabase_MeasuresTime()
    {
        var tempDb = Path.Combine(_tempDir, "bench_index.db");
        using var db = new SessionDatabase(tempDb);
        var discovery = new SessionDiscoveryService(ProviderRegistry.Default, db);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var progress = new Progress<ScanProgress>(p =>
        {
            Console.WriteLine($"[{sw.ElapsedMilliseconds} ms] {p.StatusMessage}");
        });

        var results = await discovery.DiscoverAllSessionsAsync(progress);
        sw.Stop();
        Assert.NotNull(results);
        Assert.True(sw.ElapsedMilliseconds > 0);
        var recent = db.GetRecentSessions();
        Assert.NotNull(recent);
        Assert.Equal(results.Count, recent.Count);
        Console.WriteLine($"Total time for {results.Count} sessions: {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void ModelHelpers_IsSubagentPrompt_DetectsAgentPrompts()
    {
        Assert.True(ModelHelpers.IsSubagentPrompt("You are an Independent Layout & Proportionality Auditor..."));
        Assert.True(ModelHelpers.IsSubagentPrompt("<USER_REQUEST>\nYou are the High-Resolution Distortion & Multiplier Mechanics Specialist...\n</USER_REQUEST>"));
        Assert.True(ModelHelpers.IsSubagentPrompt("Your task is to analyze the codebase and report back."));
        Assert.True(ModelHelpers.IsSubagentPrompt("Your role is to verify all assertions."));
        Assert.True(ModelHelpers.IsSubagentPrompt("You are a helpful subagent that inspects code."));

        // User conversations should NOT be flagged
        Assert.False(ModelHelpers.IsSubagentPrompt("Make sure grok build cli is included"));
        Assert.False(ModelHelpers.IsSubagentPrompt("<USER_REQUEST>Help me fix the login bug</USER_REQUEST>"));
        Assert.False(ModelHelpers.IsSubagentPrompt("Very simple. Obtain this. Make it a .NET GUI application."));
        Assert.False(ModelHelpers.IsSubagentPrompt(null));
        Assert.False(ModelHelpers.IsSubagentPrompt(""));
    }

    [Fact]
    public void ModelHelpers_DecodeFileUri_HandlesVariousEncodings()
    {
        var decoded1 = ModelHelpers.DecodeFileUri("file:///d%3A/%23DEV/sample/project");
        Assert.NotNull(decoded1);
        Assert.Contains("#DEV", decoded1);
        Assert.StartsWith("d:", decoded1, StringComparison.OrdinalIgnoreCase);

        var decoded2 = ModelHelpers.DecodeFileUri("file:///C:/Users/test/myrepo");
        Assert.NotNull(decoded2);
        Assert.StartsWith("c:", decoded2, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("myrepo", decoded2);

        var decoded3 = ModelHelpers.DecodeFileUri("D:\\Workspace\\sample\\casr");
        Assert.NotNull(decoded3);
        Assert.Equal("D:\\Workspace\\sample\\casr", decoded3);
    }

    [Fact]
    public void CursorProvider_ResolveWorkspace_FromSiblingWorkspaceJson()
    {
        var wsDir = Path.Combine(_tempDir, "workspaceStorage", "test_hash_123");
        Directory.CreateDirectory(wsDir);
        var wsJson = Path.Combine(wsDir, "workspace.json");
        File.WriteAllText(wsJson, "{\"folder\": \"file:///v%3A/SampleApp\"}");

        var dbFile = Path.Combine(wsDir, "state.vscdb");
        File.WriteAllBytes(dbFile, Array.Empty<byte>());

        var provider = new CursorProvider();
        var summary = provider.ReadSummary($"{dbFile}::test-session");

        Assert.NotNull(summary.Workspace);
        Assert.StartsWith("v:", summary.Workspace, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SampleApp", summary.Workspace);
    }

    [Fact]
    public void SessionDatabase_SubagentPersistence_RoundTrips()
    {
        var tempDb = Path.Combine(_tempDir, "test_subagents.db");
        using var db = new SessionDatabase(tempDb);

        var userSession = new SessionSummary
        {
            SessionId = "user-sess-1",
            Provider = "antigravity",
            ProviderDisplayName = "Antigravity",
            Title = "Real User Conversation",
            Workspace = "C:\\Projects\\App",
            IsSubagent = false
        };

        var subagentSession = new SessionSummary
        {
            SessionId = "sub-sess-2",
            Provider = "antigravity",
            ProviderDisplayName = "Antigravity",
            Title = "You are an Independent Auditor",
            Workspace = null,
            IsSubagent = true
        };

        db.UpsertSummary(userSession);
        db.UpsertSummary(subagentSession);

        var recent = db.GetRecentSessions(10);
        Assert.Equal(2, recent.Count);

        var fetchedUser = recent.First(s => s.SessionId == "user-sess-1");
        var fetchedSub = recent.First(s => s.SessionId == "sub-sess-2");

        Assert.False(fetchedUser.IsSubagent);
        Assert.Equal("C:\\Projects\\App", fetchedUser.Workspace);

        Assert.True(fetchedSub.IsSubagent);
    }

    [Fact]
    public void BackupService_PathTokenization_RoundTrips()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var testPath = Path.Combine(appData, "Cursor", "User", "test.json");

        var tokenized = BackupService.TokenizePath(testPath);
        Assert.StartsWith("%APPDATA%", tokenized);

        var expanded = BackupService.ExpandPath(tokenized);
        Assert.Equal(testPath, expanded);
    }

    [Fact]
    public async Task BackupService_CreateInspectAndRestore_RoundTrips()
    {
        // 1. Setup mock agent file (provider-neutral path under the temp root)
        var mockDir = Path.Combine(_tempDir, "mock_agent", "state");
        Directory.CreateDirectory(mockDir);
        var historyFile = Path.Combine(mockDir, "history.json");
        File.WriteAllText(historyFile, "[{\"id\":\"task-1\",\"task\":\"Test Task\"}]");

        var backupZip = Path.Combine(_tempDir, "test_backup.zip");
        var service = new BackupService();

        // Allow the restore guard to write into this test's temp tree.
        Environment.SetEnvironmentVariable("CASR_BACKUP_EXTRA_ROOTS", _tempDir);
        try
        {
            // 2. Build an archive with a single manifest entry pointing at the mock file
            using (var zipStream = new FileStream(backupZip, FileMode.Create))
            using (var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("raw/agent/state/history.json");
                using (var writer = new StreamWriter(entry.Open()))
                {
                    await writer.WriteAsync("[{\"id\":\"task-1\",\"task\":\"Test Task\"}]");
                }

                var manifest = new BackupManifest
                {
                    TotalFilesCount = 1,
                    Files = new()
                    {
                        new BackupFileEntry
                        {
                            ZipPath = "raw/agent/state/history.json",
                            DestinationTokenPath = historyFile,
                            Provider = "grok",
                            SizeBytes = 36
                        }
                    }
                };
                var mEntry = archive.CreateEntry("backup_manifest.json");
                using (var mWriter = new StreamWriter(mEntry.Open()))
                {
                    await mWriter.WriteAsync(JsonSerializer.Serialize(manifest));
                }
            }

            // 3. Inspect backup
            var inspected = BackupService.InspectBackup(backupZip);
            Assert.NotNull(inspected);
            Assert.Single(inspected.Files);
            Assert.Equal("grok", inspected.Files[0].Provider);

            // 4. Test Restore
            var restoredCount = await service.RestoreBackupAsync(backupZip, new RestoreOptions { CreateBackupCopies = true });
            Assert.Equal(1, restoredCount);
            Assert.True(File.Exists(historyFile));

            var content = await File.ReadAllTextAsync(historyFile);
            Assert.Contains("Test Task", content);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CASR_BACKUP_EXTRA_ROOTS", null);
        }
    }

    [Trait("Category", "LiveSystem")]
    [Fact]
    public void BackupService_CollectItems_FindsRealAgentFiles()
    {
        var service = new BackupService();
        var scope = new BackupScope();
        var items = service.CollectItems(scope);

        Assert.NotEmpty(items);
        Assert.Contains(items, i => i.Provider == "antigravity");
        Assert.Contains(items, i => i.Provider == "cursor");
        Assert.Contains(items, i => i.Provider == "grok");
    }

    [Trait("Category", "LiveSystem")]
    [Fact]
    public async Task BackupService_CreateRealBackup_GeneratesValidArchive()
    {
        var backupDir = Path.Combine(_tempDir, "real_backup");
        Directory.CreateDirectory(backupDir);
        var targetZip = Path.Combine(backupDir, "agents_backup.zip");

        var service = new BackupService();
        var scope = new BackupScope
        {
            IncludeAntigravity = true,
            IncludeCursor = true,
            IncludeGrok = true,
            IncludeCasrDatabase = true,
            IncludeCanonicalExport = false
        };

        var manifest = await service.CreateBackupAsync(targetZip, scope);
        Assert.NotNull(manifest);
        Assert.True(File.Exists(targetZip));
        Assert.True(new FileInfo(targetZip).Length > 1000);

        var inspected = BackupService.InspectBackup(targetZip);
        Assert.NotNull(inspected);
        Assert.Equal(manifest.TotalFilesCount, inspected.TotalFilesCount);
    }

    [Fact]
    public void UserSettings_SelectedTheme_RealDisplayNameAndIdRoundTrip()
    {
        // "Tokyo Night" was never a real theme (the DisplayName is "Tokyo Night
        // Storm", the Id "tokyo-night"): the old test passed a fantasy value and
        // proved nothing. Round-trip values ThemeManager actually serves.
        foreach (var themeValue in new[] { "Tokyo Night Storm", "tokyo-night", "VS Code Dark Modern", "vscode-dark" })
        {
            var settingsPath = Path.Combine(_tempDir, $"theme_{Guid.NewGuid():N}.json");
            var settings = new UserSettings
            {
                FilePath = settingsPath,
                SelectedTheme = themeValue
            };
            Assert.True(settings.Save(settingsPath));

            Assert.True(File.Exists(settingsPath));
            var loaded = UserSettings.Load(settingsPath);
            Assert.Equal(themeValue, loaded.SelectedTheme);
        }
    }

    [Fact]
    public void UserSettings_UnknownThemeValue_SurvivesRoundTripVerbatim()
    {
        // Unknown theme strings must reach ThemeManager.GetTheme untouched so it can
        // warn and fall back to the default; settings must not "fix" or drop them.
        var settingsPath = Path.Combine(_tempDir, "unknown_theme.json");
        File.WriteAllText(settingsPath, "{\"SelectedTheme\":\"No Such Theme\"}");

        var loaded = UserSettings.Load(settingsPath);
        Assert.Equal("No Such Theme", loaded.SelectedTheme);
    }

    [Fact]
    public void ModelHelpers_CleanTitle_StripsSlashCommandsXmlTagsAndMarkdown()
    {
        Assert.Equal("Fix the navigation bar issue", ModelHelpers.CleanTitle("/plan Fix the navigation bar issue"));
        Assert.Equal("Optimize Database Queries", ModelHelpers.CleanTitle("<USER_REQUEST>\n/plan Optimize Database Queries\n</USER_REQUEST>"));
        Assert.Equal("What is CASR architecture?", ModelHelpers.CleanTitle("/ask What is CASR architecture?"));
        Assert.Equal("Performance enhancements for resumer", ModelHelpers.CleanTitle("/brainstorm Performance enhancements for resumer"));
        Assert.Equal("Refactor database logic", ModelHelpers.CleanTitle("### Refactor database logic"));
        Assert.Equal("Setup CI/CD build pipeline", ModelHelpers.CleanTitle("- Setup CI/CD build pipeline"));
        Assert.Equal("Line 1 Line 2 Line 3", ModelHelpers.CleanTitle("Line 1\r\nLine 2\r\nLine 3"));
        Assert.Equal("(Untitled)", ModelHelpers.CleanTitle("<USER_REQUEST></USER_REQUEST>"));
        Assert.Equal("(Untitled)", ModelHelpers.CleanTitle(null));
        Assert.Equal("(Untitled)", ModelHelpers.CleanTitle(""));
        Assert.Equal("(Untitled)", ModelHelpers.CleanTitle("/plan"));
        Assert.Equal("Migrate database schema", ModelHelpers.CleanTitle("<USER_REQUEST>\n### /plan: Migrate database schema\n</USER_REQUEST>"));
    }

    [Fact]
    public void AntigravityProvider_PrefersGenuineTitleOverPreviewAndLastPrompt()
    {
        var mockAgyDir = Path.Combine(_tempDir, "mock_gemini", "antigravity-cli");
        Directory.CreateDirectory(mockAgyDir);
        var dbPath = Path.Combine(mockAgyDir, "conversation_summaries.db");
        var historyPath = Path.Combine(mockAgyDir, "history.jsonl");

        // 1. Create SQLite DB with conversation_summaries
        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE conversation_summaries (
                    conversation_id TEXT PRIMARY KEY,
                    title TEXT,
                    preview TEXT,
                    step_count INTEGER,
                    last_modified_time TEXT,
                    workspace_uris TEXT,
                    parent_conversation_id TEXT,
                    nesting_depth INTEGER
                );
                INSERT INTO conversation_summaries VALUES (
                    'sess-pref-test-123',
                    '/plan Build High-Speed Resumer',
                    'Done. All 45 files have been created successfully.',
                    10,
                    '2026-09-01T15:00:00Z',
                    null,
                    null,
                    0
                );";
            cmd.ExecuteNonQuery();
        }

        // 2. Create history.jsonl with a different last prompt
        File.WriteAllLines(historyPath, new[]
        {
            JsonSerializer.Serialize(new
            {
                conversationId = "sess-pref-test-123",
                workspace = "C:\\Projects\\Resumer",
                display = "Can you also add a dark theme button?"
            })
        });

        // 3. Create mock transcript in brain
        var brainDir = Path.Combine(mockAgyDir, "brain", "sess-pref-test-123", ".system_generated", "logs");
        Directory.CreateDirectory(brainDir);
        var transcriptPath = Path.Combine(brainDir, "transcript.jsonl");
        File.WriteAllLines(transcriptPath, new[]
        {
            JsonSerializer.Serialize(new
            {
                step_index = 0,
                source = "USER_EXPLICIT",
                type = "USER_INPUT",
                created_at = "2026-09-01T14:00:00Z",
                content = "<USER_REQUEST>\nBuild High-Speed Resumer\n</USER_REQUEST>"
            }),
            JsonSerializer.Serialize(new
            {
                step_index = 1,
                source = "USER_EXPLICIT",
                type = "USER_INPUT",
                created_at = "2026-09-01T14:50:00Z",
                content = "Can you also add a dark theme button?"
            }),
            JsonSerializer.Serialize(new
            {
                step_index = 2,
                source = "MODEL",
                type = "PLANNER_RESPONSE",
                created_at = "2026-09-01T15:00:00Z",
                content = "Done. All 45 files have been created successfully."
            })
        });

        Environment.SetEnvironmentVariable("GEMINI_HOME", Path.Combine(_tempDir, "mock_gemini"));
        AntigravityProvider.InvalidateCache();
        try
        {
            var provider = new AntigravityProvider();

            // Read summary: MUST pick genuine title, NOT preview, NOT last prompt, and stripped of /plan
            var summary = provider.ReadSummary(dbPath);
            Assert.Equal("Build High-Speed Resumer", summary.Title);
            Assert.NotEqual("Done. All 45 files have been created successfully.", summary.Title);
            Assert.NotEqual("Can you also add a dark theme button?", summary.Title);
            Assert.NotNull(summary.Title);
            Assert.False(summary.Title.StartsWith("/plan"));

            // Read session: MUST pick genuine title
            var session = provider.ReadSession(dbPath);
            Assert.Equal("Build High-Speed Resumer", session.Title);
            Assert.NotEqual("Done. All 45 files have been created successfully.", session.Title);
            Assert.NotEqual("Can you also add a dark theme button?", session.Title);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEMINI_HOME", null);
            AntigravityProvider.InvalidateCache();
        }
    }

    [Fact]
    public void AntigravityProvider_FastExtractSummary_ExtractsFirstUserStepWhenNoTitleInDb()
    {
        var mockAgyDir = Path.Combine(_tempDir, "mock_gemini2", "antigravity-cli");
        Directory.CreateDirectory(mockAgyDir);

        var brainDir = Path.Combine(mockAgyDir, "brain", "sess-no-db-title", ".system_generated", "logs");
        Directory.CreateDirectory(brainDir);
        var transcriptPath = Path.Combine(brainDir, "transcript.jsonl");

        File.WriteAllLines(transcriptPath, new[]
        {
            JsonSerializer.Serialize(new
            {
                step_index = -1,
                source = "SYSTEM",
                type = "CONVERSATION_HISTORY",
                created_at = "2026-09-01T10:00:00Z",
                content = "Session initialized with system prompt"
            }),
            JsonSerializer.Serialize(new
            {
                step_index = 0,
                source = "USER_EXPLICIT",
                type = "USER_INPUT",
                created_at = "2026-09-01T10:01:00Z",
                content = "<USER_REQUEST>\n/ask Fix the memory leak in parser\n</USER_REQUEST>"
            }),
            JsonSerializer.Serialize(new
            {
                step_index = 1,
                source = "MODEL",
                type = "PLANNER_RESPONSE",
                created_at = "2026-09-01T10:02:00Z",
                content = "Analyzing memory profiling data"
            }),
            JsonSerializer.Serialize(new
            {
                step_index = 2,
                source = "USER_EXPLICIT",
                type = "USER_INPUT",
                created_at = "2026-09-01T10:05:00Z",
                content = "Thanks, problem is solved!"
            })
        });

        Environment.SetEnvironmentVariable("GEMINI_HOME", Path.Combine(_tempDir, "mock_gemini2"));
        AntigravityProvider.InvalidateCache();
        try
        {
            var provider = new AntigravityProvider();
            var summary = provider.ReadSummary(transcriptPath);

            Assert.Equal("Fix the memory leak in parser", summary.Title);
            Assert.NotEqual("Thanks, problem is solved!", summary.Title);
            Assert.NotEqual("Session initialized with system prompt", summary.Title);
            Assert.NotNull(summary.Title);
            Assert.False(summary.Title.StartsWith("/ask"));

            var session = provider.ReadSession(transcriptPath);
            Assert.Equal("Fix the memory leak in parser", session.Title);
            Assert.NotEqual("Thanks, problem is solved!", session.Title);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEMINI_HOME", null);
            AntigravityProvider.InvalidateCache();
        }
    }

    [Fact]
    public void GrokProvider_UsesInitialUserPrompt_WhenNoGeneratedTitle()
    {
        var provider = new GrokProvider();
        var mockSessionDir = Path.Combine(_tempDir, "grok_no_title", "C%3A%2FTestApp", "sess-grok-notitle");
        Directory.CreateDirectory(mockSessionDir);

        var summaryFile = Path.Combine(mockSessionDir, "summary.json");
        var updatesFile = Path.Combine(mockSessionDir, "updates.jsonl");

        var summaryRecord = new
        {
            info = new { id = "sess-grok-notitle", cwd = "C:\\TestApp" },
            created_at = "2026-09-03T10:00:00Z",
            last_active_at = "2026-09-03T10:30:00Z",
            num_messages = 4,
            current_model_id = "grok-code"
        };
        File.WriteAllText(summaryFile, JsonSerializer.Serialize(summaryRecord));

        var update1 = new
        {
            method = "session/update",
            @params = new
            {
                sessionId = "sess-grok-notitle",
                update = new { sessionUpdate = "user_message_chunk", content = "/plan Create high-throughput streaming buffer" }
            }
        };
        var update2 = new
        {
            method = "session/update",
            @params = new
            {
                sessionId = "sess-grok-notitle",
                update = new { sessionUpdate = "agent_message_chunk", content = "Implementing buffer ring" }
            }
        };
        var update3 = new
        {
            method = "session/update",
            @params = new
            {
                sessionId = "sess-grok-notitle",
                update = new { sessionUpdate = "user_message_chunk", content = "Can you make it thread-safe?" }
            }
        };
        File.WriteAllLines(updatesFile, new[]
        {
            JsonSerializer.Serialize(update1),
            JsonSerializer.Serialize(update2),
            JsonSerializer.Serialize(update3)
        });

        var summary = provider.ReadSummary(summaryFile);
        Assert.Equal("Create high-throughput streaming buffer", summary.Title);
        Assert.NotEqual("Can you make it thread-safe?", summary.Title);
        Assert.NotNull(summary.Title);
        Assert.False(summary.Title.StartsWith("/plan"));

        var session = provider.ReadSession(summaryFile);
        Assert.Equal("Create high-throughput streaming buffer", session.Title);
        Assert.NotEqual("Can you make it thread-safe?", session.Title);
    }

    // ===== OPENCODE PROVIDER TESTS =====

    [Fact]
    public void OpenCodeProvider_ResumeCommand_AndSessionRoots_VerifyInvariants()
    {
        var provider = new OpenCodeProvider();
        Assert.Equal("opencode", provider.Slug);
        Assert.Equal("OpenCode", provider.Name);
        Assert.Equal("opencode", provider.CliAlias);

        var resumeCmd1 = provider.ResumeCommand("ses_1234abcdef");
        Assert.Equal("opencode -s ses_1234abcdef", resumeCmd1);

        var resumeCmd2 = provider.ResumeCommand("ses_custom", @"C:\Projects\MyRepo");
        Assert.Contains("-s ses_custom", resumeCmd2);

        var roots = provider.SessionRoots();
        Assert.NotNull(roots);
        Assert.NotEmpty(roots);
        Assert.Contains(roots, r => r.Contains("opencode", StringComparison.OrdinalIgnoreCase));
    }

    [Trait("Category", "LiveSystem")]
    [Fact]
    public void OpenCodeProvider_Detect_IdentifiesRealSystemOpenCode()
    {
        var provider = new OpenCodeProvider();
        var detect = provider.Detect();
        Assert.True(detect.Installed);
        Assert.NotEmpty(detect.Evidence);
    }

    [Trait("Category", "LiveSystem")]
    [Fact]
    public void OpenCodeProvider_ReadRealSystemSessions_ExtractsTitlesAndWorkspaces()
    {
        var provider = new OpenCodeProvider();
        var sessions = provider.ListSessions();
        Assert.NotNull(sessions);
        Assert.NotEmpty(sessions);

        var first = sessions[0];
        Assert.False(string.IsNullOrWhiteSpace(first.SessionId));

        var summary = provider.ReadSummary(first.Path);
        Assert.Equal(first.SessionId, summary.SessionId);
        Assert.Equal("opencode", summary.Provider);
        Assert.Equal("OpenCode", summary.ProviderDisplayName);
        Assert.False(string.IsNullOrWhiteSpace(summary.Title));
        Assert.NotNull(summary.StartedAt);
    }

    [Fact]
    public void OpenCodeProvider_MockDatabase_ReadsSummaryAndSession()
    {
        var mockDir = Path.Combine(_tempDir, "mock_opencode");
        Directory.CreateDirectory(mockDir);
        var dbPath = Path.Combine(mockDir, "opencode.db");

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE session (
                    id TEXT PRIMARY KEY,
                    project_id TEXT,
                    parent_id TEXT,
                    slug TEXT,
                    directory TEXT,
                    title TEXT,
                    version TEXT,
                    time_created INTEGER,
                    time_updated INTEGER,
                    model TEXT,
                    cost REAL,
                    tokens_input INTEGER,
                    tokens_output INTEGER,
                    tokens_reasoning INTEGER,
                    tokens_cache_read INTEGER,
                    tokens_cache_write INTEGER,
                    metadata TEXT,
                    workspace_id TEXT,
                    path TEXT,
                    agent TEXT
                );
                CREATE TABLE message (
                    id TEXT PRIMARY KEY,
                    session_id TEXT,
                    time_created INTEGER,
                    time_updated INTEGER,
                    data TEXT
                );
                CREATE TABLE part (
                    id TEXT PRIMARY KEY,
                    message_id TEXT,
                    session_id TEXT,
                    time_created INTEGER,
                    time_updated INTEGER,
                    data TEXT
                );

                INSERT INTO session (id, project_id, parent_id, slug, directory, title, time_created, time_updated, model, tokens_input, tokens_output) VALUES (
                    'ses_mockopencode001', 'global', NULL, 'mock-slug', 'C:\Test\OpenCodeApp',
                    'Migrate database schema', 1700000000000, 1700001000000,
                    '{""id"":""gpt-4o"",""providerID"":""openai""}', 5000, 1200
                );
                INSERT INTO message VALUES (
                    'msg_001', 'ses_mockopencode001', 1700000000000, 1700000001000,
                    '{""role"":""user"",""time"":{""created"":1700000000000}}'
                );
                INSERT INTO message VALUES (
                    'msg_002', 'ses_mockopencode001', 1700000010000, 1700000020000,
                    '{""role"":""assistant"",""time"":{""created"":1700000010000,""completed"":1700000020000},""cost"":0.01,""tokens"":{""total"":500,""input"":400,""output"":100,""reasoning"":0,""cache"":{""write"":0,""read"":0}}}'
                );
                INSERT INTO part VALUES (
                    'prt_001', 'msg_001', 'ses_mockopencode001', 1700000000000, 1700000001000,
                    '{""type"":""text"",""text"":""Migrate my database schema to use UUIDs""}'
                );
                INSERT INTO part VALUES (
                    'prt_002', 'msg_002', 'ses_mockopencode001', 1700000010000, 1700000020000,
                    '{""type"":""text"",""text"":""I will help you migrate the schema.""}'
                );
                INSERT INTO part VALUES (
                    'prt_003', 'msg_002', 'ses_mockopencode001', 1700000012000, 1700000015000,
                    '{""type"":""tool"",""tool"":{""id"":""call_xyz"",""name"":""bash"",""input"":""{}"",""output"":""migration done"",""state"":""completed""}}'
                );
            ";
            cmd.ExecuteNonQuery();
        }

        Environment.SetEnvironmentVariable("OPENCODE_HOME", mockDir);
        try
        {
            var provider = new OpenCodeProvider();

            var summary = provider.ReadSummary($"{dbPath}::ses_mockopencode001");
            Assert.Equal("ses_mockopencode001", summary.SessionId);
            Assert.Equal("opencode", summary.Provider);
            Assert.Equal("Migrate database schema", summary.Title);
            Assert.Equal("C:\\Test\\OpenCodeApp", summary.Workspace);
            Assert.Equal(2, summary.MessagesCount);
            Assert.NotNull(summary.StartedAt);

            var session = provider.ReadSession($"{dbPath}::ses_mockopencode001");
            Assert.Equal("ses_mockopencode001", session.SessionId);
            Assert.Equal("opencode", session.ProviderSlug);
            Assert.Equal("Migrate database schema", session.Title);
            Assert.Equal(2, session.Messages.Count);
            Assert.Equal(MessageRole.User, session.Messages[0].Role);
            Assert.Contains("Migrate my database schema", session.Messages[0].Content);
            Assert.Equal(MessageRole.Assistant, session.Messages[1].Role);
            Assert.Contains("I will help you migrate the schema.", session.Messages[1].Content);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENCODE_HOME", null);
        }
    }

    [Fact]
    public void HermesProvider_ResumeCommand_AndContract_VerifyInvariants()
    {
        var provider = new HermesProvider();
        Assert.Equal("hermes", provider.Slug);
        Assert.Equal("Hermes", provider.Name);
        Assert.Equal("hermes", provider.CliAlias);

        var resumeCmd = provider.ResumeCommand("sess-test-456");
        Assert.Equal("hermes --resume sess-test-456", resumeCmd);

        var registry = ProviderRegistry.Default;
        Assert.NotNull(registry.FindBySlug("hermes"));
        Assert.NotNull(registry.FindByAlias("hermes"));

        var roots = provider.SessionRoots();
        Assert.NotEmpty(roots);
        Assert.Contains("hermes", roots[0], StringComparison.OrdinalIgnoreCase);

        // Hermes can host injected history via `hermes sessions import --from claude` (JSONL shim).
        Assert.True(provider.CanWrite);
        Assert.Throws<InvalidOperationException>(() => provider.WriteSession(new CanonicalSession(), new WriteOptions()));
    }

    [Trait("Category", "LiveSystem")]
    [Fact]
    public void HermesProvider_Detect_IdentifiesRealSystemHermes()
    {
        var provider = new HermesProvider();
        var detect = provider.Detect();

        // On this environment, Hermes is installed with state.db and hermes.exe
        Assert.True(detect.Installed);
        Assert.NotEmpty(detect.Evidence);
        Assert.Contains(detect.Evidence, e => e.Contains("state.db") || e.Contains("hermes.exe") || e.Contains("Hermes"));
    }

    [Trait("Category", "LiveSystem")]
    [Fact]
    public void HermesProvider_ReadRealSystemSessions_ExtractsSessionsAndMessages()
    {
        var provider = new HermesProvider();
        var sessions = provider.ListSessions();
        Assert.NotNull(sessions);
        Assert.NotEmpty(sessions);

        var first = sessions[0];
        Assert.False(string.IsNullOrWhiteSpace(first.SessionId));
        Assert.Contains("::", first.Path);

        // Test ReadSummary
        var summary = provider.ReadSummary(first.Path);
        Assert.Equal(first.SessionId, summary.SessionId);
        Assert.Equal("hermes", summary.Provider);
        Assert.Equal("Hermes", summary.ProviderDisplayName);
        Assert.False(string.IsNullOrWhiteSpace(summary.Title));
        Assert.True(summary.MessagesCount > 0);
        Assert.NotNull(summary.StartedAt);

        // Test ReadSession
        var session = provider.ReadSession(first.Path);
        Assert.Equal(first.SessionId, session.SessionId);
        Assert.Equal("hermes", session.ProviderSlug);
        Assert.Equal(summary.Title, session.Title);
        Assert.NotEmpty(session.Messages);

        // Verify OwnsSession
        var ownedPath = provider.OwnsSession(first.SessionId);
        Assert.NotNull(ownedPath);
        Assert.Equal(first.Path, ownedPath);
    }

    [Fact]
    public void HermesProvider_MockDatabase_FullCoverage()
    {
        var mockDbDir = Path.Combine(_tempDir, "mock_hermes");
        Directory.CreateDirectory(mockDbDir);
        var dbPath = Path.Combine(mockDbDir, "state.db");

        using (var conn = new SqliteConnection($"Data Source={dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE sessions (
                    id TEXT PRIMARY KEY,
                    source TEXT,
                    user_id TEXT,
                    session_key TEXT,
                    chat_id TEXT,
                    chat_type TEXT,
                    thread_id TEXT,
                    display_name TEXT,
                    origin_json TEXT,
                    expiry_finalized INTEGER,
                    model TEXT,
                    model_config TEXT,
                    system_prompt TEXT,
                    system_prompt_hash TEXT,
                    parent_session_id TEXT,
                    started_at REAL,
                    ended_at REAL,
                    end_reason TEXT,
                    message_count INTEGER,
                    tool_call_count INTEGER,
                    input_tokens INTEGER,
                    output_tokens INTEGER,
                    cache_read_tokens INTEGER,
                    cache_write_tokens INTEGER,
                    reasoning_tokens INTEGER,
                    cwd TEXT,
                    git_branch TEXT,
                    git_repo_root TEXT,
                    billing_provider TEXT,
                    billing_base_url TEXT,
                    billing_mode TEXT,
                    estimated_cost_usd REAL,
                    actual_cost_usd REAL,
                    cost_status TEXT,
                    cost_source TEXT,
                    pricing_version TEXT,
                    title TEXT,
                    title_source TEXT,
                    last_activity_at REAL,
                    last_activity_description TEXT,
                    last_activity_provenance TEXT,
                    api_call_count INTEGER,
                    handoff_state TEXT,
                    handoff_platform TEXT,
                    handoff_error TEXT,
                    compression_failure_cooldown_until REAL,
                    compression_failure_error TEXT,
                    compression_fallback_streak INTEGER,
                    compression_ineffective_count INTEGER,
                    profile_name TEXT,
                    rewind_count INTEGER,
                    archived INTEGER,
                    pinned INTEGER,
                    last_read_at REAL
                );

                CREATE TABLE messages (
                    id INTEGER PRIMARY KEY,
                    session_id TEXT,
                    role TEXT,
                    content TEXT,
                    tool_call_id TEXT,
                    tool_calls TEXT,
                    tool_name TEXT,
                    effect_disposition TEXT,
                    timestamp REAL,
                    token_count INTEGER,
                    finish_reason TEXT,
                    reasoning TEXT,
                    reasoning_content TEXT,
                    reasoning_details TEXT,
                    platform_message_id TEXT,
                    observed INTEGER,
                    active INTEGER,
                    compacted INTEGER,
                    api_content TEXT,
                    display_kind TEXT,
                    display_metadata TEXT
                );

                INSERT INTO sessions (
                    id, source, display_name, title, cwd, model, started_at, ended_at, last_activity_at, message_count, tool_call_count, parent_session_id
                ) VALUES (
                    'mock-hermes-001', 'cli', NULL, '/plan Refactor Core Parser', 'C:\Test\HermesApp', 'deepseek-v4', 1700000000.0, 1700000100.0, 1700000090.0, 3, 1, NULL
                );

                INSERT INTO messages (id, session_id, role, content, tool_call_id, tool_calls, tool_name, timestamp, reasoning)
                VALUES (
                    1, 'mock-hermes-001', 'user', '/plan Refactor Core Parser', NULL, NULL, NULL, 1700000000.0, NULL
                );

                INSERT INTO messages (id, session_id, role, content, tool_call_id, tool_calls, tool_name, timestamp, reasoning)
                VALUES (
                    2, 'mock-hermes-001', 'assistant', 'I will search for files.', NULL,
                    '[ { ""id"": ""call_abc123"", ""function"": { ""name"": ""find_files"", ""arguments"": ""{\""pattern\"": \""*.cs\""}"" } } ]',
                    NULL, 1700000010.0, 'User wants to refactor the core parser.'
                );

                INSERT INTO messages (id, session_id, role, content, tool_call_id, tool_calls, tool_name, timestamp, reasoning)
                VALUES (
                    3, 'mock-hermes-001', 'tool', '{""matches"": 5}', 'call_abc123', NULL, 'find_files', 1700000015.0, NULL
                );
            ";
            cmd.ExecuteNonQuery();
        }

        var provider = new HermesProvider();

        // 1. Read Summary with path containing ::
        var summary = provider.ReadSummary($"{dbPath}::mock-hermes-001");
        Assert.Equal("mock-hermes-001", summary.SessionId);
        Assert.Equal("Refactor Core Parser", summary.Title);
        Assert.Equal("C:\\Test\\HermesApp", summary.Workspace);
        Assert.Equal("deepseek-v4", summary.ModelName);
        Assert.Equal(3, summary.MessagesCount);
        Assert.Equal(1, summary.ToolCallsCount);
        Assert.False(summary.IsSubagent);

        // 2. Read Session
        var canonical = provider.ReadSession($"{dbPath}::mock-hermes-001");
        Assert.Equal("mock-hermes-001", canonical.SessionId);
        Assert.Equal("hermes", canonical.ProviderSlug);
        Assert.Equal(3, canonical.Messages.Count);

        // Message 0: User
        Assert.Equal(MessageRole.User, canonical.Messages[0].Role);
        Assert.Equal("/plan Refactor Core Parser", canonical.Messages[0].Content);

        // Message 1: Assistant with ToolCall
        Assert.Equal(MessageRole.Assistant, canonical.Messages[1].Role);
        Assert.Single(canonical.Messages[1].ToolCalls);
        Assert.Equal("call_abc123", canonical.Messages[1].ToolCalls[0].Id);
        Assert.Equal("find_files", canonical.Messages[1].ToolCalls[0].Name);
        Assert.Contains("*.cs", canonical.Messages[1].ToolCalls[0].ArgumentsJson);
        Assert.Equal("User wants to refactor the core parser.", canonical.Messages[1].Extra["reasoning"]);

        // Message 2: Tool result
        Assert.Equal(MessageRole.Tool, canonical.Messages[2].Role);
        Assert.Single(canonical.Messages[2].ToolResults);
        Assert.Equal("call_abc123", canonical.Messages[2].ToolResults[0].CallId);
        Assert.Equal("find_files", canonical.Messages[2].Extra["tool_name"]);
    }
}
