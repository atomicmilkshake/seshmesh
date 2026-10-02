using System;

namespace Casr.Core.Export;

public enum ExportFormat
{
    Markdown,
    Html,
    Json,
    Native
}

public class ExportOptions
{
    public bool IncludeToolCalls { get; set; } = true;
    public bool IncludeToolResults { get; set; } = true;
    public bool IncludeThinking { get; set; } = true;
    public bool IncludeMetadataHeader { get; set; } = true;
}
