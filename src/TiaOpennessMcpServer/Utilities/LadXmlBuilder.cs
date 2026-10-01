using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TiaOpennessMcpServer.Utilities;

// ── Model ─────────────────────────────────────────────────────────────────────
// The model describes a rung; it never contains UIds or wires. The builder owns those.

/// <summary>
/// One LAD element. <see cref="Type"/> is one of:
/// contact, coil, scoil, rcoil, ton, branch.
/// </summary>
public sealed class LadElement
{
    public string  Type     { get; set; } = "";
    /// <summary>Tag operand: "Motor", "\"DB\".Member", "#localVar".</summary>
    public string? Operand  { get; set; }
    /// <summary>contact only: true = normally closed.</summary>
    public bool    Negated  { get; set; }
    /// <summary>ton only: timer instance. "#Tmr" = multi-instance in an FB, otherwise an instance DB name.</summary>
    public string? Instance { get; set; }
    /// <summary>ton only: preset time, e.g. "T#5s", or a "#local"/tag holding a Time.</summary>
    public string? Pt       { get; set; }
    /// <summary>branch only: parallel paths, each a series of elements.</summary>
    public List<List<LadElement>>? Branches { get; set; }
}

public sealed class LadNetwork
{
    public string? Title   { get; set; }
    public string? Comment { get; set; }
    /// <summary>Series logic left to right: contacts, timers, branches.</summary>
    public List<LadElement> Elements { get; set; } = new();
    /// <summary>Terminal coils, all driven by the rung's final power flow.</summary>
    public List<LadElement> Outputs  { get; set; } = new();
}

public sealed class LadMember
{
    public string Name     { get; set; } = "";
    public string Datatype { get; set; } = "Bool";
}

public sealed class LadInterface
{
    public List<LadMember> Input    { get; set; } = new();
    public List<LadMember> Output   { get; set; } = new();
    public List<LadMember> InOut    { get; set; } = new();
    public List<LadMember> Static   { get; set; } = new();
    public List<LadMember> Temp     { get; set; } = new();
    public List<LadMember> Constant { get; set; } = new();
}

public sealed class LadValidationException : Exception
{
    public IReadOnlyList<string> Errors { get; }
    public LadValidationException(IReadOnlyList<string> errors)
        : base("LAD pre-flight validation failed:\n - " + string.Join("\n - ", errors))
        => Errors = errors;
}

// ── Builder ───────────────────────────────────────────────────────────────────

/// <summary>
/// Emits SimaticML <c>FlgNet</c> (V20, v5 schema) for LAD networks. Element and pin names
/// follow the real exports in docs/lad-samples/.
/// </summary>
public static class LadXmlBuilder
{
    public static readonly XNamespace FlgNs  = "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5";
    public static readonly XNamespace IfaceNs = "http://www.siemens.com/automation/Openness/SW/Interface/v5";

    private const int FirstUId = 21; // matches TIA's own exports

    /// <summary>Builds the <c>&lt;FlgNet&gt;</c> element for one network.</summary>
    public static XElement BuildFlgNet(LadNetwork net)
    {
        var ctx = new Ctx();
        var cur = Endpoint.Power;

        cur = EmitSeries(ctx, net.Elements, cur, "elements");

        if (net.Outputs.Count == 0 && net.Elements.Count == 0)
            throw new ArgumentException("A network needs at least one element or output.");

        foreach (var o in net.Outputs)
        {
            var part = o.Type.ToLowerInvariant() switch
            {
                "coil"  => "Coil",
                "scoil" => "SCoil",
                "rcoil" => "RCoil",
                _ => throw new ArgumentException(
                    $"Output element type '{o.Type}' is not supported; use coil, scoil or rcoil."),
            };
            var uid = ctx.NewUId();
            ctx.Parts.Add(new XElement(FlgNs + "Part", new XAttribute("Name", part), new XAttribute("UId", uid)));
            ctx.Connect(cur, uid, "in");
            ctx.ConnectOperand(RequireOperand(o), uid, "operand");
        }

        return ctx.ToFlgNet();
    }

