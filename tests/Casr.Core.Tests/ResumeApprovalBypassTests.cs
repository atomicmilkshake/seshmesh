using Casr.Core.Services;
using Xunit;

namespace Casr.Core.Tests;

public class ResumeApprovalBypassTests
{
    // Strings from docs/SPEC-index-fidelity-and-resume-bypass.md section 2.
    // Off-state must stay byte-identical to today's resume builders.
    public static TheoryData<string, string, string> Commands => new()
    {
        { "antigravity", "agy --conversation ses_1 --model \"gemini-3.1-pro-high\"", "agy --dangerously-skip-permissions --conversation ses_1 --model \"gemini-3.1-pro-high\"" },
        { "agy", "agy --conversation ses_1 --model \"gemini-3.1-pro-high\"", "agy --dangerously-skip-permissions --conversation ses_1 --model \"gemini-3.1-pro-high\"" },
        { "opencode", "opencode -s ses_1", "opencode --auto -s ses_1" },
        { "codex", "codex resume 'ses_1' --cd 'J:\\work'", "codex --dangerously-bypass-approvals-and-sandbox resume 'ses_1' --cd 'J:\\work'" },
        { "hermes", "hermes --resume ses_1", "hermes --yolo --resume ses_1" },
        { "grok", "grok --resume ses_1", "grok --yolo --resume ses_1" },
        { "openclaude", "openclaude --resume ses_1", "openclaude --dangerously-skip-permissions --resume ses_1" },
        { "pi", "pi --session ses_1", "pi --session ses_1" },
        { "cursor", "cursor \"J:\\work\"", "cursor \"J:\\work\"" },
    };

    [Theory]
    [MemberData(nameof(Commands))]
    public void Apply_Off_ReturnsTheCommandUnchanged(string slug, string command, string _)
    {
        Assert.Equal(command, ResumeApprovalBypass.Apply(slug, command, enabled: false));
    }

    [Theory]
    [MemberData(nameof(Commands))]
    public void Apply_On_InsertsTheVerifiedFlagOnce(string slug, string command, string expected)
    {
        var once = ResumeApprovalBypass.Apply(slug, command, enabled: true);
        Assert.Equal(expected, once);
        Assert.Equal(once, ResumeApprovalBypass.Apply(slug, once, enabled: true));
    }

    [Fact]
    public void FlagFor_ClaudeIsNotOpenClaude_AndUnknownIsNull()
    {
        Assert.Null(ResumeApprovalBypass.FlagFor("claude"));
        Assert.Null(ResumeApprovalBypass.FlagFor("cursor"));
        Assert.Null(ResumeApprovalBypass.FlagFor("pi"));
        Assert.Null(ResumeApprovalBypass.FlagFor(""));
        Assert.Null(ResumeApprovalBypass.FlagFor(null));
        Assert.Equal("--yolo", ResumeApprovalBypass.FlagFor(" Grok "));
        Assert.Equal("--dangerously-skip-permissions", ResumeApprovalBypass.FlagFor("AGY"));
    }

    [Fact]
    public void Apply_EmptyCommand_StaysEmpty()
    {
        Assert.Equal("", ResumeApprovalBypass.Apply("opencode", "", enabled: true));
        Assert.Equal("   ", ResumeApprovalBypass.Apply("opencode", "   ", enabled: true));
    }
}
