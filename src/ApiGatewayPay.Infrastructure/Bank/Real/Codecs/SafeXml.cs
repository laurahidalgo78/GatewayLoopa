using System.Xml;
using System.Xml.Linq;
using ApiGatewayPay.Core.Ports.Bank;

namespace ApiGatewayPay.Infrastructure.Bank.Real.Codecs;

/// <summary>Parser XML seguro: sin DTD (XXE / billion laughs), sin resolución externa y con límite de tamaño.</summary>
public static class SafeXml
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = 5_000_000,
        MaxCharactersFromEntities = 0,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    public static XDocument Parse(string xml)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), Settings);
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException)
        {
            throw new BankIntegrityException("Respuesta XML inválida.");
        }
    }

    public static IEnumerable<XElement> All(this XContainer c, string localName) => c.Descendants().Where(e => e.Name.LocalName == localName);

    public static XElement? First(this XContainer c, string localName) => c.All(localName).FirstOrDefault();

    public static string? Text(this XContainer c, string localName) => c.First(localName)?.Value.Trim();

    public static string Required(this XContainer c, string localName) =>
        c.Text(localName) is { Length: > 0 and <= 256 } v ? v : throw new BankIntegrityException($"Elemento requerido ausente: {localName}");
}
