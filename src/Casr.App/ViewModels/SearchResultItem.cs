using System;
using Casr.Core.Models;

namespace Casr.App.ViewModels;

/// <summary>
/// Represents an individual search hit presented in the dedicated Search Results surface.
/// Contains search-specific attribution (rank, score, engine source badge, match excerpt)
/// and wraps the underlying session without mutating it.
/// </summary>
public class SearchResultItem : ViewModelBase
{
    public int Rank { get; init; }
    public double Score { get; init; }
    public string FormattedScore => Math.Abs(Score) > double.Epsilon ? $"{Score:F2}" : "—";
    public string SessionId { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string ProviderDisplayName { get; init; } = string.Empty;
    public string TitleDisplay { get; init; } = string.Empty;
    public string WorkspaceDisplay { get; init; } = "—";
    public string MatchSnippet { get; init; } = string.Empty;
    public string SourceBadge { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public int MessageIndex { get; init; } = -1;
    public DateTime? RecencyDate { get; init; }
    public string DisplayDate { get; init; } = string.Empty;
    public int MessagesCount { get; init; }
    public string TurnsDisplay { get; init; } = string.Empty;
    public int ToolCallsCount { get; init; }
    public bool IsSubagent { get; init; }
    public SessionSummary Session { get; init; } = null!;
}
