using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Casr.Core.Models;
using Casr.Core.Providers;
using Casr.Core.Services;
using Xunit;

namespace Casr.Core.Tests;

/// <summary>
/// Harness-import pipeline probe. Runs ONLY when CASR_PROBE_PROVIDER is set to a provider
/// slug (e.g. "grok"); evaluates that harness's full import path against the LIVE store:
/// Detect -> ListSessions -> ReadSummary -> ReadSession -> Resume command.
/// Writes a report to CASR_PROBE_OUT. Skipped (silently) in normal test runs.
/// </summary>
public class HarnessImportProbe
{
    private static readonly List<string> _lines = new();

    private static string? ProbeSlug() => Environment.GetEnvironmentVariable("CASR_PROBE_PROVIDER");
    private static string? ProbeOut() => Environment.GetEnvironmentVariable("CASR_PROBE_OUT");

    private static void Emit(string line)
    {
        Console.WriteLine(line);
        _lines.Add(line);
    }

    private static void FlushReport()
    {
        try
        {
            var reportPath = ProbeOut();
            if (!string.IsNullOrWhiteSpace(reportPath))
            {
                File.WriteAllText(reportPath, string.Join("\n", _lines));
            }
        }
        catch { }
    }

    private static string Truncate(string? s, int n)
    {
        if (s == null) return "";
        return s.Length <= n ? s : s.Substring(0, n) + "...";
    }

    [Trait("Category", "LiveSystem")]
    [Fact]
    public void Probe_OneHarnessImportPipeline()
    {
        var slug = ProbeSlug();
        if (string.IsNullOrWhiteSpace(slug)) return; // not an eval run; skip silently

        var registry = ProviderRegistry.Default;
        var provider = registry.FindBySlug(slug) ?? registry.FindByAlias(slug);
        if (provider == null)
        {
            Emit($"VERDICT: FAIL — no provider registered for slug '{slug}'");
            FlushReport();
            return;
        }

        Emit($"== HARNESS PROBE: {provider.Name} (slug '{provider.Slug}', cli '{provider.CliAlias}') ==");
        var problems = new List<string>();
        var checks = new List<string>();

        // A. Detect
        try
        {
            var d = provider.Detect();
            Emit($"A. Detect: Installed={d.Installed}");
            foreach (var e in d.Evidence)
            {
                Emit($"      {e}");
            }
            checks.Add("Detect");
        }
        catch (Exception ex)
        {
            problems.Add($"Detect threw: {ex.Message}");
        }

        // B + C: list, then read up to 400 summaries
        try
        {
            var sessions = provider.ListSessions();
            Emit($"B. ListSessions: {sessions?.Count ?? -1} entries");
            checks.Add("ListSessions");

            if (sessions != null && sessions.Count == 0)
            {
                var d2 = provider.Detect();
                if (d2.Installed)
                {
                    problems.Add("Installed but ZERO sessions enumerated — store layout mismatch or empty store");
                }
            }

            if (sessions != null && sessions.Count > 0)
            {
                var sw = Stopwatch.StartNew();
                var summaries = new List<SessionSummary>();
                var taken = 0;
                foreach (var (id, path) in sessions)
                {
                    if (taken >= 400) break;
                    taken++;
                    try
                    {
                        summaries.Add(provider.ReadSummary(path));
                    }
                    catch (Exception ex)
                    {
                        Emit($"      summary error {id}: {ex.Message}");
                    }
                }
                sw.Stop();
                Emit($"C. ReadSummary: {summaries.Count}/{taken} ok in {sw.ElapsedMilliseconds} ms");
                checks.Add("ReadSummary");

                if (summaries.Any())
                {
                    var sorted = summaries.OrderByDescending(s => s.RecencyDate).ToList();
                    var sample = sorted.First();
                    Emit($"      sample: title='{Truncate(sample.Title, 60)}' ws='{sample.Workspace ?? ""}' msgs={sample.MessagesCount} tools={sample.ToolCallsCount} sub={sample.IsSubagent}");

                    // D. Full session import
                    try
                    {
                        var sw2 = Stopwatch.StartNew();
                        var full = provider.ReadSession(sample.SourcePath);
                        sw2.Stop();
                        var users = 0;
                        var tools = 0;
                        var firstUser = "";
                        foreach (var msg in full.Messages)
                        {
                            if (msg.Role == MessageRole.User)
                            {
                                users++;
                                if (firstUser == "" && !string.IsNullOrWhiteSpace(msg.Content)) firstUser = msg.Content;
                            }
                            tools += msg.ToolCalls.Count;
                        }
                        Emit($"D. ReadSession: {full.Messages.Count} msgs ({users} user, {tools} tool-calls) in {sw2.ElapsedMilliseconds} ms");
                        Emit($"      first user: {Truncate(firstUser, 100)}");
                        Emit($"      title: '{Truncate(full.Title, 60)}'");
                        checks.Add("ReadSession");
                        if (full.Messages.Count == 0)
                        {
                            problems.Add("Summary OK but full transcript imports ZERO messages — check ReadSession store parsing");
                        }
                    }
                    catch (Exception ex)
                    {
                        problems.Add($"ReadSession failed: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            problems.Add($"ListSessions threw: {ex.Message}");
        }

        // E. Resume command
        try
        {
            var cmd = provider.ResumeCommand("00000000-0000-4000-8000-000000000000");
            Emit($"E. Resume command: {cmd}");
            checks.Add("ResumeCommand");
        }
        catch (Exception ex)
        {
            problems.Add($"ResumeCommand failed: {ex.Message}");
        }

        var verdict = problems.Count == 0 ? "PASS" : "FAIL";
        Emit($"VERDICT: {verdict} — stages=[{string.Join(", ", checks)}] problems=[{string.Join("; ", problems)}]");
        FlushReport();
    }
}
