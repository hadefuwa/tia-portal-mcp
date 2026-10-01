using System.Xml.Linq;
using TiaOpennessMcpServer.Utilities;
using Xunit;

namespace LadTests;

/// <summary>
/// Samples 14-22 are TIA V20's re-export of arithmetic, scaling, flip-flop, counter and system-function
/// blocks built from these exact requests, imported and compiled live (0 errors).
/// </summary>
public class BoxGoldenTests
{
    private static readonly XNamespace Ns = LadXmlBuilder.FlgNs;

    private static List<XElement> Gold(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "lad-samples")))
            dir = dir.Parent;
        return XDocument.Load(Path.Combine(dir!.FullName, "docs", "lad-samples", file))
            .Descendants(Ns + "FlgNet").ToList();
    }

    private static void Match(string file, params LadNetwork[] nets)
    {
        var gold = Gold(file);
        Assert.Equal(gold.Count, nets.Length);
        for (int i = 0; i < nets.Length; i++)
            Assert.Equal(LiveGoldenTests.Edges(gold[i]), LiveGoldenTests.Edges(LadXmlBuilder.BuildFlgNet(nets[i])));
    }

    private static LadElement C(string op) => new() { Type = "contact", Operand = op };
    private static LadElement Coil(string op) => new() { Type = "coil", Operand = op };
    private static LadElement Math(string t, string dest, params string[] ops) => new() { Type = t, Operand = dest, Operands = ops.ToList() };

    [Fact] public void Add_ThreeInputs_MatchesTia()
        => Match("14-live-add-3-inputs-fb.xml", new LadNetwork { Elements = { C("#Start"), Math("add", "#R1", "#A", "#B", "5") } });
    [Fact] public void Sub_MatchesTia() => Match("15-live-sub-fb.xml", new LadNetwork { Elements = { Math("sub", "#R2", "#A", "#B") } });
    [Fact] public void Mul_MatchesTia() => Match("16-live-mul-fb.xml", new LadNetwork { Elements = { Math("mul", "#R3", "#A", "#B") } });
    [Fact] public void Div_MatchesTia() => Match("17-live-div-fb.xml", new LadNetwork { Elements = { Math("div", "#R4", "#A", "#B") } });
    [Fact] public void Mod_MatchesTia() => Match("18-live-mod-fb.xml", new LadNetwork { Elements = { Math("mod", "#R5", "#A", "#B") } });

    [Fact]
    public void ArithmeticParts_HaveTiaAttributes()
    {
        // Add/Mul carry Card + AutomaticTyped; Sub/Div/Mod only AutomaticTyped. All are DisabledENO.
        foreach (var (type, name, card) in new[] { ("add", "Add", true), ("mul", "Mul", true), ("sub", "Sub", false), ("div", "Div", false), ("mod", "Mod", false) })
        {
            var part = LadXmlBuilder.BuildFlgNet(new LadNetwork { Elements = { Math(type, "#R", "#A", "#B") } })
                .Descendants(Ns + "Part").Single(p => (string?)p.Attribute("Name") == name);
            Assert.Equal("true", (string?)part.Attribute("DisabledENO"));
            Assert.Equal(card, part.Element(Ns + "TemplateValue") is not null);
            Assert.NotNull(part.Element(Ns + "AutomaticTyped"));
        }
    }

    [Fact]
    public void NormalizeThenScale_MatchesTia()
        => Match("19-live-normalize-scale-x-fb.xml", new LadNetwork
        {
            Elements =
            {
                new LadElement { Type = "norm_x", DataType = "Int", DestType = "Real", Operand = "#Nrm",
                                 InPins = new() { ["min"] = "0", ["value"] = "#A", ["max"] = "27648" } },
                new LadElement { Type = "scale_x", DataType = "Real", DestType = "Real", Operand = "#Scl",
                                 InPins = new() { ["min"] = "0.0", ["value"] = "#Nrm", ["max"] = "#X" } },
            },
        });

    [Fact]
    public void SrAndRs_MatchTia()
        => Match("20-live-sr-rs-fb.xml",
            new LadNetwork
            {
                Elements = { C("#S"), new LadElement { Type = "sr", Operand = "#Mem1", Other = new() { C("#R") } } },
                Outputs = { Coil("#Q1") },
            },
            new LadNetwork
            {
                Elements = { C("#R"), new LadElement { Type = "rs", Operand = "#Mem2", Other = new() { C("#S") } } },
                Outputs = { Coil("#Q2") },
            });

    [Fact]
    public void CtuAndCtd_MatchTia_OutputPinIsQ()
        => Match("21-live-ctu-ctd-fb.xml",
            new LadNetwork
            {
                Elements = { C("#Pulse"), new LadElement { Type = "ctu", Instance = "#Up", Pv = "10", Other = new() { C("#Rst") } } },
                Outputs = { Coil("#Hit") },
            },
            new LadNetwork
            {
                Elements = { C("#Pulse"), new LadElement { Type = "ctd", Instance = "#Dn", Pv = "10", Other = new() { C("#Rst") } } },
                Outputs = { Coil("#Zero") },
            });

    [Fact]
    public void SystemFunctionPart_WrSysT_MatchesTia()
        => Match("22-live-system-function-wr-sys-t-fb.xml", new LadNetwork
        {
            Elements =
            {
                C("#Go"),
                new LadElement { Type = "part", Name = "WR_SYS_T", Version = "1.0",
                                 Templates = new() { ["date_type"] = "DTL" },
                                 InPins = new() { ["IN"] = "#Dt" }, OutPins = new() { ["RET_VAL"] = "#Err" } },
            },
        });

    [Fact]
    public void SrRsCtuCtd_RequireTheOtherPath_AndPartRequiresName()
    {
        Assert.Throws<ArgumentException>(() => LadXmlBuilder.BuildFlgNet(new LadNetwork { Elements = { new LadElement { Type = "sr", Operand = "#M" } } }));
        Assert.Throws<ArgumentException>(() => LadXmlBuilder.BuildFlgNet(new LadNetwork { Elements = { new LadElement { Type = "ctu", Instance = "#U", Pv = "1" } } }));
        Assert.Throws<ArgumentException>(() => LadXmlBuilder.BuildFlgNet(new LadNetwork { Elements = { new LadElement { Type = "part" } } }));
        Assert.Throws<ArgumentException>(() => LadXmlBuilder.BuildFlgNet(new LadNetwork { Elements = { Math("sub", "#R", "#A") } }));
        Assert.Throws<ArgumentException>(() => LadXmlBuilder.BuildFlgNet(new LadNetwork { Elements = { Math("add", "#R", "#A") } }));
    }

    [Fact]
    public void ArithmeticAndEnoLessPart_EndTheRung()
    {
        Assert.Throws<ArgumentException>(() => LadXmlBuilder.BuildFlgNet(new LadNetwork { Elements = { Math("add", "#R", "#A", "#B") }, Outputs = { Coil("#X") } }));
        Assert.Throws<ArgumentException>(() => LadXmlBuilder.BuildFlgNet(new LadNetwork { Elements = { new LadElement { Type = "part", Name = "WWW" }, C("#X") } }));
    }
}
