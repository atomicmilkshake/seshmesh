using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Casr.Core.Models;

public enum MessageRole
{
    User,
    Assistant,
    Tool,
    System,
    Other
}

public class ToolCall
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ArgumentsJson { get; set; } = string.Empty;
}

public class ToolResult
{
    public string? CallId { get; set; }
    public string Content { get; set; } = string.Empty;
    public bool IsError { get; set; }
}

public class CanonicalMessage
{
    public int Index { get; set; }
    public MessageRole Role { get; set; } = MessageRole.User;
    public string Content { get; set; } = string.Empty;
    public long? TimestampEpochMs { get; set; }
    public string? Author { get; set; }
    public List<ToolCall> ToolCalls { get; set; } = new();
    public List<ToolResult> ToolResults { get; set; } = new();
    public Dictionary<string, object?> Extra { get; set; } = new();

    public DateTime? Timestamp => TimestampEpochMs.HasValue
        ? DateTimeOffset.FromUnixTimeMilliseconds(TimestampEpochMs.Value).LocalDateTime
        : null;
}

public class CanonicalSession
{
    public string SessionId { get; set; } = string.Empty;
    public string ProviderSlug { get; set; } = string.Empty;
    public string? Workspace { get; set; }
    public string? Title { get; set; }
    public long? StartedAtEpochMs { get; set; }
    public long? EndedAtEpochMs { get; set; }
    public List<CanonicalMessage> Messages { get; set; } = new();
    public Dictionary<string, object?> Metadata { get; set; } = new();
    public string SourcePath { get; set; } = string.Empty;
    public string? ModelName { get; set; }
    public bool IsSubagent { get; set; }

    public DateTime? StartedAt => StartedAtEpochMs.HasValue
        ? DateTimeOffset.FromUnixTimeMilliseconds(StartedAtEpochMs.Value).LocalDateTime
        : null;

    public DateTime? EndedAt => EndedAtEpochMs.HasValue
        ? DateTimeOffset.FromUnixTimeMilliseconds(EndedAtEpochMs.Value).LocalDateTime
        : null;
}

