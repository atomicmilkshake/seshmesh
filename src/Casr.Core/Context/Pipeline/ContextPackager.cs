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

    public CanonicalSession Package(CanonicalSession source, HarnessCapabilities targetCapabilities)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (targetCapabilities == null) throw new ArgumentNullException(nameof(targetCapabilities));

        var context = Normalize(source);
        if (context.Messages.Count == 0)
        {
            return source;
        }

        // 1. Check token budget & perform compaction if needed
        CompactIfNeeded(context, targetCapabilities);

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
            var thinkingContent = ctxMsg.GetThinkingContent();

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

        return result;
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
}
