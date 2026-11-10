using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;

namespace MppToXml.Mspdi;

/// <summary>MSPDI XML with preservation of elements outside the known schema model.</summary>
public sealed class MspdiDocument
{
    public const string Namespace = "http://schemas.microsoft.com/project";
    public XDocument Xml { get; }
    public MspdiDocument(XDocument xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        if (xml.Root?.Name != XName.Get("Project", Namespace)) throw new InvalidDataException("Expected an MSPDI Project root.");
        Xml = new XDocument(xml);
    }
    public static MspdiDocument Load(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CloseInput = false, MaxCharactersInDocument = 268435456 });
        return new(XDocument.Load(reader));
    }
    public void Save(Stream output, bool indent = true)
    {
        ArgumentNullException.ThrowIfNull(output);
        using var writer = XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = indent, CloseOutput = false, CheckCharacters = true });
        Xml.Save(writer);
    }
    public MspdiProject ToModel()
    {
        using var reader = Xml.CreateReader();
        return (MspdiProject)new XmlSerializer(typeof(MspdiProject)).Deserialize(reader)!;
    }
    public static MspdiDocument FromModel(MspdiProject model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var doc = new XDocument();
        using (var writer = doc.CreateWriter())
        {
            var ns = new XmlSerializerNamespaces(); ns.Add("", Namespace);
            new XmlSerializer(typeof(MspdiProject)).Serialize(writer, model, ns);
        }
        return new(doc);
    }
}
