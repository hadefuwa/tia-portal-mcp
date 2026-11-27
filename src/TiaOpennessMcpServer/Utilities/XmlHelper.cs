using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace TiaOpennessMcpServer.Utilities;

/// <summary>Helpers for reading/writing TIA Portal export XML (SimaticML format).</summary>
public static class XmlHelper
{
    private static readonly XmlWriterSettings PrettySettings = new()
    {
        Indent             = true,
        IndentChars        = "  ",
        NewLineChars       = "\n",
        Encoding           = new UTF8Encoding(false),
        OmitXmlDeclaration = false,
    };


    /// <summary>
    /// Generates SimaticML XML for a LAD block: one CompileUnit per network, UIds and wires
    /// auto-assigned. Mirrors <see cref="CreateSclBlockXml"/>; see <see cref="LadXmlBuilder"/>.
    /// Throws <see cref="LadValidationException"/> if the pre-flight check fails.
    /// </summary>
    public static string CreateLadBlockXml(
        string blockName, string blockType, int? blockNumber,
        LadInterface? iface, IReadOnlyList<LadNetwork> networks, string? cultureName = null)
        => LadXmlBuilder.CreateLadBlockXml(blockName, blockType, blockNumber, iface, networks, cultureName);

    /// <summary>
    /// Generates SimaticML XML for a GlobalDB by parsing VAR...END_VAR from SCL source.
    /// GlobalDB uses member declarations, not CompileUnits.
    /// </summary>
    public static string CreateGlobalDbXml(string blockName, int? blockNumber, string sclSource)
    {
        var number  = blockNumber.HasValue ? $" Number=\"{blockNumber}\"" : "";
        var autoNum = blockNumber.HasValue ? "false" : "true";

        var members     = ParseSclVarSection(sclSource);
        var startValues = ParseSclStartValues(sclSource);

        var memberXml = string.Join("\n", members.Select(m =>
        {
            var name = SecurityElement.Escape(m.Name);
            var type = SecurityElement.Escape(m.Type);
            if (startValues.TryGetValue(m.Name, out var sv))
                return $"              <Member Name=\"{name}\" Datatype=\"{type}\">\n                <StartValue>{SecurityElement.Escape(sv)}</StartValue>\n              </Member>";
            return $"              <Member Name=\"{name}\" Datatype=\"{type}\" />";
        }));

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Document>
              <Engineering version="{TiaVersion.Name}" />
              <SW.Blocks.GlobalDB ID="0"{number}>
                <AttributeList>
                  <AutoNumber>{autoNum}</AutoNumber>
                  <Interface>
                    <Sections xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5">
                      <Section Name="Static">
            {memberXml}
                      </Section>
                    </Sections>
                  </Interface>
                  <Name>{SecurityElement.Escape(blockName)}</Name>
                  <Namespace />
                  <ProgrammingLanguage>DB</ProgrammingLanguage>
                </AttributeList>
                <ObjectList />
              </SW.Blocks.GlobalDB>
            </Document>
            """;
    }

    private static IReadOnlyList<(string Name, string Type)> ParseSclVarSection(string sclSource)
    {
        var members = new List<(string, string)>();
        var rx = new System.Text.RegularExpressions.Regex(
            @"^\s+(\w+)\s*:\s*([\w\[\]\.""]+)\s*;",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        foreach (System.Text.RegularExpressions.Match m in rx.Matches(sclSource))
            members.Add((m.Groups[1].Value, m.Groups[2].Value));
        return members;
    }

    // Parses the BEGIN...END_DATA_BLOCK section for start value assignments, e.g.:
    //   Colour1 := '000100';
    //   ToggleCount := 50;
    private static IReadOnlyDictionary<string, string> ParseSclStartValues(string sclSource)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var beginMatch = System.Text.RegularExpressions.Regex.Match(
            sclSource, @"\bBEGIN\b(.*?)(?:END_DATA_BLOCK|$)",
            System.Text.RegularExpressions.RegexOptions.Singleline |
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!beginMatch.Success) return result;

        var body = beginMatch.Groups[1].Value;
        var rx = new System.Text.RegularExpressions.Regex(
            @"(\w+)\s*:=\s*(.+?)\s*;",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        foreach (System.Text.RegularExpressions.Match m in rx.Matches(body))
            result[m.Groups[1].Value] = m.Groups[2].Value.Trim();
        return result;
    }

    /// <summary>Generates SimaticML XML for an InstanceDB block.</summary>
    public static string CreateInstanceDbXml(string name, string instanceOfName, int? number)
    {
        var autoNum = number.HasValue ? "false" : "true";
        var numAttr = number.HasValue ? $"\n      <Number>{number}</Number>" : "";
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Document>
              <Engineering version="{TiaVersion.Name}" />
              <SW.Blocks.InstanceDB ID="0">
                <AttributeList>
                  <AutoNumber>{autoNum}</AutoNumber>
                  <InstanceOfName>{instanceOfName}</InstanceOfName>
                  <InstanceOfType>FB</InstanceOfType>
                  <Name>{name}</Name>
                  <Namespace />{numAttr}
                  <ProgrammingLanguage>DB</ProgrammingLanguage>
                </AttributeList>
                <ObjectList />   <!-- no MultilingualText: en-US would break an en-GB project -->

              </SW.Blocks.InstanceDB>
            </Document>
            """;
    }

