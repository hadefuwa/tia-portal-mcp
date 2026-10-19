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
    /// <summary>compare (eq/ne/gt/ge/lt/le) only: the right-hand operand; <see cref="Operand"/> is the left.</summary>
    public string? Operand2 { get; set; }
    /// <summary>compare only: operand type (Int, DInt, Real, ...). Default Int.</summary>
    public string? DataType { get; set; }
    /// <summary>move only: value or tag to copy into <see cref="Operand"/> (the destination).</summary>
    public string? Source { get; set; }
    /// <summary>call only: block name, e.g. "Mixer".</summary>
    public string? Block { get; set; }
    /// <summary>call only: FB or FC. For an FB, <see cref="Instance"/> is its instance DB ("Mixer_DB") or "#multi".</summary>
    public string? BlockType { get; set; }
    /// <summary>call only: parameters to wire. Each needs name, section, datatype and operand.</summary>
    public List<LadParam>? Parameters { get; set; }
    /// <summary>add/sub/mul/div/mod: the input values (in1..inN); <see cref="Operand"/> is the result destination.</summary>
    public List<string>? Operands { get; set; }
    /// <summary>norm_x/scale_x: result type (Real default); <see cref="DataType"/> is the input type.</summary>
    public string? DestType { get; set; }
    /// <summary>counters: preset value (PV).</summary>
    public string? Pv { get; set; }
    /// <summary>sr/rs/ctu/ctd: the second power path, starting at the power rail. sr: reset path; rs: set path; ctu: reset path (R); ctd: load path (LD).</summary>
    public List<LadElement>? Other { get; set; }
    /// <summary>part only: instruction name exactly as TIA exports it, e.g. "WR_SYS_T".</summary>
    public string? Name { get; set; }
    /// <summary>part only: instruction version, e.g. "1.0".</summary>
    public string? Version { get; set; }
    /// <summary>part only: template values, e.g. {"date_type":"DTL"}. "Card" is written as a Cardinality, anything else as a Type.</summary>
    public Dictionary<string, string>? Templates { get; set; }
    /// <summary>part only: input pin name -> operand.</summary>
    public Dictionary<string, string>? InPins { get; set; }
    /// <summary>part only: output pin name -> destination operand.</summary>
    public Dictionary<string, string>? OutPins { get; set; }
    /// <summary>part only: true if the box has an eno that power flow continues from. Default false (terminal).</summary>
    public bool Eno { get; set; }
}

public sealed class LadParam
{
    public string Name     { get; set; } = "";
    /// <summary>Input, Output or InOut.</summary>
    public string Section  { get; set; } = "Input";
    public string Datatype { get; set; } = "Bool";
    public string Operand  { get; set; } = "";
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