public class SessionSummary
{
    public string SessionId { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string ProviderDisplayName { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? NativeName { get; set; }
    public int MessagesCount { get; set; }
    public string? Workspace { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? LastActiveAt { get; set; }
    public long FileSizeBytes { get; set; }
    public string? ModelName { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public int ToolCallsCount { get; set; }
    public bool IsSubagent { get; set; }

    /// <summary>
    /// Set when transcript rows have been written. Null means the row is still
    /// a catalog entry. Distinct from <see cref="MessagesCount"/>, which stays
    /// the provider's own step or line count.
    /// </summary>
    public long? ContentIndexedAtEpochMs { get; set; }

    /// <summary>COUNT of stored message rows for this session. Zero when none have been written.</summary>
    public int StoredMessageCount { get; set; }

    public bool ContentIndexed => ContentIndexedAtEpochMs.HasValue;

    /// <summary>
    /// Number shown in the Turns column. After a content index, this is the
    /// stored row count. <see cref="MessagesCount"/> stays the provider count
    /// so the content-index skip gate can still compare against it.
    /// </summary>
    public int DisplayedMessageCount =>
        ContentIndexed && !CursorCatalogOnly ? StoredMessageCount : MessagesCount;

    /// <summary>Cursor with no content stamp. The summary count stays visible beside the words.</summary>
    public bool CursorCatalogOnly =>
        string.Equals(Provider, "cursor", StringComparison.OrdinalIgnoreCase) && !ContentIndexed;

    public string TurnsDisplay => CursorCatalogOnly
        ? $"{MessagesCount} not indexed"
        : DisplayedMessageCount.ToString();

    /// <summary>Non-empty workspace path that is not a directory on this machine.</summary>
    public bool WorkspaceMissing =>
        !string.IsNullOrWhiteSpace(Workspace) && !Directory.Exists(Workspace);

    public DateTime RecencyDate => LastActiveAt ?? StartedAt ?? DateTime.MinValue;
    public string DisplayDate => (LastActiveAt ?? StartedAt)?.ToString("yyyy-MM-dd HH:mm:ss") ?? "Unknown";
    public string FileSizeDisplay => $"{FileSizeBytes / 1024.0:F1} KB";
    public string WorkspaceDisplay => string.IsNullOrWhiteSpace(Workspace)
        ? "—"
        : WorkspaceMissing ? Workspace + " (missing folder)" : Workspace;
    public string TitleDisplay => string.IsNullOrWhiteSpace(Title) ? "(Untitled session)" : Title;
}

public static class ModelHelpers
{
    public static string EffectiveWorkspace(CanonicalSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.Workspace) && Directory.Exists(session.Workspace))
        {
            return session.Workspace;
        }

        return Environment.CurrentDirectory;
    }

    public static string FlattenContent(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString() ?? string.Empty;

            case JsonValueKind.Array:
                var parts = new List<string>();
                foreach (var item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        parts.Add(item.GetString() ?? string.Empty);
                    }
                    else if (item.ValueKind == JsonValueKind.Object)
                    {
                        if (item.TryGetProperty("type", out var typeProp))
                        {
                            var typeStr = typeProp.GetString();
                            if (typeStr is "text" or "input_text" or "output_text")
                            {
                                if (item.TryGetProperty("text", out var textProp))
                                {
                                    parts.Add(textProp.GetString() ?? string.Empty);
                                }
                            }
                            else if (typeStr == "tool_use")
                            {
                                var toolName = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : "tool";
                                parts.Add($"[Tool: {toolName}]");
                            }
                        }
                        else if (item.TryGetProperty("text", out var textProp))
                        {
                            parts.Add(textProp.GetString() ?? string.Empty);
                        }
                    }
                }
                return string.Join("\n", parts);

            case JsonValueKind.Object:
                // Object content (e.g. {"text": ...} or {"content": ...}): extract the
                // text instead of dumping the raw JSON blob into message content.
                if (element.TryGetProperty("text", out var objText) && objText.ValueKind == JsonValueKind.String)
                {
                    return objText.GetString() ?? string.Empty;
                }
                if (element.TryGetProperty("content", out var objContent))
                {
                    return FlattenContent(objContent);
                }
                if (element.TryGetProperty("parts", out var objParts))
                {
                    return FlattenContent(objParts);
                }
                var childTexts = new List<string>();
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.NameEquals("text")) continue;
                    var val = prop.Value;
                    if (val.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in val.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.Object &&
                                item.TryGetProperty("text", out var itemText) &&
                                itemText.ValueKind == JsonValueKind.String)
                            {
                                childTexts.Add(itemText.GetString() ?? string.Empty);
                            }
                        }
                    }
                    else if (val.ValueKind == JsonValueKind.Object &&
                             val.TryGetProperty("text", out var childText) &&
                             childText.ValueKind == JsonValueKind.String)
                    {
                        childTexts.Add(childText.GetString() ?? string.Empty);
                    }
                }
                if (childTexts.Count > 0)
                {
                    return string.Join("\n", childTexts);
                }
                return element.ToString();

            default:
                return element.ToString();
        }
    }

    public static string CleanTitle(string? title, int maxLength = 80)
    {
        if (string.IsNullOrWhiteSpace(title)) return "(Untitled)";

        var text = title.Trim();

        // 1. Extract content from XML wrapper tags like <USER_REQUEST>...</USER_REQUEST> if present
        var userReqMatch = Regex.Match(text, @"<USER_REQUEST>(.*?)(?:</USER_REQUEST>|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (userReqMatch.Success && !string.IsNullOrWhiteSpace(userReqMatch.Groups[1].Value))
        {
            text = userReqMatch.Groups[1].Value.Trim();
        }

        // 2. Strip XML/HTML wrapper tags
        text = Regex.Replace(text, @"</?[a-zA-Z0-9_\-]+(?:\s+[^>]*)?>", " ");

        // 3. Normalize all whitespace (newlines, carriage returns, tabs) into single spaces
        text = Regex.Replace(text, @"[\r\n\t]+", " ");
        text = Regex.Replace(text, @"\s{2,}", " ").Trim();

        // 4. Repeatedly strip leading markdown headings, bullet points, and slash commands until stable
        while (true)
        {
            var before = text;

            // Strip leading markdown headings (#, ##, etc.) and bullet points (-, *, +, 1., •)
            text = Regex.Replace(text, @"^(?:#{1,6}|\*|\-|\+|\d+\.|•)\s*", "").Trim();

            // Strip leading slash commands (e.g. /plan, /ask, /brainstorm, /code, /test, etc.)
            text = Regex.Replace(text, @"^/[a-zA-Z0-9_\-]+(?::|\s+|$)\s*", "", RegexOptions.IgnoreCase).Trim();

            if (text == before) break;
        }

        // 5. Clean up surrounding quotes or markers
        text = text.Trim(' ', '\t', '"', '\'', '`');

        if (string.IsNullOrWhiteSpace(text)) return "(Untitled)";

        // 6. Truncate if requested
        if (maxLength > 0 && text.Length > maxLength)
        {
            return text.Substring(0, Math.Max(0, maxLength - 3)).TrimEnd() + "...";
        }

        return text;
    }

    public static string TruncateTitle(string? title, int maxLength = 80)
    {
        return CleanTitle(title, maxLength);
    }

    public static long? ParseTimestamp(object? value)
    {
        if (value == null) return null;

        if (value is long l)
        {
            return l > 1000000000000L ? l : l * 1000;
        }

        if (value is double d)
        {
            var asLong = (long)d;
            return asLong > 1000000000000L ? asLong : asLong * 1000;
        }

        if (value is JsonElement elem)
        {
            if (elem.ValueKind == JsonValueKind.Number && elem.TryGetInt64(out var num))
            {
                return num > 1000000000000L ? num : num * 1000;
            }
            if (elem.ValueKind == JsonValueKind.String)
            {
                value = elem.GetString();
            }
        }

        if (value is string s)
        {
            if (long.TryParse(s, out var num))
            {
                return num > 1000000000000L ? num : num * 1000;
            }
            if (DateTimeOffset.TryParse(s, out var dto))
            {
                return dto.ToUnixTimeMilliseconds();
            }
        }

        return null;
    }

    public static bool IsSubagentPrompt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var clean = CleanTitle(text, maxLength: -1);
        if (string.IsNullOrWhiteSpace(clean) || clean.Equals("(Untitled)", StringComparison.OrdinalIgnoreCase)) return false;

        return clean.StartsWith("You are ", StringComparison.OrdinalIgnoreCase) ||
               clean.StartsWith("You are a ", StringComparison.OrdinalIgnoreCase) ||
               clean.StartsWith("You are an ", StringComparison.OrdinalIgnoreCase) ||
               clean.StartsWith("You are the ", StringComparison.OrdinalIgnoreCase) ||
               clean.StartsWith("Your task is ", StringComparison.OrdinalIgnoreCase) ||
               clean.StartsWith("Your role is ", StringComparison.OrdinalIgnoreCase) ||
               clean.StartsWith("Your mission is ", StringComparison.OrdinalIgnoreCase) ||
               clean.StartsWith("Your objective is ", StringComparison.OrdinalIgnoreCase) ||
               clean.StartsWith("System Auditor", StringComparison.OrdinalIgnoreCase) ||
               clean.StartsWith("Auditor prompt", StringComparison.OrdinalIgnoreCase) ||
               clean.StartsWith("SYSTEM:", StringComparison.OrdinalIgnoreCase) ||
               clean.StartsWith("SYSTEM PROMPT:", StringComparison.OrdinalIgnoreCase) ||
               clean.StartsWith("You must act as ", StringComparison.OrdinalIgnoreCase) ||
               clean.StartsWith("Act as ", StringComparison.OrdinalIgnoreCase);
    }

    public static string? DecodeFileUri(string? uriString)
    {
        if (string.IsNullOrWhiteSpace(uriString)) return null;
        try
        {
            var raw = uriString.Trim().Trim('"', '\'');
            if (raw.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                {
                    var local = uri.LocalPath;
                    if (local.Length >= 3 && local[0] == '/' && char.IsLetter(local[1]) && local[2] == ':')
                    {
                        local = local.Substring(1);
                    }
                    return Path.GetFullPath(local);
                }
            }
            if (Path.IsPathRooted(raw))
            {
                return Path.GetFullPath(raw);
            }
        }
        catch { }
        return uriString;
    }
}
