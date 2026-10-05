namespace Casr.Core.Context.Pipeline;

/// <summary>
/// Options for cross-provider packaging / conversion. Defaults mirror the
/// upstream <c>casr resume</c> flags: hidden reasoning is dropped for
/// cross-agent handoffs, the transferred history is capped at ~200k tokens,
/// and each tool observation is capped at 4k characters.
/// </summary>
public class ConversionOptions
{
    /// <summary>Prepend synthetic conversion-notice + recent-snapshot messages (casr <c>--enrich</c>).</summary>
    public bool Enrich { get; set; }

    /// <summary>Re-read the written session and compare it to what was written (casr verification step).</summary>
    public bool Verify { get; set; } = true;

    /// <summary>Keep the source agent's hidden reasoning traces (casr <c>--keep-reasoning</c>; default drops them).</summary>
    public bool KeepReasoning { get; set; }

    /// <summary>Rough token cap for the transferred history; oldest middle turns are dropped first. 0 = use the target harness limit and never drop whole turns.</summary>
    public int MaxContextTokens { get; set; } = 200_000;

    /// <summary>Truncate each tool result/observation to this many characters, always. 0 = only truncate when the harness token limit is exceeded (previous behavior).</summary>
    public int MaxToolOutput { get; set; } = 4_000;

    /// <summary>Passed through to providers whose writers overwrite an existing artifact.</summary>
    public bool Force { get; set; } = true;

    /// <summary>casr-parity defaults.</summary>
    public static ConversionOptions Default => new();

    /// <summary>
    /// The packaging behavior that predates the conversion dialog: reasoning kept,
    /// per-harness tool truncation only, no whole-turn dropping, no read-back verify.
    /// Used by the legacy <c>CrossResume(summary, slug, force)</c> overload so existing
    /// callers keep their exact output.
    /// </summary>
    public static ConversionOptions Legacy => new()
    {
        KeepReasoning = true,
        MaxContextTokens = 0,
        MaxToolOutput = 0,
        Verify = false,
        Enrich = false
    };
}
