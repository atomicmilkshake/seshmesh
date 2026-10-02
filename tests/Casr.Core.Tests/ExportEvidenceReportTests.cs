using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Casr.Core.Export;
using Casr.Core.Models;
using Casr.Core.Storage;
using Xunit;

namespace Casr.Core.Tests;

/// <summary>
/// Search-evidence report tests (TESTING_STANDARD 5-pillar style): ingress
/// fixtures are validated, the report is written to an isolated temp dir, and
/// the bytes on disk are inspected out-of-band (size, BOM, content, re-parse).
/// </summary>
public class ExportEvidenceReportTests
{
    private static (SessionSummary Summary, SearchResult Hit) Fixture(string id, string title)
    {
        var summary = new SessionSummary
        {
            SessionId = id,
            Provider = "opencode",
            ProviderDisplayName = "OpenCode",
            Title = title,
            Workspace = @"C:\Projects\MyRepo",
        };
        Assert.False(string.IsNullOrWhiteSpace(summary.SessionId), "fixture summary needs an id");
        var hit = new SearchResult
        {
            SessionId = id,
            Snippet = "refactor the <b>authentication</b> module广泛",
            Role = "user",
            Rank = 1.5,
            MessageIndex = 7,
            Source = MatchSource.Hybrid,
            Sources = new List<MatchSource> { MatchSource.Fts, MatchSource.SemanticVector },
            MatchedTerms = new List<string> { "authentication", "refactor" },
        };
        Assert.True(hit.MatchedTerms.Count == 2, "fixture hit needs matched terms");
        return (summary, hit);
    }

    [Fact]
    public async Task ExportAsync_WritesNonEmptyBomFreeReportWithAllSessions()
    {
        // Pillar 1: ingress validation.
        var (s1, h1) = Fixture("ses_evidence_1", "Auth refactor");
        var (s2, h2) = Fixture("ses_evidence_2", "Token validator");
        var items = new List<SearchEvidenceItem>
        {
            SearchEvidenceReport.FromHit(s1, h1),
            SearchEvidenceReport.FromHit(s2, h2),
        };
        Assert.Equal(2, items.Count);

        // Pillar 2: isolated execution.
        var tempDir = Path.Combine(Path.GetTempPath(), "casr_evidence_" + Guid.NewGuid().ToString("N"));
        try
        {
            var dest = Path.Combine(tempDir, "report.md");
            await SearchEvidenceReport.ExportAsync("authentication", "Hybrid",
                new DateTime(2026, 9, 26, 12, 0, 0), items, dest);

            // Pillar 4: independent, out-of-band egress inspection.
            Assert.True(File.Exists(dest), "report file missing from destination");
            var rawBytes = await File.ReadAllBytesAsync(dest);
            Assert.True(rawBytes.Length > 0, "report exists but is 0 bytes");
            Assert.False(rawBytes.Length >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF,
                "report must be UTF-8 with NO BOM");

            // Pillar 3+5: content integrity + re-parse with an independent reader.
            var text = Encoding.UTF8.GetString(rawBytes);
            Assert.Contains("authentication", text, StringComparison.Ordinal);
            Assert.Contains("Hybrid", text, StringComparison.Ordinal);
            Assert.Contains("2026-09-26 12:00:00", text, StringComparison.Ordinal);
            Assert.Contains("| Sessions | 2 |", text, StringComparison.Ordinal);
            Assert.Contains("Auth refactor", text, StringComparison.Ordinal);
            Assert.Contains("Token validator", text, StringComparison.Ordinal);
            Assert.Contains("ses_evidence_1", text, StringComparison.Ordinal);
            Assert.Contains("ses_evidence_2", text, StringComparison.Ordinal);
            Assert.Contains("hybrid", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("message #7", text, StringComparison.Ordinal);
            Assert.Contains("C:\\Projects\\MyRepo", text, StringComparison.Ordinal);
            // Snippet survives (highlight tags stripped to plain text).
            Assert.Contains("authentication", text, StringComparison.Ordinal);
            Assert.DoesNotContain("<b>", text, StringComparison.Ordinal);
            // Matched terms render per session.
            Assert.Contains("`refactor`", text, StringComparison.Ordinal);
            // One section per session.
            Assert.Contains("## 1.", text, StringComparison.Ordinal);
            Assert.Contains("## 2.", text, StringComparison.Ordinal);
            Assert.DoesNotContain("## 3.", text, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAsync_EmptyResults_RefusesLikeSessionExport()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "casr_evidence_empty_" + Guid.NewGuid().ToString("N"));
        try
        {
            var dest = Path.Combine(tempDir, "report.md");
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SearchEvidenceReport.ExportAsync("nothing", "Keyword", DateTime.Now,
                    new List<SearchEvidenceItem>(), dest));
            Assert.Contains("no matching sessions", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(dest), "empty export must not leave a file behind");
            Assert.False(Directory.Exists(tempDir), "empty export must not create directories");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void GenerateDefaultFileName_SanitizesHostileQuery()
    {
        var name = SearchEvidenceReport.GenerateDefaultFileName(
            "Fix: User/Login? *Special* \"Quotes\" <Angle> | Pipe", new DateTime(2026, 9, 26, 8, 5, 4));

        Assert.StartsWith("search-evidence_20260926_080504_", name, StringComparison.Ordinal);
        Assert.EndsWith(".md", name, StringComparison.Ordinal);
        foreach (var c in new[] { ':', '/', '\\', '?', '*', '"', '<', '>', '|' })
            Assert.DoesNotContain(c.ToString(), name, StringComparison.Ordinal);
        Assert.True(name.Length < 120, $"file name too long: {name}");
    }

    [Fact]
    public void BadgeFor_MapsEverySource()
    {
        Assert.Equal("keyword", SearchEvidenceReport.BadgeFor(MatchSource.Fts));
        Assert.Equal("exact", SearchEvidenceReport.BadgeFor(MatchSource.Exact));
        Assert.Equal("regex", SearchEvidenceReport.BadgeFor(MatchSource.Regex));
        Assert.Equal("vector", SearchEvidenceReport.BadgeFor(MatchSource.SemanticVector));
        Assert.Equal("hybrid", SearchEvidenceReport.BadgeFor(MatchSource.Hybrid));
    }
}
