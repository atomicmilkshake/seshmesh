using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Casr.Core.Context.Capabilities;
using Casr.Core.Context.Models;
using Casr.Core.Models;

namespace Casr.Core.Context.Pipeline;

public class ContextPackager : IContextPackager
{
    public static readonly ContextPackager Default = new();

    public CanonicalContext Normalize(CanonicalSession session)
    {
        return CanonicalContext.FromCanonicalSession(session);
    }

    /// <summary>Legacy packaging (pre-conversion-dialog behavior), preserved for existing callers.</summary>
    public CanonicalSession Package(CanonicalSession source, HarnessCapabilities targetCapabilities)
    {
        return Package(source, targetCapabilities, ConversionOptions.Legacy);
    }

    public CanonicalSession Package(CanonicalSession source, HarnessCapabilities targetCapabilities, ConversionOptions options)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (targetCapabilities == null) throw new ArgumentNullException(nameof(targetCapabilities));
        options ??= ConversionOptions.Default;

        var context = Normalize(source);
        if (context.Messages.Count == 0)
        {
            return source;
        }

        // 1. Token budget & compaction (reasoning drop, tool truncation, turn dropping)
        ApplyConversionBudget(context, targetCapabilities, options);

        // 2. Build packaged messages matching target tool style & thinking support
        var packagedMessages = new List<CanonicalMessage>();
        int msgIndex = 0;

        foreach (var ctxMsg in context.Messages)
        {
            var canMsg = new CanonicalMessage
            {
                Index = msgIndex++,
                Role = ctxMsg.Role,
                TimestampEpochMs = ctxMsg.TimestampEpochMs,
                Author = ctxMsg.Author,
                Extra = new Dictionary<string, object?>(ctxMsg.Extra)
            };

            var textContent = ctxMsg.GetTextContent();
            var thinkingContent = options.KeepReasoning ? ctxMsg.GetThinkingContent() : null;

            // Thinking handling
            if (!string.IsNullOrWhiteSpace(thinkingContent))
            {
                if (targetCapabilities.SupportsNativeThinking)
                {
                    canMsg.Extra["thinking"] = thinkingContent;
                    canMsg.Extra["reasoning"] = thinkingContent;
                    canMsg.Extra["reasoning_content"] = thinkingContent;
                }
                else
                {
                    // Synthesize thinking into text block
                    var thinkingBlock = $"[Reasoning Process]\n{thinkingContent}\n[/Reasoning Process]";
                    textContent = string.IsNullOrWhiteSpace(textContent)
                        ? thinkingBlock
                        : $"{thinkingBlock}\n\n{textContent}";
                }
            }
            else if (!options.KeepReasoning)
            {
                // Drop the source agent's hidden reasoning for cross-agent handoffs.
                canMsg.Extra.Remove("thinking");
                canMsg.Extra.Remove("reasoning");
                canMsg.Extra.Remove("reasoning_content");
            }

            // Tool handling
            switch (targetCapabilities.ToolStyle)
            {
                case ToolPackagingStyle.Native:
                    // Preserve native tool calls & results
                    foreach (var tc in ctxMsg.GetToolCalls())
                    {
                        canMsg.ToolCalls.Add(new ToolCall
                        {
                            Id = tc.Id,
                            Name = tc.ToolName,
                            ArgumentsJson = tc.ArgumentsJson
                        });
                    }

                    foreach (var tr in ctxMsg.GetToolResults())
                    {
                        canMsg.ToolResults.Add(new ToolResult
                        {
                            CallId = tr.CallId,
                            Content = tr.Output,
                            IsError = tr.IsError
                        });
                        if (!string.IsNullOrEmpty(tr.ToolName))
                        {
                            canMsg.Extra["tool_name"] = tr.ToolName;
                        }
                    }
                    canMsg.Content = textContent;
                    break;

                case ToolPackagingStyle.MarkdownSynthesis:
                    var sb = new StringBuilder();
                    if (!string.IsNullOrWhiteSpace(textContent))
                    {
                        sb.AppendLine(textContent);
                    }

                    // Synthesize tool calls
                    foreach (var tc in ctxMsg.GetToolCalls())
                    {
                        if (sb.Length > 0 && !sb.ToString().EndsWith("\n\n")) sb.AppendLine();
                        sb.AppendLine($"[Tool Call: {tc.ToolName}]");
                        if (!string.IsNullOrWhiteSpace(tc.ArgumentsJson))
                        {
                            sb.AppendLine("Arguments:");
                            sb.AppendLine("```json");
                            sb.AppendLine(tc.ArgumentsJson.Trim());
                            sb.AppendLine("```");
                        }
                    }

                    // Synthesize tool results
                    foreach (var tr in ctxMsg.GetToolResults())
                    {
                        if (sb.Length > 0 && !sb.ToString().EndsWith("\n\n")) sb.AppendLine();
                        var name = !string.IsNullOrEmpty(tr.ToolName) ? tr.ToolName : "Tool";
                        var status = tr.IsError ? " (Failed)" : " (Success)";
                        sb.AppendLine($"[Tool Result: {name}{status}]");
                        if (!string.IsNullOrWhiteSpace(tr.Output))
                        {
                            sb.AppendLine("```");
                            sb.AppendLine(tr.Output.Trim());
                            sb.AppendLine("```");
                        }
                    }

                    canMsg.Content = sb.ToString().Trim();
                    // Clear native lists as they are synthesized into content
                    canMsg.ToolCalls.Clear();
                    canMsg.ToolResults.Clear();
                    break;

                case ToolPackagingStyle.OmitToolHistory:
                    canMsg.Content = textContent;
                    canMsg.ToolCalls.Clear();
                    canMsg.ToolResults.Clear();
                    break;
            }

            // Flag for usage requirement if specified
            if (targetCapabilities.RequiresUsageOnAssistant && canMsg.Role == MessageRole.Assistant)
            {
                canMsg.Extra["requires_usage"] = true;
            }

            packagedMessages.Add(canMsg);
        }