    /// <summary>Emits a series of elements; returns the endpoint carrying power flow afterwards.</summary>
    private static Endpoint EmitSeries(Ctx ctx, List<LadElement> items, Endpoint cur, string where)
    {
        foreach (var e in items)
        {
            switch (e.Type.ToLowerInvariant())
            {
                case "contact":
                {
                    var uid  = ctx.NewUId();
                    var part = new XElement(FlgNs + "Part", new XAttribute("Name", "Contact"), new XAttribute("UId", uid));
                    if (e.Negated) part.Add(new XElement(FlgNs + "Negated", new XAttribute("Name", "operand")));
                    ctx.Parts.Add(part);
                    ctx.Connect(cur, uid, "in");
                    ctx.ConnectOperand(RequireOperand(e), uid, "operand");
                    cur = Endpoint.Pin(uid, "out");
                    break;
                }
                case "ton":
                {
                    if (string.IsNullOrWhiteSpace(e.Instance))
                        throw new ArgumentException("ton requires 'instance' (\"#Tmr\" for an FB multi-instance, or an instance DB name).");
                    if (string.IsNullOrWhiteSpace(e.Pt))
                        throw new ArgumentException("ton requires 'pt' (preset time, e.g. \"T#5s\").");
                    var uid = ctx.NewUId();
                    var inst = ctx.InstanceElement(e.Instance!);
                    ctx.Parts.Add(new XElement(FlgNs + "Part",
                        new XAttribute("Name", "TON"), new XAttribute("Version", "1.0"), new XAttribute("UId", uid),
                        inst,
                        new XElement(FlgNs + "TemplateValue",
                            new XAttribute("Name", "time_type"), new XAttribute("Type", "Type"), "Time")));
                    ctx.Connect(cur, uid, "IN");
                    ctx.ConnectOperand(e.Pt!, uid, "PT");
                    ctx.OpenOutput(uid, "ET");
                    cur = Endpoint.Pin(uid, "Q");
                    break;
                }
                case "branch":
                {
                    if (e.Branches is null || e.Branches.Count < 2)
                        throw new ArgumentException("branch requires at least two 'branches'.");
                    var outs = new List<Endpoint>();
                    for (int i = 0; i < e.Branches.Count; i++)
                    {
                        if (e.Branches[i].Count == 0)
                            throw new ArgumentException($"branch path {i + 1} is empty; an empty path is a bare wire, which LAD cannot express here.");
                        outs.Add(EmitSeries(ctx, e.Branches[i], cur, $"{where}.branch[{i}]"));
                    }
                    var uid = ctx.NewUId();
                    ctx.Parts.Add(new XElement(FlgNs + "Part",
                        new XAttribute("Name", "O"), new XAttribute("UId", uid),
                        new XElement(FlgNs + "TemplateValue",
                            new XAttribute("Name", "Card"), new XAttribute("Type", "Cardinality"), outs.Count)));
                    for (int i = 0; i < outs.Count; i++)
                        ctx.Connect(outs[i], uid, "in" + (i + 1));
                    cur = Endpoint.Pin(uid, "out");
                    break;
                }
                case "coil": case "scoil": case "rcoil":
                    throw new ArgumentException(
                        $"'{e.Type}' belongs in the network's 'outputs', not in 'elements' ({where}).");
                default:
                    throw new ArgumentException(
                        $"Unsupported element type '{e.Type}' ({where}). Supported: contact, ton, branch (elements); coil, scoil, rcoil (outputs).");
            }
        }
        return cur;
    }

    private static string RequireOperand(LadElement e) =>
        string.IsNullOrWhiteSpace(e.Operand)
            ? throw new ArgumentException($"{e.Type} requires an 'operand'.")
            : e.Operand!;

    // ── Document assembly ─────────────────────────────────────────────────────

