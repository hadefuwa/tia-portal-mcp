using System.Xml.Linq;
using TiaOpennessMcpServer.Utilities;
using Xunit;

namespace LadTests;

public class LadXmlBuilderTests
{
    private static readonly XNamespace Ns = LadXmlBuilder.FlgNs;

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string SamplesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "lad-samples")))
            dir = dir.Parent;
        return dir is null ? throw new DirectoryNotFoundException("docs/lad-samples not found")
                           : Path.Combine(dir.FullName, "docs", "lad-samples");
    }

    private static string Block(string type, int? number, LadInterface? iface, LadNetwork[] nets, string? culture = null)
        => LadXmlBuilder.CreateLadBlockXml("Blk", type, number, iface, nets, culture);

    private static LadElement Contact(string op, bool nc = false) => new() { Type = "contact", Operand = op, Negated = nc };
    private static LadElement Coil(string op, string type = "coil") => new() { Type = type, Operand = op };

    private static LadNetwork SealIn() => new()
    {
        Elements =
        {
            new LadElement { Type = "branch", Branches = new() { new() { Contact("Start") }, new() { Contact("Motor") } } },
            Contact("Stop", nc: true),
        },
        Outputs = { Coil("Motor") },
    };

    private static XElement Net(LadNetwork n) => LadXmlBuilder.BuildFlgNet(n);

    /// <summary>Canonical edge list: UIds replaced by part names / access descriptions.</summary>
    private static List<string> Edges(XElement flg)
    {
        var label = new Dictionary<string, string>();
        foreach (var p in flg.Element(Ns + "Parts")!.Elements())
        {
            var uid = p.Attribute("UId")!.Value;
            if (p.Name.LocalName == "Access")
                label[uid] = "Access:" + p.Attribute("Scope")!.Value + ":" +
                    string.Join(".", p.Descendants(Ns + "Component").Select(c => c.Attribute("Name")!.Value)) +
                    string.Concat(p.Descendants(Ns + "ConstantValue").Select(c => c.Value));
            else
                label[uid] = p.Attribute("Name")?.Value ?? p.Name.LocalName;
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

    // ── structure ─────────────────────────────────────────────────────────────

    [Fact]
    public void SealIn_HasExpectedTopology()
    {
        var expected = new[]
        {
            "Access:GlobalVariable:Motor -> Contact.operand",
            "Access:GlobalVariable:Motor -> Coil.operand",
            "Access:GlobalVariable:Start -> Contact.operand",
            "Access:GlobalVariable:Stop -> Contact.operand",
            "Contact.out -> O.in1",
            "Contact.out -> O.in2",
            "O.out -> Contact.in",
            "Contact.out -> Coil.in",
            "Powerrail -> Contact.in",
            "Powerrail -> Contact.in",
        }.OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, Edges(Net(SealIn())));
    }

    [Fact]
    public void SealIn_PowerrailIsOneWireWithTwoDestinations()
    {
        var power = Net(SealIn()).Element(Ns + "Wires")!.Elements().Single(w => w.Element(Ns + "Powerrail") is not null);
        Assert.Equal(2, power.Elements(Ns + "NameCon").Count());
    }

    [Fact]
    public void SealIn_OrCardinalityMatchesBranchCount()
    {
        var o = Net(SealIn()).Descendants(Ns + "Part").Single(p => (string?)p.Attribute("Name") == "O");
        Assert.Equal("2", o.Element(Ns + "TemplateValue")!.Value);
    }

    [Fact]
    public void NormallyClosedContact_EmitsNegatedOperand()
    {
        var nc = Net(SealIn()).Descendants(Ns + "Part").Single(p => p.Element(Ns + "Negated") is not null);
        Assert.Equal("operand", nc.Element(Ns + "Negated")!.Attribute("Name")!.Value);
    }

    [Fact]
    public void Ton_HasVersionTemplateInstanceAndOpenET()
    {
        var net = new LadNetwork
        {
            Elements = { Contact("Start"), new LadElement { Type = "ton", Instance = "#Tmr", Pt = "T#5s" } },
            Outputs  = { Coil("Done") },
        };
        var flg = Net(net);
        var ton = flg.Descendants(Ns + "Part").Single(p => (string?)p.Attribute("Name") == "TON");
        Assert.Equal("1.0", (string?)ton.Attribute("Version"));
        Assert.Equal("Time", ton.Element(Ns + "TemplateValue")!.Value);
        Assert.Equal("LocalVariable", (string?)ton.Element(Ns + "Instance")!.Attribute("Scope"));

        var edges = Edges(flg);
        Assert.Contains("TON.Q -> Coil.in", edges);
        Assert.Contains("TON.ET -> Open", edges);
        Assert.Contains("Access:TypedConstant:T#5s -> TON.PT", edges);
        Assert.Contains("Contact.out -> TON.IN", edges);
    }

    [Fact]
    public void SetAndReset_UseSCoilAndRCoil()
    {
        var net = new LadNetwork { Elements = { Contact("A") }, Outputs = { Coil("X", "scoil"), Coil("Y", "rcoil") } };
        var names = Net(net).Descendants(Ns + "Part").Select(p => (string?)p.Attribute("Name")).ToList();
        Assert.Contains("SCoil", names);
        Assert.Contains("RCoil", names);
    }

    [Fact]
    public void Symbols_DbMembersAndLocals_SplitIntoComponents()
    {
        var net = new LadNetwork { Elements = { Contact("\"My DB\".Struct.Bit"), Contact("#loc") }, Outputs = { Coil("Out") } };
        var edges = Edges(Net(net));
        Assert.Contains("Access:GlobalVariable:My DB.Struct.Bit -> Contact.operand", edges);
        Assert.Contains("Access:LocalVariable:loc -> Contact.operand", edges);
    }

    // ── golden / conformance against real V20 exports ─────────────────────────

    [Fact]
    public void Golden_ContactCoil_MatchesStartupObExport()
    {
        // Sample 01: Powerrail -> Coil.in, GlobalVariable operand NTP_Initialise.
        var sample = XDocument.Load(Path.Combine(SamplesDir(), "01-contact-coil-startup-ob.xml"));
        var golden = sample.Descendants(Ns + "FlgNet").First();
        var built  = Net(new LadNetwork { Outputs = { Coil("NTP_Initialise") } });
        Assert.Equal(Edges(golden), Edges(built));
    }

    [Fact]
    public void Conformance_EveryPartPinEmittedExistsInRealExports()
    {
        // Pin vocabulary harvested from every sample: "PartName.Pin".
        var known = new HashSet<string>();
        foreach (var f in Directory.GetFiles(SamplesDir(), "*.xml"))
            foreach (var flg in XDocument.Load(f).Descendants(Ns + "FlgNet"))
            {
                var names = flg.Element(Ns + "Parts")?.Elements(Ns + "Part")
                    .ToDictionary(p => p.Attribute("UId")!.Value, p => p.Attribute("Name")!.Value)
                    ?? new Dictionary<string, string>();
                foreach (var nc in flg.Descendants(Ns + "NameCon"))
                    if (names.TryGetValue(nc.Attribute("UId")!.Value, out var part))
                        known.Add(part + "." + nc.Attribute("Name")!.Value);
            }

        var net = new LadNetwork
        {
            Elements =
            {
                new LadElement { Type = "branch", Branches = new() { new() { Contact("A") }, new() { Contact("B", true) } } },
                new LadElement { Type = "ton", Instance = "#T", Pt = "T#1s" },
            },
            Outputs = { Coil("C"), Coil("S", "scoil"), Coil("R", "rcoil") },
        };
        var flgNet = Net(net);
        var idToName = flgNet.Element(Ns + "Parts")!.Elements(Ns + "Part")
            .ToDictionary(p => p.Attribute("UId")!.Value, p => p.Attribute("Name")!.Value);
        var emitted = flgNet.Descendants(Ns + "NameCon")
            .Where(nc => idToName.ContainsKey(nc.Attribute("UId")!.Value))
            .Select(nc => idToName[nc.Attribute("UId")!.Value] + "." + nc.Attribute("Name")!.Value)
            .Distinct().ToList();

        var unknown = emitted.Where(e => !known.Contains(e)).ToList();
        Assert.True(unknown.Count == 0, "Pins not seen in any real V20 export: " + string.Join(", ", unknown));
    }

    // ── block document ────────────────────────────────────────────────────────

    [Fact]
    public void BlockXml_IsLadWithOneCompileUnitPerNetwork_AndNamespaceIsChildElement()
    {
        var xml = Block("FB", null, null, new[] { SealIn(), SealIn() });
        var doc = XDocument.Parse(xml);
        Assert.Equal(2, doc.Descendants("SW.Blocks.CompileUnit").Count());
        var fb = doc.Descendants("SW.Blocks.FB").Single();
        Assert.Equal("LAD", fb.Element("AttributeList")!.Element("ProgrammingLanguage")!.Value);
        Assert.NotNull(fb.Element("AttributeList")!.Element("Namespace")); // child element, not an XML attribute (CLAUDE.md)
        Assert.Null(fb.Attribute("Namespace"));
        Assert.DoesNotContain("MultilingualText", xml); // no culture => no culture-mismatch risk
    }

    [Fact]
    public void BlockXml_IdsAreUnique_WithCulture()
    {
        var xml = Block("FC", 5, null, new[] { SealIn(), SealIn(), SealIn() }, "en-GB");
        var ids = XDocument.Parse(xml).Descendants().Where(e => e.Attribute("ID") is not null)
            .Select(e => e.Attribute("ID")!.Value).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Interface_MembersAreEmitted()
    {
        var iface = new LadInterface { Input = { new LadMember { Name = "Start", Datatype = "Bool" } } };
        Assert.Contains("Member Name=\"Start\" Datatype=\"Bool\"", Block("FB", null, iface, new[] { SealIn() }));
    }

    // ── pre-flight validation ─────────────────────────────────────────────────

    [Fact]
    public void Validator_AcceptsBuilderOutput()
        => Assert.Empty(LadValidator.Validate(Block("FB", null, null, new[] { SealIn() })));

    [Fact]
    public void Validator_RejectsMalformedXml()
        => Assert.Contains(LadValidator.Validate("<a><b></a>"), e => e.Contains("not well-formed"));

    [Fact]
    public void Validator_RejectsDuplicateUId()
    {
        var doc = XDocument.Parse(Block("FB", null, null, new[] { SealIn() }));
        var parts = doc.Descendants(Ns + "Part").ToList();
        parts[1].SetAttributeValue("UId", parts[0].Attribute("UId")!.Value);
        Assert.Contains(LadValidator.Validate(doc.ToString()), e => e.Contains("used twice"));
    }

    [Fact]
    public void Validator_RejectsDanglingWireEndpoint()
    {
        var doc = XDocument.Parse(Block("FB", null, null, new[] { SealIn() }));
        doc.Descendants(Ns + "NameCon").First().SetAttributeValue("UId", "9999");
        Assert.Contains(LadValidator.Validate(doc.ToString()), e => e.Contains("9999"));
    }

    // ── model errors are clear ────────────────────────────────────────────────

    [Theory]
    [InlineData("coil", "outputs")]
    [InlineData("nonsense", "Unsupported element type")]
    public void BadElementType_GivesClearMessage(string type, string expected)
    {
        var net = new LadNetwork { Elements = { new LadElement { Type = type, Operand = "X" } } };
        var ex = Assert.Throws<ArgumentException>(() => Net(net));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Branch_NeedsTwoNonEmptyPaths()
    {
        var one = new LadNetwork { Elements = { new LadElement { Type = "branch", Branches = new() { new() { Contact("A") } } } } };
        Assert.Throws<ArgumentException>(() => Net(one));
        var empty = new LadNetwork { Elements = { new LadElement { Type = "branch", Branches = new() { new() { Contact("A") }, new() } } } };
        Assert.Throws<ArgumentException>(() => Net(empty));
    }

    [Fact]
    public void UnsupportedBlockType_IsRejected()
        => Assert.Throws<ArgumentException>(() => Block("UDT", null, null, new[] { SealIn() }));
}
