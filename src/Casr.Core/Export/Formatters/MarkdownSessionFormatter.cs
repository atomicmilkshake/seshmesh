using System;
using System.IO;
using System.Linq;
using System.Text;
using Casr.Core.Models;

namespace Casr.Core.Export.Formatters;

public static class MarkdownSessionFormatter
{
    private static readonly string[] ThinkingKeys = new[] { "thinking", "reasoning", "reasoning_content" };

    internal static string FormatTimestamp(DateTime dt)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(dt);
        var dto = new DateTimeOffset(dt, offset);
        return dto.ToString("yyyy-MM-dd HH:mm:ss zzz");
    }

    /// <summary>
    /// Returns a fence longer than any backtick run inside <paramref name="content"/>
    /// so embedded ``` blocks can never close ours early.
    /// </summary>
    internal static string FenceFor(string? content)
    {
        if (string.IsNullOrEmpty(content)) return "```";
        int maxRun = 0;
        int run = 0;
        foreach (var c in content)
        {
            if (c == '`') { run++; maxRun = Math.Max(maxRun, run); }
            else run = 0;
        }
        int len = Math.Max(3, maxRun + 1);
        return new string('`', len);
    }

    public static string Format(CanonicalSession session, ExportOptions? options = null)
    {
        options ??= new ExportOptions();
        var sb = new StringBuilder();

        if (options.IncludeMetadataHeader)
        {
            sb.AppendLine($"# {session.Title ?? "Session Transcript"}");
            sb.AppendLine();
            sb.AppendLine($"- **Session ID:** `{session.SessionId}`");
            sb.AppendLine($"- **Provider:** {session.ProviderSlug}");
            if (!string.IsNullOrWhiteSpace(session.Workspace))
            {
                sb.AppendLine($"- **Workspace:** `{session.Workspace}`");
            }
            if (!string.IsNullOrWhiteSpace(session.ModelName))
            {
                sb.AppendLine($"- **Model:** {session.ModelName}");
            }
            if (session.StartedAt.HasValue)
            {
                sb.AppendLine($"- **Started:** {FormatTimestamp(session.StartedAt.Value)}");
            }
            if (session.EndedAt.HasValue)
            {
                sb.AppendLine($"- **Ended:** {FormatTimestamp(session.EndedAt.Value)}");
            }
            sb.AppendLine($"- **Total Messages:** {session.Messages.Count}");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
        }

        foreach (var msg in session.Messages)
        {
            var timeStr = msg.Timestamp.HasValue ? $" • {FormatTimestamp(msg.Timestamp.Value)}" : string.Empty;
            var roleHeader = msg.Role switch
            {
                MessageRole.User => $"## 👤 User (Turn {msg.Index + 1}{timeStr})",
                MessageRole.Assistant => $"## 🤖 Assistant (Turn {msg.Index + 1}{timeStr})",
                MessageRole.Tool => $"## 🛠 Tool (Turn {msg.Index + 1}{timeStr})",
                MessageRole.System => $"## ⚙ System (Turn {msg.Index + 1}{timeStr})",
                _ => $"## 💬 {msg.Role} (Turn {msg.Index + 1}{timeStr})"
            };

            sb.AppendLine(roleHeader);
            sb.AppendLine();

            // Thinking / reasoning block if available
            if (options.IncludeThinking && msg.Extra != null)
            {
                string? thinking = null;
                if (msg.Extra.TryGetValue("thinking", out var th) && th != null)
                {
                    thinking = th.ToString();
                }
                else if (msg.Extra.TryGetValue("reasoning", out var rs) && rs != null)
                {
                    thinking = rs.ToString();
                }
                else if (msg.Extra.TryGetValue("reasoning_content", out var rc) && rc != null)
                {
                    thinking = rc.ToString();
                }

                if (!string.IsNullOrWhiteSpace(thinking))
                {
                    sb.AppendLine("<details>");
                    sb.AppendLine("<summary>💭 <em>Thought Process</em></summary>");
                    sb.AppendLine();
                    sb.AppendLine(thinking.Trim());
                    sb.AppendLine();
                    sb.AppendLine("</details>");
                    sb.AppendLine();
                }
            }

            // Message text content
            if (!string.IsNullOrWhiteSpace(msg.Content))
            {
                sb.AppendLine(msg.Content.Trim());
                sb.AppendLine();
            }

            // Tool Calls
            if (options.IncludeToolCalls && msg.ToolCalls.Count > 0)
            {
                foreach (var tool in msg.ToolCalls)
                {
                    var idSuffix = string.IsNullOrWhiteSpace(tool.Id) ? string.Empty : $" ({tool.Id})";
                    sb.AppendLine($"### 🛠 Call: `{tool.Name}`{idSuffix}");
                    if (!string.IsNullOrWhiteSpace(tool.ArgumentsJson))
                    {
                        var args = tool.ArgumentsJson.Trim();
                        var fence = FenceFor(args);
                        sb.AppendLine($"{fence}json");
                        sb.AppendLine(args);
                        sb.AppendLine(fence);
                    }
                    sb.AppendLine();
                }
            }

            // Tool Results
            if (options.IncludeToolResults && msg.ToolResults.Count > 0)
            {
                foreach (var res in msg.ToolResults)
                {
                    var errorBadge = res.IsError ? " ❌ (Error)" : " ✔";
                    sb.AppendLine($"### ⚙ Result: `{res.CallId ?? "output"}`{errorBadge}");
                    if (!string.IsNullOrWhiteSpace(res.Content))
                    {
                        var body = res.Content.Trim();
                        var fence = FenceFor(body);
                        sb.AppendLine(fence);
                        sb.AppendLine(body);
                        sb.AppendLine(fence);
                    }
                    sb.AppendLine();
                }
            }

            // Leftover Extra (anything that is not thinking/reasoning) as generic context.
            if (msg.Extra != null && msg.Extra.Count > 0)
            {
                var leftover = msg.Extra
                    .Where(kv => Array.IndexOf(ThinkingKeys, kv.Key) < 0
                        && !string.Equals(kv.Key, "thinking", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(kv.Key, "reasoning", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(kv.Key, "reasoning_content", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (leftover.Count > 0)
                {
                    sb.AppendLine("### 📎 Additional context");
                    foreach (var kv in leftover)
                    {
                        sb.AppendLine($"- **{kv.Key}:** `{kv.Value?.ToString()?.Trim() ?? "null"}`");
                    }
                    sb.AppendLine();
                }
            }

            sb.AppendLine("---");
            sb.AppendLine();
        }

        if (session.Metadata != null && session.Metadata.Count > 0)
        {
            sb.AppendLine("## 📎 Additional Session Context");
            sb.AppendLine();
            foreach (var kv in session.Metadata)
            {
                sb.AppendLine($"- **{kv.Key}:** `{kv.Value?.ToString()?.Trim() ?? "null"}`");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