    /// <summary>
    /// Full block document, mirroring <c>CreateSclBlockXml</c> but LAD: one CompileUnit per network.
    /// <paramref name="cultureName"/> (e.g. "en-GB") is only needed to emit network titles/comments; it
    /// MUST match the project's culture, so it is omitted by default (see CLAUDE.md, GlobalDB notes).
    /// </summary>
    public static string CreateLadBlockXml(
        string blockName, string blockType, int? blockNumber,
        LadInterface? iface, IReadOnlyList<LadNetwork> networks, string? cultureName = null)
    {
        if (string.IsNullOrWhiteSpace(blockName)) throw new ArgumentException("blockName is required.");
        var type = blockType.ToUpperInvariant();
        if (type is not ("FB" or "FC" or "OB"))
            throw new ArgumentException($"blockType '{blockType}' is not supported for LAD; use FB, FC or OB.");
        if (networks.Count == 0) throw new ArgumentException("At least one network is required.");
        iface ??= new LadInterface();

        // Interface sections. FB: no Return; FC: Return + no Static; OB: neither Static nor Return.
        XElement Section(string name, List<LadMember> members) =>
            new XElement(IfaceNs + "Section", new XAttribute("Name", name),
                members.Select(m => new XElement(IfaceNs + "Member",
                    new XAttribute("Name", m.Name), new XAttribute("Datatype", m.Datatype))));

        // Confirmed against live V20: an OB only has Input/Temp/Constant; TIA rejects an Output (or InOut) section.
        if (type == "OB" && (iface.Output.Count > 0 || iface.InOut.Count > 0 || iface.Static.Count > 0))
            throw new ArgumentException("An OB interface can only have input, temp and constant members.");

        var sections = new List<XElement> { Section("Input", iface.Input) };
        if (type != "OB") { sections.Add(Section("Output", iface.Output)); sections.Add(Section("InOut", iface.InOut)); }
        if (type == "FB") sections.Add(Section("Static", iface.Static));
        sections.Add(Section("Temp", iface.Temp));
        sections.Add(Section("Constant", iface.Constant));
        if (type == "FC")
            sections.Add(Section("Return", new List<LadMember> { new LadMember { Name = "Ret_Val", Datatype = "Void" } }));

        var attrs = new XElement("AttributeList",
            new XElement("AutoNumber", blockNumber.HasValue ? "false" : "true"),
            new XElement("Interface", new XElement(IfaceNs + "Sections", sections)),
            new XElement("MemoryLayout", "Optimized"),
            new XElement("Name", blockName),
            new XElement("Namespace"));
        if (blockNumber.HasValue) attrs.Add(new XElement("Number", blockNumber.Value));
        attrs.Add(new XElement("ProgrammingLanguage", "LAD"));
        if (type == "OB") attrs.Add(new XElement("SecondaryType", "ProgramCycle"));

        // Compile units. IDs are hex and must be unique across the whole document.
        int nextId = 1;
        string Id() => (nextId++).ToString("X");

        var objects = new XElement("ObjectList");
        foreach (var n in networks)
        {
            var cuObjects = new XElement("ObjectList");
            if (cultureName is not null)
            {
                cuObjects.Add(TextBlock(Id(), Id(), "Comment", cultureName, n.Comment));
                cuObjects.Add(TextBlock(Id(), Id(), "Title",   cultureName, n.Title));
            }
            objects.Add(new XElement("SW.Blocks.CompileUnit",
                new XAttribute("ID", Id()), new XAttribute("CompositionName", "CompileUnits"),
                new XElement("AttributeList",
                    new XElement("NetworkSource", BuildFlgNet(n)),
                    new XElement("ProgrammingLanguage", "LAD")),
                cuObjects));
        }

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("Document",
                new XElement("Engineering", new XAttribute("version", "V20")),
                new XElement($"SW.Blocks.{type}", new XAttribute("ID", "0"), attrs, objects)));

        var xml = doc.Declaration + "\n" + doc.ToString();