        // 3. Optional synthetic context enrichment (casr --enrich analogue): a
        // conversion notice plus a recent-conversation snapshot, both marked so a
        // later reader can tell them apart from the original transcript.
        bool enriched = false;
        if (options.Enrich && packagedMessages.Count > 0)
        {
            enriched = PrependEnrichmentMessages(packagedMessages, source, targetCapabilities.HarnessSlug) > 0;
        }

        var result = new CanonicalSession
        {
            SessionId = source.SessionId,
            ProviderSlug = targetCapabilities.HarnessSlug,
            Workspace = source.Workspace,
            Title = source.Title,
            StartedAtEpochMs = source.StartedAtEpochMs,
            EndedAtEpochMs = source.EndedAtEpochMs,
            ModelName = source.ModelName,
            IsSubagent = source.IsSubagent,
            Metadata = new Dictionary<string, object?>(source.Metadata),
            Messages = packagedMessages
        };

        if (enriched)
        {
            result.Metadata["seshmesh_enrichment_applied"] = true;
        }

        return result;
    }

    /// <summary>
    /// Applies the conversion budget: drops hidden reasoning unless kept, truncates
    /// oversized tool observations, then drops the oldest middle turns if the history
    /// is still over the token cap (the original task message and the most recent
    /// turns are preserved; tool call/result pairs stay within a turn so they are never
    /// severed).
    /// </summary>
    private static void ApplyConversionBudget(CanonicalContext context, HarnessCapabilities target, ConversionOptions options)
    {
        // 1. Reasoning
        if (!options.KeepReasoning)
        {
            foreach (var msg in context.Messages)
            {
                msg.Parts.RemoveAll(p => p is ThinkingPart);
            }
        }

        // 2. Tool observations
        if (options.MaxToolOutput > 0)
        {
            TruncateAllToolResults(context, options.MaxToolOutput);
        }
        else if (EstimateTokens(context) > target.TokenContextLimit)
        {
            // Legacy behavior: per-turn caps, only when the harness limit is exceeded.
            CompactIfNeeded(context, target);
        }

        // 3. Token cap (turn dropping only when an explicit budget is set; legacy
        //    MaxContextTokens == 0 keeps the harness limit as a truncation trigger).
        if (options.MaxContextTokens > 0)
        {
            DropOldestTurns(context, options.MaxContextTokens, target.PreserveRecentTurnsCount);
        }
    }

    private static void DropOldestTurns(CanonicalContext context, int tokenLimit, int preserveRecentTurns)
    {
        if (tokenLimit <= 0) return;

        int protectedTurns = 1 + Math.Max(1, preserveRecentTurns);
        while (context.Turns.Count > protectedTurns && EstimateTokens(context) > tokenLimit)
        {
            var drop = context.Turns[1];
            foreach (var msg in drop.Messages)
            {
                context.Messages.Remove(msg);
            }
            context.Turns.RemoveAt(1);
        }
    }

    private static int PrependEnrichmentMessages(List<CanonicalMessage> messages, CanonicalSession source, string targetSlug)
    {
        var sourceProvider = string.IsNullOrWhiteSpace(source.ProviderSlug) ? "unknown" : source.ProviderSlug;
        var targetProvider = string.IsNullOrWhiteSpace(targetSlug) ? "unknown" : targetSlug;

        long? firstTimestamp = messages
            .Where(m => m.TimestampEpochMs.HasValue)
            .Select(m => m.TimestampEpochMs!.Value)
            .DefaultIfEmpty(0L)
            .Min();
        long? noticeTimestamp = firstTimestamp is > 0 ? firstTimestamp - 2 : null;
        long? summaryTimestamp = noticeTimestamp.HasValue ? noticeTimestamp + 1 : null;

        var noticeLines = new List<string>
        {
            "[seshmesh synthetic context]",
            $"This session was originally created in {sourceProvider} and converted to {targetProvider} format by SeshMesh.",
            $"Original session ID: {source.SessionId}.",
            "Some provider-specific context may have been lost in conversion.",
            $"Original message count: {messages.Count}."
        };
        if (!string.IsNullOrWhiteSpace(source.Workspace))
        {
            noticeLines.Add($"Workspace: {source.Workspace}");
        }

        var (summaryCount, summaryLines) = BuildRecentSummary(messages, 4, 180);

        var notice = new CanonicalMessage
        {
            Index = 0,
            Role = MessageRole.System,
            Content = string.Join("\n", noticeLines),
            TimestampEpochMs = noticeTimestamp,
            Author = "seshmesh-enrichment",
            Extra = new Dictionary<string, object?>
            {
                ["seshmesh_enrichment"] = true,
                ["synthetic"] = true,
                ["enrichment_type"] = "conversion_notice",
                ["source_provider"] = sourceProvider,
                ["target_provider"] = targetProvider,
                ["source_session_id"] = source.SessionId
            }
        };

        var summary = new CanonicalMessage
        {
            Index = 1,
            Role = MessageRole.System,
            Content = "[seshmesh synthetic context]\nRecent conversation snapshot (last " + summaryCount + " message(s)):\n" + summaryLines,
            TimestampEpochMs = summaryTimestamp,
            Author = "seshmesh-enrichment",
            Extra = new Dictionary<string, object?>
            {
                ["seshmesh_enrichment"] = true,
                ["synthetic"] = true,
                ["enrichment_type"] = "recent_summary",
                ["source_provider"] = sourceProvider,
                ["target_provider"] = targetProvider,
                ["source_session_id"] = source.SessionId,
                ["summary_message_count"] = summaryCount
            }
        };

        messages.Insert(0, summary);
        messages.Insert(0, notice);

        for (int i = 0; i < messages.Count; i++)
        {
            messages[i].Index = i;
        }

        return 2;
    }

    private static (int Count, string Text) BuildRecentSummary(List<CanonicalMessage> messages, int maxMessages, int maxCharsPerMessage)
    {
        int start = Math.Max(0, messages.Count - maxMessages);
        var lines = new List<string>();
        for (int i = start; i < messages.Count; i++)
        {
            var msg = messages[i];
            lines.Add($"- {RoleLabel(msg.Role)}: {CompactSummaryText(msg.Content, maxCharsPerMessage)}");
        }

        if (lines.Count == 0)
        {
            lines.Add("- (no messages)");
        }

        return (lines.Count, string.Join("\n", lines));
    }

    private static string RoleLabel(MessageRole role) => role switch
    {
        MessageRole.User => "user",
        MessageRole.Assistant => "assistant",
        MessageRole.Tool => "tool",
        MessageRole.System => "system",
        _ => "other"
    };

    private static string CompactSummaryText(string? text, int maxChars)
    {
        var compact = string.Join(" ", (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (compact.Length == 0) return "[empty]";
        if (compact.Length <= maxChars) return compact;
        if (maxChars <= 3) return new string('.', maxChars);
        return compact.Substring(0, maxChars - 3) + "...";
    }

    private static void CompactIfNeeded(CanonicalContext context, HarnessCapabilities target)
    {
        int estimatedTokens = EstimateTokens(context);
        if (estimatedTokens <= target.TokenContextLimit)
        {
            return;
        }

        int turnCount = context.Turns.Count;
        if (turnCount <= 2)
        {
            // Truncate tool results directly if we only have 1-2 turns but massive tool output
            TruncateAllToolResults(context, target.MaxToolResultCharsRecent);
            return;
        }

        // Identify turns:
        // Turn 0: User initial goal/intent (protected)
        // Last PreserveRecentTurnsCount: protected with MaxToolResultCharsRecent
        // Middle turns: older turns compacted with MaxToolResultCharsOlder
        int recentStartIndex = Math.Max(1, turnCount - target.PreserveRecentTurnsCount);

        for (int i = 0; i < turnCount; i++)
        {
            var turn = context.Turns[i];
            bool isGoalTurn = (i == 0);
            bool isRecentTurn = (i >= recentStartIndex);

            int maxToolChars = (isGoalTurn || isRecentTurn)
                ? target.MaxToolResultCharsRecent
                : target.MaxToolResultCharsOlder;

            foreach (var msg in turn.Messages)
            {
                for (int p = 0; p < msg.Parts.Count; p++)
                {
                    if (msg.Parts[p] is ToolResultPart tr && tr.Output != null && tr.Output.Length > maxToolChars)
                    {
                        var truncated = tr.Output.Substring(0, maxToolChars) +
                            $"\n\n[... truncated {tr.Output.Length - maxToolChars} characters for context packaging budget ...]";
                        msg.Parts[p] = new ToolResultPart(tr.CallId, truncated, tr.IsError, tr.ToolName);
                    }
                }
            }
        }
    }

    private static void TruncateAllToolResults(CanonicalContext context, int maxChars)
    {
        if (maxChars <= 0) return;
        foreach (var msg in context.Messages)
        {
            for (int p = 0; p < msg.Parts.Count; p++)
            {
                if (msg.Parts[p] is ToolResultPart tr && tr.Output != null && tr.Output.Length > maxChars)
                {
                    var truncated = tr.Output.Substring(0, maxChars) +
                        $"\n\n[... truncated {tr.Output.Length - maxChars} characters for context packaging budget ...]";
                    msg.Parts[p] = new ToolResultPart(tr.CallId, truncated, tr.IsError, tr.ToolName);
                }
            }
        }
    }

    public static int EstimateTokens(CanonicalContext context)
    {
        long charCount = 0;
        foreach (var msg in context.Messages)
        {
            foreach (var part in msg.Parts)
            {
                switch (part)
                {
                    case TextPart tp:
                        charCount += tp.Text?.Length ?? 0;
                        break;
                    case ThinkingPart th:
                        charCount += th.ReasoningText?.Length ?? 0;
                        break;
                    case ToolCallPart tc:
                        charCount += (tc.ToolName?.Length ?? 0) + (tc.ArgumentsJson?.Length ?? 0);
                        break;
                    case ToolResultPart tr:
                        charCount += tr.Output?.Length ?? 0;
                        break;
                }
            }
        }

        return (int)Math.Ceiling(charCount / 4.0);
    }

    /// <summary>Estimates tokens of an already-packaged canonical session (post tool synthesis/enrichment).</summary>
    public static int EstimateTokens(CanonicalSession session)
    {
        long charCount = 0;
        foreach (var msg in session.Messages)
        {
            charCount += msg.Content?.Length ?? 0;
            foreach (var tc in msg.ToolCalls)
            {
                charCount += (tc.Name?.Length ?? 0) + (tc.ArgumentsJson?.Length ?? 0);
            }
            foreach (var tr in msg.ToolResults)
            {
                charCount += tr.Content?.Length ?? 0;
            }
        }

        return (int)Math.Ceiling(charCount / 4.0);
    }
}
