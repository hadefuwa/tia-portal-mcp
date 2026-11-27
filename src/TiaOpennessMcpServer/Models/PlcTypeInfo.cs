namespace TiaOpennessMcpServer.Models;

/// <summary>Summary of a PLC data type (UDT) as listed from PlcSoftware.TypeGroup.</summary>
public sealed class PlcTypeInfo
{
    public string Name { get; set; } = "";
    public string Namespace { get; set; } = "";

    /// <summary>Folder path within the PLC data types tree; empty when at the root.</summary>
    public string Group { get; set; } = "";

    public bool IsConsistent { get; set; }
    public bool IsKnowHow { get; set; }
    public string Modified { get; set; } = "";
}

/// <summary>A PLC data type plus its exported SimaticML.</summary>
public sealed class PlcTypeContent
{
    public string Name { get; set; } = "";
    public string Namespace { get; set; } = "";
    public bool IsConsistent { get; set; }
    public bool IsKnowHow { get; set; }
    public string Modified { get; set; } = "";
    public int SizeBytes { get; set; }
    public string XmlContent { get; set; } = "";
}
