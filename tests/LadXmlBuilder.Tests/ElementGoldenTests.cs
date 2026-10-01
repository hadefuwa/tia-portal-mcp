using System.Xml.Linq;
using TiaOpennessMcpServer.Utilities;
using Xunit;

namespace LadTests;

/// <summary>
/// Samples 09-13 are TIA V20's re-export of blocks built from these exact requests, imported and
/// compiled live (0 errors). The builder must reproduce their wiring topology.
/// </summary>
public class ElementGoldenTests
{
    private static readonly XNamespace Ns = LadXmlBuilder.FlgNs;

    private static List<XElement> GoldenNets(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "lad-samples")))
            dir = dir.Parent;
        return XDocument.Load(Path.Combine(dir!.FullName, "docs", "lad-samples", file))
            .Descendants(Ns + "FlgNet").ToList();
    }

    private static LadElement C(string op, bool nc = false) => new() { Type = "contact", Operand = op, Negated = nc };
    private static LadElement Cmp(string t, string a, string b, string? dt = null) => new() { Type = t, Operand = a, Operand2 = b, DataType = dt };
    private static LadElement Tmr(string t, string inst, string pt) => new() { Type = t, Instance = inst, Pt = pt };
    private static LadElement Mv(string src, string dst) => new() { Type = "move", Source = src, Operand = dst };
    private static LadElement Coil(string op) => new() { Type = "coil", Operand = op };

    private static void AssertMatches(string golden, params LadNetwork[] nets)
    {
        var gold = GoldenNets(golden);
        Assert.Equal(gold.Count, nets.Length);
        for (int i = 0; i < nets.Length; i++)
            Assert.Equal(LiveGoldenTests.Edges(gold[i]), LiveGoldenTests.Edges(LadXmlBuilder.BuildFlgNet(nets[i])));
    }

    [Fact]
    public void CompareEdgeMoveTofTp_MatchTiaReexport()
    {
        AssertMatches("09-live-compare-edge-move-tof-tp-fb.xml",
            new LadNetwork { Elements = { Cmp("gt", "#Level", "100") }, Outputs = { Coil("#Run") } },
            new LadNetwork { Elements = { C("#Start"), new LadElement { Type = "pbox", Operand = "#Edge1" }, Mv("5", "#Counter") } },
            new LadNetwork { Elements = { C("#Start", true), new LadElement { Type = "nbox", Operand = "#Edge2" }, Mv("0", "#Counter") } },
            new LadNetwork { Elements = { C("#Start"), Tmr("tof", "#Toff", "T#3s") }, Outputs = { Coil("#Off") } },
            new LadNetwork { Elements = { C("#Start"), Tmr("tp", "#Tp", "T#1s") }, Outputs = { Coil("#Pulse") } },
            new LadNetwork
            {
                Elements = { Cmp("eq", "#Counter", "3"), Cmp("le", "#Level", "7", "Int"), Cmp("ne", "#Level", "0"),
                             Cmp("ge", "#Level", "1"), Cmp("lt", "#Level", "999") },
                Outputs = { Coil("#Three") },
            });
    }

    private static LadElement Call(string block, string type, string? inst, params LadParam[] ps) =>
        new() { Type = "call", Block = block, BlockType = type, Instance = inst, Parameters = ps.ToList() };
    private static LadParam In(string n, string op, string dt = "Bool")  => new() { Name = n, Section = "Input",  Datatype = dt, Operand = op };
    private static LadParam Out(string n, string op, string dt = "Bool") => new() { Name = n, Section = "Output", Datatype = dt, Operand = op };

    [Fact]
    public void CallFc_WithParams_MatchesTiaReexport()
        => AssertMatches("10-live-call-fc-with-params.xml", new LadNetwork
        {
            Elements = { C("#Start"), Call("ZZ_LadTest_CalleeFc", "FC", null, In("X", "#Start"), Out("Y", "#Done")) },
        });

    [Fact]
    public void CallFb_InstanceDb_MatchesTiaReexport()
        => AssertMatches("11-live-call-fb-instance-db.xml", new LadNetwork
        {
            Elements = { C("#Start"), Call("ZZ_LadTest_CalleeFb", "FB", "ZZ_LadTest_CalleeFb_DB", In("A", "#Start"), Out("B", "#Done")) },
        });

    [Fact]
    public void CallFb_MultiInstance_MatchesTiaReexport()
        => AssertMatches("12-live-call-fb-multi-instance.xml", new LadNetwork
        {
            Elements = { Call("ZZ_LadTest_CalleeFb", "FB", "#Sub", In("A", "#Start"), Out("B", "#Done")) },
        });

    [Fact]
    public void CallWithoutParams_WiresOnlyEn()
    {
        // On import TIA fills in the callee's parameter list and, on re-export, writes eno/output pins
        // to OpenCon. The builder need not write those: only the power-flow wire into en must match.
        var gold = LiveGoldenTests.Edges(GoldenNets("13-live-call-no-params-ob.xml").Single()).Where(e => e.StartsWith("Powerrail")).ToList();
        var built = LiveGoldenTests.Edges(LadXmlBuilder.BuildFlgNet(new LadNetwork
        { Elements = { Call("ZZ_LadTest_CalleeFb", "FB", "ZZ_LadTest_CalleeFb_DB") } })).Where(e => e.StartsWith("Powerrail")).ToList();
        Assert.Single(gold);
        Assert.Equal(gold, built);
    }

    // ── validation ────────────────────────────────────────────────────────────

    [Fact]
    public void Move_MustBeLast_AndCannotHaveOutputsOrSitInBranch()
    {
        Assert.Throws<ArgumentException>(() => LadXmlBuilder.BuildFlgNet(new LadNetwork { Elements = { Mv("1", "#A"), C("#B") } }));
        Assert.Throws<ArgumentException>(() => LadXmlBuilder.BuildFlgNet(new LadNetwork { Elements = { Mv("1", "#A") }, Outputs = { Coil("#B") } }));
        Assert.Throws<ArgumentException>(() => LadXmlBuilder.BuildFlgNet(new LadNetwork
        { Elements = { new LadElement { Type = "branch", Branches = new() { new() { Mv("1", "#A") }, new() { C("#B") } } } } }));
    }

    [Fact]
    public void Compare_NeedsSecondOperand_And_Call_NeedsBlockTypeAndInstance()
    {
        Assert.Throws<ArgumentException>(() => LadXmlBuilder.BuildFlgNet(new LadNetwork { Elements = { new LadElement { Type = "gt", Operand = "#A" } } }));
        Assert.Throws<ArgumentException>(() => LadXmlBuilder.BuildFlgNet(new LadNetwork { Elements = { Call("F", "FX", null) } }));
        Assert.Throws<ArgumentException>(() => LadXmlBuilder.BuildFlgNet(new LadNetwork { Elements = { Call("F", "FB", null) } }));
    }

    [Fact]
    public void Compare_DefaultsToInt_AndUsesPreIn1In2Out()
    {
        var flg = LadXmlBuilder.BuildFlgNet(new LadNetwork { Elements = { Cmp("gt", "#A", "5") }, Outputs = { Coil("#B") } });
        var part = flg.Descendants(Ns + "Part").Single(p => (string?)p.Attribute("Name") == "Gt");
        Assert.Equal("Int", part.Element(Ns + "TemplateValue")!.Value);
        var pins = flg.Descendants(Ns + "NameCon").Where(n => (string?)n.Attribute("UId") == (string?)part.Attribute("UId"))
                      .Select(n => (string)n.Attribute("Name")!).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "in1", "in2", "out", "pre" }, pins);
    }
}
