namespace TiaOpennessMcpServer.Models;

public enum BlockType { OB, FB, FC, DB, UDT, GlobalDB }
public enum ProgrammingLanguage { SCL, LAD, FBD, STL, GRAPH, CEM }

public record BlockInfo
{
    public required string             Name     { get; init; }
    public required BlockType          Type     { get; init; }
    public required int                Number   { get; init; }
    public required ProgrammingLanguage Language { get; init; }
    public required string             Author   { get; init; }
    public required string             Comment  { get; init; }
    public required string             Modified { get; init; }
    public required bool               IsKnowHow { get; init; }
    public required long               SizeBytes  { get; init; }
}

public sealed record BlockContent : BlockInfo
{
    public required string SourceCode { get; init; }
    public required string XmlContent { get; init; }
}

public sealed record NetworkTexts
{
    public string? Title   { get; init; }
    public string? Comment { get; init; }
}

public sealed record BlockTextsRequest
{
    public string?         BlockTitle   { get; init; }
    public string?         BlockComment { get; init; }
    public NetworkTexts[]? Networks     { get; init; }
}

public sealed record HmiTagCreateRequest
{
    public required string Name     { get; init; }
    public required string DataType { get; init; }
    public          string PlcTag   { get; init; } = "";
    public          string Connection { get; init; } = "HMI_Connection_6";
}

public sealed record BlockCreateRequest
{
    public required string             Name     { get; init; }
    public required BlockType          Type     { get; init; }
    public required int?               Number   { get; init; }
    public required ProgrammingLanguage Language { get; init; }
    public required string             SourceCode { get; init; }
    public          string             Author   { get; init; } = "TIA-MCP";
    public          string             Comment  { get; init; } = "";
}
