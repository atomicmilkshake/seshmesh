using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Casr.Core.Tests.Infrastructure;

public record CliParsedCommand(string Binary, List<string> Arguments);

/// <summary>
/// Validates generated resume commands against the real CLI argument syntax specifications
/// of each supported agent harness.
/// </summary>
public static class CliSwitchValidator
{
    /// <summary>
    /// Parses command tokens with double-quoted arguments and PowerShell single-quoted literals.
    /// </summary>
    public static CliParsedCommand Parse(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            throw new ArgumentException("Command line cannot be empty", nameof(commandLine));
        }

        var tokens = new List<string>();
        var pattern = @"[^\s""']+|""([^""]*)""|'((?:[^']|'')*)'";
        var matches = Regex.Matches(commandLine, pattern);

        foreach (Match match in matches)
        {
            if (match.Groups[1].Success)
            {
                tokens.Add(match.Groups[1].Value);
            }
            else if (match.Groups[2].Success)
            {
                tokens.Add(match.Groups[2].Value.Replace("''", "'", StringComparison.Ordinal));
            }
            else
            {
                tokens.Add(match.Value);
            }
        }

        if (tokens.Count == 0)
        {
            throw new InvalidOperationException("No tokens found in command line");
        }

        return new CliParsedCommand(tokens[0], tokens.GetRange(1, tokens.Count - 1));
    }

    /// <summary>
    /// Verifies that a generated resume command adheres to the harness's CLI specification.
    /// </summary>
    public static void ValidateResumeCommand(string providerSlug, string commandLine, string expectedSessionId, string? expectedWorkspace = null)
    {
        var parsed = Parse(commandLine);

        switch (providerSlug.ToLowerInvariant())
        {
            case "opencode":
                // opencode -s <id>
                var sIdx = parsed.Arguments.IndexOf("-s");
                if (sIdx < 0) sIdx = parsed.Arguments.IndexOf("--session");
                if (sIdx < 0 || sIdx + 1 >= parsed.Arguments.Count)
                {
                    throw new InvalidOperationException($"OpenCode command must contain '-s <sessionId>': {commandLine}");
                }
                if (parsed.Arguments[sIdx + 1] != expectedSessionId)
                {
                    throw new InvalidOperationException($"OpenCode session ID mismatch. Expected '{expectedSessionId}', got '{parsed.Arguments[sIdx + 1]}'");
                }
                break;

            case "pi":
                // pi --session <id>
                var piIdx = parsed.Arguments.IndexOf("--session");
                if (piIdx < 0 || piIdx + 1 >= parsed.Arguments.Count)
                {
                    throw new InvalidOperationException($"Pi command must contain '--session <sessionId>': {commandLine}");
                }
                if (parsed.Arguments[piIdx + 1] != expectedSessionId)
                {
                    throw new InvalidOperationException($"Pi session ID mismatch. Expected '{expectedSessionId}', got '{parsed.Arguments[piIdx + 1]}'");
                }
                break;

            case "hermes":
                // hermes --resume <id>
                var hIdx = parsed.Arguments.IndexOf("--resume");
                if (hIdx < 0 || hIdx + 1 >= parsed.Arguments.Count)
                {
                    throw new InvalidOperationException($"Hermes command must contain '--resume <sessionId>': {commandLine}");
                }
                if (parsed.Arguments[hIdx + 1] != expectedSessionId)
                {
                    throw new InvalidOperationException($"Hermes session ID mismatch. Expected '{expectedSessionId}', got '{parsed.Arguments[hIdx + 1]}'");
                }
                break;

            case "grok":
                // grok --resume <id>
                var gIdx = parsed.Arguments.IndexOf("--resume");
                if (gIdx < 0 || gIdx + 1 >= parsed.Arguments.Count)
                {
                    throw new InvalidOperationException($"Grok command must contain '--resume <sessionId>': {commandLine}");
                }
                if (parsed.Arguments[gIdx + 1] != expectedSessionId)
                {
                    throw new InvalidOperationException($"Grok session ID mismatch. Expected '{expectedSessionId}', got '{parsed.Arguments[gIdx + 1]}'");
                }
                break;

            case "antigravity":
                // agy --conversation <id> --model <model>
                var cIdx = parsed.Arguments.IndexOf("--conversation");
                if (cIdx < 0 || cIdx + 1 >= parsed.Arguments.Count)
                {
                    throw new InvalidOperationException($"Antigravity command must contain '--conversation <sessionId>': {commandLine}");
                }
                if (parsed.Arguments[cIdx + 1] != expectedSessionId)
                {
                    throw new InvalidOperationException($"Antigravity session ID mismatch. Expected '{expectedSessionId}', got '{parsed.Arguments[cIdx + 1]}'");
                }
                var mIdx = parsed.Arguments.IndexOf("--model");
                if (mIdx < 0 || mIdx + 1 >= parsed.Arguments.Count || string.IsNullOrWhiteSpace(parsed.Arguments[mIdx + 1]))
                {
                    throw new InvalidOperationException($"Antigravity command must contain '--model <model>' (per-session model persistence): {commandLine}");
                }
                break;

            case "openclaude":
                // openclaude --resume <id>
                var ocIdx = parsed.Arguments.IndexOf("--resume");
                if (ocIdx < 0 || ocIdx + 1 >= parsed.Arguments.Count)
                {
                    throw new InvalidOperationException($"OpenClaude command must contain '--resume <sessionId>': {commandLine}");
                }
                if (parsed.Arguments[ocIdx + 1] != expectedSessionId)
                {
                    throw new InvalidOperationException($"OpenClaude session ID mismatch. Expected '{expectedSessionId}', got '{parsed.Arguments[ocIdx + 1]}'");
                }
                break;

            case "codex":
                // codex resume <session UUID> [--cd <workspace>]
                if (parsed.Arguments.Count < 2 ||
                    !parsed.Arguments[0].Equals("resume", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Codex command must use 'codex resume <sessionId>': {commandLine}");
                }
                if (parsed.Arguments[1] != expectedSessionId)
                {
                    throw new InvalidOperationException($"Codex session ID mismatch. Expected '{expectedSessionId}', got '{parsed.Arguments[1]}'");
                }
                if (!string.IsNullOrWhiteSpace(expectedWorkspace))
                {
                    var cdIndex = parsed.Arguments.IndexOf("--cd");
                    if (cdIndex < 0) cdIndex = parsed.Arguments.IndexOf("-C");
                    if (cdIndex < 0 || cdIndex + 1 >= parsed.Arguments.Count ||
                        !string.Equals(parsed.Arguments[cdIndex + 1], expectedWorkspace, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Codex command must resume in workspace '{expectedWorkspace}' using --cd: {commandLine}");
                    }
                }
                break;

            case "cursor":
                // cursor "<workspace>" — opens the workspace, never a session id.
                // 'cursor .' is forbidden: it opens the caller's CWD, not the session workspace.
                if (commandLine.Trim().Equals("cursor .", StringComparison.OrdinalIgnoreCase)
                    || commandLine.Trim().EndsWith(" cursor .", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Cursor command must never be bare 'cursor .': {commandLine}");
                }
                if (!string.IsNullOrWhiteSpace(expectedWorkspace))
                {
                    if (!parsed.Arguments.Contains(expectedWorkspace))
                    {
                        throw new InvalidOperationException($"Cursor command must contain workspace path '{expectedWorkspace}': {commandLine}");
                    }
                }
                break;

            default:
                throw new NotSupportedException($"Unknown provider slug: {providerSlug}");
        }
    }
}
