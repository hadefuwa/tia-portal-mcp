namespace TiaOpennessMcpServer.Utilities;

/// <summary>
/// The TIA Portal version this build targets. Chosen at build time with
/// <c>-p:TiaVersion=19</c> or <c>-p:TiaVersion=20</c> (see the csproj); there is no default.
/// </summary>
public static class TiaVersion
{
#if TIA_V20
    public const string Name = "V20";
#elif TIA_V19
    public const string Name = "V19";
#else
#error Build with -p:TiaVersion=19 or -p:TiaVersion=20
    public const string Name = "";
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
