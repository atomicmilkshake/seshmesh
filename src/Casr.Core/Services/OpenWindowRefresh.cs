using Casr.Core.Models;

namespace Casr.Core.Services;

/// <summary>
/// While the window stays open, repeat the launch scan: change-gated discovery,
/// then the incremental content index. Two minutes is long enough that an
/// unchanged machine stays quiet, and short enough that a conversation written
/// by another CLI shows up without pressing Refresh.
/// A tick during a scan or index is dropped. Queueing would chain passes for
/// as long as each one overruns the interval.
/// </summary>
public static class OpenWindowRefresh
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    public static bool ShouldStart(bool isScanning, bool isIndexing, bool disposed)
        => !disposed && !isScanning && !isIndexing;

    public static bool SameSession(SessionSummary? a, SessionSummary? b)
    {
        if (a == null || b == null) return false;
        return string.Equals(a.SessionId, b.SessionId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Provider, b.Provider, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The open transcript is re-read only when the summary that just arrived
    /// describes a different file generation than the one on screen.
    /// </summary>
    public static bool TranscriptNeedsTopUp(SessionSummary? shown, SessionSummary? arrived)
    {
        if (!SameSession(shown, arrived)) return false;
        return shown!.MessagesCount != arrived!.MessagesCount
            || shown.FileSizeBytes != arrived.FileSizeBytes
            || shown.LastActiveAt != arrived.LastActiveAt
            || !string.Equals(shown.SourcePath, arrived.SourcePath, StringComparison.OrdinalIgnoreCase);
    }
}
