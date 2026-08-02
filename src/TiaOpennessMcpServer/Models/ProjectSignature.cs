namespace TiaOpennessMcpServer.Models;

public sealed record ProjectSignature
{
    public required string CapturedAt  { get; init; }
    public required string ProjectName { get; init; }
    public required string ProjectPath { get; init; }
    public required IReadOnlyList<DeviceSignature> Devices { get; init; }
}

public sealed record DeviceSignature
{
    public required string Name { get; init; }
    public required IReadOnlyList<BlockSignature>   Blocks    { get; init; }
    public required IReadOnlyList<TagTableSig>      TagTables { get; init; }
}

public sealed record BlockSignature
{
    public required string Name         { get; init; }
    public required string Type         { get; init; }
    public required int    Number       { get; init; }
    public required string Language     { get; init; }
    public required bool   IsConsistent { get; init; }
}

public sealed record TagTableSig
{
    public required string Name     { get; init; }
    public required int    TagCount { get; init; }
}

public sealed record TagRenameItem
{
    public required string From { get; init; }
    public required string To   { get; init; }
}
