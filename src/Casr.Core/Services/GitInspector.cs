using System;
using System.IO;
using System.Linq;
using Casr.Core.Models;

namespace Casr.Core.Services;

/// <summary>
/// Resolves git repository metadata for a workspace directory by walking parent
/// directories looking for a <c>.git</c> directory or file (worktree/submodule
/// gitfile). Read-only: never runs git, never mutates the repository.
/// </summary>
public static class GitInspector
{
    public static GitRepoInfo? TryResolve(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace)) return null;

        try
        {
            var dir = new DirectoryInfo(workspace);
            if (!dir.Exists) return null;

            var gitEntry = FindGitEntry(dir);
            if (gitEntry == null) return null;

            var repoRoot = gitEntry.Value.RepoRoot;
            var repoName = new DirectoryInfo(repoRoot).Name;
            var (branch, sha) = ReadHead(gitEntry.Value.GitDir);

            return new GitRepoInfo
            {
                RepoRoot = repoRoot,
                RepoName = repoName,
                Branch = branch,
                HeadSha = sha
            };
        }
        catch
        {
            return null;
        }
    }

    private static (string RepoRoot, string GitDir)? FindGitEntry(DirectoryInfo start)
    {
        var current = start;
        while (current != null)
        {
            var dotGitPath = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(dotGitPath))
            {
                return (current.FullName, dotGitPath);
            }
            if (File.Exists(dotGitPath))
            {
                // Worktree/submodule gitfile: "gitdir: <relative-or-absolute path>"
                try
                {
                    var line = File.ReadLines(dotGitPath).FirstOrDefault();
                    if (line != null && line.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                    {
                        var value = line.Substring("gitdir:".Length).Trim();
                        if (value.Length > 0)
                        {
                            var resolved = Path.IsPathRooted(value)
                                ? value
                                : Path.GetFullPath(Path.Combine(current.FullName, value));
                            if (Directory.Exists(resolved))
                            {
                                return (current.FullName, resolved);
                            }
                        }
                    }
                }
                catch
                {
                    // Malformed gitfile: keep walking up.
                }
            }

            current = current.Parent;
        }

        return null;
    }

    private static (string? Branch, string? Sha) ReadHead(string gitDir)
    {
        try
        {
            var headPath = Path.Combine(gitDir, "HEAD");
            if (!File.Exists(headPath)) return (null, null);

            var head = File.ReadAllText(headPath).Trim();
            if (head.Length == 0) return (null, null);

            const string refPrefix = "ref:";
            if (head.StartsWith(refPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var refName = head.Substring(refPrefix.Length).Trim();
                const string headsPrefix = "refs/heads/";
                if (refName.StartsWith(headsPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return (refName.Substring(headsPrefix.Length), null);
                }

                // Other refs (tags, remotes): show the full ref name.
                return (refName, null);
            }

            // Detached HEAD: the file itself is a 40-char SHA.
            var shortSha = head.Length >= 8 ? head.Substring(0, 8) : head;
            return ($"detached {shortSha}", head);
        }
        catch
        {
            return (null, null);
        }
    }
}
