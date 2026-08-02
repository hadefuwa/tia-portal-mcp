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

    /// <summary>Extracts the SCL source from a SimaticML block export XML.</summary>
    public static string? ExtractSclSource(string xmlContent)
    {
        try
        {
            var doc = XDocument.Parse(xmlContent);
            XNamespace ns = "http://www.siemens.com/automation/Openness/SW/Interface/v5";

            var sourceElement = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Source" &&
                                     e.Attribute("Name")?.Value == "BlockSource");

            if (sourceElement is null) return null;

            // TIA encodes SCL in base-64 within the Source element
            var rawValue = sourceElement.Value.Trim();
            if (rawValue.Length == 0) return null;

            var bytes = Convert.FromBase64String(rawValue);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Injects SCL source back into a SimaticML export document.</summary>
    public static string InjectSclSource(string xmlContent, string sclSource)
    {
        var doc = XDocument.Parse(xmlContent);

        var sourceElement = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "Source" &&
                                 e.Attribute("Name")?.Value == "BlockSource");

        if (sourceElement is null)
            throw new InvalidOperationException("No <Source Name=\"BlockSource\"> found in XML.");

        sourceElement.Value = Convert.ToBase64String(Encoding.UTF8.GetBytes(sclSource));

        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(sb, PrettySettings))
            doc.Save(writer);

        return sb.ToString();
    }

    /// <summary>Generates a minimal SimaticML skeleton for a new SCL block.</summary>
    public static string CreateSclBlockXml(
        string blockName, string blockType, int? blockNumber, string sclSource)
    {
        var number = blockNumber.HasValue ? $" Number=\"{blockNumber}\"" : "";
        var encodedSource = Convert.ToBase64String(Encoding.UTF8.GetBytes(sclSource));

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Document>
              <Engineering version="V20" />
              <SW.Blocks.{blockType} ID="0"{number}>
                <AttributeList>
                  <AutoNumber>false</AutoNumber>
                  <Name>{blockName}</Name>
                  <ProgrammingLanguage>SCL</ProgrammingLanguage>
                </AttributeList>
                <ObjectList>
                  <MultilingualText ID="1" CompositionName="Comment">
                    <ObjectList>
                      <MultilingualTextItem ID="2" CompositionName="Items">
                        <AttributeList>
                          <Culture>en-US</Culture>
                          <Text />
                        </AttributeList>
                      </MultilingualTextItem>
                    </ObjectList>
                  </MultilingualText>
                  <SW.Blocks.CompileUnit ID="3" CompositionName="CompileUnits">
                    <AttributeList>
                      <NetworkSource>
                        <FlgNet xmlns="http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v4">
                          <Parts />
                          <Wires />
                        </FlgNet>
                      </NetworkSource>
                      <ProgrammingLanguage>SCL</ProgrammingLanguage>
                    </AttributeList>
                  </SW.Blocks.CompileUnit>
                  <Section xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5" Name="Input" />
                  <Section xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5" Name="Output" />
                  <Section xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5" Name="InOut" />
                  <Section xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5" Name="Static" />
                  <Section xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5" Name="Temp" />
                  <Section xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5" Name="Constant" />
                  <Section xmlns="http://www.siemens.com/automation/Openness/SW/Interface/v5" Name="Return" />
                  <Source Name="BlockSource">{encodedSource}</Source>
                </ObjectList>
              </SW.Blocks.{blockType}>
            </Document>
            """;
    }

    /// <summary>
    /// Generates SimaticML XML for a GlobalDB by parsing VAR...END_VAR from SCL source.
    /// GlobalDB uses member declarations, not CompileUnits.
    /// </summary>
    public static string CreateGlobalDbXml(string blockName, int? blockNumber, string sclSource)
    {
        var number  = blockNumber.HasValue ? $" Number=\"{blockNumber}\"" : "";
        var autoNum = blockNumber.HasValue ? "false" : "true";

        var members = ParseSclVarSection(sclSource);
        var memberXml = string.Join("\n", members.Select(m =>
            $"              <Member Name=\"{SecurityElement.Escape(m.Name)}\" Datatype=\"{SecurityElement.Escape(m.Type)}\" />"));

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Document>
              <Engineering version="V20" />
              <SW.Blocks.GlobalDB ID="0" Namespace=""{number}>
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
                  <ProgrammingLanguage>DB</ProgrammingLanguage>
                </AttributeList>
                <ObjectList>
                  <MultilingualText ID="1" CompositionName="Comment">
                    <ObjectList>
                      <MultilingualTextItem ID="2" CompositionName="Items">
                        <AttributeList>
                          <Culture>en-US</Culture>
                          <Text />
                        </AttributeList>
                      </MultilingualTextItem>
                    </ObjectList>
                  </MultilingualText>
                </ObjectList>
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

    /// <summary>Generates SimaticML XML for an InstanceDB block.</summary>
    public static string CreateInstanceDbXml(string name, string instanceOfName, int? number)
    {
        var autoNum = number.HasValue ? "false" : "true";
        var numAttr = number.HasValue ? $"\n      <Number>{number}</Number>" : "";
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Document>
              <Engineering version="V20" />
              <SW.Blocks.InstanceDB ID="0">
                <AttributeList>
                  <AutoNumber>{autoNum}</AutoNumber>
                  <InstanceOfName>{instanceOfName}</InstanceOfName>
                  <Name>{name}</Name>{numAttr}
                  <ProgrammingLanguage>DB</ProgrammingLanguage>
                </AttributeList>
                <ObjectList>
                  <MultilingualText ID="1" CompositionName="Comment">
                    <ObjectList>
                      <MultilingualTextItem ID="2" CompositionName="Items">
                        <AttributeList>
                          <Culture>en-US</Culture>
                          <Text />
                        </AttributeList>
                      </MultilingualTextItem>
                    </ObjectList>
                  </MultilingualText>
                </ObjectList>
              </SW.Blocks.InstanceDB>
            </Document>
            """;
    }

    /// <summary>Generates SimaticML XML for a PLC tag table — used for import and batch-rename.</summary>
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
              <Engineering version="V20" />
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
