using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using OTST.Domain.Abstractions;
using OTST.Domain.Models;

namespace OTST.Domain.Services.Intrekking;

public sealed class IntrekkingProcessor
{
    private static readonly XNamespace AanleveringNs = "https://standaarden.overheid.nl/lvbb/stop/aanlevering/";
    private static readonly XNamespace StopDataNs = "https://standaarden.overheid.nl/stop/imop/data/";
    private static readonly XNamespace StopTekstNs = "https://standaarden.overheid.nl/stop/imop/tekst/";
    private static readonly XNamespace ManifestOwNs = "http://www.geostandaarden.nl/bestanden-ow/manifest-ow";
    private static readonly XNamespace OwObjectNs = "http://www.geostandaarden.nl/imow/owobject";
    private static readonly XNamespace OpObjectNs = "http://www.geostandaarden.nl/imow/opobject";
    private static readonly XNamespace LvbbNs = "http://www.overheid.nl/2017/lvbb";

    private readonly ITimeProvider _timeProvider;

    public IntrekkingProcessor(ITimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? SystemTimeProvider.Instance;
    }

    public IntrekkingResult CreateIntrekking(ZipArchive sourceZip, ZipAnalysisResult analysis, bool isValidation)
    {
        if (analysis.BevoegdGezag is null)
        {
            throw new InvalidOperationException("Analyse bevat geen bevoegd gezag; intrekking kan niet worden opgebouwd.");
        }

        var doelId = GenerateDoelId(analysis.BevoegdGezag);
        var metadata = ExtractRegelingMetadata(sourceZip, analysis.BevoegdGezag);
        var modifiedOwFiles = ProcessOwFiles(sourceZip, doelId);

        var besluitXml = GenerateBesluitXml(analysis, metadata, doelId);
        var opdrachtXml = CreateOpdrachtXml(analysis, isValidation);

        return new IntrekkingResult(besluitXml, opdrachtXml, modifiedOwFiles, doelId);
    }