    /// <summary>Generates SimaticML XML for a PLC tag table â€” used for import and batch-rename.</summary>
    public static string CreateTagTableXml(
        string tableName, IEnumerable<(string Name, string DataType, string Address, bool Accessible, bool Writable, string Comment)> tags)
    {
        var sb = new StringBuilder();
        int id = 2;
        foreach (var t in tags)
        {
            var comment = string.IsNullOrWhiteSpace(t.Comment) ? "<Text />" : $"<Text>{SecurityElement.Escape(t.Comment)}</Text>";
            sb.Append($"""

                  <SW.Tags.PlcTag ID="{id++}" CompositionName="Tags">
                    <AttributeList>
                      <DataTypeName>{SecurityElement.Escape(t.DataType)}</DataTypeName>
                      <ExternalAccessible>{t.Accessible.ToString().ToLowerInvariant()}</ExternalAccessible>
                      <ExternalVisible>true</ExternalVisible>
                      <ExternalWritable>{t.Writable.ToString().ToLowerInvariant()}</ExternalWritable>
                      <LogicalAddress>{SecurityElement.Escape(t.Address)}</LogicalAddress>
                      <Name>{SecurityElement.Escape(t.Name)}</Name>
                    </AttributeList>
                    <ObjectList>
                      <MultilingualText ID="{id++}" CompositionName="Comment">
                        <ObjectList>
                          <MultilingualTextItem ID="{id++}" CompositionName="Items">
                            <AttributeList>
                              <Culture>en-US</Culture>
                              {comment}
                            </AttributeList>
                          </MultilingualTextItem>
                        </ObjectList>
                      </MultilingualText>
                    </ObjectList>
                  </SW.Tags.PlcTag>
            """);
        }
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Document>
              <Engineering version="{TiaVersion.Name}" />
              <SW.Tags.PlcTagTable ID="0" CompositionName="TagTables">
                <AttributeList>
                  <Name>{SecurityElement.Escape(tableName)}</Name>
                </AttributeList>
                <ObjectList>{sb}
                </ObjectList>
              </SW.Tags.PlcTagTable>
            </Document>
            """;
    }

    public static string Prettify(string xmlContent)
    {
        var doc = XDocument.Parse(xmlContent);
        var sb  = new StringBuilder();
        using (var writer = XmlWriter.Create(sb, PrettySettings))
            doc.Save(writer);
        return sb.ToString();
    }
}
