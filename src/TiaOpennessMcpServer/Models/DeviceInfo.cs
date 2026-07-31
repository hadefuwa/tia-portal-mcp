namespace TiaOpennessMcpServer.Models;

public sealed record DeviceInfo
{
    public required string Name           { get; init; }
    public required string TypeIdentifier { get; init; }
    public required string DeviceType     { get; init; }
    public required string CpuModel       { get; init; }
    public required string IpAddress      { get; init; }
    public required string SubnetMask     { get; init; }
    public required string Gateway        { get; init; }
    public required int    SlotCount      { get; init; }
    public required IReadOnlyList<ModuleInfo> Modules { get; init; }
}

public sealed record ModuleInfo
{
    public required int    Slot           { get; init; }
    public required string Name           { get; init; }
    public required string OrderNumber    { get; init; }
    public required string ModuleType     { get; init; }
    public required string InputAddress   { get; init; }
    public required string OutputAddress  { get; init; }
}

public sealed record IoPoint
{
    public required string Address     { get; init; }
    public required string SymbolName  { get; init; }
    public required string DataType    { get; init; }
    public required string Comment     { get; init; }
    public required bool   IsInput     { get; init; }
}
