namespace TiaOpennessMcpServer.Utilities;

/// <summary>
/// The TIA Portal version this build targets. Chosen at build time with
/// <c>-p:TiaVersion=19</c> or <c>-p:TiaVersion=20</c> (see the csproj); default 19.
/// </summary>
public static class TiaVersion
{
#if TIA_V20
    public const string Name = "V20";
#else
    public const string Name = "V19";
#endif

    /// <summary>Folders searched for Siemens.Engineering*.dll at run time.</summary>
    public static string[] SearchPaths { get; } = new[]
    {
        $@"C:\Program Files\Siemens\Automation\Portal {Name}\PublicAPI\{Name}",
        $@"C:\Program Files\Siemens\Automation\Portal {Name}\Bin\PublicAPI",
        $@"C:\Program Files\Siemens\Automation\Portal {Name}\Bin\PublicAPI\Client",
        $@"C:\Program Files\Siemens\Automation\Portal {Name}\Bin",
    };
}
