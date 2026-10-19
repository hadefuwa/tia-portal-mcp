using TiaOpennessMcpServer.Utilities;

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
    /// <summary>Opt-in: link the new tag to <see cref="PlcTag"/> over <see cref="Connection"/>. Default false.</summary>
    public          bool   BindPlc  { get; init; }
}

public sealed record FaceplateTagUpdate
{
    public required string ContainerName  { get; init; }
    public required string ParameterName  { get; init; }
    public required string NewValue       { get; init; }
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

public sealed class LadBlockRequest
{
    public string                 Name      { get; set; } = "";
    public string                 Type      { get; set; } = "FB";
    public int?                   Number    { get; set; }
    public LadInterface?          Interface { get; set; }
    public List<LadNetwork>       Networks  { get; set; } = new();
    /// <summary>Project culture (e.g. "en-GB"); only needed if networks carry titles/comments.</summary>
    public string?                Culture   { get; set; }
    /// <summary>Replace an existing block of the same name. Default false.</summary>
    public bool                   Overwrite { get; set; }
    /// <summary>Compile after import and return the compiler output. Default true.</summary>
    public bool                   Compile   { get; set; } = true;
    /// <summary>Build and validate the XML only; do not touch TIA Portal.</summary>
    public bool                   DryRun    { get; set; }
}

public sealed record LadBlockResult
{
    public required string  Name             { get; init; }
    public required bool    Imported         { get; init; }
    public          string? Xml              { get; init; }
    public          string? XmlPath          { get; init; }
    public          string? CompileOutput    { get; init; }
    public          bool?   CompileSucceeded { get; init; }
}
