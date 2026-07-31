namespace TiaOpennessMcpServer.Models;

public sealed record ProjectInfo
{
    public required string Name        { get; init; }
    public required string Path        { get; init; }
    public required string Author      { get; init; }
    public required string Comment     { get; init; }
    public required string CreatedDate { get; init; }
    public required string ModifiedDate { get; init; }
    public required string PortalVersion { get; init; }
    public required int    DeviceCount  { get; init; }
    public required bool   IsModified   { get; init; }
}
