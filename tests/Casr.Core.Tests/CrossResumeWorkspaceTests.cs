using System;
using System.Collections.Generic;
using Casr.Core.Models;
using Casr.Core.Providers;
using Casr.Core.Services;
using Xunit;

namespace Casr.Core.Tests;

/// <summary>
/// Workspace-backfill tests for SessionResumerService.CrossResume: when the stale
/// index summary has no Workspace but the canonical read knows it, the result must
/// carry the canonical workspace — on both the writer path and the NotSupportedException
/// fallback-launch path.
/// Hermetic: uses in-memory stub providers registered into a private ProviderRegistry.
/// </summary>
public class CrossResumeWorkspaceTests
{
    private const string CanonicalWorkspace = @"F:\canonical\workspace";

    private sealed class StubSourceProvider : IProvider
    {
        public string Name => "StubSource";
        public string Slug => "stubsrc";
        public string CliAlias => "stubsrc";
        public DetectionResult Detect() => new();
        public IReadOnlyList<string> SessionRoots() => Array.Empty<string>();
        public string? OwnsSession(string sessionId) => null;
        public SessionSummary ReadSummary(string path) => new();
        public string ResumeCommand(string sessionId, string? workspace = null) => $"stubsrc -s {sessionId}";
        public IReadOnlyList<(string SessionId, string Path)>? ListSessions() => null;

        public CanonicalSession ReadSession(string path) => new CanonicalSession
        {
            SessionId = "src-1",
            ProviderSlug = Slug,
            Workspace = CanonicalWorkspace, // canonical read knows the real workspace
            Title = "stub source session",
            Messages = new List<CanonicalMessage>
            {
                new CanonicalMessage { Index = 0, Role = MessageRole.User, Content = "hi" }
            }
        };

        public WrittenSession WriteSession(CanonicalSession session, WriteOptions opts) =>
            throw new NotSupportedException();
    }

    private sealed class StubWritableTarget : IProvider
    {
        public string Name => "StubTarget";
        public string Slug => "stubtgt";
        public string CliAlias => "stubtgt";
        public bool CanWrite => true;
        public DetectionResult Detect() => new();
        public IReadOnlyList<string> SessionRoots() => Array.Empty<string>();
        public string? OwnsSession(string sessionId) => null;
        public SessionSummary ReadSummary(string path) => new();
        public CanonicalSession ReadSession(string path) => new();
        public string ResumeCommand(string sessionId, string? workspace = null) => $"stubtgt -s {sessionId}";
        public IReadOnlyList<(string SessionId, string Path)>? ListSessions() => null;

        public CanonicalSession? LastWritten;

        public WrittenSession WriteSession(CanonicalSession session, WriteOptions opts)
        {
            LastWritten = session;
            // Writer that does NOT set Workspace — the service must backfill it.
            return new WrittenSession
            {
                SessionId = "tgt-1",
                ResumeCommand = "stubtgt -s tgt-1"
            };
        }
    }

    private sealed class StubReadOnlyTarget : IProvider
    {
        public string Name => "StubRO";
        public string Slug => "stubro";
        public string CliAlias => "stubro";
        public DetectionResult Detect() => new();
        public IReadOnlyList<string> SessionRoots() => Array.Empty<string>();
        public string? OwnsSession(string sessionId) => null;
        public SessionSummary ReadSummary(string path) => new();
        public CanonicalSession ReadSession(string path) => new();
        public string ResumeCommand(string sessionId, string? workspace = null) => "stubro";
        public IReadOnlyList<(string SessionId, string Path)>? ListSessions() => null;

        public WrittenSession WriteSession(CanonicalSession session, WriteOptions opts) =>
            throw new NotSupportedException("read-only stub");
    }

    private static (SessionResumerService Service, SessionSummary Summary) Build(IProvider target)
    {
        var registry = new ProviderRegistry(new IProvider[] { new StubSourceProvider(), target });
        var summary = new SessionSummary
        {
            SessionId = "src-1",
            Provider = "stubsrc",
            SourcePath = "stub://src-1",
            Workspace = null // stale index row: no workspace
        };
        return (new SessionResumerService(registry), summary);
    }

    [Fact]
    public void CrossResume_WriterPath_BackfillsWorkspace_FromCanonicalRead()
    {
        var (service, summary) = Build(new StubWritableTarget());

        var written = service.CrossResume(summary, "stubtgt", force: true);

        Assert.Equal(CanonicalWorkspace, written.Workspace);
        Assert.False(written.IsFallbackLaunch);
        Assert.Equal("stubtgt -s tgt-1", written.ResumeCommand);
    }

    [Fact]
    public void CrossResume_FallbackPath_BackfillsWorkspace_FromCanonicalRead()
    {
        var (service, summary) = Build(new StubReadOnlyTarget());

        var written = service.CrossResume(summary, "stubro", force: true);

        Assert.True(written.IsFallbackLaunch);
        Assert.Equal(CanonicalWorkspace, written.Workspace);
        Assert.Equal("stubro", written.ResumeCommand);
    }
}
