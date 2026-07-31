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

    public static string Prettify(string xmlContent)
    {
        var doc = XDocument.Parse(xmlContent);
        var sb  = new StringBuilder();
        using (var writer = XmlWriter.Create(sb, PrettySettings))
            doc.Save(writer);
        return sb.ToString();
    }
}
