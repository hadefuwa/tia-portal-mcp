namespace TiaOpennessMcpServer.Utilities;

/// <summary>
/// The TIA Portal version this build targets. Required at build time:
/// <c>-p:TiaVersion=19</c>, <c>20</c> or <c>21</c> (see the csproj); there is no default.
/// </summary>
public static class TiaVersion
{
#if TIA_V21
    public const string Name = "V21";
#elif TIA_V20
    public const string Name = "V20";
#elif TIA_V19
    public const string Name = "V19";
#else
#error Build with -p:TiaVersion=19, 20 or 21
    public const string Name = "";
#endif

    private const string Root = @"C:\Program Files\Siemens\Automation";

    /// <summary>Folders searched for Siemens.Engineering*.dll at run time. V21 keeps its DLLs under net48\.</summary>
    public static string[] SearchPaths { get; } = new[]
    {
        $@"{Root}\Portal {Name}\PublicAPI\{Name}\net48",
        $@"{Root}\Portal {Name}\PublicAPI\{Name}",
        $@"{Root}\Portal {Name}\Bin\PublicAPI",
        $@"{Root}\Portal {Name}\Bin\PublicAPI\Client",
        $@"{Root}\Portal {Name}\Bin",
    };
}
