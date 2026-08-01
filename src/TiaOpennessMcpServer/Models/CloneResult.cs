namespace TiaOpennessMcpServer.Models;

public class CloneResult
{
    public bool   Success           { get; set; }
    public string ProjectPath       { get; set; } = "";
    public int    DevicesCreated    { get; set; }
    public int    BlocksExported    { get; set; }
    public int    BlocksImported    { get; set; }
    public int    TagTablesExported { get; set; }
    public int    TagTablesImported { get; set; }
    public List<string> Warnings   { get; set; } = new();
    public string? Error            { get; set; }
}
