namespace TiaOpennessMcpServer.Models;

public sealed record TagTableInfo
{
    public required string Name     { get; init; }
    public required int    TagCount { get; init; }
    public required string Comment  { get; init; }
}

public sealed record TagInfo
{
    public required string Name      { get; init; }
    public required string DataType  { get; init; }
    public required string Address   { get; init; }
    public required bool   Accessible { get; init; }
    public required bool   Writable   { get; init; }
    public required string Comment   { get; init; }
}

public sealed record TagDefinition
{
    public required string Name     { get; init; }
    public required string DataType { get; init; }
    public required string Address  { get; init; }
    public          bool   Accessible { get; init; } = true;
    public          bool   Writable   { get; init; } = true;
    public          string Comment  { get; init; } = "";
}
