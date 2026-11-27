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

public sealed record HmiScreenItemSpec
{
    /// <summary>text | iofield | button | rectangle | circle | toggleswitch</summary>
    public required string Type { get; init; }
    public required string Name { get; init; }
    public int Left { get; init; }
    public int Top { get; init; }
    public uint Width { get; init; } = 100;
    public uint Height { get; init; } = 30;
    /// <summary>Caption for text / button / toggleswitch.</summary>
    public string? Text { get; init; }
    /// <summary>HMI tag shown/edited by an iofield.</summary>
    public string? ProcessValue { get; init; }
    /// <summary>JavaScript run when a button is tapped, e.g. Tags("BC_Pattern").Write(3);</summary>
    public string? OnTap { get; init; }
    /// <summary>"Input", "Output" or "InputOutput" for an iofield.</summary>
    public string? IoType { get; init; }
    /// <summary>Extra attributes applied with SetAttribute (string, number, bool). Errors are reported, not fatal.</summary>
    public Dictionary<string, object?>? Attributes { get; init; }
    /// <summary>polygon / polyline only: flat list x1,y1,x2,y2,... in screen pixels.</summary>
    public List<int>? Points { get; init; }
}

public sealed record HmiScreenBuildRequest
{
    public bool Create { get; init; }
    public uint? Width { get; init; }
    public uint? Height { get; init; }
    /// <summary>Delete every existing item on the screen first.</summary>
    public bool Clear { get; init; }
    public List<HmiScreenItemSpec> Items { get; init; } = new();
}

public sealed record HmiColourMapEntry
{
    /// <summary>Tag value that selects this entry, e.g. "1".</summary>
    public required string Value { get; init; }
    /// <summary>Optional end of the range; omitted = the single value above.</summary>
    public string? To { get; init; }
    /// <summary>Property value for that tag value: a colour "#FF0000", "true"/"false" (e.g. Visible) or a number.</summary>
    public required string Color { get; init; }
    /// <summary>Optional: slow | medium | fast makes the property flash while this entry is active.</summary>
    public string? Flashing { get; init; }
}

/// <summary>Re-point an EXISTING HMI tag at a different PLC tag (see HmiTagService.RelinkTagsAsync).</summary>
public sealed record HmiTagRelinkRequest
{
    public required string Name       { get; init; }
    public required string PlcTag     { get; init; }
    /// <summary>HMI connection name. Empty = keep the tag's current connection.</summary>
    public          string Connection { get; init; } = "";
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
