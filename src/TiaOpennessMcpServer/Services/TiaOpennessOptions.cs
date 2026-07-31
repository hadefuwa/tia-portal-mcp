namespace TiaOpennessMcpServer.Services;

public sealed class TiaOpennessOptions
{
    public string Version                  { get; set; } = "V20";
    public string DefaultMode              { get; set; } = "WithoutUserInterface";
    public int    ConnectionTimeoutSeconds { get; set; } = 60;
    public bool   AutoSaveOnDisconnect     { get; set; } = true;
    public string ExportDirectory          { get; set; } = @"C:\Temp\TiaExports";
}
