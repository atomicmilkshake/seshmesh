using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Casr.Core.Models;

namespace Casr.Core.Providers;

public class OpenClaudeProvider : IProvider
{
    public string Name => "OpenClaude";
    public string Slug => "openclaude";
    public string CliAlias => "openclaude";
    public bool CanWrite => true;

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public static string GetHomeDir()
    {
        var envConfig = Environment.GetEnvironmentVariable("OPENCLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(envConfig)) return envConfig;

        var envHome = Environment.GetEnvironmentVariable("OPENCLAUDE_HOME");
        if (!string.IsNullOrWhiteSpace(envHome)) return envHome;

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openclaude");
    }

    private static string GetProjectsDir() => Path.Combine(GetHomeDir(), "projects");
    private static string GetSessionsDir() => Path.Combine(GetHomeDir(), "sessions");

    public static string ProjectDirKey(string workspace)
    {
        var sb = new StringBuilder();
        foreach (var ch in workspace)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append('-');
            }
        }
        return sb.ToString();
    }

    private static (bool Found, string? Path, string? Version) CheckCli()
    {
        // 1. Check npm global installation directory
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            var npmCmd = Path.Combine(appData, "npm", "openclaude.cmd");
            if (File.Exists(npmCmd))
            {
                var ver = TryReadNpmPackageVersion(appData);
                return (true, npmCmd, ver);
            }

            var npmBin = Path.Combine(appData, "npm", "openclaude");
            if (File.Exists(npmBin))
            {
                var ver = TryReadNpmPackageVersion(appData);
                return (true, npmBin, ver);
            }
        }

        // 2. Check PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            var extensions = OperatingSystem.IsWindows()
                ? new[] { ".cmd", ".bat", ".exe", "" }
                : new[] { "" };

            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var ext in extensions)
                    {
                        var candidate = Path.Combine(dir, "openclaude" + ext);
                        if (File.Exists(candidate))
                        {
                            return (true, candidate, null);
                        }
                    }
                }
                catch { }
            }
        }

        return (false, null, null);
    }

    private static string? TryReadNpmPackageVersion(string appData)
    {
        try
        {
            var pkgJson = Path.Combine(appData, "npm", "node_modules", "@gitlawb", "openclaude", "package.json");
            if (File.Exists(pkgJson))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(pkgJson));
                if (doc.RootElement.TryGetProperty("version", out var verProp))
                {
                    return verProp.GetString();
                }
            }
        }
        catch { }
        return null;
    }

    public DetectionResult Detect()
    {
        var result = new DetectionResult();
        var home = GetHomeDir();
        if (Directory.Exists(home))
        {
            result.Installed = true;
            result.Evidence.Add($"Directory exists: {home}");
        }

        var proj = GetProjectsDir();
        if (Directory.Exists(proj))
        {
            result.Installed = true;
            result.Evidence.Add($"Projects dir exists: {proj}");
        }

        var (cliFound, cliPath, cliVersion) = CheckCli();
        if (cliFound)
        {
            result.Installed = true;
            result.Evidence.Add($"CLI found: {cliPath}");
            if (!string.IsNullOrEmpty(cliVersion))
            {
                result.Version = cliVersion;
            }
        }

        return result;
    }

    public IReadOnlyList<string> SessionRoots()
    {
        var roots = new List<string>();
        var proj = GetProjectsDir();
        if (Directory.Exists(proj)) roots.Add(proj);

        var sess = GetSessionsDir();
        if (Directory.Exists(sess)) roots.Add(sess);

        return roots;
    }

    public string? OwnsSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        // Exact filename match first in each root — never a substring bind.
        foreach (var root in new[] { GetProjectsDir(), GetSessionsDir() })
        {
            if (!Directory.Exists(root)) continue;
            var direct = Directory.EnumerateFiles(root, $"{sessionId}.jsonl", SearchOption.AllDirectories).FirstOrDefault();
            if (direct != null) return direct;
        }

        // Content verification: only files whose name already suggests a match are
        // parsed, and only an inner sessionId field equal to the query claims it.
        foreach (var root in new[] { GetProjectsDir(), GetSessionsDir() })
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
                {
                    if (!Path.GetFileNameWithoutExtension(file).Contains(sessionId, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        foreach (var line in File.ReadLines(file))
                        {
                            if (string.IsNullOrWhiteSpace(line) || !line.Contains(sessionId, StringComparison.Ordinal)) continue;
                            using var doc = JsonDocument.Parse(line);
                            if (doc.RootElement.TryGetProperty("sessionId", out var sidProp) &&
                                string.Equals(sidProp.GetString(), sessionId, StringComparison.OrdinalIgnoreCase))
                            {
                                return file;
                            }
                        }
                    }
                    catch { }
                }

                // Prefix fallback only when it identifies a UNIQUE candidate.
                string? prefixHit = null;
                var prefixHits = 0;
                foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
                {
                    if (Path.GetFileNameWithoutExtension(file).StartsWith(sessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        prefixHits++;
                        prefixHit = file;
                        if (prefixHits > 1) break;
                    }
                }
                if (prefixHits == 1) return prefixHit;
            }
            catch { }
        }

        return null;
    }

    public IReadOnlyList<(string SessionId, string Path)>? ListSessions()
    {
        var list = new List<(string SessionId, string Path)>();
        foreach (var root in SessionRoots())
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                list.Add((id, file));
            }
        }
        return list;
    }

    public SessionSummary ReadSummary(string path)
    {
        var sessionId = Path.GetFileNameWithoutExtension(path);
        var fileInfo = File.Exists(path) ? new FileInfo(path) : null;

        var summary = new SessionSummary
        {
            SessionId = sessionId,
            Provider = Slug,
            ProviderDisplayName = Name,
            SourcePath = path,
            FileSizeBytes = fileInfo?.Length ?? 0,
            LastActiveAt = fileInfo?.LastWriteTime,
            StartedAt = fileInfo?.CreationTime
        };

        if (path.Contains(Path.DirectorySeparatorChar + "subagents" + Path.DirectorySeparatorChar) ||
            path.Contains("/subagents/"))
        {
            summary.IsSubagent = true;
        }

        if (!File.Exists(path)) return summary;

        string? customTitle = null;
        string? agentName = null;
        string? firstUserText = null;
        string? lastPrompt = null;
        string? workspace = null;
        string? model = null;
        int messageCount = 0;
        int toolCallsCount = 0;
        long? minTimestamp = null;
        long? maxTimestamp = null;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;

            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("isSidechain", out var scProp) && scProp.ValueKind == JsonValueKind.True)
                    {
                        summary.IsSubagent = true;
                    }

                    if (workspace == null && root.TryGetProperty("cwd", out var cwdProp))
                    {
                        workspace = cwdProp.GetString();
                    }

                    if (root.TryGetProperty("timestamp", out var tsProp))
                    {
                        var ts = ModelHelpers.ParseTimestamp(tsProp);
                        if (ts.HasValue)
                        {
                            if (!minTimestamp.HasValue || ts.Value < minTimestamp.Value) minTimestamp = ts.Value;
                            if (!maxTimestamp.HasValue || ts.Value > maxTimestamp.Value) maxTimestamp = ts.Value;
                        }
                    }

                    var type = root.TryGetProperty("type", out var tProp) ? tProp.GetString() : null;
                    if (type == null) continue;

                    if (type == "custom-title" && root.TryGetProperty("customTitle", out var ctProp))
                    {
                        customTitle ??= ctProp.GetString();
                    }
                    else if (type == "agent-name" && root.TryGetProperty("agentName", out var anProp))
                    {
                        agentName ??= anProp.GetString();
                    }
                    else if (type == "last-prompt" && root.TryGetProperty("lastPrompt", out var lpProp))
                    {
                        lastPrompt ??= lpProp.GetString();
                    }
                    else if (type == "user")
                    {
                        messageCount++;
                        if (firstUserText == null && root.TryGetProperty("message", out var userMsg) && userMsg.ValueKind == JsonValueKind.Object)
                        {
                            if (userMsg.TryGetProperty("content", out var cntProp))
                            {
                                if (cntProp.ValueKind == JsonValueKind.String)
                                {
                                    var str = cntProp.GetString();
                                    if (!string.IsNullOrWhiteSpace(str)) firstUserText = str;
                                }
                                else if (cntProp.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var item in cntProp.EnumerateArray())
                                    {
                                        if (item.ValueKind == JsonValueKind.Object &&
                                            item.TryGetProperty("type", out var bt) &&
                                            bt.GetString() == "text" &&
                                            item.TryGetProperty("text", out var txt))
                                        {
                                            var str = txt.GetString();
                                            if (!string.IsNullOrWhiteSpace(str))
                                            {
                                                firstUserText = str;
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    else if (type == "assistant")
                    {
                        messageCount++;
                        if (root.TryGetProperty("message", out var asstMsg) && asstMsg.ValueKind == JsonValueKind.Object)
                        {
                            if (model == null && asstMsg.TryGetProperty("model", out var mdlProp))
                            {
                                model = mdlProp.GetString();
                            }

                            if (asstMsg.TryGetProperty("content", out var cntProp) && cntProp.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in cntProp.EnumerateArray())
                                {
                                    if (item.ValueKind == JsonValueKind.Object &&
                                        item.TryGetProperty("type", out var bt) &&
                                        bt.GetString() == "tool_use")
                                    {
                                        toolCallsCount++;
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }
        catch { }

        summary.Workspace = workspace;
        summary.MessagesCount = messageCount;
        summary.ToolCallsCount = toolCallsCount;
        // No hardcoded model default: null means "unknown, not observed in this file".
        summary.ModelName = model;

        if (minTimestamp.HasValue)
        {
            summary.StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(minTimestamp.Value).LocalDateTime;
        }
        if (maxTimestamp.HasValue)
        {
            summary.LastActiveAt = DateTimeOffset.FromUnixTimeMilliseconds(maxTimestamp.Value).LocalDateTime;
        }

        var rawTitle = customTitle ?? agentName ?? firstUserText ?? lastPrompt;
        summary.Title = ModelHelpers.CleanTitle(rawTitle);
        if (summary.Title.Equals("(Untitled)", StringComparison.OrdinalIgnoreCase))
        {
            summary.Title = $"OpenClaude Session {sessionId.Substring(0, Math.Min(8, sessionId.Length))}";
        }

        if (ModelHelpers.IsSubagentPrompt(summary.Title))
        {
            summary.IsSubagent = true;
        }

        return summary;
    }

    public CanonicalSession ReadSession(string path)
    {
        var sessionId = Path.GetFileNameWithoutExtension(path);
        var session = new CanonicalSession
        {
            SessionId = sessionId,
            ProviderSlug = Slug,
            SourcePath = path
        };

        if (!File.Exists(path)) throw new FileNotFoundException($"OpenClaude session file not found: {path}", path);

        if (path.Contains(Path.DirectorySeparatorChar + "subagents" + Path.DirectorySeparatorChar) ||
            path.Contains("/subagents/"))
        {
            session.IsSubagent = true;
        }

        var messages = new List<CanonicalMessage>();
        string? workspace = null;
        string? customTitle = null;
        string? agentName = null;
        string? firstUserText = null;
        string? lastPrompt = null;
        string? model = null;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (root.TryGetProperty("isSidechain", out var scProp) && scProp.ValueKind == JsonValueKind.True)
                {
                    session.IsSubagent = true;
                }

                if (workspace == null && root.TryGetProperty("cwd", out var cwdProp))
                {
                    workspace = cwdProp.GetString();
                }

                long? timestamp = null;
                if (root.TryGetProperty("timestamp", out var tsProp))
                {
                    timestamp = ModelHelpers.ParseTimestamp(tsProp);
                }

                if (session.StartedAtEpochMs == null || (timestamp.HasValue && timestamp < session.StartedAtEpochMs))
                {
                    session.StartedAtEpochMs = timestamp;
                }
                if (timestamp.HasValue && (session.EndedAtEpochMs == null || timestamp > session.EndedAtEpochMs))
                {
                    session.EndedAtEpochMs = timestamp;
                }

                var type = root.TryGetProperty("type", out var tProp) ? tProp.GetString() : null;
                if (type == null) continue;

                if (type == "custom-title" && root.TryGetProperty("customTitle", out var ctProp))
                {
                    customTitle ??= ctProp.GetString();
                    continue;
                }
                if (type == "agent-name" && root.TryGetProperty("agentName", out var anProp))
                {
                    agentName ??= anProp.GetString();
                    continue;
                }
                if (type == "last-prompt" && root.TryGetProperty("lastPrompt", out var lpProp))
                {
                    lastPrompt ??= lpProp.GetString();
                    continue;
                }

                if (type is not "user" and not "assistant")
                {
                    continue;
                }

                var role = type == "user" ? MessageRole.User : MessageRole.Assistant;
                string content = string.Empty;
                var toolCalls = new List<ToolCall>();
                var toolResults = new List<ToolResult>();

                if (root.TryGetProperty("message", out var msgObj) && msgObj.ValueKind == JsonValueKind.Object)
                {
                    if (msgObj.TryGetProperty("model", out var mdlProp))
                    {
                        model ??= mdlProp.GetString();
                    }

                    if (msgObj.TryGetProperty("content", out var cntProp))
                    {
                        if (cntProp.ValueKind == JsonValueKind.Array)
                        {
                            var textParts = new List<string>();
                            foreach (var item in cntProp.EnumerateArray())
                            {
                                if (item.ValueKind == JsonValueKind.String)
                                {
                                    textParts.Add(item.GetString() ?? "");
                                }
                                else if (item.ValueKind == JsonValueKind.Object)
                                {
                                    var itemType = item.TryGetProperty("type", out var itProp) ? itProp.GetString() : null;
                                    if (itemType is "text" or "input_text" or "output_text")
                                    {
                                        if (item.TryGetProperty("text", out var tTxt))
                                        {
                                            textParts.Add(tTxt.GetString() ?? "");
                                        }
                                    }
                                    else if (itemType == "tool_use")
                                    {
                                        var tcId = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                                        var tcName = item.TryGetProperty("name", out var nmProp) ? nmProp.GetString() ?? "tool" : "tool";
                                        string tcArgs = string.Empty;
                                        if (item.TryGetProperty("input", out var inProp))
                                        {
                                            tcArgs = inProp.GetRawText();
                                        }
                                        toolCalls.Add(new ToolCall { Id = tcId, Name = tcName, ArgumentsJson = tcArgs });
                                        textParts.Add($"[Tool: {tcName}]");
                                    }
                                    else if (itemType == "tool_result")
                                    {
                                        var callId = item.TryGetProperty("tool_use_id", out var tuId) ? tuId.GetString() : null;
                                        var isErr = item.TryGetProperty("is_error", out var errProp) && errProp.ValueKind == JsonValueKind.True;
                                        string trContent = string.Empty;
                                        if (item.TryGetProperty("content", out var trCnt))
                                        {
                                            trContent = trCnt.ValueKind == JsonValueKind.String ? trCnt.GetString() ?? "" : trCnt.GetRawText();
                                        }
                                        toolResults.Add(new ToolResult { CallId = callId, Content = trContent, IsError = isErr });
                                    }
                                    else if (item.TryGetProperty("text", out var tTxt))
                                    {
                                        textParts.Add(tTxt.GetString() ?? "");
                                    }
                                }
                            }
                            content = string.Join("\n", textParts);
                        }
                        else
                        {
                            content = ModelHelpers.FlattenContent(cntProp);
                        }
                    }
                }
                else if (root.TryGetProperty("content", out var directContent))
                {
                    content = ModelHelpers.FlattenContent(directContent);
                }

                if (role == MessageRole.User && toolResults.Count > 0 && string.IsNullOrWhiteSpace(content))
                {
                    role = MessageRole.Tool;
                }

                if (role == MessageRole.User && firstUserText == null && !string.IsNullOrWhiteSpace(content))
                {
                    firstUserText = content;
                }

                messages.Add(new CanonicalMessage
                {
                    Index = messages.Count,
                    Role = role,
                    Content = content,
                    TimestampEpochMs = timestamp,
                    Author = role == MessageRole.Assistant ? (model ?? "openclaude") : "user",
                    ToolCalls = toolCalls,
                    ToolResults = toolResults
                });
            }
            catch
            {
                // Continue reading lines
            }
        }

        session.Messages = messages;
        session.Workspace = workspace;
        session.ModelName = model;

        var rawTitle = customTitle ?? agentName ?? firstUserText ?? lastPrompt;
        session.Title = ModelHelpers.CleanTitle(rawTitle);
        if (session.Title.Equals("(Untitled)", StringComparison.OrdinalIgnoreCase))
        {
            session.Title = $"OpenClaude Session {sessionId.Substring(0, Math.Min(8, sessionId.Length))}";
        }

        if (ModelHelpers.IsSubagentPrompt(session.Title))
        {
            session.IsSubagent = true;
        }

        return session;
    }

    public WrittenSession WriteSession(CanonicalSession session, WriteOptions opts)
    {
        var targetId = Guid.NewGuid().ToString();
        var workspace = ModelHelpers.EffectiveWorkspace(session);
        var projectKey = ProjectDirKey(workspace);
        var targetDir = Path.Combine(GetProjectsDir(), projectKey);
        Directory.CreateDirectory(targetDir);

        var targetPath = Path.Combine(targetDir, $"{targetId}.jsonl");
        // NOTE: targetPath embeds a fresh Guid so it cannot pre-exist; the old
        // Force/.bak branch was dead code and has been removed (no BackupPath).

        var tempPath = targetPath + $".tmp.{Guid.NewGuid():N}";
        try
        {
            using (var writer = new StreamWriter(tempPath, false, Utf8NoBom))
        {
            var nowIso = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

            // 1. Initial mode record
            var modeRecord = new Dictionary<string, object?>
            {
                ["type"] = "mode",
                ["mode"] = "normal",
                ["sessionId"] = targetId
            };
            writer.WriteLine(JsonSerializer.Serialize(modeRecord));

            // 2. Custom title record if title is provided
            if (!string.IsNullOrWhiteSpace(session.Title))
            {
                var titleRecord = new Dictionary<string, object?>
                {
                    ["type"] = "custom-title",
                    ["customTitle"] = session.Title,
                    ["sessionId"] = targetId
                };
                writer.WriteLine(JsonSerializer.Serialize(titleRecord));
            }

            string? previousUuid = null;
            string? lastUserPrompt = null;

            foreach (var msg in session.Messages)
            {
                var messageUuid = Guid.NewGuid().ToString();
                var msgTimestampIso = msg.TimestampEpochMs.HasValue
                    ? DateTimeOffset.FromUnixTimeMilliseconds(msg.TimestampEpochMs.Value).ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                    : nowIso;
                var isAssistant = msg.Role == MessageRole.Assistant;
                var entryType = isAssistant ? "assistant" : "user";
                // Only mark sidechain when the message explicitly carries that state —
                // session.IsSubagent describes the session, not every line in it.
                var isSidechain = msg.Extra.TryGetValue("isSidechain", out var scVal)
                    && scVal is bool scBool && scBool;

                if (!isAssistant && !string.IsNullOrWhiteSpace(msg.Content))
                {
                    lastUserPrompt = msg.Content;
                }

                object messagePayload;
                if (isAssistant)
                {
                    var contentBlocks = new List<object>();
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
                        object inputObj;
                        try
                        {
                            inputObj = !string.IsNullOrWhiteSpace(tc.ArgumentsJson)
                                ? JsonSerializer.Deserialize<object>(tc.ArgumentsJson) ?? new Dictionary<string, object>()
                                : new Dictionary<string, object>();
                        }
                        catch
                        {
                            inputObj = new Dictionary<string, object>();
                        }

                        contentBlocks.Add(new Dictionary<string, object?>
                        {
                            ["type"] = "tool_use",
                            ["id"] = tc.Id ?? Guid.NewGuid().ToString(),
                            ["name"] = tc.Name,
                            ["input"] = inputObj
                        });
                    }

                    messagePayload = new Dictionary<string, object?>
                    {
                        ["role"] = "assistant",
                        ["content"] = contentBlocks,
                        ["model"] = session.ModelName ?? "unknown"
                    };
                }
                else
                {
                    if (msg.ToolResults.Count > 0)
                    {
                        var resultBlocks = new List<object>();
                        foreach (var tr in msg.ToolResults)
                        {
                            resultBlocks.Add(new Dictionary<string, object?>
                            {
                                ["type"] = "tool_result",
                                ["tool_use_id"] = tr.CallId ?? string.Empty,
                                ["content"] = tr.Content,
                                ["is_error"] = tr.IsError
                            });
                        }
                        messagePayload = new Dictionary<string, object?>
                        {
                            ["role"] = "user",
                            ["content"] = resultBlocks
                        };
                    }
                    else
                    {
                        messagePayload = new Dictionary<string, object?>
                        {
                            ["role"] = "user",
                            ["content"] = msg.Content
                        };
                    }
                }

                var lineObj = new Dictionary<string, object?>
                {
                    ["parentUuid"] = previousUuid,
                    ["isSidechain"] = isSidechain,
                    ["type"] = entryType,
                    ["message"] = messagePayload,
                    ["uuid"] = messageUuid,
                    ["timestamp"] = msgTimestampIso,
                    ["userType"] = "external",
                    ["cwd"] = workspace,
                    ["sessionId"] = targetId
                };

                writer.WriteLine(JsonSerializer.Serialize(lineObj));
                previousUuid = messageUuid;
            }

            // 3. Trailing last-prompt record for OpenClaude fast summary
            if (!string.IsNullOrWhiteSpace(lastUserPrompt))
            {
                var lastPromptRecord = new Dictionary<string, object?>
                {
                    ["type"] = "last-prompt",
                    ["lastPrompt"] = lastUserPrompt,
                    ["sessionId"] = targetId
                };
                writer.WriteLine(JsonSerializer.Serialize(lastPromptRecord));
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
            ResumeCommand = ResumeCommand(targetId, workspace)
        };
    }

    public string ResumeCommand(string sessionId, string? workspace = null)
    {
        return $"openclaude --resume {sessionId}";
    }
}
