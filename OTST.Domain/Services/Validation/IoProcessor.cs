using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using OTST.Domain.Abstractions;
using OTST.Domain.Models;

namespace OTST.Domain.Services.Validation;

internal sealed class IoProcessor
{
    private static readonly XNamespace AanleveringNs = "https://standaarden.overheid.nl/lvbb/stop/aanlevering/";
    private static readonly XNamespace DataNs = "https://standaarden.overheid.nl/stop/imop/data/";
    private static readonly XNamespace GeoNs = "https://standaarden.overheid.nl/stop/imop/geo/";
    private static readonly XNamespace GioNs = "https://standaarden.overheid.nl/stop/imop/gio/";
    private static readonly XNamespace BasisGeoNs = "http://www.geostandaarden.nl/basisgeometrie/1.0";
    private static readonly XNamespace GmlNs = "http://www.opengis.net/gml/3.2";
    private static readonly XNamespace XsiNs = "http://www.w3.org/2001/XMLSchema-instance";

    private readonly ITimeProvider _timeProvider;

    public IoProcessor(ITimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public IoProcessingResult Process(ZipArchive archive, ZipAnalysisResult analysis, ZipAnalysisResult.InformatieObjectInfo ioInfo)
    {
        if (ioInfo.Folder is null)
        {
            throw new InvalidOperationException("Informatieobject bevat geen mapnaam.");
        }

        var folderName = ioInfo.Folder.TrimEnd('/');
        var files = ExtractFiles(archive, folderName);

        var ioXmlBytes = CreateIoXml(archive, analysis, ioInfo, files, folderName);

        var ioXmlFileName = $"{folderName}.xml";

        var additionalFiles = files.Select(file => new FileContent(file.FileName, file.Content)).ToList();

        return new IoProcessingResult(ioXmlFileName, ioXmlBytes, additionalFiles);
    }

    private IReadOnlyList<IoFileRecord> ExtractFiles(ZipArchive archive, string folderName)
    {
        var records = new List<IoFileRecord>();

        foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith(folderName + "/", StringComparison.OrdinalIgnoreCase)))
        {
            var fileName = Path.GetFileName(entry.FullName);
            if (string.IsNullOrEmpty(fileName))
            {
                continue;
            }

            if (fileName.EndsWith(".gml", StringComparison.OrdinalIgnoreCase))
            {
                using var input = entry.Open();
                using var ms = new MemoryStream();
                input.CopyTo(ms);
                var wrapped = WrapGmlContent(ms.ToArray());
                records.Add(new IoFileRecord(fileName, wrapped, ComputeSha512(wrapped)));
            }
            else if (fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
                     fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                     fileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                     fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                using var input = entry.Open();
                using var ms = new MemoryStream();
                input.CopyTo(ms);
                var content = ms.ToArray();
                records.Add(new IoFileRecord(fileName, content, ComputeSha512(content)));
            }
        }

