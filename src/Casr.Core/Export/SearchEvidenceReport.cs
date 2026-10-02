using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Casr.Core.Logging;
using Casr.Core.Models;
using Casr.Core.Storage;

namespace Casr.Core.Export;

/// <summary>
/// One per-session row of a search-evidence report: the session plus the best
/// deep-search hit rendered for it (badge, snippet, matched terms, message index).
/// </summary>
public sealed class SearchEvidenceItem
{
    public SessionSummary Summary { get; set; } = new();
    public string Badge { get; set; } = "match";
    public string Snippet { get; set; } = string.Empty;
    public List<string> MatchedTerms { get; set; } = new();
    public int MessageIndex { get; set; } = -1;
}

/// <summary>
/// Writes a Markdown "search with evidence" report: query, engine, timestamp and
/// one section per matching session (title, workspace, source badge, snippet,
/// matched terms, message index). Written UTF-8 with NO BOM so downstream
/// parsers never see a preamble.
/// </summary>
public static class SearchEvidenceReport
{
    private static readonly UTF8Encoding NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string BadgeFor(MatchSource source) => source switch
    {
        MatchSource.Fts => "keyword",
        MatchSource.Exact => "exact",
        MatchSource.Regex => "regex",
        MatchSource.SemanticVector => "vector",
        MatchSource.Hybrid => "hybrid",
        _ => "match",
    };

    public static SearchEvidenceItem FromHit(SessionSummary summary, SearchResult hit)
    {
        if (summary == null) throw new ArgumentNullException(nameof(summary));
        if (hit == null) throw new ArgumentNullException(nameof(hit));
        return new SearchEvidenceItem
        {
            Summary = summary,
            Badge = BadgeFor(hit.Source),
            Snippet = hit.PlainSnippet,
            MatchedTerms = (hit.MatchedTerms ?? new List<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            MessageIndex = hit.MessageIndex,
        };
    }

    /// <summary>Default file name for a report; the query is sanitized like export titles.</summary>
    public static string GenerateDefaultFileName(string query, DateTime? timestamp = null)
    {
        var ts = timestamp ?? DateTime.Now;
        var slug = SessionExportService.SanitizeFileNamePart(
            string.IsNullOrWhiteSpace(query) ? "search" : query);
        if (slug.Length > 40) slug = slug.Substring(0, 40).TrimEnd('-', '_');
        return $"search-evidence_{ts:yyyyMMdd_HHmmss}_{slug}.md";
    }

    public static string BuildMarkdown(
        string query,
        string mode,
        DateTime timestamp,
        IReadOnlyList<SearchEvidenceItem> items)
    {
        if (items == null || items.Count == 0)
        {
            // Mirror SessionExportService's empty guard: an evidence file with zero
            // sessions is a dead artifact the user mistakes for a real report.
            throw new InvalidOperationException(
                "Cannot export search results: the current search has no matching sessions. " +
                "Export of an empty result set was aborted.");
        }

        var sb = new StringBuilder(4096);
        sb.Append("# Search evidence report\n\n");
        sb.Append("| Field | Value |\n| --- | --- |\n");
        sb.Append($"| Query | `{EscapeCell(query)}` |\n");
        sb.Append($"| Engine | {EscapeCell(string.IsNullOrWhiteSpace(mode) ? "Hybrid" : mode)} |\n");
        sb.Append($"| Exported | {timestamp:yyyy-MM-dd HH:mm:ss} |\n");
        sb.Append($"| Sessions | {items.Count} |\n\n");

        var n = 0;
        foreach (var item in items)
        {
            n++;
            var s = item.Summary;
            var title = string.IsNullOrWhiteSpace(s.Title) ? "(Untitled session)" : s.Title!;
            sb.Append($"## {n}. {EscapeHeading(title)}\n\n");
            sb.Append($"- Session: `{s.SessionId}`\n");
            sb.Append($"- Provider: {EscapeCell(s.ProviderDisplayName)}\n");
            sb.Append($"- Workspace: `{EscapeCell(s.Workspace ?? string.Empty)}`\n");
            sb.Append($"- Match: `{item.Badge}`");
            sb.Append(item.MessageIndex >= 0 ? $" at message #{item.MessageIndex}\n" : "\n");
            if (item.MatchedTerms.Count > 0)
                sb.Append($"- Matched terms: {string.Join(", ", item.MatchedTerms.Select(t => $"`{EscapeCell(t)}`"))}\n");
            var snippet = string.IsNullOrWhiteSpace(item.Snippet) ? "(no excerpt)" : item.Snippet;
            sb.Append($"> {EscapeQuote(snippet)}\n\n");
        }

        return sb.ToString();
    }

    public static async Task ExportAsync(
        string query,
        string mode,
        DateTime timestamp,
        IReadOnlyList<SearchEvidenceItem> items,
        string destinationPath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("Destination path is required.", nameof(destinationPath));
        // Build first so the empty guard fires before any directory is created.
        var markdown = BuildMarkdown(query, mode, timestamp, items);
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(destinationPath, markdown, NoBom, ct);
        CasrLogger.Info("EXPORT", $"Wrote search evidence report ({items!.Count} sessions) to {destinationPath}");
    }

    private static string EscapeCell(string value) =>
        (value ?? string.Empty).Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static string EscapeHeading(string value) =>
        EscapeCell(value).Replace("#", "\\#");

    private static string EscapeQuote(string value) =>
        (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
}