        if (ctx.Terminated && net.Outputs.Count > 0)
            throw new ArgumentException("a network ending in a terminal box (move, add, sub, ...) cannot have outputs; use a separate network.");

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
            if (ctx.Terminated)
                throw new ArgumentException($"nothing can follow a terminal box such as move/add ({where}); put it last, with no outputs after it. Use a separate network.");
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
                case "eq": case "ne": case "gt": case "ge": case "lt": case "le":
                {
                    var uid = ctx.NewUId();
                    var name = char.ToUpperInvariant(e.Type[0]) + e.Type.Substring(1).ToLowerInvariant();
                    ctx.Parts.Add(new XElement(FlgNs + "Part", new XAttribute("Name", name), new XAttribute("UId", uid),
                        new XElement(FlgNs + "TemplateValue",
                            new XAttribute("Name", "SrcType"), new XAttribute("Type", "Type"),
                            string.IsNullOrWhiteSpace(e.DataType) ? "Int" : e.DataType)));
                    ctx.Connect(cur, uid, "pre");
                    ctx.ConnectOperand(RequireOperand(e), uid, "in1");
                    ctx.ConnectOperand(string.IsNullOrWhiteSpace(e.Operand2)
                        ? throw new ArgumentException($"{e.Type} requires 'operand2' (the right-hand value).") : e.Operand2!, uid, "in2");
                    cur = Endpoint.Pin(uid, "out");
                    break;
                }
                case "pbox": case "nbox":
                {
                    // Edge detect. The operand is the edge-memory bit (a Bool that is not used anywhere else).
                    var uid = ctx.NewUId();
                    ctx.Parts.Add(new XElement(FlgNs + "Part",
                        new XAttribute("Name", e.Type.Equals("pbox", StringComparison.OrdinalIgnoreCase) ? "PBox" : "NBox"),
                        new XAttribute("UId", uid)));
                    ctx.Connect(cur, uid, "in");
                    ctx.ConnectOperand(RequireOperand(e), uid, "bit");
                    cur = Endpoint.Pin(uid, "out");
                    break;
                }
                case "move":
                {
                    if (string.IsNullOrWhiteSpace(e.Source))
                        throw new ArgumentException("move requires 'source' (value or tag) and 'operand' (destination).");
                    var uid = ctx.NewUId();
                    ctx.Parts.Add(new XElement(FlgNs + "Part",
                        new XAttribute("Name", "Move"), new XAttribute("UId", uid), new XAttribute("DisabledENO", "true"),
                        new XElement(FlgNs + "TemplateValue",
                            new XAttribute("Name", "Card"), new XAttribute("Type", "Cardinality"), 1)));
                    ctx.Connect(cur, uid, "en");
                    ctx.ConnectOperand(e.Source!, uid, "in");
                    ctx.ConnectOperandOut(uid, "out1", RequireOperand(e));
                    ctx.Terminated = true; // DisabledENO: Move has no eno, so no power flow continues from it
                    cur = Endpoint.Power;
                    break;
                }
                case "call":
                {
                    if (string.IsNullOrWhiteSpace(e.Block))
                        throw new ArgumentException("call requires 'block' (the name of the FB or FC).");
                    var bt = (e.BlockType ?? "").ToUpperInvariant();
                    if (bt is not ("FB" or "FC"))
                        throw new ArgumentException("call requires 'blockType': FB or FC.");
                    if (bt == "FB" && string.IsNullOrWhiteSpace(e.Instance))
                        throw new ArgumentException("an FB call requires 'instance' (instance DB name, or \"#multi\" for a multi-instance).");
                    var uid = ctx.NewUId();
                    var info = new XElement(FlgNs + "CallInfo", new XAttribute("Name", e.Block!), new XAttribute("BlockType", bt));
                    if (bt == "FB") info.Add(ctx.InstanceElement(e.Instance!));
                    var ps = e.Parameters ?? new List<LadParam>();
                    foreach (var p in ps)
                    {
                        if (string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.Operand))
                            throw new ArgumentException("every call parameter needs 'name' and 'operand'.");
                        if (!p.Section.Equals("Input", StringComparison.OrdinalIgnoreCase) &&
                            !p.Section.Equals("Output", StringComparison.OrdinalIgnoreCase) &&
                            !p.Section.Equals("InOut", StringComparison.OrdinalIgnoreCase))
                            throw new ArgumentException($"call parameter '{p.Name}': section must be Input, Output or InOut.");
                        info.Add(new XElement(FlgNs + "Parameter",
                            new XAttribute("Name", p.Name), new XAttribute("Section", p.Section), new XAttribute("Type", p.Datatype)));
                    }
                    ctx.Parts.Add(new XElement(FlgNs + "Call", new XAttribute("UId", uid), info));
                    ctx.Connect(cur, uid, "en");
                    foreach (var p in ps)
                    {
                        if (p.Section.Equals("Output", StringComparison.OrdinalIgnoreCase))
                            ctx.ConnectOperandOut(uid, p.Name, p.Operand);
                        else
                            ctx.ConnectOperand(p.Operand, uid, p.Name);
                    }
                    cur = Endpoint.Pin(uid, "eno");
                    break;
                }
                case "add": case "mul": case "sub": case "div": case "mod":
                {
                    // Arithmetic boxes are DisabledENO (no eno), so like move they end the rung.
                    var t = e.Type.ToLowerInvariant();
                    var ops = e.Operands ?? new List<string>();
                    bool nary = t is "add" or "mul";
                    if (nary ? ops.Count < 2 : ops.Count != 2)
                        throw new ArgumentException($"{t} requires 'operands' with {(nary ? "2 or more values" : "exactly 2 values")} and 'operand' (the result destination).");
                    var content = new List<XElement>();
                    if (nary) content.Add(Template("Card", "Cardinality", ops.Count.ToString()));
                    content.Add(new XElement(FlgNs + "AutomaticTyped", new XAttribute("Name", "SrcType")));
                    EmitBox(ctx, cur, char.ToUpperInvariant(t[0]) + t.Substring(1), null, true, content, null, "en",
                        ops.Select((o, i) => ("in" + (i + 1), o)).ToList(), new() { ("out", RequireOperand(e)) }, null);
                    ctx.Terminated = true;
                    cur = Endpoint.Power;
                    break;
                }
                case "norm_x": case "scale_x":
                {
                    // Pins en/eno/min/value/max/out. min/value/max come from 'inPins'; 'operand' is the result destination.
                    var pins = e.InPins ?? new Dictionary<string, string>();
                    foreach (var need in new[] { "min", "value", "max" })
                        if (!pins.ContainsKey(need)) throw new ArgumentException($"{e.Type} requires inPins.{need}.");
                    bool norm = e.Type.Equals("norm_x", StringComparison.OrdinalIgnoreCase);
                    var content = new List<XElement>
                    {
                        Template("SrcType",  "Type", string.IsNullOrWhiteSpace(e.DataType) ? (norm ? "Int" : "Real") : e.DataType!),
                        Template("DestType", "Type", string.IsNullOrWhiteSpace(e.DestType) ? "Real" : e.DestType!),
                    };
                    cur = EmitBox(ctx, cur, norm ? "Normalize" : "Scale_X", null, true, content, null, "en",
                        new() { ("min", pins["min"]), ("value", pins["value"]), ("max", pins["max"]) },
                        new() { ("out", RequireOperand(e)) }, "eno");
                    break;
                }
                case "sr": case "rs":
                {
                    // Flip-flop: the preceding rung flow drives the first input, 'other' (from the power rail) the second.
                    bool sr = e.Type.Equals("sr", StringComparison.OrdinalIgnoreCase);
                    if (e.Other is null || e.Other.Count == 0)
                        throw new ArgumentException($"{e.Type} requires 'other': the {(sr ? "reset" : "set")} path (elements starting at the power rail).");
                    var second = EmitSeries(ctx, e.Other, Endpoint.Power, $"{where}.{e.Type}.other");
                    var uid = ctx.NewUId();
                    ctx.Parts.Add(new XElement(FlgNs + "Part", new XAttribute("Name", sr ? "Sr" : "Rs"), new XAttribute("UId", uid)));
                    ctx.Connect(cur, uid, sr ? "s" : "r");
                    ctx.Connect(second, uid, sr ? "r1" : "s1");
                    ctx.ConnectOperand(RequireOperand(e), uid, "operand");
                    cur = Endpoint.Pin(uid, "q");
                    break;
                }
                case "ctu": case "ctd":
                {
                    bool up = e.Type.Equals("ctu", StringComparison.OrdinalIgnoreCase);
                    if (string.IsNullOrWhiteSpace(e.Instance)) throw new ArgumentException($"{e.Type} requires 'instance' (\"#Cnt\" multi-instance or an instance DB name).");
                    if (string.IsNullOrWhiteSpace(e.Pv))       throw new ArgumentException($"{e.Type} requires 'pv' (preset value).");
                    if (e.Other is null || e.Other.Count == 0) throw new ArgumentException($"{e.Type} requires 'other': the {(up ? "reset (R)" : "load (LD)")} path (elements starting at the power rail).");
                    var second = EmitSeries(ctx, e.Other, Endpoint.Power, $"{where}.{e.Type}.other");
                    var content = new List<XElement>
                    {
                        Template("value_type", "Type", string.IsNullOrWhiteSpace(e.DataType) ? "Int" : e.DataType!),
                    };
                    var uid = ctx.NewUId();
                    var part = new XElement(FlgNs + "Part", new XAttribute("Name", up ? "CTU" : "CTD"),
                        new XAttribute("Version", "1.0"), new XAttribute("UId", uid), ctx.InstanceElement(e.Instance!), content);
                    ctx.Parts.Add(part);
                    ctx.Connect(cur, uid, up ? "CU" : "CD");
                    ctx.Connect(second, uid, up ? "R" : "LD");
                    ctx.ConnectOperand(e.Pv!, uid, "PV");
                    ctx.OpenOutput(uid, "CV");
                    cur = Endpoint.Pin(uid, "Q"); // TIA re-exports CTU/CTD output as Q (QU/QD are CTUD)
                    break;
                }
                case "part":
                {
                    // Escape hatch for system functions (WR_SYS_T, WWW, ...). Pin names must be exactly TIA's;
                    // copy them from a real export. Terminal unless 'eno' is true.
                    if (string.IsNullOrWhiteSpace(e.Name)) throw new ArgumentException("part requires 'name' (the instruction name exactly as TIA exports it).");
                    var content = (e.Templates ?? new()).Select(kv =>
                        Template(kv.Key, kv.Key == "Card" ? "Cardinality" : "Type", kv.Value)).ToList();
                    cur = EmitBox(ctx, cur, e.Name!, e.Version, false, content, e.Instance, "en",
                        (e.InPins ?? new()).Select(kv => (kv.Key, kv.Value)).ToList(),
                        (e.OutPins ?? new()).Select(kv => (kv.Key, kv.Value)).ToList(), e.Eno ? "eno" : null);
                    if (!e.Eno) ctx.Terminated = true;
                    break;
                }
                case "ton": case "tof": case "tp":
                {
                    var timer = e.Type.ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(e.Instance))
                        throw new ArgumentException($"{e.Type} requires 'instance' (\"#Tmr\" for an FB multi-instance, or an instance DB name).");
                    if (string.IsNullOrWhiteSpace(e.Pt))
                        throw new ArgumentException($"{e.Type} requires 'pt' (preset time, e.g. \"T#5s\").");
                    var uid = ctx.NewUId();
                    var inst = ctx.InstanceElement(e.Instance!);
                    ctx.Parts.Add(new XElement(FlgNs + "Part",
                        new XAttribute("Name", timer), new XAttribute("Version", "1.0"), new XAttribute("UId", uid),
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
                        if (ctx.Terminated)
                            throw new ArgumentException($"a terminal box (move, add, ...) cannot be inside a branch ({where}.branch[{i}]); it has no power-flow output to merge.");
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
                        $"Unsupported element type '{e.Type}' ({where}). Supported elements: contact, eq/ne/gt/ge/lt/le, pbox, nbox, ton, tof, tp, ctu, ctd, sr, rs, branch, call, move, add/sub/mul/div/mod, norm_x, scale_x, part; outputs: coil, scoil, rcoil.");
            }
        }
        return cur;
    }

    private static XElement Template(string name, string type, string value) =>
        new XElement(FlgNs + "TemplateValue", new XAttribute("Name", name), new XAttribute("Type", type), value);

    /// <summary>
    /// Emits one box: the part, power flow into <paramref name="flowIn"/>, value inputs, value outputs, and
    /// returns the endpoint power flow continues from (<paramref name="flowOut"/>, or the rail if the box is terminal).
    /// </summary>
    private static Endpoint EmitBox(Ctx ctx, Endpoint cur, string name, string? version, bool disabledEno,
        List<XElement> content, string? instance, string flowIn,
        List<(string Pin, string Operand)> ins, List<(string Pin, string Operand)> outs, string? flowOut)
    {
        var uid = ctx.NewUId();
        var part = new XElement(FlgNs + "Part", new XAttribute("Name", name));
        if (version is not null) part.Add(new XAttribute("Version", version));
        part.Add(new XAttribute("UId", uid));
        if (disabledEno) part.Add(new XAttribute("DisabledENO", "true"));
        if (!string.IsNullOrWhiteSpace(instance)) part.Add(ctx.InstanceElement(instance!));
        part.Add(content);
        ctx.Parts.Add(part);
        ctx.Connect(cur, uid, flowIn);
        foreach (var (pin, op) in ins)  ctx.ConnectOperand(op, uid, pin);
        foreach (var (pin, op) in outs) ctx.ConnectOperandOut(uid, pin, op);
        return flowOut is null ? Endpoint.Power : Endpoint.Pin(uid, flowOut);
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

        public bool Terminated { get; set; }

        public int NewUId() => ++_uid;

        /// <summary>Output pin -> destination operand (Move.out1, Call output params): source first, then IdentCon.</summary>
        public void ConnectOperandOut(int srcUId, string srcPin, string operand)
        {
            var access = NewAccess(operand);
            var id = int.Parse(access.Attribute("UId")!.Value);
            Accesses.Add(access);
            var key = $"D{srcUId}:{srcPin}";
            _wires[key] = (new XElement(FlgNs + "NameCon", new XAttribute("UId", srcUId), new XAttribute("Name", srcPin)),
                           new List<XElement> { new XElement(FlgNs + "IdentCon", new XAttribute("UId", id)) });
            _wireOrder.Add(key);
        }

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