        var errors = LadValidator.Validate(xml);
        if (errors.Count > 0) throw new LadValidationException(errors);
        return xml;
    }

    private static XElement TextBlock(string outerId, string itemId, string composition, string culture, string? text) =>
        new XElement("MultilingualText", new XAttribute("ID", outerId), new XAttribute("CompositionName", composition),
            new XElement("ObjectList",
                new XElement("MultilingualTextItem", new XAttribute("ID", itemId), new XAttribute("CompositionName", "Items"),
                    new XElement("AttributeList",
                        new XElement("Culture", culture),
                        string.IsNullOrEmpty(text) ? new XElement("Text") : new XElement("Text", text)))));

    // ── Internals ─────────────────────────────────────────────────────────────

    private readonly struct Endpoint
    {
        public bool   IsPower { get; }
        public int    UId     { get; }
        public string Name    { get; }
        private Endpoint(bool p, int u, string n) { IsPower = p; UId = u; Name = n; }
        public static Endpoint Power => new(true, 0, "");
        public static Endpoint Pin(int uid, string name) => new(false, uid, name);
        public string Key => IsPower ? "P" : $"{UId}:{Name}";
        public XElement ToXml() => IsPower
            ? new XElement(FlgNs + "Powerrail")
            : new XElement(FlgNs + "NameCon", new XAttribute("UId", UId), new XAttribute("Name", Name));
    }

    private sealed class Ctx
    {
        private int _uid = FirstUId - 1;
        public List<XElement> Accesses { get; } = new();
        public List<XElement> Parts    { get; } = new();
        // One wire per source: a source driving several pins is a single <Wire> (as in TIA's exports).
        private readonly Dictionary<string, (XElement Src, List<XElement> Dests)> _wires = new();
        private readonly List<string> _wireOrder = new();

        public int NewUId() => ++_uid;

        public void Connect(Endpoint src, int destUId, string destPin)
        {
            if (!_wires.TryGetValue(src.Key, out var w))
            {
                w = (src.ToXml(), new List<XElement>());
                _wires[src.Key] = w;
                _wireOrder.Add(src.Key);
            }
            w.Dests.Add(new XElement(FlgNs + "NameCon", new XAttribute("UId", destUId), new XAttribute("Name", destPin)));
        }

        public void ConnectOperand(string operand, int destUId, string destPin)
        {
            var access = NewAccess(operand);
            var id = int.Parse(access.Attribute("UId")!.Value);
            Accesses.Add(access);
            var key = $"I{id}";
            _wires[key] = (new XElement(FlgNs + "IdentCon", new XAttribute("UId", id)),
                           new List<XElement> { new XElement(FlgNs + "NameCon", new XAttribute("UId", destUId), new XAttribute("Name", destPin)) });
            _wireOrder.Add(key);
        }

        /// <summary>Output pin left unconnected on purpose (TON.ET) — TIA writes an OpenCon for it.</summary>
        public void OpenOutput(int srcUId, string srcPin)
        {
            var open = NewUId();
            var key = $"O{srcUId}:{srcPin}";
            _wires[key] = (new XElement(FlgNs + "NameCon", new XAttribute("UId", srcUId), new XAttribute("Name", srcPin)),
                           new List<XElement> { new XElement(FlgNs + "OpenCon", new XAttribute("UId", open)) });
            _wireOrder.Add(key);
        }

        public XElement InstanceElement(string instance)
        {
            var uid = NewUId();
            if (instance.StartsWith("#"))
                return new XElement(FlgNs + "Instance",
                    new XAttribute("Scope", "LocalVariable"), new XAttribute("UId", uid),
                    new XElement(FlgNs + "Component", new XAttribute("Name", instance.Substring(1))));
            return new XElement(FlgNs + "Instance",
                new XAttribute("Scope", "GlobalVariable"), new XAttribute("UId", uid),
                new XElement(FlgNs + "Component", new XAttribute("Name", Unquote(instance))));
        }

        private XElement NewAccess(string operand)
        {
            var uid = NewUId();
            var op = operand.Trim();

            // Typed constant: T#5s, S5T#2s, DT#..., 16#FF ... (a non-empty prefix before '#').
            if (Regex.IsMatch(op, @"^[A-Za-z0-9]+#.+"))
                return new XElement(FlgNs + "Access", new XAttribute("Scope", "TypedConstant"), new XAttribute("UId", uid),
                    new XElement(FlgNs + "Constant", new XElement(FlgNs + "ConstantValue", op)));

            if (Regex.IsMatch(op, @"^-?\d+$"))
                return Literal(uid, "Int", op);
            if (Regex.IsMatch(op, @"^-?\d+\.\d+$"))
                return Literal(uid, "Real", op);
            if (op.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || op.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
                return Literal(uid, "Bool", op.ToLowerInvariant());

            bool local = op.StartsWith("#");
            var path = SplitSymbol(local ? op.Substring(1) : op);
            return new XElement(FlgNs + "Access",
                new XAttribute("Scope", local ? "LocalVariable" : "GlobalVariable"), new XAttribute("UId", uid),
                new XElement(FlgNs + "Symbol",
                    path.Select(p => new XElement(FlgNs + "Component", new XAttribute("Name", p)))));
        }

        private static XElement Literal(int uid, string type, string value) =>
            new XElement(FlgNs + "Access", new XAttribute("Scope", "LiteralConstant"), new XAttribute("UId", uid),
                new XElement(FlgNs + "Constant",
                    new XElement(FlgNs + "ConstantType", type),
                    new XElement(FlgNs + "ConstantValue", value)));

        public XElement ToFlgNet()
        {
            // Wire UIds are allocated last, after every part/access, so numbering stays stable.
            var wires = new XElement(FlgNs + "Wires");
            foreach (var key in _wireOrder)
            {
                var (src, dests) = _wires[key];
                wires.Add(new XElement(FlgNs + "Wire", new XAttribute("UId", NewUId()), src, dests));
            }
            return new XElement(FlgNs + "FlgNet",
                new XElement(FlgNs + "Parts", Accesses, Parts),
                wires);
        }
    }

    private static string Unquote(string s) =>
        s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"' ? s.Substring(1, s.Length - 2) : s;

    /// <summary>Splits <c>"DB".Struct.Member</c> into components, honouring quotes (names may hold spaces).</summary>
    internal static List<string> SplitSymbol(string s)
    {
        var parts = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQuote = false;
        foreach (var ch in s)
        {
            if (ch == '"') { inQuote = !inQuote; continue; }
            if (ch == '.' && !inQuote) { parts.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(ch);
        }
        parts.Add(sb.ToString());
        if (parts.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException($"Operand '{s}' is not a valid symbol.");
        return parts;
    }
}

// ── Pre-flight validation ─────────────────────────────────────────────────────

/// <summary>
/// Catches structural mistakes before TIA's importer turns them into opaque errors:
/// well-formedness, unique UIds per network, every wire endpoint resolving, no input pin driven twice.
/// </summary>
public static class LadValidator
{
    public static List<string> Validate(string xml)
    {
        var errors = new List<string>();
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (Exception ex) { errors.Add($"XML is not well-formed: {ex.Message}"); return errors; }

        int netIndex = 0;
        foreach (var flg in doc.Descendants(LadXmlBuilder.FlgNs + "FlgNet"))
        {
            netIndex++;
            var ns  = LadXmlBuilder.FlgNs;
            string where = $"network {netIndex}";

            var parts = flg.Element(ns + "Parts")?.Elements().ToList() ?? new();
            var wires = flg.Element(ns + "Wires")?.Elements(ns + "Wire").ToList() ?? new();

            // 1. UId uniqueness across parts, accesses, instances, wires, open connections.
            var seen = new Dictionary<string, string>();
            void Claim(XElement e, string kind)
            {
                var id = e.Attribute("UId")?.Value;
                if (id is null) { errors.Add($"{where}: <{e.Name.LocalName}> has no UId."); return; }
                if (seen.TryGetValue(id, out var prev))
                    errors.Add($"{where}: UId {id} used twice ({prev} and {kind}).");
                else seen[id] = kind;
            }
            foreach (var p in parts) Claim(p, p.Name.LocalName + (p.Attribute("Name") is { } a ? $" '{a.Value}'" : ""));
            foreach (var inst in parts.SelectMany(p => p.Descendants(ns + "Instance"))) Claim(inst, "Instance");
            foreach (var w in wires)
            {
                Claim(w, "Wire");
                foreach (var oc in w.Elements(ns + "OpenCon")) Claim(oc, "OpenCon");
            }

            // 2. Every IdentCon/NameCon resolves to a part/access/call.
            var targets = new HashSet<string>(parts.Select(p => p.Attribute("UId")?.Value ?? ""));
            var drivenInputs = new HashSet<string>();
            foreach (var w in wires)
            {
                var conns = w.Elements().ToList();
                if (conns.Count < 2)
                    errors.Add($"{where}: wire {w.Attribute("UId")?.Value} has fewer than two endpoints.");

                foreach (var c in conns.Where(c => c.Name.LocalName is "IdentCon" or "NameCon"))
                {
                    var id = c.Attribute("UId")?.Value ?? "";
                    if (!targets.Contains(id))
                        errors.Add($"{where}: wire {w.Attribute("UId")?.Value} references UId {id}, which is not a part or access.");
                }

                // 3. An input pin may be driven by only one wire. Sources: Powerrail, IdentCon, or the
                //    first NameCon when the wire has no Powerrail/IdentCon (output pin -> input pins).
                var first = conns.FirstOrDefault();
                bool srcIsFirst = first is not null;
                foreach (var d in conns.Skip(srcIsFirst ? 1 : 0).Where(c => c.Name.LocalName == "NameCon"))
                {
                    var key = $"{d.Attribute("UId")?.Value}:{d.Attribute("Name")?.Value}";
                    if (!drivenInputs.Add(key))
                        errors.Add($"{where}: pin {key} is driven by more than one wire.");
                }
            }

            // 4. Every Access must be consumed by a wire.
            var referenced = new HashSet<string>(wires.SelectMany(w => w.Elements(ns + "IdentCon"))
                .Select(c => c.Attribute("UId")?.Value ?? ""));
            foreach (var acc in parts.Where(p => p.Name.LocalName == "Access"))
                if (!referenced.Contains(acc.Attribute("UId")!.Value))
                    errors.Add($"{where}: Access UId {acc.Attribute("UId")!.Value} is not connected to anything.");
        }
        return errors;
    }
}
