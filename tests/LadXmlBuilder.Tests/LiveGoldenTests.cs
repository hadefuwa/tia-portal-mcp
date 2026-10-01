using System.Xml.Linq;
using TiaOpennessMcpServer.Utilities;
using Xunit;

namespace LadTests;

/// <summary>
/// Samples 05-08 are TIA V20's own export of blocks this builder generated, imported and compiled
/// (0 errors) on a live project. Each test rebuilds the same block and requires the same wiring
/// topology and block header as TIA's re-export. TIA renumbers UIds, so topology is compared, not text.
/// </summary>
public class LiveGoldenTests
{
    private static readonly XNamespace Ns    = LadXmlBuilder.FlgNs;
    private static readonly XNamespace IfNs  = "http://www.siemens.com/automation/Openness/SW/Interface/v5";

    private static XDocument Sample(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "lad-samples")))
            dir = dir.Parent;
        return XDocument.Load(Path.Combine(dir!.FullName, "docs", "lad-samples", file));
    }

    private static LadElement Contact(string op, bool nc = false) => new() { Type = "contact", Operand = op, Negated = nc };

    internal static List<string> Edges(XElement flg)
    {
        var label = new Dictionary<string, string>();
        foreach (var p in flg.Element(Ns + "Parts")!.Elements())
        {
            var uid = p.Attribute("UId")!.Value;
            if (p.Name.LocalName == "Call")
            {
                var ci = p.Element(Ns + "CallInfo")!;
                label[uid] = "Call:" + ci.Attribute("Name")!.Value + ci.Elements(Ns + "Instance").Select(i =>
                    "@" + i.Attribute("Scope")!.Value + ":" + i.Element(Ns + "Component")!.Attribute("Name")!.Value).FirstOrDefault();
                continue;
            }
            label[uid] = p.Name.LocalName == "Access"
                ? "Access:" + p.Attribute("Scope")!.Value + ":" +
                  string.Join(".", p.Descendants(Ns + "Component").Select(c => c.Attribute("Name")!.Value)) +
                  string.Concat(p.Descendants(Ns + "ConstantValue").Select(c => c.Value))
                : p.Name.LocalName == "Part" && p.Element(Ns + "Instance") is { } inst
                    ? p.Attribute("Name")!.Value + "@" + inst.Attribute("Scope")!.Value + ":" +
                      string.Join(".", inst.Descendants(Ns + "Component").Select(c => c.Attribute("Name")!.Value))
                    : (p.Attribute("Name")?.Value ?? p.Name.LocalName) +
                      string.Concat(p.Elements(Ns + "TemplateValue").Select(t => $"({t.Attribute("Name")!.Value}={t.Value})")) +
                      (p.Element(Ns + "Negated") is null ? "" : "(NC)");
        }
        var edges = new List<string>();
        foreach (var w in flg.Element(Ns + "Wires")!.Elements())
        {
            var c = w.Elements().ToList();
            string End(XElement e) => e.Name.LocalName switch
            {
                "Powerrail" => "Powerrail",
                "IdentCon"  => label[e.Attribute("UId")!.Value],
                "OpenCon"   => "Open",
                _           => label[e.Attribute("UId")!.Value] + "." + e.Attribute("Name")!.Value,
            };
            foreach (var d in c.Skip(1)) edges.Add(End(c[0]) + " -> " + End(d));
        }
        edges.Sort(StringComparer.Ordinal);
        return edges;
    }

    private static List<string> SectionNames(XElement block) =>
        block.Descendants(IfNs + "Sections").First().Elements(IfNs + "Section")
             .Select(s => s.Attribute("Name")!.Value).ToList();

    private static XElement BuiltBlock(string type, int? number, LadInterface iface, LadNetwork net)
        => XDocument.Parse(LadXmlBuilder.CreateLadBlockXml("Blk", type, number, iface, new[] { net }, null)).Root!.Elements().Last();

    private static XElement GoldenBlock(string file) => Sample(file).Root!.Elements().Last();

    private static XElement Flg(XElement block) => block.Descendants(Ns + "FlgNet").Single();

    private static LadInterface SealInIface() => new()
    {
        Input  = { new LadMember { Name = "Start" }, new LadMember { Name = "Stop" } },
        Output = { new LadMember { Name = "Motor" } },
    };

    private static LadNetwork SealInNet() => new()
    {
        Elements =
        {
            new LadElement { Type = "branch", Branches = new() { new() { Contact("#Start") }, new() { Contact("#Motor") } } },
            Contact("#Stop", nc: true),
        },
        Outputs = { new LadElement { Type = "coil", Operand = "#Motor" } },
    };

    [Fact]
    public void SealInFb_MatchesTiaReexport()
    {
        var built = BuiltBlock("FB", null, SealInIface(), SealInNet());
        var gold  = GoldenBlock("05-live-sealin-fb.xml");
        Assert.Equal("SW.Blocks.FB", gold.Name.LocalName);
        Assert.Equal(Edges(Flg(gold)), Edges(Flg(built)));
        Assert.Equal(SectionNames(gold), SectionNames(built));
        Assert.Null(built.Element("AttributeList")!.Element("Namespace")!.Attribute("Name")); // child element, no attribute
        Assert.NotNull(gold.Element("AttributeList")!.Element("Namespace"));
    }

    [Fact]
    public void TonMultiInstanceFb_MatchesTiaReexport()
    {
        var iface = new LadInterface
        {
            Input  = { new LadMember { Name = "Start" } },
            Output = { new LadMember { Name = "Done" } },
            Static = { new LadMember { Name = "Tmr", Datatype = "TON" } },
        };
        var net = new LadNetwork
        {
            Elements = { Contact("#Start"), new LadElement { Type = "ton", Instance = "#Tmr", Pt = "T#2s" } },
            Outputs  = { new LadElement { Type = "coil", Operand = "#Done" } },
        };
        var built = BuiltBlock("FB", null, iface, net);
        var gold  = GoldenBlock("06-live-ton-multiinstance-fb.xml");
        Assert.Equal(Edges(Flg(gold)), Edges(Flg(built)));

        // The #local "Tmr" instance is a plain LocalVariable component, as TIA exports it.
        var inst = Flg(gold).Descendants(Ns + "Instance").Single();
        Assert.Equal("LocalVariable", (string)inst.Attribute("Scope")!);
        Assert.Equal("Tmr", (string)inst.Element(Ns + "Component")!.Attribute("Name")!);
        // TIA normalises the declared type "TON" to TON_TIME.
        Assert.Equal("TON_TIME", (string)gold.Descendants(IfNs + "Member").Single(m => (string)m.Attribute("Name")! == "Tmr").Attribute("Datatype")!);
    }

    [Fact]
    public void Fc_MatchesTiaReexport_AndKeepsRetValVoid()
    {
        var iface = new LadInterface
        {
            Input  = { new LadMember { Name = "Start" } },
            Output = { new LadMember { Name = "Run" } },
        };
        var net = new LadNetwork { Elements = { Contact("#Start") }, Outputs = { new LadElement { Type = "coil", Operand = "#Run" } } };
        var built = BuiltBlock("FC", null, iface, net);
        var gold  = GoldenBlock("07-live-contact-coil-fc.xml");
        Assert.Equal(Edges(Flg(gold)), Edges(Flg(built)));
        Assert.Equal(SectionNames(gold), SectionNames(built));
        Assert.Contains("Return", SectionNames(gold));
        var ret = gold.Descendants(IfNs + "Section").Single(s => (string)s.Attribute("Name")! == "Return").Element(IfNs + "Member")!;
        Assert.Equal("Ret_Val", (string)ret.Attribute("Name")!);
        Assert.Equal("Void",    (string)ret.Attribute("Datatype")!);
    }

    [Fact]
    public void Ob_MatchesTiaReexport_AndHasOnlyInputTempConstant()
    {
        var iface = new LadInterface { Temp = { new LadMember { Name = "a" }, new LadMember { Name = "b" } } };
        var net = new LadNetwork { Elements = { Contact("#a") }, Outputs = { new LadElement { Type = "coil", Operand = "#b" } } };
        var built = BuiltBlock("OB", 124, iface, net);
        var gold  = GoldenBlock("08-live-contact-coil-ob.xml");
        Assert.Equal(Edges(Flg(gold)), Edges(Flg(built)));
        Assert.Equal(new[] { "Input", "Temp", "Constant" }, SectionNames(built));
        Assert.Equal("ProgramCycle", (string)gold.Element("AttributeList")!.Element("SecondaryType")!);
        Assert.Equal("ProgramCycle", (string)built.Element("AttributeList")!.Element("SecondaryType")!);
        Assert.Equal("124", (string)built.Element("AttributeList")!.Element("Number")!);
    }

    [Fact]
    public void Ob_RejectsOutputStaticAndInOutMembers()
    {
        // TIA: "Section 'Output' is not valid for this block."
        var net = new LadNetwork { Elements = { Contact("#a") }, Outputs = { new LadElement { Type = "coil", Operand = "#a" } } };
        Assert.Throws<ArgumentException>(() => BuiltBlock("OB", 124, new LadInterface { Output = { new LadMember { Name = "o" } } }, net));
        Assert.Throws<ArgumentException>(() => BuiltBlock("OB", 124, new LadInterface { InOut  = { new LadMember { Name = "o" } } }, net));
        Assert.Throws<ArgumentException>(() => BuiltBlock("OB", 124, new LadInterface { Static = { new LadMember { Name = "o" } } }, net));
    }
}
