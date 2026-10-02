using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Casr.Core.Models;

namespace Casr.Core.Context.Models;

/// <summary>
/// A normalized message within the canonical context representation, composed of typed MessageParts.
/// </summary>
public class ContextMessage
{
    public int Index { get; set; }
    public MessageRole Role { get; set; } = MessageRole.User;
    public List<MessagePart> Parts { get; set; } = new();
    public long? TimestampEpochMs { get; set; }
    public string? Author { get; set; }
    public Dictionary<string, object?> Extra { get; set; } = new();

    public string GetTextContent()
    {
        var textParts = Parts.OfType<TextPart>().Select(p => p.Text).Where(t => !string.IsNullOrEmpty(t));
        return string.Join("\n", textParts);
    }

    public string? GetThinkingContent()
    {
        var thinkingParts = Parts.OfType<ThinkingPart>().Select(p => p.ReasoningText).Where(t => !string.IsNullOrEmpty(t));
        var text = string.Join("\n", thinkingParts);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public IEnumerable<ToolCallPart> GetToolCalls() => Parts.OfType<ToolCallPart>();

    public IEnumerable<ToolResultPart> GetToolResults() => Parts.OfType<ToolResultPart>();
}

/// <summary>
/// A logical conversation turn consisting of a user trigger and subsequent assistant/tool interactions.
/// </summary>
public class ContextTurn
{
    public int TurnIndex { get; set; }
    public ContextMessage? UserMessage { get; set; }
    public List<ContextMessage> Messages { get; set; } = new();
}

/// <summary>
/// Harness-agnostic canonical context model. Holds full semantic fidelity including
/// structured message parts, thinking, tool calls, and turn groupings.
/// </summary>
public class CanonicalContext
{
    public string SessionId { get; set; } = string.Empty;
    public string SourceProviderSlug { get; set; } = string.Empty;
    public string? Workspace { get; set; }
    public string? Title { get; set; }
    public long? StartedAtEpochMs { get; set; }
    public long? EndedAtEpochMs { get; set; }
    public string? ModelName { get; set; }
    public bool IsSubagent { get; set; }
    public List<ContextMessage> Messages { get; set; } = new();
    public List<ContextTurn> Turns { get; set; } = new();
    public Dictionary<string, object?> Metadata { get; set; } = new();

