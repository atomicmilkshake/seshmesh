using System;

namespace Casr.Core.Services;

/// <summary>
/// Inserts one harness-specific approval-bypass flag into an already-built
/// resume command. The command builders stay flag-free; this is the only
/// insertion point. Flag spellings were taken from the CLIs installed on
/// this machine on 2026-10-01 (see docs/SPEC-index-fidelity-and-resume-bypass.md).
/// </summary>
public static class ResumeApprovalBypass
{
    /// <summary>
    /// The flag token for <paramref name="providerSlug"/>, or null when this
    /// harness has no approval-bypass switch. <c>agy</c> maps to Antigravity.
    /// <c>claude</c> does not map to OpenClaude.
    /// </summary>
    public static string? FlagFor(string? providerSlug)
    {
        if (string.IsNullOrWhiteSpace(providerSlug)) return null;
        return providerSlug.Trim().ToLowerInvariant() switch
        {
            "antigravity" or "agy" => "--dangerously-skip-permissions",
            "opencode" => "--auto",
            "codex" => "--dangerously-bypass-approvals-and-sandbox",
            "hermes" => "--yolo",
            "grok" => "--yolo",
            "openclaude" => "--dangerously-skip-permissions",
            _ => null
        };
    }

    /// <summary>
    /// Returns <paramref name="command"/> unchanged when the switch is off,
    /// the harness has no flag, the command is empty, or the flag is already
    /// its own whitespace-delimited token. Otherwise inserts the flag as the
    /// first argument, immediately after the executable token.
    /// </summary>
    public static string Apply(string? providerSlug, string command, bool enabled)
    {
        if (!enabled || string.IsNullOrWhiteSpace(command)) return command;
        var flag = FlagFor(providerSlug);
        if (flag == null || ContainsToken(command, flag)) return command;

        var split = IndexOfFirstWhitespace(command);
        if (split < 0) return command + " " + flag;
        return string.Concat(command.AsSpan(0, split), " ", flag, command.AsSpan(split));
    }

    private static bool ContainsToken(string command, string flag)
    {
        var index = 0;
        while (index < command.Length)
        {
            while (index < command.Length && char.IsWhiteSpace(command[index])) index++;
            if (index >= command.Length) break;
            var start = index;
            while (index < command.Length && !char.IsWhiteSpace(command[index])) index++;
            if (command.AsSpan(start, index - start).Equals(flag, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static int IndexOfFirstWhitespace(string command)
    {
        for (var i = 0; i < command.Length; i++)
        {
            if (char.IsWhiteSpace(command[i])) return i;
        }
        return -1;
    }
}
