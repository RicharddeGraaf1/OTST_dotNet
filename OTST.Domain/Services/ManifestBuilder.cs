using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace OTST.Domain.Services;

public static class ManifestBuilder
{
    private static readonly XNamespace LvbbNs = "http://www.overheid.nl/2017/lvbb";
    private static readonly IReadOnlyDictionary<string, string> ContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["xml"] = "application/xml",
        ["gml"] = "application/gml+xml",
        ["jpg"] = "image/jpeg",
        ["jpeg"] = "image/jpeg",
        ["png"] = "image/png",
        ["pdf"] = "application/pdf"
    };

    public static byte[] BuildManifest(IEnumerable<string> addedFiles, bool isIntrekking)
    {
        var files = addedFiles
            .Where(name => !(isIntrekking && name.StartsWith("IO-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(LvbbNs + "manifest",
                files.Select(file =>
                {
                    var extension = Path.GetExtension(file).Trim('.');
                    ContentTypes.TryGetValue(extension, out var contentType);
                    contentType ??= "application/octet-stream";

                    return new XElement(LvbbNs + "bestand",
                        new XElement(LvbbNs + "bestandsnaam", file),
                        new XElement(LvbbNs + "contentType", contentType));
                })
            )
        );

        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            IndentChars = "   ",
            NewLineChars = "\r\n",
            OmitXmlDeclaration = false,
            NewLineHandling = NewLineHandling.Replace
        };

        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, settings))
        {
            doc.Save(writer);
        }

        return ms.ToArray();
    }
}