    private List<KeyValuePair<string, byte[]>> ProcessOwFiles(ZipArchive sourceZip, string doelId)
    {
        var result = new List<KeyValuePair<string, byte[]>>();

        foreach (var entry in sourceZip.Entries)
        {
            if (!entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isOwBestand = entry.FullName.StartsWith("OW-bestanden/", StringComparison.OrdinalIgnoreCase)
                              || entry.FullName.Contains("/OW/", StringComparison.OrdinalIgnoreCase);

            if (!isOwBestand)
            {
                continue;
            }

            using var entryStream = entry.Open();
            var document = XDocument.Load(entryStream, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            document.Declaration ??= new XDeclaration("1.0", "UTF-8", "yes");

            var modified = false;

            if (entry.FullName.Contains("manifest-ow", StringComparison.OrdinalIgnoreCase))
            {
                var doelElement = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "DoelID");
                if (doelElement != null)
                {
                    doelElement.Value = doelId;
                    modified = true;
                }
            }

            foreach (var owObject in document.Descendants().Where(e => e.Name.LocalName == "owObject"))
            {
                var objectElement = owObject.Elements().FirstOrDefault(e => e.NodeType == XmlNodeType.Element);
                if (objectElement is null)
                {
                    continue;
                }

                var isRegeltekst = entry.FullName.IndexOf("regeltekst", StringComparison.OrdinalIgnoreCase) >= 0
                                   || string.Equals(objectElement.Name.LocalName, "Regeltekst", StringComparison.OrdinalIgnoreCase);

                var statusNamespace = isRegeltekst ? OpObjectNs : OwObjectNs;
                var existingStatus = objectElement.Elements().FirstOrDefault(e =>
                    e.Name.LocalName == "status" && e.Name.Namespace == statusNamespace);

                if (existingStatus != null)
                {
                    if (existingStatus.Value != "beëindigen")
                    {
                        existingStatus.Value = "beëindigen";
                        modified = true;
                    }
                    continue;
                }

                var statusElement = new XElement(statusNamespace + "status", "beëindigen");
                if (objectElement.FirstNode is XElement firstElement)
                {
                    firstElement.AddBeforeSelf(statusElement);
                }
                else
                {
                    objectElement.AddFirst(statusElement);
                }

                modified = true;
            }

            if (modified)
            {
                var settings = CreateXmlWriterSettings();
                using var ms = new MemoryStream();
                using (var writer = XmlWriter.Create(ms, settings))
                {
                    document.Save(writer);
                }

                var targetFileName = Path.GetFileName(entry.FullName);
                result.Add(new KeyValuePair<string, byte[]>(targetFileName, ms.ToArray()));
            }
        }

        return result;
    }

    private RegelingMetadata ExtractRegelingMetadata(ZipArchive sourceZip, string bevoegdGezag)
    {
        var entry = sourceZip.GetEntry("Regeling/Metadata.xml")
                   ?? sourceZip.Entries.FirstOrDefault(e =>
                       e.FullName.EndsWith("Metadata.xml", StringComparison.OrdinalIgnoreCase)
                       && e.FullName.Contains("Regeling", StringComparison.OrdinalIgnoreCase));

        if (entry != null)
        {
            using var stream = entry.Open();
            var document = XDocument.Load(stream);
            var metadataElement = document.Descendants(StopDataNs + "RegelingMetadata").FirstOrDefault();
            if (metadataElement != null)
            {
                var children = metadataElement.Elements().ToList();
                return new RegelingMetadata(children);
            }
        }

        // Fallback values (similar to Java implementation)
        var fallbackElements = new List<XElement>
        {
            new(StopDataNs + "officieleTitel", $"{bevoegdGezag} intrekking"),
            new(StopDataNs + "eindverantwoordelijke", $"/tooi/id/gemeente/{bevoegdGezag}"),
            new(StopDataNs + "maker", $"/tooi/id/gemeente/{bevoegdGezag}"),
            new(StopDataNs + "soortBestuursorgaan", "/tooi/def/thes/kern/c_411b319c"),
            new XElement(StopDataNs + "onderwerpen",
                new XElement(StopDataNs + "onderwerp", "/tooi/def/concept/c_1c12723d"))
        };

        return new RegelingMetadata(fallbackElements);
    }

    private byte[] GenerateBesluitXml(ZipAnalysisResult analysis, RegelingMetadata metadata, string doelId)
    {
        var frbrWork = (analysis.FrbrWork ?? string.Empty)
            .Replace("/akn/nl/act", "/akn/nl/bill", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/')
            .Replace("/intrekking", "_intrekking", StringComparison.OrdinalIgnoreCase);

        var frbrExpression = $"{frbrWork}/nld@2023-11-15;2";
        var morgen = _timeProvider.Today.AddDays(1);

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", "no"),
            new XElement(AanleveringNs + "AanleveringBesluit",
                new XAttribute(XNamespace.Xmlns + "data", StopDataNs),
                new XAttribute(XNamespace.Xmlns + "tekst", StopTekstNs),
                new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
                new XAttribute("schemaversie", "1.2.0"),
                new XElement(AanleveringNs + "BesluitVersie",
                    new XElement(StopDataNs + "ExpressionIdentificatie",
                        new XElement(StopDataNs + "FRBRWork", frbrWork),
                        new XElement(StopDataNs + "FRBRExpression", frbrExpression),
                        new XElement(StopDataNs + "soortWork", "/join/id/stop/work_003")
                    ),
                    BuildBesluitMetadata(metadata),
                    new XElement(StopDataNs + "Procedureverloop",
                        new XElement(StopDataNs + "bekendOp", morgen.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                        new XElement(StopDataNs + "procedurestappen",
                            new XElement(StopDataNs + "Procedurestap",
                                new XElement(StopDataNs + "soortStap", "/join/id/stop/procedure/stap_003"),
                                new XElement(StopDataNs + "voltooidOp", morgen.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                            )
                        )
                    ),
                    new XElement(StopDataNs + "ConsolidatieInformatie",
                        new XElement(StopDataNs + "Intrekkingen",
                            new XElement(StopDataNs + "Intrekking",
                                new XElement(StopDataNs + "doelen",
                                    new XElement(StopDataNs + "doel", doelId)
                                ),
                                new XElement(StopDataNs + "instrument", analysis.FrbrWork ?? string.Empty),
                                new XElement(StopDataNs + "eId", "art_I")
                            )
                        ),
                        new XElement(StopDataNs + "Tijdstempels",
                            new XElement(StopDataNs + "Tijdstempel",
                                new XElement(StopDataNs + "doel", doelId),
                                new XElement(StopDataNs + "soortTijdstempel", "juridischWerkendVanaf"),
                                new XElement(StopDataNs + "datum", morgen.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                                new XElement(StopDataNs + "eId", "art_I")
                            )
                        )
                    ),
                    BuildBesluitCompact(analysis)
                )
            )
        );

        var settings = CreateXmlWriterSettings();
        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, settings))
        {
            doc.Save(writer);
        }
        return ms.ToArray();
    }

    private XElement BuildBesluitMetadata(RegelingMetadata metadata)
    {
        var element = new XElement(StopDataNs + "BesluitMetadata",
            new XAttribute("schemaversie", "1.3.0"));

        var heeftSoortProcedure = false;

        foreach (var child in metadata.Elements)
        {
            if (string.Equals(child.Name.LocalName, "soortRegeling", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var clone = new XElement(child);
            clone.Name = StopDataNs + child.Name.LocalName;

            if (clone.Name.LocalName == "heeftCiteertitelInformatie")
            {
                foreach (var citeertitel in clone.Descendants().Where(d => d.Name.LocalName == "citeertitel"))
                {
                    citeertitel.Value = $"{citeertitel.Value.Trim()} intrekking";
                }
            }
            else if (clone.Name.LocalName == "officieleTitel")
            {
                clone.Value = $"{clone.Value.Trim()} intrekking";
            }
            else if (clone.Name.LocalName == "soortProcedure")
            {
                heeftSoortProcedure = true;
            }

            element.Add(clone);
        }

        if (!heeftSoortProcedure)
        {
            element.Add(new XElement(StopDataNs + "soortProcedure", "/join/id/stop/proceduretype_definitief"));
        }

        return element;
    }

    private XElement BuildBesluitCompact(ZipAnalysisResult analysis)
    {
        var opschrift = new XElement(StopTekstNs + "RegelingOpschrift",
            new XAttribute("eId", "longTitle"),
            new XAttribute("wId", "__longTitle"),
            new XElement(StopTekstNs + "Al", $"Intrekkingsbesluit voor {analysis.FrbrWork}"));

        var artikel = new XElement(StopTekstNs + "Artikel",
            new XAttribute("eId", "art_I"),
            new XAttribute("wId", "__art_I"),
            new XElement(StopTekstNs + "Kop",
                new XElement(StopTekstNs + "Label", "Artikel"),
                new XElement(StopTekstNs + "Nummer", "I")
            ),
            new XElement(StopTekstNs + "Inhoud",
                new XElement(StopTekstNs + "Al", $"De regeling treedt uit werking per {_timeProvider.Today.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}")
            )
        );

        return new XElement(StopTekstNs + "BesluitCompact",
            opschrift,
            new XElement(StopTekstNs + "Lichaam",
                new XAttribute("eId", "body"),
                new XAttribute("wId", "body"),
                artikel
            )
        );
    }

    private byte[] CreateOpdrachtXml(ZipAnalysisResult analysis, bool isValidation)
    {
        var rootName = isValidation ? "validatieOpdracht" : "publicatieOpdracht";
        var now = _timeProvider.Now;
        var datum = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var tijd = now.ToString("HHmmss", CultureInfo.InvariantCulture);
        var leveringId = $"OTST_{(isValidation ? "val" : "pub")}_intr_{analysis.BevoegdGezag}_{datum}_{tijd}";
        var bekendmaking = GetNextWorkingDay(_timeProvider.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", "no"),
            new XElement(LvbbNs + rootName,
                new XElement("idLevering", leveringId),
                new XElement("idBevoegdGezag", "00000001003214345000"),
                new XElement("idAanleveraar", "00000001003214345000"),
                new XElement("publicatie", "intrekkingsbesluit.xml"),
                new XElement("datumBekendmaking", bekendmaking)
            )
        );

        var settings = CreateXmlWriterSettings();
        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, settings))
        {
            doc.Save(writer);
        }

        return ms.ToArray();
    }

    private static XmlWriterSettings CreateXmlWriterSettings() => new()
    {
        Encoding = new UTF8Encoding(false),
        Indent = true,
        IndentChars = "   ",
        NewLineChars = "\r\n",
        NewLineHandling = NewLineHandling.Replace,
        OmitXmlDeclaration = false
    };

    private string GenerateDoelId(string bevoegdGezag)
    {
        var now = _timeProvider.Now;
        return string.Format(CultureInfo.InvariantCulture,
            "/join/id/proces/{0}/{1}/Intrekking_{2}_{3}",
            bevoegdGezag,
            now.Year,
            now.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            now.ToString("HHmmss", CultureInfo.InvariantCulture));
    }

    private static DateOnly GetNextWorkingDay(DateOnly date)
    {
        var next = date.AddDays(1);
        while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            next = next.AddDays(1);
        }
        return next;
    }

    public sealed record IntrekkingResult(
        byte[] BesluitXml,
        byte[] OpdrachtXml,
        IReadOnlyList<KeyValuePair<string, byte[]>> ModifiedFiles,
        string DoelId);

    private sealed class RegelingMetadata
    {
        public RegelingMetadata(IEnumerable<XElement> elements)
        {
            Elements = elements.Select(e => new XElement(e)).ToList();
        }

        public List<XElement> Elements { get; }
    }
}