        return records;
    }

    private static byte[] CreateIoXml(ZipArchive archive, ZipAnalysisResult analysis, ZipAnalysisResult.InformatieObjectInfo ioInfo, IReadOnlyList<IoFileRecord> files, string folderName)
    {
        if (ioInfo.FrbrWork is null || ioInfo.FrbrExpression is null)
        {
            throw new InvalidOperationException($"FRBR gegevens ontbreken voor informatieobject {folderName}.");
        }

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", "no"),
            new XElement(AanleveringNs + "AanleveringInformatieObject",
                new XAttribute(XNamespace.Xmlns + "geo", GeoNs),
                new XAttribute(XNamespace.Xmlns + "xsi", XsiNs),
                new XAttribute("schemaversie", "1.2.0"),
                new XAttribute(XsiNs + "schemaLocation", "https://standaarden.overheid.nl/lvbb/stop/aanlevering https://standaarden.overheid.nl/lvbb/1.2.0/lvbb-stop-aanlevering.xsd"),
                new XElement(AanleveringNs + "InformatieObjectVersie",
                    new XElement(AanleveringNs + "ExpressionIdentificatie",
                        new XAttribute(XNamespace.Xmlns + "data", DataNs.NamespaceName),
                        new XElement(DataNs + "FRBRWork", ioInfo.FrbrWork),
                        new XElement(DataNs + "FRBRExpression", ioInfo.FrbrExpression),
                        new XElement(DataNs + "soortWork", "/join/id/stop/work_010")
                    ),
                    BuildVersieMetadata(analysis, files),
                    LoadIoMetadata(archive, folderName)
                )
            )
        );

        return Encoding.UTF8.GetBytes(doc.ToString(SaveOptions.DisableFormatting));
    }

    private static XElement BuildVersieMetadata(ZipAnalysisResult analysis, IReadOnlyList<IoFileRecord> files)
    {
        var metadata = new XElement(AanleveringNs + "InformatieObjectVersieMetadata",
            new XAttribute(XNamespace.Xmlns + "data", DataNs.NamespaceName),
            new XElement(DataNs + "heeftGeboorteregeling", analysis.FrbrWork ?? string.Empty)
        );

        if (files.Count > 0)
        {
            var bestanden = new XElement(DataNs + "heeftBestanden");
            foreach (var file in files)
            {
                bestanden.Add(
                    new XElement(DataNs + "heeftBestand",
                        new XElement(DataNs + "Bestand",
                            new XElement(DataNs + "bestandsnaam", file.FileName),
                            new XElement(DataNs + "hash", file.Hash)
                        )
                    )
                );
            }

            metadata.Add(bestanden);
        }

        return metadata;
    }

    private static XElement LoadIoMetadata(ZipArchive archive, string folderName)
    {
        var metadataEntry = archive.GetEntry($"{folderName}/Metadata.xml");
        if (metadataEntry is null)
        {
            throw new InvalidOperationException($"Metadata.xml ontbreekt voor {folderName}.");
        }

        using var stream = metadataEntry.Open();
        var metadataDoc = XDocument.Load(stream);

        var root = metadataDoc.Root ?? throw new InvalidOperationException("Metadata bestand heeft geen root element.");

        return ConvertElementToNamespace(root, DataNs);
    }

    private static XElement ConvertElementToNamespace(XElement element, XNamespace targetNamespace)
    {
        var converted = new XElement(targetNamespace + element.Name.LocalName,
            element.Attributes().Where(a => !a.IsNamespaceDeclaration),
            element.Nodes().Select(n =>
                n is XElement child ? ConvertElementToNamespace(child, targetNamespace) :
                n is XText text ? new XText(text.Value) :
                n));

        return converted;
    }

    private byte[] WrapGmlContent(byte[] gmlContent)
    {
        using var ms = new MemoryStream(gmlContent);
        var gmlDoc = XDocument.Load(ms);

        var root = gmlDoc.Root ?? throw new InvalidOperationException("GML document mist root element.");

        if (root.Name.LocalName == "GeoInformatieObjectVaststelling" && root.Name.Namespace == GeoNs)
        {
            var wasIds = root.Descendants(GeoNs + "wasID").ToList();
            if (wasIds.Count == 0)
            {
                return gmlContent;
            }

            foreach (var wasId in wasIds)
            {
                wasId.Remove();
            }

            return Encoding.UTF8.GetBytes(gmlDoc.ToString(SaveOptions.DisableFormatting));
        }

        var today = _timeProvider.Today.ToString("yyyy-MM-dd");

        var wrappedDoc = new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(GeoNs + "GeoInformatieObjectVaststelling",
                new XAttribute(XNamespace.Xmlns + "geo", GeoNs.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "basisgeo", BasisGeoNs.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "gio", GioNs.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "gml", GmlNs.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "xsi", XsiNs.NamespaceName),
                new XAttribute("schemaversie", "1.3.0"),
                new XAttribute(XsiNs + "schemaLocation", "https://standaarden.overheid.nl/stop/imop/geo/ https://standaarden.overheid.nl/stop/1.3.0/imop-geo.xsd"),
                new XElement(GeoNs + "context",
                    new XElement(GioNs + "GeografischeContext",
                        new XElement(GioNs + "achtergrondVerwijzing", "cbs"),
                        new XElement(GioNs + "achtergrondActualiteit", today)
                    )
                ),
                new XElement(GeoNs + "vastgesteldeVersie", ConvertElementToNamespace(root, root.Name.Namespace))
            )
        );

        return Encoding.UTF8.GetBytes(wrappedDoc.ToString(SaveOptions.DisableFormatting));
    }

    private static string ComputeSha512(byte[] content)
    {
        using var sha = SHA512.Create();
        var hash = sha.ComputeHash(content);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal sealed record IoProcessingResult(string IoXmlFileName, byte[] IoXmlContent, IReadOnlyList<FileContent> AdditionalFiles);

    internal sealed record FileContent(string FileName, byte[] Content);

    private sealed record IoFileRecord(string FileName, byte[] Content, string Hash);
}

