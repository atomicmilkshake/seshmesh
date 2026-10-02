using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Casr.Core.Models;

namespace Casr.Core.Export.Formatters;

public static class JsonSessionFormatter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly HashSet<string> ThinkingKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "thinking", "reasoning", "reasoning_content"
    };

    public static string Format(CanonicalSession session, ExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        options ??= new ExportOptions();

        // Honor IncludeToolCalls / IncludeToolResults / IncludeThinking by filtering
        // a deep clone before serialization. Full fidelity is the default
        // (all three flags true) — disabling a flag strips that slice only.
        if (options.IncludeToolCalls && options.IncludeToolResults && options.IncludeThinking)
        {
            return JsonSerializer.Serialize(session, SerializerOptions);
        }

        var clone = new CanonicalSession
        {
            SessionId = session.SessionId,
            ProviderSlug = session.ProviderSlug,
            Workspace = session.Workspace,
            Title = session.Title,
            StartedAtEpochMs = session.StartedAtEpochMs,
            EndedAtEpochMs = session.EndedAtEpochMs,
            SourcePath = session.SourcePath,
            ModelName = session.ModelName,
            IsSubagent = session.IsSubagent,
            Metadata = session.Metadata != null
                ? new Dictionary<string, object?>(session.Metadata)
                : new Dictionary<string, object?>(),
            Messages = new List<CanonicalMessage>()
        };

        foreach (var msg in session.Messages ?? new List<CanonicalMessage>())
        {
            var copy = new CanonicalMessage
            {
                Index = msg.Index,
                Role = msg.Role,
                Content = msg.Content,
                TimestampEpochMs = msg.TimestampEpochMs,
                Author = msg.Author,
                ToolCalls = options.IncludeToolCalls
                    ? new List<ToolCall>(msg.ToolCalls ?? new List<ToolCall>())
                    : new List<ToolCall>(),
                ToolResults = options.IncludeToolResults
                    ? new List<ToolResult>(msg.ToolResults ?? new List<ToolResult>())
                    : new List<ToolResult>(),
                Extra = new Dictionary<string, object?>()
            };

            if (msg.Extra != null)
            {
                foreach (var kv in msg.Extra)
                {
                    if (!options.IncludeThinking && ThinkingKeys.Contains(kv.Key))
                    {
                        continue;
                    }
                    copy.Extra[kv.Key] = kv.Value;
                }
            }

            clone.Messages.Add(copy);
        }

        return JsonSerializer.Serialize(clone, SerializerOptions);
    }
}
