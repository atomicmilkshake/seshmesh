using System;
using System.Net;
using System.Text;
using Casr.Core.Models;

namespace Casr.Core.Export.Formatters;

public static class HtmlSessionFormatter
{
    internal static string FormatTimestamp(DateTime dt)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(dt);
        var dto = new DateTimeOffset(dt, offset);
        return dto.ToString("yyyy-MM-dd HH:mm:ss zzz");
    }

    public static string Format(CanonicalSession session, ExportOptions? options = null)
    {
        options ??= new ExportOptions();
        var sb = new StringBuilder();

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"utf-8\">");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine($"  <title>{WebUtility.HtmlEncode(session.Title ?? "Session Transcript")}</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine(@"    :root {
      --bg: #1e1e2e;
      --card-bg: #252538;
      --border: #383852;
      --text-main: #cdd6f4;
      --text-muted: #a6adc8;
      --accent: #89b4fa;
      --user-badge: #a6e3a1;
      --asst-badge: #89b4fa;
      --tool-badge: #fab387;
      --sys-badge: #f38ba8;
      --code-bg: #181825;
      --error-bg: #451b24;
      --error-border: #f38ba8;
    }
    @media (prefers-color-scheme: light) {
      :root {
        --bg: #f8f9fa;
        --card-bg: #ffffff;
        --border: #e2e8f0;
        --text-main: #1e293b;
        --text-muted: #64748b;
        --accent: #2563eb;
        --user-badge: #16a34a;
        --asst-badge: #2563eb;
        --tool-badge: #d97706;
        --sys-badge: #dc2626;
        --code-bg: #f1f5f9;
        --error-bg: #fee2e2;
        --error-border: #ef4444;
      }
    }
    body {
      font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
      background-color: var(--bg);
      color: var(--text-main);
      line-height: 1.6;
      margin: 0;
      padding: 24px 16px;
    }
    .container {
      max-width: 900px;
      margin: 0 auto;
    }
    .header-card {
      background: var(--card-bg);
      border: 1px solid var(--border);
      border-radius: 8px;
      padding: 20px;
      margin-bottom: 24px;
      box-shadow: 0 4px 6px -1px rgba(0,0,0,0.1);
    }
    .header-card h1 {
      margin-top: 0;
      margin-bottom: 12px;
      font-size: 1.5rem;
    }
    .meta-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
      gap: 10px;
      font-size: 0.9rem;
      color: var(--text-muted);
    }
    .meta-item strong {
      color: var(--text-main);
    }
    .turn {
      background: var(--card-bg);
      border: 1px solid var(--border);
      border-radius: 8px;
      padding: 18px;
      margin-bottom: 16px;
    }
    .turn-header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      margin-bottom: 12px;
      border-bottom: 1px solid var(--border);
      padding-bottom: 8px;
    }
    .badge {
      display: inline-block;
      padding: 3px 8px;
      border-radius: 4px;
      font-size: 0.8rem;
      font-weight: 600;
      text-transform: uppercase;
      letter-spacing: 0.5px;
    }
    .badge-user { background: var(--user-badge); color: #111; }
    .badge-assistant { background: var(--asst-badge); color: #111; }
    .badge-tool { background: var(--tool-badge); color: #111; }
    .badge-system { background: var(--sys-badge); color: #fff; }
    .turn-time {
      font-size: 0.85rem;
      color: var(--text-muted);
    }
    .content {
      white-space: pre-wrap;
      word-break: break-word;
    }
    details {
      margin: 10px 0;
      background: var(--code-bg);
      border: 1px solid var(--border);
      border-radius: 6px;
      padding: 8px 12px;
    }
    summary {
      cursor: pointer;
      font-weight: 600;
      color: var(--accent);
      user-select: none;
    }
    pre {
      background: var(--code-bg);
      border: 1px solid var(--border);
      border-radius: 6px;
      padding: 12px;
      overflow-x: auto;
      font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
      font-size: 0.88rem;
      margin: 8px 0;
    }
    code {
      font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
    }
    .error-box {
      background: var(--error-bg);
      border: 1px solid var(--error-border);
      color: var(--error-border);
      padding: 8px 12px;
      border-radius: 6px;
      margin-top: 6px;
    }
    @media print {
      body { background: white; color: black; padding: 0; }
      .header-card, .turn, pre, details { border: 1px solid #ccc; background: white; color: black; box-shadow: none; }
      details[open] { display: block; }
    }
");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("  <div class=\"container\">");

        if (options.IncludeMetadataHeader)
        {
            sb.AppendLine("    <div class=\"header-card\">");
            sb.AppendLine($"      <h1>{WebUtility.HtmlEncode(session.Title ?? "Session Transcript")}</h1>");
            sb.AppendLine("      <div class=\"meta-grid\">");
            sb.AppendLine($"        <div class=\"meta-item\"><strong>Session ID:</strong> <code>{WebUtility.HtmlEncode(session.SessionId)}</code></div>");
            sb.AppendLine($"        <div class=\"meta-item\"><strong>Provider:</strong> {WebUtility.HtmlEncode(session.ProviderSlug)}</div>");
            if (!string.IsNullOrWhiteSpace(session.Workspace))
            {
                sb.AppendLine($"        <div class=\"meta-item\"><strong>Workspace:</strong> <code>{WebUtility.HtmlEncode(session.Workspace)}</code></div>");
            }
            if (!string.IsNullOrWhiteSpace(session.ModelName))
            {
                sb.AppendLine($"        <div class=\"meta-item\"><strong>Model:</strong> {WebUtility.HtmlEncode(session.ModelName)}</div>");
            }
            if (session.StartedAt.HasValue)
            {
                sb.AppendLine($"        <div class=\"meta-item\"><strong>Started:</strong> {WebUtility.HtmlEncode(FormatTimestamp(session.StartedAt.Value))}</div>");
            }
            if (session.EndedAt.HasValue)
            {
                sb.AppendLine($"        <div class=\"meta-item\"><strong>Ended:</strong> {WebUtility.HtmlEncode(FormatTimestamp(session.EndedAt.Value))}</div>");
            }
            sb.AppendLine($"        <div class=\"meta-item\"><strong>Messages:</strong> {session.Messages.Count}</div>");
            sb.AppendLine("      </div>");
            sb.AppendLine("    </div>");
        }

        foreach (var msg in session.Messages)
        {
            var badgeClass = msg.Role switch
            {
                MessageRole.User => "badge-user",
                MessageRole.Assistant => "badge-assistant",
                MessageRole.Tool => "badge-tool",
                MessageRole.System => "badge-system",
                _ => "badge-system"
            };

            var roleName = msg.Role switch
            {
                MessageRole.User => "User",
                MessageRole.Assistant => "Assistant",
                MessageRole.Tool => "Tool",
                MessageRole.System => "System",
                _ => msg.Role.ToString()
            };

            var timeStr = msg.Timestamp.HasValue ? FormatTimestamp(msg.Timestamp.Value) : "";

            sb.AppendLine("    <div class=\"turn\">");
            sb.AppendLine("      <div class=\"turn-header\">");
            sb.AppendLine($"        <span class=\"badge {badgeClass}\">{roleName} (Turn {msg.Index + 1})</span>");
            if (!string.IsNullOrEmpty(timeStr))
            {
                sb.AppendLine($"        <span class=\"turn-time\">{timeStr}</span>");
            }
            sb.AppendLine("      </div>");

            // Thinking block
            if (options.IncludeThinking && msg.Extra != null)
            {
                string? thinking = null;
                if (msg.Extra.TryGetValue("thinking", out var th) && th != null) thinking = th.ToString();
                else if (msg.Extra.TryGetValue("reasoning", out var rs) && rs != null) thinking = rs.ToString();
                else if (msg.Extra.TryGetValue("reasoning_content", out var rc) && rc != null) thinking = rc.ToString();

                if (!string.IsNullOrWhiteSpace(thinking))
                {
                    sb.AppendLine("      <details>");
                    sb.AppendLine("        <summary>💭 Thought Process</summary>");
                    sb.AppendLine($"        <pre><code>{WebUtility.HtmlEncode(thinking.Trim())}</code></pre>");
                    sb.AppendLine("      </details>");
                }
            }

            // Message text
            if (!string.IsNullOrWhiteSpace(msg.Content))
            {
                sb.AppendLine($"      <div class=\"content\">{WebUtility.HtmlEncode(msg.Content.Trim())}</div>");
            }

            // Tool Calls
            if (options.IncludeToolCalls && msg.ToolCalls.Count > 0)
            {
                foreach (var tool in msg.ToolCalls)
                {
                    var idSuffix = string.IsNullOrWhiteSpace(tool.Id) ? string.Empty : $" ({WebUtility.HtmlEncode(tool.Id)})";
                    sb.AppendLine("      <details>");
                    sb.AppendLine($"        <summary>🛠 Tool Call: {WebUtility.HtmlEncode(tool.Name)}{idSuffix}</summary>");
                    if (!string.IsNullOrWhiteSpace(tool.ArgumentsJson))
                    {
                        sb.AppendLine($"        <pre><code>{WebUtility.HtmlEncode(tool.ArgumentsJson.Trim())}</code></pre>");
                    }
                    sb.AppendLine("      </details>");
                }
            }

            // Tool Results
            if (options.IncludeToolResults && msg.ToolResults.Count > 0)
            {
                foreach (var res in msg.ToolResults)
                {
                    var status = res.IsError ? "❌ Error" : "✔ Success";
                    sb.AppendLine("      <details>");
                    sb.AppendLine($"        <summary>⚙ Tool Result ({WebUtility.HtmlEncode(res.CallId ?? "output")}) - {status}</summary>");
                    if (!string.IsNullOrWhiteSpace(res.Content))
                    {
                        var cls = res.IsError ? "class=\"error-box\"" : "";
                        sb.AppendLine($"        <pre {cls}><code>{WebUtility.HtmlEncode(res.Content.Trim())}</code></pre>");
                    }
                    sb.AppendLine("      </details>");
                }
            }

            // Leftover Extra (anything that is not thinking/reasoning) as generic context.
            if (msg.Extra != null && msg.Extra.Count > 0)
            {
                var leftover = new List<KeyValuePair<string, object?>>();
                foreach (var kv in msg.Extra)
                {
                    if (string.Equals(kv.Key, "thinking", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(kv.Key, "reasoning", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(kv.Key, "reasoning_content", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    leftover.Add(kv);
                }
                if (leftover.Count > 0)
                {
                    sb.AppendLine("      <details>");
                    sb.AppendLine("        <summary>📎 Additional context</summary>");
                    sb.AppendLine("        <ul>");
                    foreach (var kv in leftover)
                    {
                        sb.AppendLine($"          <li><strong>{WebUtility.HtmlEncode(kv.Key)}:</strong> <code>{WebUtility.HtmlEncode(kv.Value?.ToString()?.Trim() ?? "null")}</code></li>");
                    }
                    sb.AppendLine("        </ul>");
                    sb.AppendLine("      </details>");
                }
            }

            sb.AppendLine("    </div>");
        }

        if (session.Metadata != null && session.Metadata.Count > 0)
        {
            sb.AppendLine("    <div class=\"turn\">");
            sb.AppendLine("      <div class=\"turn-header\">");
            sb.AppendLine("        <span class=\"badge badge-system\">📎 Additional Session Context</span>");
            sb.AppendLine("      </div>");
            sb.AppendLine("      <ul>");
            foreach (var kv in session.Metadata)
            {
                sb.AppendLine($"        <li><strong>{WebUtility.HtmlEncode(kv.Key)}:</strong> <code>{WebUtility.HtmlEncode(kv.Value?.ToString()?.Trim() ?? "null")}</code></li>");
            }
            sb.AppendLine("      </ul>");
            sb.AppendLine("    </div>");
        }

        sb.AppendLine("  </div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }
}
