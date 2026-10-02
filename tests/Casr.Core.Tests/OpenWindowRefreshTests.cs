using System;
using Casr.Core.Models;
using Casr.Core.Services;
using Xunit;

namespace Casr.Core.Tests;

public class OpenWindowRefreshTests
{
    [Fact]
    public void Interval_IsTwoMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(2), OpenWindowRefresh.Interval);
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    public void ShouldStart_OnlyWhenIdle(bool scanning, bool indexing, bool disposed, bool expected)
    {
        Assert.Equal(expected, OpenWindowRefresh.ShouldStart(scanning, indexing, disposed));
    }

    [Fact]
    public void TranscriptNeedsTopUp_WhenFileGenerationChanges()
    {
        var shown = Sample();
        Assert.False(OpenWindowRefresh.TranscriptNeedsTopUp(shown, Sample()));
        Assert.False(OpenWindowRefresh.TranscriptNeedsTopUp(null, shown));
        Assert.False(OpenWindowRefresh.TranscriptNeedsTopUp(shown, null));

        var other = Sample();
        other.SessionId = "other";
        Assert.False(OpenWindowRefresh.TranscriptNeedsTopUp(shown, other));

        var moreMessages = Sample();
        moreMessages.MessagesCount = 9;
        var bigger = Sample();
        bigger.FileSizeBytes = 50;
        var newer = Sample();
        newer.LastActiveAt = shown.LastActiveAt!.Value.AddMinutes(1);
        var moved = Sample();
        moved.SourcePath = @"J:\other\session.jsonl";

        Assert.True(OpenWindowRefresh.TranscriptNeedsTopUp(shown, moreMessages));
        Assert.True(OpenWindowRefresh.TranscriptNeedsTopUp(shown, bigger));
        Assert.True(OpenWindowRefresh.TranscriptNeedsTopUp(shown, newer));
        Assert.True(OpenWindowRefresh.TranscriptNeedsTopUp(shown, moved));
    }

    private static SessionSummary Sample() => new()
    {
        SessionId = "ses_1",
        Provider = "opencode",
        MessagesCount = 4,
        FileSizeBytes = 20,
        LastActiveAt = new DateTime(2026, 10, 2, 1, 0, 0, DateTimeKind.Local),
        SourcePath = @"J:\work\session.jsonl"
    };
}
