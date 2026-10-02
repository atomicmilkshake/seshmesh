using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Casr.Core.Logging;
using Casr.Core.Models;

namespace Casr.Core.Providers;

public class PiProvider : IProvider
{
    public const string DefaultModelId = "default";

    public string Name => "Pi";
    public string Slug => "pi";
    public string CliAlias => "pi";
    public bool CanWrite => true;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public static string GetHomeDir()
    {
        var envHome = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
        if (!string.IsNullOrWhiteSpace(envHome)) return envHome;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "agent");
    }

    public static string GetSessionsDir()
    {
        var envSessions = Environment.GetEnvironmentVariable("PI_CODING_AGENT_SESSION_DIR");
        if (!string.IsNullOrWhiteSpace(envSessions)) return envSessions;
        return Path.Combine(GetHomeDir(), "sessions");
    }

    /// <summary>
    /// Computes the safe subdirectory name for a workspace path under ~/.pi/agent/sessions/
    /// exactly matching Pi's getDefaultSessionDirPath implementation:
    /// safePath = "--" + resolvedCwd.replace(/^[/\\]/, "").replace(/[/\\:]/g, "-") + "--"
    /// </summary>
    public static string EncodeWorkspacePath(string workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return "--default--";
        }

        var resolved = Path.GetFullPath(workspace);
        var trimmed = resolved.TrimStart('/', '\\');
        var safe = trimmed.Replace('/', '-').Replace('\\', '-').Replace(':', '-');
        return $"--{safe}--";
    }

    public static string GetWorkspaceSessionDir(string workspace)
    {
        return Path.Combine(GetSessionsDir(), EncodeWorkspacePath(workspace));
    }

    /// <summary>
    /// Extracts the session ID from a Pi filename: &lt;timestamp&gt;_&lt;sessionId&gt;.jsonl
    /// </summary>
    public static string ExtractSessionIdFromFileName(string baseName)
    {
        var idx = baseName.IndexOf('_');
        if (idx >= 0 && idx < baseName.Length - 1)
        {
            return baseName.Substring(idx + 1);
        }
        return baseName;
    }

    public static string? FindPiCli()
    {
        var envBin = Environment.GetEnvironmentVariable("PI_BIN");
        if (!string.IsNullOrWhiteSpace(envBin) && File.Exists(envBin)) return envBin;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var npmCmd = Path.Combine(appData, "npm", "pi.cmd");
        if (File.Exists(npmCmd)) return npmCmd;
        var npmPs1 = Path.Combine(appData, "npm", "pi.ps1");
        if (File.Exists(npmPs1)) return npmPs1;
        var npmSh = Path.Combine(appData, "npm", "pi");
        if (File.Exists(npmSh)) return npmSh;

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var p1 = Path.Combine(dir, "pi.cmd");
            if (File.Exists(p1)) return p1;
            var p2 = Path.Combine(dir, "pi.exe");
            if (File.Exists(p2)) return p2;
            var p3 = Path.Combine(dir, "pi");
            if (File.Exists(p3)) return p3;
        }

        return null;
    }

    public DetectionResult Detect()
    {
        var result = new DetectionResult();

        var sessionsDir = GetSessionsDir();
        if (Directory.Exists(sessionsDir))
        {
            result.Installed = true;
            result.Evidence.Add($"Sessions dir exists: {sessionsDir}");
        }

        var agentDir = GetHomeDir();
        if (Directory.Exists(agentDir))
        {
            result.Installed = true;
            result.Evidence.Add($"Agent dir exists: {agentDir}");
        }

        var cli = FindPiCli();
        if (cli != null)
        {
            result.Installed = true;
            result.Evidence.Add($"CLI found: {cli}");
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var packageJson = Path.Combine(appData, "npm", "node_modules", "@earendil-works", "pi-coding-agent", "package.json");
        if (File.Exists(packageJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(packageJson));
                if (doc.RootElement.TryGetProperty("version", out var vProp))
                {
                    result.Version = vProp.GetString();
                }
            }
            catch { }
        }

        // Fallback locations: alternate package scope/name and a package.json next
        // to the resolved CLI (portable installs keep them side by side).
        if (string.IsNullOrWhiteSpace(result.Version))
        {
            var candidates = new List<string>
            {
                Path.Combine(appData, "npm", "node_modules", "pi-coding-agent", "package.json")
            };
            if (cli != null)
            {
                var cliDir = Path.GetDirectoryName(cli);
                if (!string.IsNullOrWhiteSpace(cliDir))
                {
                    candidates.Add(Path.Combine(cliDir, "package.json"));
                    var parent = Path.GetDirectoryName(cliDir);
                    if (!string.IsNullOrWhiteSpace(parent))
                        candidates.Add(Path.Combine(parent, "package.json"));
                }
            }
            foreach (var candidate in candidates)
            {
                try
                {
                    if (!File.Exists(candidate)) continue;
                    using var doc = JsonDocument.Parse(File.ReadAllText(candidate));
                    if (doc.RootElement.TryGetProperty("version", out var vProp) &&
                        !string.IsNullOrWhiteSpace(vProp.GetString()))
                    {
                        result.Version = vProp.GetString();
                        break;
                    }
                }
                catch { }
            }
        }

        return result;
    }

    public IReadOnlyList<string> SessionRoots()
    {
        var roots = new List<string>();
        var dir = GetSessionsDir();
        if (Directory.Exists(dir)) roots.Add(dir);
        return roots;
    }

    public string? OwnsSession(string sessionId)
    {
        var root = GetSessionsDir();
        if (!Directory.Exists(root)) return null;

        if (File.Exists(sessionId) && sessionId.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            var fullPath = Path.GetFullPath(sessionId);
            var fullRoot = Path.GetFullPath(root);
            if (fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return fullPath;
            }

            try
            {
                using var reader = new StreamReader(fullPath);
                var firstLine = reader.ReadLine();
                if (firstLine != null && firstLine.Contains("\"type\":\"session\"", StringComparison.OrdinalIgnoreCase))
                {
                    return fullPath;
                }
            }
            catch { }

            return null;
        }

        try
        {
            var match = Directory.EnumerateFiles(root, $"*_{sessionId}.jsonl", SearchOption.AllDirectories).FirstOrDefault();
            if (match != null) return match;

            match = Directory.EnumerateFiles(root, $"{sessionId}.jsonl", SearchOption.AllDirectories).FirstOrDefault();
            if (match != null) return match;

            // Prefer an exact id match over prefix matching — a prefix match on a
            // shorter query can silently bind the wrong session.
            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                var baseName = Path.GetFileNameWithoutExtension(file);
                var id = ExtractSessionIdFromFileName(baseName);
                if (id.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
            // Prefix fallback only when it identifies a UNIQUE candidate: with two
            // sessions "abc..." and "abd..." a first-match prefix bind on "ab"
            // returns whichever the filesystem enumerates first.
            string? prefixHit = null;
            var prefixHits = 0;
            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                var baseName = Path.GetFileNameWithoutExtension(file);
                var id = ExtractSessionIdFromFileName(baseName);
                if (id.StartsWith(sessionId, StringComparison.OrdinalIgnoreCase) ||
                    baseName.StartsWith(sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    prefixHits++;
                    prefixHit = file;
                    if (prefixHits > 1) break;
                }
            }
            if (prefixHits == 1) return prefixHit;
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("PI", $"Error in OwnsSession for '{sessionId}': {ex.Message}");
        }

        return null;
    }

    public IReadOnlyList<(string SessionId, string Path)>? ListSessions()
    {
        var list = new List<(string SessionId, string Path)>();
        var root = GetSessionsDir();
        if (!Directory.Exists(root)) return list;

        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                var baseName = Path.GetFileNameWithoutExtension(file);
                var id = ExtractSessionIdFromFileName(baseName);
                list.Add((id, file));
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Warn("PI", $"Error listing sessions: {ex.Message}");
        }

        return list;
    }

    private static (string Text, List<ToolCall> ToolCalls, string? Thinking) ExtractMessageContent(JsonElement contentElement)
    {
        var textParts = new List<string>();
        var toolCalls = new List<ToolCall>();
        string? thinking = null;

        if (contentElement.ValueKind == JsonValueKind.String)
        {
            textParts.Add(contentElement.GetString() ?? string.Empty);
        }
        else if (contentElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in contentElement.EnumerateArray())
            {
                if (block.ValueKind == JsonValueKind.String)
                {
                    textParts.Add(block.GetString() ?? string.Empty);
                    continue;
                }

                if (block.ValueKind != JsonValueKind.Object) continue;

                var blockType = block.TryGetProperty("type", out var btProp) ? btProp.GetString() : null;

                if (blockType == "text" && block.TryGetProperty("text", out var tProp))
                {
                    var txt = tProp.GetString();
                    if (!string.IsNullOrEmpty(txt)) textParts.Add(txt);
                }
                else if (blockType == "thinking" && block.TryGetProperty("thinking", out var thProp))
                {
                    thinking = thProp.GetString();
                }
                else if (blockType is "toolCall" or "tool_use")
                {
                    var tcId = block.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                    var tcName = block.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "tool" : "tool";
                    var argsJson = string.Empty;
                    if (block.TryGetProperty("arguments", out var argsProp))
                    {
                        argsJson = argsProp.GetRawText();
                    }
                    else if (block.TryGetProperty("input", out var inProp))
                    {
                        argsJson = inProp.GetRawText();
                    }

                    toolCalls.Add(new ToolCall
                    {
                        Id = tcId,
                        Name = tcName,
                        ArgumentsJson = argsJson
                    });
                }
            }
        }

        return (string.Join("\n", textParts), toolCalls, thinking);
    }

    public SessionSummary ReadSummary(string path)
    {
        var fileInfo = File.Exists(path) ? new FileInfo(path) : null;
        var fileNameWithoutExt = Path.GetFileNameWithoutExtension(path);
        var fallbackId = ExtractSessionIdFromFileName(fileNameWithoutExt);

        var summary = new SessionSummary
        {
            SessionId = fallbackId,
            Provider = Slug,
            ProviderDisplayName = Name,
            SourcePath = path,
            FileSizeBytes = fileInfo?.Length ?? 0,
            LastActiveAt = fileInfo?.LastWriteTime,
            StartedAt = fileInfo?.CreationTime
        };

        if (!File.Exists(path)) return summary;

        string? firstUserMessage = null;
        string? namedTitle = null;
        string? activeModel = null;
        DateTime? firstTimestamp = null;
        DateTime? lastTimestamp = null;
        int messagesCount = 0;
        int toolCallsCount = 0;

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                var type = root.TryGetProperty("type", out var tProp) ? tProp.GetString() : null;

                if (type == "session")
                {
                    if (root.TryGetProperty("id", out var idProp))
                    {
                        summary.SessionId = idProp.GetString() ?? fallbackId;
                    }
                    if (root.TryGetProperty("cwd", out var cwdProp))
                    {
                        summary.Workspace = cwdProp.GetString();
                    }
                    if (root.TryGetProperty("timestamp", out var tsProp))
                    {
                        var ts = ModelHelpers.ParseTimestamp(tsProp);
                        if (ts.HasValue)
                        {
                            summary.StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(ts.Value).LocalDateTime;
                            firstTimestamp = summary.StartedAt;
                        }
                    }
                }
                else if (type == "session_info")
                {
                    if (root.TryGetProperty("name", out var nProp) && !string.IsNullOrWhiteSpace(nProp.GetString()))
                    {
                        namedTitle = nProp.GetString()?.Trim();
                    }
                }
                else if (type == "model_change")
                {
                    if (root.TryGetProperty("modelId", out var mProp))
                    {
                        activeModel = mProp.GetString();
                    }
                }
                else if (type == "message")
                {
                    messagesCount++;

                    if (root.TryGetProperty("timestamp", out var tsProp))
                    {
                        var ts = ModelHelpers.ParseTimestamp(tsProp);
                        if (ts.HasValue)
                        {
                            var dt = DateTimeOffset.FromUnixTimeMilliseconds(ts.Value).LocalDateTime;
                            firstTimestamp ??= dt;
                            lastTimestamp = dt;
                        }
                    }

                    if (root.TryGetProperty("message", out var msgObj) && msgObj.ValueKind == JsonValueKind.Object)
                    {
                        var role = msgObj.TryGetProperty("role", out var rProp) ? rProp.GetString() : null;

                        if (msgObj.TryGetProperty("model", out var mdlProp) && !string.IsNullOrWhiteSpace(mdlProp.GetString()))
                        {
                            activeModel = mdlProp.GetString();
                        }

                        if (msgObj.TryGetProperty("content", out var cntProp))
                        {
                            var (text, toolCalls, _) = ExtractMessageContent(cntProp);
                            toolCallsCount += toolCalls.Count;

                            if (role == "user" && firstUserMessage == null && !string.IsNullOrWhiteSpace(text))
                            {
                                firstUserMessage = text;
                            }
                        }

                        if (role == "toolResult")
                        {
                            if (toolCallsCount == 0)
                            {
                                toolCallsCount++;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Debug("PI", $"Error reading summary from {path}: {ex.Message}");
        }

        summary.MessagesCount = messagesCount;
        summary.ToolCallsCount = toolCallsCount;
        summary.ModelName = activeModel ?? DefaultModelId;
        if (firstTimestamp.HasValue) summary.StartedAt = firstTimestamp.Value;
        if (lastTimestamp.HasValue) summary.LastActiveAt = lastTimestamp.Value;

        if (!string.IsNullOrWhiteSpace(namedTitle))
        {
            summary.NativeName = namedTitle;
            summary.Title = ModelHelpers.CleanTitle(namedTitle);
        }
        else if (!string.IsNullOrWhiteSpace(firstUserMessage))
        {
            summary.Title = ModelHelpers.CleanTitle(firstUserMessage);
        }
        else
        {
            summary.Title = $"Pi Session {summary.SessionId.Substring(0, Math.Min(8, summary.SessionId.Length))}";
        }

        summary.IsSubagent = ModelHelpers.IsSubagentPrompt(summary.Title);

        return summary;
    }

    public CanonicalSession ReadSession(string path)
    {
        var summary = ReadSummary(path);

        var session = new CanonicalSession
        {
            SessionId = summary.SessionId,
            ProviderSlug = Slug,
            SourcePath = path,
            Title = summary.Title,
            Workspace = summary.Workspace,
            ModelName = summary.ModelName,
            IsSubagent = summary.IsSubagent,
            StartedAtEpochMs = summary.StartedAt.HasValue
                ? new DateTimeOffset(summary.StartedAt.Value.ToUniversalTime()).ToUnixTimeMilliseconds()
                : null,
            EndedAtEpochMs = summary.LastActiveAt.HasValue
                ? new DateTimeOffset(summary.LastActiveAt.Value.ToUniversalTime()).ToUnixTimeMilliseconds()
                : null
        };

        var messages = new List<CanonicalMessage>();

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                var type = root.TryGetProperty("type", out var tProp) ? tProp.GetString() : null;
                if (type != "message") continue;

                if (!root.TryGetProperty("message", out var msgObj) || msgObj.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                long? timestamp = null;
                if (root.TryGetProperty("timestamp", out var lineTs))
                {
                    timestamp = ModelHelpers.ParseTimestamp(lineTs);
                }
                else if (msgObj.TryGetProperty("timestamp", out var msgTs))
                {
                    timestamp = ModelHelpers.ParseTimestamp(msgTs);
                }

                var roleStr = msgObj.TryGetProperty("role", out var rProp) ? rProp.GetString() : "user";
                var (content, toolCalls, thinking) = msgObj.TryGetProperty("content", out var cntProp)
                    ? ExtractMessageContent(cntProp)
                    : (string.Empty, new List<ToolCall>(), null);

                var msg = new CanonicalMessage
                {
                    Index = messages.Count,
                    Content = content,
                    TimestampEpochMs = timestamp,
                    ToolCalls = toolCalls
                };

                if (!string.IsNullOrWhiteSpace(thinking))
                {
                    msg.Extra["thinking"] = thinking;
                }

                if (roleStr == "user")
                {
                    msg.Role = MessageRole.User;
                    msg.Author = "user";
                }
                else if (roleStr == "assistant")
                {
                    msg.Role = MessageRole.Assistant;
                    var model = msgObj.TryGetProperty("model", out var mProp) ? mProp.GetString() : session.ModelName;
                    msg.Author = model ?? "pi";
                }
                else if (roleStr == "toolResult")
                {
                    msg.Role = MessageRole.Tool;
                    msg.Author = "tool";

                    var tcId = msgObj.TryGetProperty("toolCallId", out var tcIdProp) ? tcIdProp.GetString() : null;
                    var isErr = msgObj.TryGetProperty("isError", out var errProp) && errProp.GetBoolean();
                    var toolName = msgObj.TryGetProperty("toolName", out var tnProp) ? tnProp.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(toolName))
                    {
                        msg.Extra["tool_name"] = toolName;
                    }

                    msg.ToolResults.Add(new ToolResult
                    {
                        CallId = tcId,
                        Content = content,
                        IsError = isErr
                    });
                }
                else
                {
                    msg.Role = MessageRole.Other;
                    msg.Author = roleStr ?? "other";
                }

                messages.Add(msg);
            }
        }
        catch (Exception ex)
        {
            CasrLogger.Error("PI", $"Error reading session lines from {path}: {ex.Message}", ex);
        }

        session.Messages = messages;
        return session;
    }

    public WrittenSession WriteSession(CanonicalSession session, WriteOptions opts)
    {
        var targetId = Guid.NewGuid().ToString();
        var workspace = ModelHelpers.EffectiveWorkspace(session);
        var targetDir = GetWorkspaceSessionDir(workspace);
        Directory.CreateDirectory(targetDir);

        var now = DateTimeOffset.UtcNow;
        var timestampIso = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var fileTimestamp = now.ToString("yyyy-MM-ddTHH-mm-ss-fffZ");
        var targetPath = Path.Combine(targetDir, $"{fileTimestamp}_{targetId}.jsonl");
        // NOTE: targetPath embeds a fresh Guid + timestamp so it cannot pre-exist;
        // the old Force/.bak branch was dead code and has been removed.

        var tempPath = targetPath + $".tmp.{Guid.NewGuid():N}";
        try
        {
            using (var writer = new StreamWriter(tempPath, false, Utf8NoBom))
        {
            // 1. Session Header
            var header = new Dictionary<string, object?>
            {
                ["type"] = "session",
                ["version"] = 3,
                ["id"] = targetId,
                ["timestamp"] = timestampIso,
                ["cwd"] = workspace
            };
            writer.WriteLine(JsonSerializer.Serialize(header));

            string? prevId = null;

            // 2. Session Info (title)
            if (!string.IsNullOrWhiteSpace(session.Title))
            {
                var titleId = Guid.NewGuid().ToString("N")[..8];
                var infoEntry = new Dictionary<string, object?>
                {
                    ["type"] = "session_info",
                    ["id"] = titleId,
                    ["parentId"] = prevId,
                    ["timestamp"] = timestampIso,
                    ["name"] = session.Title
                };
                writer.WriteLine(JsonSerializer.Serialize(infoEntry));
                prevId = titleId;
            }

            // 3. Model Change (if model defined)
            if (!string.IsNullOrWhiteSpace(session.ModelName))
            {
                var modelEntryId = Guid.NewGuid().ToString("N")[..8];
                var modelEntry = new Dictionary<string, object?>
                {
                    ["type"] = "model_change",
                    ["id"] = modelEntryId,
                    ["parentId"] = prevId,
                    ["timestamp"] = timestampIso,
                    ["provider"] = "openai",
                    ["modelId"] = session.ModelName
                };
                writer.WriteLine(JsonSerializer.Serialize(modelEntry));
                prevId = modelEntryId;
            }

            // 4. Messages
            foreach (var msg in session.Messages)
            {
                var msgId = Guid.NewGuid().ToString("N")[..8];
                var msgIso = msg.TimestampEpochMs.HasValue
                    ? DateTimeOffset.FromUnixTimeMilliseconds(msg.TimestampEpochMs.Value).ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                    : timestampIso;
                var msgEpoch = msg.TimestampEpochMs ?? now.ToUnixTimeMilliseconds();

                Dictionary<string, object?> msgRecord;

                if (msg.Role == MessageRole.User)
                {
                    msgRecord = new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["type"] = "text",
                                ["text"] = msg.Content ?? string.Empty
                            }
                        },
                        ["timestamp"] = msgEpoch
                    };
                }
                else if (msg.Role == MessageRole.Assistant)
                {
                    var contentBlocks = new List<object>();

                    // Thinking block preservation
                    string? thinking = null;
                    if (msg.Extra.TryGetValue("thinking", out var th) && th != null) thinking = th.ToString();
                    else if (msg.Extra.TryGetValue("reasoning", out var r) && r != null) thinking = r.ToString();
                    else if (msg.Extra.TryGetValue("reasoning_content", out var rc) && rc != null) thinking = rc.ToString();

                    if (!string.IsNullOrWhiteSpace(thinking))
                    {
                        contentBlocks.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "thinking",
                            ["thinking"] = thinking
                        });
                    }

                    if (!string.IsNullOrEmpty(msg.Content))
                    {
                        contentBlocks.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "text",
                            ["text"] = msg.Content
                        });
                    }

                    foreach (var tc in msg.ToolCalls)
                    {
                        object argsObj;
                        try
                        {
                            argsObj = !string.IsNullOrWhiteSpace(tc.ArgumentsJson)
                                ? JsonSerializer.Deserialize<JsonElement>(tc.ArgumentsJson)
                                : new Dictionary<string, object?>();
                        }
                        catch
                        {
                            argsObj = new Dictionary<string, object?> { ["command"] = tc.ArgumentsJson };
                        }

                        contentBlocks.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "toolCall",
                            ["id"] = string.IsNullOrWhiteSpace(tc.Id) ? $"call_{Guid.NewGuid():N}" : tc.Id,
                            ["name"] = string.IsNullOrWhiteSpace(tc.Name) ? "tool" : tc.Name,
                            ["arguments"] = argsObj
                        });
                    }

                    var usageDict = new Dictionary<string, object?>
                    {
                        ["input"] = 0,
                        ["output"] = 0,
                        ["cacheRead"] = 0,
                        ["cacheWrite"] = 0,
                        ["totalTokens"] = 0,
                        ["cost"] = new Dictionary<string, object?>
                        {
                            ["input"] = 0,
                            ["output"] = 0,
                            ["cacheRead"] = 0,
                            ["cacheWrite"] = 0,
                            ["total"] = 0
                        }
                    };

                    msgRecord = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = contentBlocks,
                        ["model"] = session.ModelName ?? DefaultModelId,
                        ["timestamp"] = msgEpoch,
                        ["usage"] = usageDict
                    };
                }
                else if (msg.Role == MessageRole.Tool && msg.ToolCalls.Count > 0 && msg.ToolResults.Count == 0)
                {
                    // Tool call definition emitted without an assistant envelope:
                    // wrap it into an assistant message containing toolCall blocks
                    var contentBlocks = new List<object>();
                    foreach (var tc in msg.ToolCalls)
                    {
                        object argsObj;
                        try
                        {
                            argsObj = !string.IsNullOrWhiteSpace(tc.ArgumentsJson)
                                ? JsonSerializer.Deserialize<JsonElement>(tc.ArgumentsJson)
                                : new Dictionary<string, object?>();
                        }
                        catch
                        {
                            argsObj = new Dictionary<string, object?> { ["command"] = tc.ArgumentsJson };
                        }

                        contentBlocks.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "toolCall",
                            ["id"] = string.IsNullOrWhiteSpace(tc.Id) ? $"call_{Guid.NewGuid():N}" : tc.Id,
                            ["name"] = string.IsNullOrWhiteSpace(tc.Name) ? "tool" : tc.Name,
                            ["arguments"] = argsObj
                        });
                    }

                    var usageDict = new Dictionary<string, object?>
                    {
                        ["input"] = 0,
                        ["output"] = 0,
                        ["cacheRead"] = 0,
                        ["cacheWrite"] = 0,
                        ["totalTokens"] = 0,
                        ["cost"] = new Dictionary<string, object?>
                        {
                            ["input"] = 0,
                            ["output"] = 0,
                            ["cacheRead"] = 0,
                            ["cacheWrite"] = 0,
                            ["total"] = 0
                        }
                    };

                    msgRecord = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = contentBlocks,
                        ["model"] = session.ModelName ?? DefaultModelId,
                        ["timestamp"] = msgEpoch,
                        ["usage"] = usageDict
                    };
                }
                else if (msg.Role == MessageRole.Tool)
                {
                    // One toolResult record per ToolResult (previously only the first
                    // survived), with the original tool name when known.
                    var toolName = msg.Extra.TryGetValue("tool_name", out var tnVal) && tnVal is string tn && !string.IsNullOrWhiteSpace(tn)
                        ? tn
                        : "bash";
                    var results = msg.ToolResults.Count > 0
                        ? msg.ToolResults
                        : new List<ToolResult> { new ToolResult { Content = msg.Content ?? string.Empty } };

                    foreach (var tr in results)
                    {
                        var trId = Guid.NewGuid().ToString("N")[..8];
                        var toolRecord = new Dictionary<string, object?>
                        {
                            ["role"] = "toolResult",
                            ["toolCallId"] = tr.CallId ?? "call_0",
                            ["toolName"] = toolName,
                            ["content"] = new object[]
                            {
                                new Dictionary<string, object?>
                                {
                                    ["type"] = "text",
                                    ["text"] = tr.Content ?? string.Empty
                                }
                            },
                            ["isError"] = tr.IsError,
                            ["timestamp"] = msgEpoch
                        };

                        var toolLineEntry = new Dictionary<string, object?>
                        {
                            ["type"] = "message",
                            ["id"] = trId,
                            ["parentId"] = prevId,
                            ["timestamp"] = msgIso,
                            ["message"] = toolRecord
                        };

                        writer.WriteLine(JsonSerializer.Serialize(toolLineEntry));
                        prevId = trId;
                    }
                    continue;
                }
                else
                {
                    msgRecord = new Dictionary<string, object?>
                    {
                        ["role"] = "user",
                        ["content"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["type"] = "text",
                                ["text"] = msg.Content ?? string.Empty
                            }
                        },
                        ["timestamp"] = msgEpoch
                    };
                }

                var lineEntry = new Dictionary<string, object?>
                {
                    ["type"] = "message",
                    ["id"] = msgId,
                    ["parentId"] = prevId,
                    ["timestamp"] = msgIso,
                    ["message"] = msgRecord
                };

                writer.WriteLine(JsonSerializer.Serialize(lineEntry));
                prevId = msgId;
            }
            }

            File.Move(tempPath, targetPath, true);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }

        return new WrittenSession
        {
            Paths = new List<string> { targetPath },
            SessionId = targetId,
            ResumeCommand = ResumeCommand(targetId, workspace),
            Workspace = workspace
        };
    }

    /// <summary>
    /// Builds the Pi resume command. The session id resolves globally through the
    /// pi CLI's session store — the workspace is NOT passed on the command line
    /// (pi scopes storage by workspace directory internally and reads the cwd from
    /// the session file itself), so <paramref name="workspace"/> is informational
    /// only and recorded on <see cref="WrittenSession.Workspace"/>.
    /// </summary>
    public string ResumeCommand(string sessionId, string? workspace = null)
    {
        return $"pi --session {sessionId}";
    }
}