    public static CanonicalContext FromCanonicalSession(CanonicalSession session)
    {
        var context = new CanonicalContext
        {
            SessionId = session.SessionId,
            SourceProviderSlug = session.ProviderSlug,
            Workspace = session.Workspace,
            Title = session.Title,
            StartedAtEpochMs = session.StartedAtEpochMs,
            EndedAtEpochMs = session.EndedAtEpochMs,
            ModelName = session.ModelName,
            IsSubagent = session.IsSubagent,
            Metadata = new Dictionary<string, object?>(session.Metadata)
        };

        if (session.Messages == null || session.Messages.Count == 0)
        {
            return context;
        }

        var normalizedMessages = new List<ContextMessage>();
        foreach (var msg in session.Messages)
        {
            var ctxMsg = new ContextMessage
            {
                Index = msg.Index,
                Role = msg.Role,
                TimestampEpochMs = msg.TimestampEpochMs,
                Author = msg.Author,
                Extra = new Dictionary<string, object?>(msg.Extra)
            };

            // 1. Extract thinking from Extra or Content tags if present
            string? reasoning = null;
            if (msg.Extra.TryGetValue("thinking", out var th) && th != null)
                reasoning = th.ToString();
            else if (msg.Extra.TryGetValue("reasoning", out var r) && r != null)
                reasoning = r.ToString();
            else if (msg.Extra.TryGetValue("reasoning_content", out var rc) && rc != null)
                reasoning = rc.ToString();

            var content = msg.Content ?? string.Empty;

            // Extract embedded thinking tags e.g. <thought>...</thought> or <thinking>...</thinking>
            var matchThought = Regex.Match(content, @"<(thought|thinking)>(.*?)</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (matchThought.Success)
            {
                var embeddedThinking = matchThought.Groups[2].Value.Trim();
                if (!string.IsNullOrEmpty(embeddedThinking))
                {
                    reasoning = string.IsNullOrEmpty(reasoning) ? embeddedThinking : $"{reasoning}\n{embeddedThinking}";
                }
                content = content.Remove(matchThought.Index, matchThought.Length).Trim();
            }

            if (!string.IsNullOrWhiteSpace(reasoning))
            {
                ctxMsg.Parts.Add(new ThinkingPart(reasoning.Trim()));
            }

            // 2. Add text part if content remains
            if (!string.IsNullOrWhiteSpace(content))
            {
                ctxMsg.Parts.Add(new TextPart(content));
            }

            // 3. Add tool calls
            if (msg.ToolCalls != null)
            {
                foreach (var tc in msg.ToolCalls)
                {
                    ctxMsg.Parts.Add(new ToolCallPart(tc.Id ?? Guid.NewGuid().ToString(), tc.Name, tc.ArgumentsJson));
                }
            }

            // 4. Add tool results
            if (msg.ToolResults != null)
            {
                foreach (var tr in msg.ToolResults)
                {
                    string? toolName = null;
                    if (msg.Extra.TryGetValue("tool_name", out var tn) && tn != null)
                    {
                        toolName = tn.ToString();
                    }
                    ctxMsg.Parts.Add(new ToolResultPart(tr.CallId ?? Guid.NewGuid().ToString(), tr.Content, tr.IsError, toolName));
                }
            }

            normalizedMessages.Add(ctxMsg);
        }

        context.Messages = normalizedMessages;
        context.GroupTurns();
        return context;
    }

    public void GroupTurns()
    {
        Turns.Clear();
        ContextTurn? currentTurn = null;
        int turnIdx = 0;

        foreach (var msg in Messages)
        {
            if (msg.Role == MessageRole.User)
            {
                currentTurn = new ContextTurn
                {
                    TurnIndex = turnIdx++,
                    UserMessage = msg
                };
                currentTurn.Messages.Add(msg);
                Turns.Add(currentTurn);
            }
            else
            {
                if (currentTurn == null)
                {
                    currentTurn = new ContextTurn
                    {
                        TurnIndex = turnIdx++
                    };
                    Turns.Add(currentTurn);
                }
                currentTurn.Messages.Add(msg);
            }
        }
    }

    public CanonicalSession ToCanonicalSession()
    {
        var session = new CanonicalSession
        {
            SessionId = SessionId,
            ProviderSlug = SourceProviderSlug,
            Workspace = Workspace,
            Title = Title,
            StartedAtEpochMs = StartedAtEpochMs,
            EndedAtEpochMs = EndedAtEpochMs,
            ModelName = ModelName,
            IsSubagent = IsSubagent,
            Metadata = new Dictionary<string, object?>(Metadata)
        };

        var canonicalMessages = new List<CanonicalMessage>();
        foreach (var ctxMsg in Messages)
        {
            var msg = new CanonicalMessage
            {
                Index = ctxMsg.Index,
                Role = ctxMsg.Role,
                TimestampEpochMs = ctxMsg.TimestampEpochMs,
                Author = ctxMsg.Author,
                Extra = new Dictionary<string, object?>(ctxMsg.Extra)
            };

            var textContent = ctxMsg.GetTextContent();
            var thinkingContent = ctxMsg.GetThinkingContent();

            if (!string.IsNullOrWhiteSpace(thinkingContent))
            {
                msg.Extra["thinking"] = thinkingContent;
            }

            msg.Content = textContent;

            foreach (var tc in ctxMsg.GetToolCalls())
            {
                msg.ToolCalls.Add(new ToolCall
                {
                    Id = tc.Id,
                    Name = tc.ToolName,
                    ArgumentsJson = tc.ArgumentsJson
                });
            }

            foreach (var tr in ctxMsg.GetToolResults())
            {
                msg.ToolResults.Add(new ToolResult
                {
                    CallId = tr.CallId,
                    Content = tr.Output,
                    IsError = tr.IsError
                });
                if (!string.IsNullOrEmpty(tr.ToolName))
                {
                    msg.Extra["tool_name"] = tr.ToolName;
                }
            }

            canonicalMessages.Add(msg);
        }

        session.Messages = canonicalMessages;
        return session;
    }
}
