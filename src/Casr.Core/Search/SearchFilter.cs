using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Casr.Core.Search;

/// <summary>
/// Shared SQL pushdown for optional search scoping. Filters are applied in SQL
/// (predicates over a LEFT-JOINed sessions row) BEFORE limit, so a narrow filter
/// over a broad query cannot false-empty the way post-hoc in-memory filtering would
/// under a small limit. Null/empty on any axis means "no restriction" on that axis.
/// <list type="bullet">
/// <item>providerSlugs: case-insensitive exact match on sessions.provider.</item>
/// <item>workspace: case-insensitive exact match on sessions.workspace.</item>
/// <item>sinceMs: unix-ms; a row passes when
/// COALESCE(last_active_at, started_at, content_last_active_at, 0) &gt;= sinceMs.</item>
/// </list>
/// Rows with no sessions row fail any active filter (nothing to match against) and
/// pass when no filter is set (the LEFT JOIN preserves them).
/// </summary>
internal static class SearchFilterSql
{
    /// <summary>
    /// Builds the trailing " AND ..." predicate fragment for the sessions table under
    /// <paramref name="sessionsAlias"/>, binding values as parameters on
    /// <paramref name="cmd"/>. <paramref name="seq"/> disambiguates parameter names
    /// per command (callers pass a fresh counter per SqliteCommand).
    /// </summary>
    public static string BuildWhere(
        SqliteCommand cmd,
        string sessionsAlias,
        IEnumerable<string>? providerSlugs,
        string? workspace,
        long? sinceMs,
        ref int seq)
    {
        var sb = new StringBuilder();
        if (providerSlugs != null)
        {
            var slugs = providerSlugs
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim().ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (slugs.Count > 0)
            {
                var names = new List<string>(slugs.Count);
                foreach (var s in slugs)
                {
                    var p = "@fltProv" + seq++;
                    cmd.Parameters.AddWithValue(p, s);
                    names.Add(p);
                }
                sb.Append($" AND LOWER({sessionsAlias}.provider) IN ({string.Join(", ", names)})");
            }
        }
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            var p = "@fltWs" + seq++;
            cmd.Parameters.AddWithValue(p, workspace.Trim().ToLowerInvariant());
            sb.Append($" AND LOWER({sessionsAlias}.workspace) = {p}");
        }
        if (sinceMs.HasValue)
        {
            var p = "@fltSince" + seq++;
            cmd.Parameters.AddWithValue(p, sinceMs.Value);
            sb.Append($" AND COALESCE({sessionsAlias}.last_active_at, {sessionsAlias}.started_at, {sessionsAlias}.content_last_active_at, 0) >= {p}");
        }
        return sb.ToString();
    }
}
