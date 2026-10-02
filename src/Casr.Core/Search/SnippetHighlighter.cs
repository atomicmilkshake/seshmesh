using System;
using System.Collections.Generic;
using System.Text;

namespace Casr.Core.Search;

/// <summary>
/// Snippet markup helpers. Search snippets highlight matches with literal
/// <c>&lt;b&gt;...&lt;/b&gt;</c> markers over HTML-escaped content; these helpers
/// render or strip that markup without an HTML parser. Any other &lt;...&gt; in
/// the text is treated as literal content, never as markup.
/// </summary>
public static class SnippetHighlighter
{
    /// <summary>Segments of (text, bold) split on &lt;b&gt;...&lt;/b&gt; pairs.</summary>
    public static List<(string Text, bool Bold)> Parse(string? snippet)
    {
        var parts = new List<(string, bool)>();
        if (string.IsNullOrEmpty(snippet)) return parts;
        var sb = new StringBuilder();
        var i = 0;
        while (i < snippet.Length)
        {
            if (snippet.AsSpan(i).StartsWith("<b>", StringComparison.Ordinal))
            {
                if (sb.Length > 0) { parts.Add((System.Net.WebUtility.HtmlDecode(sb.ToString()), false)); sb.Clear(); }
                i += 3;
                var end = snippet.IndexOf("</b>", i, StringComparison.Ordinal);
                if (end < 0) end = snippet.Length;
                parts.Add((System.Net.WebUtility.HtmlDecode(snippet.Substring(i, end - i)), true));
                i = end + (end < snippet.Length ? 4 : 0);
            }
            else
            {
                sb.Append(snippet[i]);
                i++;
            }
        }
        if (sb.Length > 0) parts.Add((System.Net.WebUtility.HtmlDecode(sb.ToString()), false));
        return parts;
    }

    /// <summary>Plain text with all highlight markers removed.</summary>
    public static string StripTags(string? snippet)
    {
        if (string.IsNullOrEmpty(snippet)) return string.Empty;
        var sb = new StringBuilder(snippet.Length);
        foreach (var (text, _) in Parse(snippet)) sb.Append(text);
        return sb.ToString();
    }
}
