using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Casr.Core.Context.Capabilities;
using Casr.Core.Context.Pipeline;
using Casr.Core.Configuration;
using Casr.Core.Models;
using Casr.Core.Providers;
using Casr.Core.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Casr.Core.Tests;

/// <summary>
/// Conversion pipeline tests covering the casr-parity features: dry-run preview (no
/// writes), validation notes, enrichment messages, reasoning drop, context budget,
/// read-back verification, and rollback on verification failure.
/// Hermetic: every provider home is redirected to a temp directory in the constructor.
/// </summary>
public class ConversionPipelineTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _donorDb;
    private const string DonorCascadeId = "11111111-2222-3333-4444-555555555555";
    private const string DonorTrajectoryId = "66666666-7777-8888-9999-000000000000";

    public ConversionPipelineTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"casr_conv_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        Environment.SetEnvironmentVariable("PI_CODING_AGENT_SESSION_DIR", Path.Combine(_tempDir, "pi", "sessions"));
        Environment.SetEnvironmentVariable("HERMES_HOME", Path.Combine(_tempDir, "hermes"));
        Environment.SetEnvironmentVariable("GROK_HOME", Path.Combine(_tempDir, "grok"));
        Environment.SetEnvironmentVariable("GEMINI_HOME", Path.Combine(_tempDir, "gemini"));
        Environment.SetEnvironmentVariable("OPENCLAUDE_CONFIG_DIR", Path.Combine(_tempDir, "openclaude"));

        Directory.CreateDirectory(Path.Combine(_tempDir, "pi", "sessions"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "hermes", "sessions", "saved"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "grok", "sessions"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "gemini", "antigravity-cli", "conversations"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "gemini", "antigravity-cli", "brain"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "openclaude", "projects"));

        _donorDb = Path.Combine(_tempDir, "donor.db");
        CreateMinimalDonorDb(_donorDb);
        AntigravityProvider.SetWriteTemplateForTests(_donorDb, DonorCascadeId, DonorTrajectoryId);
        AntigravityProvider.InvalidateCache();
    }

    public void Dispose()
    {
        try
        {
            Environment.SetEnvironmentVariable("PI_CODING_AGENT_SESSION_DIR", null);
            Environment.SetEnvironmentVariable("HERMES_HOME", null);
            Environment.SetEnvironmentVariable("GROK_HOME", null);
            Environment.SetEnvironmentVariable("GEMINI_HOME", null);
            Environment.SetEnvironmentVariable("OPENCLAUDE_CONFIG_DIR", null);
            AntigravityProvider.ResetWriteTemplateForTests();
            AntigravityProvider.InvalidateCache();
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    private static void CreateMinimalDonorDb(string path)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        using var conn = new SqliteConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE trajectory_meta (trajectory_id TEXT, cascade_id TEXT, session_id TEXT, parent_trajectory_id TEXT);
            INSERT INTO trajectory_meta VALUES ('66666666-7777-8888-9999-000000000000', '11111111-2222-3333-4444-555555555555', 'dummy', '');
            CREATE TABLE steps (idx INTEGER PRIMARY KEY, step_type INTEGER, step_payload BLOB);";
        cmd.ExecuteNonQuery();
        var donorBytes = Encoding.UTF8.GetBytes("donor-step-content-11111111-2222-3333-4444-555555555555-run_command");
        using var ins = conn.CreateCommand();
        ins.CommandText = "INSERT INTO steps VALUES (0, 14, $p), (1, 15, $p)";
        ins.Parameters.AddWithValue("$p", donorBytes);
        ins.ExecuteNonQuery();
    }

    private static CanonicalSession BuildCanonical(string? workspace, int messages = 3, string contentPrefix = "Please implement feature X")
    {
        var session = new CanonicalSession
        {
            SessionId = $"src-{Guid.NewGuid():N}",
            ProviderSlug = "fakesrc",
            Workspace = workspace,
            Title = "Conversion Pipeline Test",
            StartedAtEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Messages = new List<CanonicalMessage>
            {
                new() { Index = 0, Role = MessageRole.User, Content = contentPrefix }
            }
        };

        if (messages >= 2)
        {
            session.Messages.Add(new CanonicalMessage
            {
                Index = 1,
                Role = MessageRole.Assistant,
                Content = "I am implementing feature X",
                Extra = new Dictionary<string, object?> { ["thinking"] = "First let's check the directory structure" },
                ToolCalls = new List<ToolCall> { new() { Id = "tc1", Name = "list_dir", ArgumentsJson = "{\"path\":\".\"}" } }
            });
        }

        if (messages >= 3)
        {
            session.Messages.Add(new CanonicalMessage
            {
                Index = 2,
                Role = MessageRole.Tool,
                Content = "file1.cs\nfile2.cs",
                ToolResults = new List<ToolResult> { new() { CallId = "tc1", Content = "file1.cs\nfile2.cs" } },
                Extra = new Dictionary<string, object?> { ["tool_name"] = "list_dir" }
            });
        }

        return session;
    }

    private sealed class FakeSourceProvider : IProvider
    {
        public CanonicalSession Session { get; set; } = new();
        public int ReadCount { get; private set; }

        public string Name => "FakeSource";
        public string Slug => "fakesrc";
        public string CliAlias => "fakesrc";
        public DetectionResult Detect() => new();
        public IReadOnlyList<string> SessionRoots() => Array.Empty<string>();
        public string? OwnsSession(string sessionId) => null;
        public SessionSummary ReadSummary(string path) => new();
        public CanonicalSession ReadSession(string path) { ReadCount++; return Session; }
        public WrittenSession WriteSession(CanonicalSession session, WriteOptions opts) => throw new NotSupportedException();
        public string ResumeCommand(string sessionId, string? workspace = null) => $"fakesrc -s {sessionId}";
        public IReadOnlyList<(string SessionId, string Path)>? ListSessions() => null;
    }

    /// <summary>Writable target whose read-back always returns a single message (count mismatch).</summary>
    private sealed class BrokenTargetProvider : IProvider
    {
        public string Root { get; set; } = string.Empty;
        public string Name => "BrokenTarget";
        public string Slug => "brokentgt";
        public string CliAlias => "brokentgt";
        public bool CanWrite => true;
        public DetectionResult Detect() => new();
        public IReadOnlyList<string> SessionRoots() => new[] { Root };
        public string? OwnsSession(string sessionId) => null;
        public SessionSummary ReadSummary(string path) => new();
        public string ResumeCommand(string sessionId, string? workspace = null) => $"brokentgt -s {sessionId}";
        public IReadOnlyList<(string SessionId, string Path)>? ListSessions() => null;

        public WrittenSession WriteSession(CanonicalSession session, WriteOptions opts)
        {
            var id = Guid.NewGuid().ToString();
            var dir = Path.Combine(Root, id);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, id + ".jsonl");
            File.WriteAllLines(path, session.Messages.Select(m => m.Content ?? string.Empty));
            return new WrittenSession
            {
                SessionId = id,
                Paths = new List<string> { path },
                ResumeCommand = ResumeCommand(id, session.Workspace),
                Workspace = session.Workspace
            };
        }

        public CanonicalSession ReadSession(string path) => new()
        {
            SessionId = "read-back",
            Messages = new List<CanonicalMessage>
            {
                new() { Index = 0, Role = MessageRole.User, Content = "only one message" }
            }
        };
    }

    private static SessionSummary SummaryFor(CanonicalSession session, string? workspace = null) => new()
    {
        SessionId = session.SessionId,
        Provider = session.ProviderSlug,
        ProviderDisplayName = "FakeSource",
        Workspace = workspace ?? session.Workspace,
        SourcePath = "fake://session",
        Title = session.Title,
        MessagesCount = session.Messages.Count
    };

    // ------------------------------------------------------------------ preview

    [Fact]
    public void PrepareConversion_ReportsValidationNotes_AndWritesNothing()
    {
        var source = BuildCanonical(workspace: null, messages: 1);
        var fake = new FakeSourceProvider { Session = source };
        var registry = new ProviderRegistry(new IProvider[] { fake, new PiProvider() });
        var service = new SessionResumerService(registry);

        var preview = service.PrepareConversion(SummaryFor(source, workspace: null), "pi");

        Assert.False(preview.SameProvider);
        Assert.True(preview.TargetCanHostHistory);
        Assert.Equal(1, preview.SourceMessageCount);
        Assert.Contains(preview.Validation.Warnings, w => w.Contains("no workspace", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.Validation.Warnings, w => w.Contains("no assistant messages", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.Validation.Warnings, w => w.Contains("no timestamps", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.Validation.Warnings, w => w.Contains("Very short session", StringComparison.OrdinalIgnoreCase));
        Assert.True(fake.ReadCount >= 1);

        // Dry run: no file may exist under the target's temp store.
        var piFiles = Directory.GetFiles(Path.Combine(_tempDir, "pi", "sessions"), "*", SearchOption.AllDirectories);
        Assert.Empty(piFiles);
    }

    [Fact]
    public void PrepareConversion_SameProvider_ShortCircuitsWithoutReading()
    {
        var source = BuildCanonical(_tempDir);
        var fake = new FakeSourceProvider { Session = source };
        var registry = new ProviderRegistry(new IProvider[] { fake });
        var service = new SessionResumerService(registry);

        var summary = SummaryFor(source);
        summary.Provider = "fakesrc";
        var preview = service.PrepareConversion(summary, "fakesrc");

        Assert.True(preview.SameProvider);
        Assert.Equal(0, fake.ReadCount);
        Assert.Contains("fakesrc -s", preview.ResumeCommandPreview);
    }

    [Fact]
    public void PrepareConversion_ReadOnlyTarget_ReportsFallbackLaunch()
    {
        var source = BuildCanonical(_tempDir);
        var fake = new FakeSourceProvider { Session = source };
        var registry = new ProviderRegistry(new IProvider[] { fake, new CursorProvider() });
        var service = new SessionResumerService(registry);

        var preview = service.PrepareConversion(SummaryFor(source), "cursor");

        Assert.False(preview.TargetCanHostHistory);
        Assert.False(preview.SameProvider);
        Assert.Contains(preview.Warnings, w => w.Contains("does not support injecting", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, preview.PackagedMessageCount);
    }

    // ------------------------------------------------------------------ packaging

    [Fact]
    public void Package_Enrich_PrependsMarkedSyntheticMessages()
    {
        var source = BuildCanonical(_tempDir);
        var caps = HarnessCapabilities.For("openclaude");
        var options = new ConversionOptions { Enrich = true };

        var packaged = ContextPackager.Default.Package(source, caps, options);

        Assert.Equal(source.Messages.Count + 2, packaged.Messages.Count);
        Assert.True(packaged.Metadata.ContainsKey("seshmesh_enrichment_applied"));

        var notice = packaged.Messages[0];
        var summary = packaged.Messages[1];
        Assert.Equal(MessageRole.System, notice.Role);
        Assert.Equal(MessageRole.System, summary.Role);
        Assert.Equal(0, notice.Index);
        Assert.Equal(1, summary.Index);
        Assert.Equal("seshmesh-enrichment", notice.Author);
        Assert.Equal(true, notice.Extra["seshmesh_enrichment"]);
        Assert.Equal("conversion_notice", notice.Extra["enrichment_type"]);
        Assert.Equal("recent_summary", summary.Extra["enrichment_type"]);
        Assert.Contains(source.SessionId, notice.Content);
        Assert.Contains("Recent conversation snapshot", summary.Content);
        Assert.Contains("[seshmesh synthetic context]", notice.Content);

        // The original transcript survives verbatim after the two injected messages.
        Assert.Equal(source.Messages[0].Content, packaged.Messages[2].Content);
    }

    [Fact]
    public void Package_DropsReasoningByDefault_AndKeepsItWhenAsked()
    {
        var source = BuildCanonical(_tempDir);
        var caps = HarnessCapabilities.For("openclaude");

        var dropped = ContextPackager.Default.Package(source, caps, ConversionOptions.Default);
        var assistantDropped = dropped.Messages[1];
        Assert.False(assistantDropped.Extra.ContainsKey("thinking"));
        Assert.False(assistantDropped.Extra.ContainsKey("reasoning"));
        Assert.DoesNotContain("[Reasoning Process]", assistantDropped.Content);

        var kept = ContextPackager.Default.Package(source, caps, new ConversionOptions { KeepReasoning = true });
        var assistantKept = kept.Messages[1];
        Assert.Equal("First let's check the directory structure", assistantKept.Extra["thinking"]?.ToString());
    }

    [Fact]
    public void Package_MaxToolOutput_TruncatesObservationsAlways()
    {
        var source = BuildCanonical(_tempDir);
        source.Messages[2].ToolResults[0].Content = new string('X', 10_000);

        // Native-style target: the ToolResult part is truncated.
        var native = ContextPackager.Default.Package(source, HarnessCapabilities.For("antigravity"),
            new ConversionOptions { MaxToolOutput = 4_000, KeepReasoning = true });
        var nativeResult = native.Messages[2].ToolResults.Single();
        Assert.True(nativeResult.Content.Length < 5_000, $"expected truncation, got {nativeResult.Content.Length}");
        Assert.Contains("truncated", nativeResult.Content);

        // Markdown-synthesis target: the rendered content is truncated too.
        var markdown = ContextPackager.Default.Package(source, HarnessCapabilities.For("grok"),
            new ConversionOptions { MaxToolOutput = 4_000, KeepReasoning = true });
        Assert.Contains("truncated", markdown.Messages[2].Content);
        Assert.DoesNotContain(new string('X', 5_000), markdown.Messages[2].Content);
    }

    [Fact]
    public void Package_MaxContextTokens_DropsOldestTurns_KeepsGoalAndRecent()
    {
        var source = new CanonicalSession
        {
            SessionId = "budget-src",
            ProviderSlug = "fakesrc",
            Workspace = _tempDir,
            Messages = new List<CanonicalMessage>
            {
                new() { Index = 0, Role = MessageRole.User, Content = "GOAL: build the system" }
            }
        };

        for (int i = 1; i <= 6; i++)
        {
            source.Messages.Add(new CanonicalMessage { Index = source.Messages.Count, Role = MessageRole.User, Content = new string('U', 2_000) + $" turn {i}" });
            source.Messages.Add(new CanonicalMessage { Index = source.Messages.Count, Role = MessageRole.Assistant, Content = new string('A', 2_000) + $" answer {i}" });
        }

        var packaged = ContextPackager.Default.Package(source, HarnessCapabilities.For("openclaude"),
            new ConversionOptions { MaxContextTokens = 2_000, MaxToolOutput = 0, KeepReasoning = true });

        Assert.True(packaged.Messages.Count < source.Messages.Count, "the budget must drop whole turns");
        Assert.Equal("GOAL: build the system", packaged.Messages[0].Content);
        Assert.Contains("answer 6", packaged.Messages[^1].Content);
        Assert.True(ContextPackager.EstimateTokens(packaged) < ContextPackager.EstimateTokens(source),
            "the packaged history must be smaller than the source");
    }

    // ------------------------------------------------------------------ verification

    [Theory]
    [InlineData("grok")]
    [InlineData("pi")]
    [InlineData("hermes")]
    [InlineData("antigravity")]
    [InlineData("openclaude")]
    public void CrossResume_VerifiesRoundTrip_ForFileWriters(string targetSlug)
    {
        var source = BuildCanonical(_tempDir);
        var fake = new FakeSourceProvider { Session = source };
        var registry = new ProviderRegistry(new IProvider[] { fake, new GrokProvider(), new PiProvider(), new HermesProvider(), new AntigravityProvider(), new OpenClaudeProvider() });
        var service = new SessionResumerService(registry);

        var written = service.CrossResume(SummaryFor(source), targetSlug, ConversionOptions.Default);

        Assert.False(written.IsFallbackLaunch);
        Assert.NotNull(written.Verification);
        Assert.True(written.Verification!.Attempted);
        Assert.False(written.Verification.Unverifiable);
        Assert.True(written.Verification.Passed, written.Verification.Message);
        Assert.Equal(written.Verification.OriginalMessages, written.Verification.ReadBackMessages);
        Assert.Equal(source.Messages.Count, written.Verification.ReadBackMessages);
    }

    [Theory]
    [InlineData("grok")]
    [InlineData("pi")]
    [InlineData("hermes")]
    [InlineData("antigravity")]
    [InlineData("openclaude")]
    public void CrossResume_Enrich_ReadBackIncludesSyntheticMessages(string targetSlug)
    {
        var source = BuildCanonical(_tempDir);
        var fake = new FakeSourceProvider { Session = source };
        var registry = new ProviderRegistry(new IProvider[] { fake, new GrokProvider(), new PiProvider(), new HermesProvider(), new AntigravityProvider(), new OpenClaudeProvider() });
        var service = new SessionResumerService(registry);

        var options = new ConversionOptions { Enrich = true, Verify = true };
        var written = service.CrossResume(SummaryFor(source), targetSlug, options);

        Assert.NotNull(written.Verification);
        Assert.True(written.Verification!.Passed, written.Verification.Message);
        Assert.False(written.Verification.Unverifiable);
        Assert.Equal(source.Messages.Count + 2, written.Verification.ReadBackMessages);
        Assert.Contains(written.Warnings, w => w.Contains("synthetic context", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CrossResume_VerificationFailure_RollsBackWrittenFiles()
    {
        var root = Path.Combine(_tempDir, "broken-target");
        Directory.CreateDirectory(root);
        var source = BuildCanonical(_tempDir);
        var fake = new FakeSourceProvider { Session = source };
        var broken = new BrokenTargetProvider { Root = root };
        var registry = new ProviderRegistry(new IProvider[] { fake, broken });
        var service = new SessionResumerService(registry);

        var ex = Assert.Throws<SessionVerificationException>(() =>
            service.CrossResume(SummaryFor(source), "brokentgt", ConversionOptions.Default));

        Assert.False(ex.Verification.Passed);
        Assert.True(ex.Verification.Attempted);
        Assert.True(ex.Verification.RolledBack, ex.Verification.RollbackMessage);
        Assert.Contains("message count mismatch", ex.Verification.Message, StringComparison.OrdinalIgnoreCase);
        Assert.All(ex.Written.Paths, p => Assert.False(File.Exists(p), $"unverified artifact should be removed: {p}"));
    }

    [Fact]
    public void CrossResume_TolerantTarget_ReportsFoldedRowsAsWarning_NotFailure()
    {
        var root = Path.Combine(_tempDir, "folding-target");
        Directory.CreateDirectory(root);
        var source = BuildCanonical(_tempDir);
        var fake = new FakeSourceProvider { Session = source };
        var folding = new FoldingTargetProvider { Root = root };
        var registry = new ProviderRegistry(new IProvider[] { fake, folding });
        var service = new SessionResumerService(registry);

        var written = service.CrossResume(SummaryFor(source), "foldingtgt", ConversionOptions.Default);

        Assert.NotNull(written.Verification);
        Assert.True(written.Verification!.Passed, written.Verification.Message);
        Assert.Contains(written.Verification.Warnings, w => w.Contains("folds message rows", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FoldingTargetProvider : IProvider
    {
        public string Root { get; set; } = string.Empty;
        public string Name => "FoldingTarget";
        public string Slug => "foldingtgt";
        public string CliAlias => "foldingtgt";
        public bool CanWrite => true;
        public bool TolerantVerification => true;
        public DetectionResult Detect() => new();
        public IReadOnlyList<string> SessionRoots() => new[] { Root };
        public string? OwnsSession(string sessionId) => null;
        public SessionSummary ReadSummary(string path) => new();
        public string ResumeCommand(string sessionId, string? workspace = null) => $"foldingtgt -s {sessionId}";
        public IReadOnlyList<(string SessionId, string Path)>? ListSessions() => null;

        public WrittenSession WriteSession(CanonicalSession session, WriteOptions opts)
        {
            var id = Guid.NewGuid().ToString();
            var path = Path.Combine(Root, id + ".jsonl");
            File.WriteAllLines(path, session.Messages.Select(m => m.Content ?? string.Empty));
            return new WrittenSession
            {
                SessionId = id,
                Paths = new List<string> { path },
                ResumeCommand = ResumeCommand(id, session.Workspace),
                Workspace = session.Workspace
            };
        }

        // Native format folds the tool message into its assistant turn: 3 in, 2 back.
        public CanonicalSession ReadSession(string path) => new()
        {
            SessionId = "folding-readback",
            Messages = new List<CanonicalMessage>
            {
                new() { Index = 0, Role = MessageRole.User, Content = "Please implement feature X" },
                new() { Index = 1, Role = MessageRole.Assistant, Content = "I am implementing feature X" }
            }
        };
    }

    [Fact]
    public void CrossResume_UnverifiableTarget_DoesNotFail()
    {
        // A target whose WriteSession produces no readable path and no OwnsSession hit:
        // verification must report Unverifiable, never a hard failure.
        var source = BuildCanonical(_tempDir);
        var fake = new FakeSourceProvider { Session = source };
        var registry = new ProviderRegistry(new IProvider[] { fake, new SilentTargetProvider() });
        var service = new SessionResumerService(registry);

        var written = service.CrossResume(SummaryFor(source), "silenttgt", ConversionOptions.Default);

        Assert.NotNull(written.Verification);
        Assert.True(written.Verification!.Unverifiable);
        Assert.True(written.Verification.Passed);
    }

    private sealed class SilentTargetProvider : IProvider
    {
        public string Name => "SilentTarget";
        public string Slug => "silenttgt";
        public string CliAlias => "silenttgt";
        public bool CanWrite => true;
        public DetectionResult Detect() => new();
        public IReadOnlyList<string> SessionRoots() => Array.Empty<string>();
        public string? OwnsSession(string sessionId) => null;
        public SessionSummary ReadSummary(string path) => new();
        public CanonicalSession ReadSession(string path) => new();
        public WrittenSession WriteSession(CanonicalSession session, WriteOptions opts) =>
            new() { SessionId = "silent-1", ResumeCommand = "silenttgt -s silent-1" };
        public string ResumeCommand(string sessionId, string? workspace = null) => $"silenttgt -s {sessionId}";
        public IReadOnlyList<(string SessionId, string Path)>? ListSessions() => null;
    }
}

/// <summary>Read-only git metadata discovery (casr --enrich-fs analogue).</summary>
public class GitInspectorTests : IDisposable
{
    private readonly string _tempDir;

    public GitInspectorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"casr_git_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void TryResolve_FindsRepoRootNameAndBranch()
    {
        var repo = Path.Combine(_tempDir, "my-project");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/feature/cool-thing\n");
        var workspace = Path.Combine(repo, "src", "deep");
        Directory.CreateDirectory(workspace);

        var info = GitInspector.TryResolve(workspace);

        Assert.NotNull(info);
        Assert.Equal(repo, info!.RepoRoot);
        Assert.Equal("my-project", info.RepoName);
        Assert.Equal("feature/cool-thing", info.Branch);
        Assert.Equal("my-project @ feature/cool-thing", info.Display);
    }

    [Fact]
    public void TryResolve_DetachedHead_UsesShortSha()
    {
        var repo = Path.Combine(_tempDir, "detached");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b\n");

        var info = GitInspector.TryResolve(repo);

        Assert.NotNull(info);
        Assert.Equal("detached 1a2b3c4d", info!.Branch);
    }

    [Fact]
    public void TryResolve_GitFile_Worktree_PointsAtRealGitDir()
    {
        var realGitDir = Path.Combine(_tempDir, "real-gitdir");
        Directory.CreateDirectory(realGitDir);
        File.WriteAllText(Path.Combine(realGitDir, "HEAD"), "ref: refs/heads/main\n");

        var worktree = Path.Combine(_tempDir, "worktree");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {realGitDir}\n");

        var info = GitInspector.TryResolve(worktree);

        Assert.NotNull(info);
        Assert.Equal(worktree, info!.RepoRoot);
        Assert.Equal("main", info.Branch);
    }

    [Fact]
    public void TryResolve_NoRepo_ReturnsNull_AndNeverThrows()
    {
        var plain = Path.Combine(_tempDir, "plain");
        Directory.CreateDirectory(plain);

        Assert.Null(GitInspector.TryResolve(plain));
        Assert.Null(GitInspector.TryResolve(null));
        Assert.Null(GitInspector.TryResolve(string.Empty));
        Assert.Null(GitInspector.TryResolve(Path.Combine(_tempDir, "does-not-exist")));
    }
}

/// <summary>Persisted conversion-dialog preferences (additive settings block).</summary>
public class UserSettingsConversionTests : IDisposable
{
    private readonly string _path;

    public UserSettingsConversionTests()
    {
        _path = Path.Combine(Path.GetTempPath(), $"casr_conv_settings_{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }

    [Fact]
    public void ConversionPreferences_CasrParityDefaults_AndRoundTrip()
    {
        var settings = UserSettings.Load(_path);

        Assert.True(settings.ConversionPreviewEnabled);
        Assert.False(settings.ConversionEnrich);
        Assert.False(settings.ConversionKeepReasoning);
        Assert.True(settings.ConversionVerify);
        Assert.Equal(200_000, settings.ConversionMaxContextTokens);
        Assert.Equal(4_000, settings.ConversionMaxToolOutput);

        settings.ConversionPreviewEnabled = false;
        settings.ConversionEnrich = true;
        settings.ConversionKeepReasoning = true;
        settings.ConversionVerify = false;
        settings.ConversionMaxContextTokens = 64_000;
        settings.ConversionMaxToolOutput = 1_500;
        Assert.True(settings.Save(_path));

        var reloaded = UserSettings.Load(_path);
        Assert.False(reloaded.ConversionPreviewEnabled);
        Assert.True(reloaded.ConversionEnrich);
        Assert.True(reloaded.ConversionKeepReasoning);
        Assert.False(reloaded.ConversionVerify);
        Assert.Equal(64_000, reloaded.ConversionMaxContextTokens);
        Assert.Equal(1_500, reloaded.ConversionMaxToolOutput);
    }

    [Fact]
    public void ConversionPreferences_HandEditedValuesAreClamped()
    {
        File.WriteAllText(_path, "{\"ConversionMaxContextTokens\":-5,\"ConversionMaxToolOutput\":999999999}");

        var settings = UserSettings.Load(_path);

        Assert.Equal(0, settings.ConversionMaxContextTokens);
        Assert.Equal(1_000_000, settings.ConversionMaxToolOutput);
    }

    [Fact]
    public void NeuralEmbeddingsSetting_DefaultOff_AndRoundTrips()
    {
        var settings = UserSettings.Load(_path);
        Assert.False(settings.NeuralEmbeddings);

        settings.NeuralEmbeddings = true;
        Assert.True(settings.Save(_path));

        var reloaded = UserSettings.Load(_path);
        Assert.True(reloaded.NeuralEmbeddings);
    }
}
