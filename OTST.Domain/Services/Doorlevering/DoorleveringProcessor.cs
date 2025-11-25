using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using OTST.Domain.Abstractions;
using OTST.Domain.Models;

namespace OTST.Domain.Services.Doorlevering;

public sealed class DoorleveringProcessor
{
    private static readonly XNamespace UitleveringNs = "https://standaarden.overheid.nl/lvbb/stop/uitlevering/";
    private static readonly XNamespace StopDataNs = "https://standaarden.overheid.nl/stop/imop/data/";
    private static readonly XNamespace StopTekstNs = "https://standaarden.overheid.nl/stop/imop/tekst/";
    private static readonly XNamespace ConsolidatieNs = "https://standaarden.overheid.nl/stop/imop/consolidatie/";
    private static readonly XNamespace ManifestOwNs = "http://www.geostandaarden.nl/bestanden-ow/manifest-ow";

    private readonly ITimeProvider _timeProvider;

    public DoorleveringProcessor(ITimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? SystemTimeProvider.Instance;
    }

    public DoorleveringResult CreateDoorlevering(ZipArchive sourceZip, ZipAnalysisResult analysis, bool isValidation)
    {
        if (analysis.BevoegdGezag is null)
        {
            throw new InvalidOperationException("Analyse bevat geen bevoegd gezag; doorlevering kan niet worden opgebouwd.");
        }

        var doelId = GenerateDoelId(analysis.BevoegdGezag, analysis.FrbrWork);
        var metadata = ExtractRegelingMetadata(sourceZip, analysis.BevoegdGezag);
        var modifiedOwFiles = ProcessOwFiles(sourceZip, doelId);

        var proefversieBesluitXml = GenerateProefversieBesluitXml(sourceZip, analysis, metadata, doelId);
        var consolidatiesXml = GenerateConsolidatiesXml(sourceZip, analysis, doelId);

        return new DoorleveringResult(proefversieBesluitXml, consolidatiesXml, modifiedOwFiles, doelId);
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

            // Voor doorlevering worden OW-bestanden niet aangepast (geen status beëindigen)
            // manifest-ow.xml wordt later bijgewerkt met alle OW-bestanden in UpdateManifestOw
            // Skip manifest-ow.xml hier
            if (entry.FullName.Contains("manifest-ow", StringComparison.OrdinalIgnoreCase))
            {
                continue;
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

        // Fallback values
        var fallbackElements = new List<XElement>
        {
            new(StopDataNs + "officieleTitel", $"{bevoegdGezag} doorlevering"),
            new(StopDataNs + "eindverantwoordelijke", $"/tooi/id/gemeente/{bevoegdGezag}"),
            new(StopDataNs + "maker", $"/tooi/id/gemeente/{bevoegdGezag}"),
            new(StopDataNs + "soortBestuursorgaan", "/tooi/def/thes/kern/c_411b319c"),
            new XElement(StopDataNs + "onderwerpen",
                new XElement(StopDataNs + "onderwerp", "/tooi/def/concept/c_1c12723d"))
        };

        return new RegelingMetadata(fallbackElements);
    }

    private byte[] GenerateProefversieBesluitXml(ZipArchive sourceZip, ZipAnalysisResult analysis, RegelingMetadata metadata, string doelId)
    {
        var frbrWork = (analysis.FrbrWork ?? string.Empty)
            .Replace("/akn/nl/act", "/akn/nl/bill", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');

        var frbrExpression = (analysis.FrbrExpression ?? string.Empty)
            .Replace("/akn/nl/act", "/akn/nl/bill", StringComparison.OrdinalIgnoreCase);
        // Voor doorlevering: gebruik de volgende maandag (6 dagen na vandaag als vandaag maandag is)
        var morgen = GetNextMonday(_timeProvider.Today);

        // Load Regeling/Tekst.xml for RegelingVersie
        var tekstEntry = sourceZip.GetEntry("Regeling/Tekst.xml");
        XElement? regelingVrijetekst = null;
        if (tekstEntry != null)
        {
            using var stream = tekstEntry.Open();
            var tekstDoc = XDocument.Load(stream);
            var root = tekstDoc.Root;
            if (root != null)
            {
                regelingVrijetekst = ConvertElementToNamespace(root, StopTekstNs);
            }
        }

        // Load Regeling/VersieMetadata.xml
        var versieMetadataEntry = sourceZip.GetEntry("Regeling/VersieMetadata.xml");
        XElement? regelingVersieMetadata = null;
        if (versieMetadataEntry != null)
        {
            using var stream = versieMetadataEntry.Open();
            var versieDoc = XDocument.Load(stream);
            var root = versieDoc.Root;
            if (root != null)
            {
                regelingVersieMetadata = ConvertElementToNamespace(root, StopDataNs);
            }
        }
        else
        {
            regelingVersieMetadata = new XElement(StopDataNs + "RegelingVersieMetadata",
                new XElement(StopDataNs + "versienummer", "1"));
        }

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", "no"),
            new XElement(UitleveringNs + "UitleveringProefversieBesluit",
                new XAttribute("schemaversie", "1.2.0"),
                new XAttribute(XNamespace.Xmlns + "lvbbu", UitleveringNs),
                BuildExpressionIdentificatie(frbrWork, frbrExpression),
                BuildProcedureverloop(morgen),
                BuildBesluitMetadata(metadata),
                new XElement(UitleveringNs + "Proefversies",
                    new XElement(ConsolidatieNs + "bekendOp", morgen.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    new XElement(ConsolidatieNs + "ontvangenOp", morgen.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    new XElement(UitleveringNs + "Proefversie",
                        new XElement(ConsolidatieNs + "gerealiseerdeDoelen",
                            new XElement(ConsolidatieNs + "doel", doelId)),
                        new XElement(ConsolidatieNs + "instrumentVersie", frbrExpression)
                    )
                ),
                new XElement(UitleveringNs + "RegelingVersie",
                    new XAttribute("schemaversie", "1.2.0"),
                    BuildExpressionIdentificatie(analysis.FrbrWork ?? string.Empty, frbrExpression),
                    regelingVersieMetadata,
                    regelingVrijetekst ?? new XElement(StopTekstNs + "RegelingVrijetekst")
                ),
                BuildAnnotatieBijProefversie(sourceZip, analysis)
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

    private XElement BuildExpressionIdentificatie(string frbrWork, string frbrExpression)
    {
        return new XElement(StopDataNs + "ExpressionIdentificatie",
            new XAttribute(XNamespace.Xmlns + "data", StopDataNs),
            new XElement(StopDataNs + "FRBRWork", frbrWork),
            new XElement(StopDataNs + "FRBRExpression", frbrExpression),
            new XElement(StopDataNs + "soortWork", DetermineSoortWork(frbrWork))
        );
    }

    private static string DetermineSoortWork(string frbrWork)
    {
        // Determine soortWork based on FRBRWork pattern
        if (frbrWork.Contains("/bill/", StringComparison.OrdinalIgnoreCase))
        {
            return "/join/id/stop/work_003"; // Besluit
        }
        if (frbrWork.Contains("/act/", StringComparison.OrdinalIgnoreCase))
        {
            return "/join/id/stop/work_019"; // Programma
        }
        return "/join/id/stop/work_003";
    }

    private XElement BuildProcedureverloop(DateOnly morgen)
    {
        // Load procedurestappen from source if available, otherwise use defaults
        var procedurestappen = new XElement(StopDataNs + "procedurestappen",
            new XElement(StopDataNs + "Procedurestap",
                new XElement(StopDataNs + "soortStap", "/join/id/stop/procedure/stap_002"),
                new XElement(StopDataNs + "voltooidOp", _timeProvider.Today.AddDays(-10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            ),
            new XElement(StopDataNs + "Procedurestap",
                new XElement(StopDataNs + "soortStap", "/join/id/stop/procedure/stap_003"),
                new XElement(StopDataNs + "voltooidOp", _timeProvider.Today.AddDays(-10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            ),
            new XElement(StopDataNs + "Procedurestap",
                new XElement(StopDataNs + "soortStap", "/join/id/stop/procedure/stap_004"),
                new XElement(StopDataNs + "voltooidOp", morgen.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            )
        );

        return new XElement(StopDataNs + "Procedureverloop",
            new XAttribute(XNamespace.Xmlns + "data", StopDataNs),
            new XElement(StopDataNs + "bekendOp", morgen.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            new XElement(StopDataNs + "ontvangenOp", morgen.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            procedurestappen
        );
    }

    private XElement BuildBesluitMetadata(RegelingMetadata metadata)
    {
        var element = new XElement(StopDataNs + "BesluitMetadata",
            new XAttribute(XNamespace.Xmlns + "data", StopDataNs));

        var heeftSoortProcedure = false;

        foreach (var child in metadata.Elements)
        {
            if (string.Equals(child.Name.LocalName, "soortRegeling", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var clone = new XElement(child);
            clone.Name = StopDataNs + child.Name.LocalName;

            if (clone.Name.LocalName == "soortProcedure")
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

    private XElement? BuildAnnotatieBijProefversie(ZipArchive sourceZip, ZipAnalysisResult analysis)
    {
        var metadataEntry = sourceZip.GetEntry("Regeling/Metadata.xml");
        if (metadataEntry == null)
        {
            return null;
        }

        using var stream = metadataEntry.Open();
        var doc = XDocument.Load(stream);
        var metadataElement = doc.Descendants(StopDataNs + "RegelingMetadata").FirstOrDefault();
        if (metadataElement == null)
        {
            return null;
        }

        var annotatie = new XElement(UitleveringNs + "AnnotatieBijProefversie",
            BuildExpressionIdentificatie(analysis.FrbrWork ?? string.Empty, analysis.FrbrExpression ?? string.Empty),
            new XElement(StopDataNs + "RegelingMetadata",
                new XAttribute(XNamespace.Xmlns + "data", StopDataNs),
                ConvertElementToNamespace(metadataElement, StopDataNs).Elements()
            )
        );

        return annotatie;
    }

    private byte[] GenerateConsolidatiesXml(ZipArchive sourceZip, ZipAnalysisResult analysis, string doelId)
    {
        var morgen = GetNextMonday(_timeProvider.Today);
        var frbrExpression = analysis.FrbrExpression ?? string.Empty;
        var frbrWork = analysis.FrbrWork ?? string.Empty;

        // Generate a consolidation FRBRWork (typically CVDR + number)
        // Use a hash of the FRBRWork to generate a deterministic value
        var cvdrNumber = GenerateCvdrNumber(frbrWork);
        var consolidatieFrbrWork = $"/akn/nl/act/gemeente/{_timeProvider.Now.Year}/CVDR{cvdrNumber}";
        var consolidatieFrbrExpression = $"{consolidatieFrbrWork}/nld@{morgen:yyyy-MM-dd}";

        // Load Regeling/Tekst.xml for RegelingVersie in consolidatie
        var tekstEntry = sourceZip.GetEntry("Regeling/Tekst.xml");
        XElement? regelingVrijetekst = null;
        if (tekstEntry != null)
        {
            using var stream = tekstEntry.Open();
            var tekstDoc = XDocument.Load(stream);
            var root = tekstDoc.Root;
            if (root != null)
            {
                regelingVrijetekst = ConvertElementToNamespace(root, StopTekstNs);
            }
        }

        // Load Regeling/VersieMetadata.xml
        var versieMetadataEntry = sourceZip.GetEntry("Regeling/VersieMetadata.xml");
        XElement? regelingVersieMetadata = null;
        if (versieMetadataEntry != null)
        {
            using var stream = versieMetadataEntry.Open();
            var versieDoc = XDocument.Load(stream);
            var root = versieDoc.Root;
            if (root != null)
            {
                regelingVersieMetadata = ConvertElementToNamespace(root, StopDataNs);
            }
        }
        else
        {
            regelingVersieMetadata = new XElement(StopDataNs + "RegelingVersieMetadata",
                new XElement(StopDataNs + "versienummer", "1"));
        }

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", "no"),
            new XElement(UitleveringNs + "Consolidaties",
                new XAttribute("schemaversie", "1.2.0"),
                new XAttribute(XNamespace.Xmlns + "lvbbu", UitleveringNs),
                new XElement(UitleveringNs + "Consolidatie",
                    new XElement(ConsolidatieNs + "ConsolidatieIdentificatie",
                        new XAttribute(XNamespace.Xmlns + "consolidatie", ConsolidatieNs),
                        new XElement(ConsolidatieNs + "FRBRWork", consolidatieFrbrWork),
                        new XElement(ConsolidatieNs + "soortWork", "/join/id/stop/work_006"),
                        new XElement(ConsolidatieNs + "isConsolidatieVan",
                            new XElement(ConsolidatieNs + "WorkIdentificatie",
                                new XElement(ConsolidatieNs + "FRBRWork", frbrWork),
                                new XElement(ConsolidatieNs + "soortWork", DetermineSoortWork(frbrWork))
                            )
                        )
                    ),
                    new XElement(ConsolidatieNs + "Toestanden",
                        new XAttribute(XNamespace.Xmlns + "consolidatie", ConsolidatieNs),
                        new XElement(ConsolidatieNs + "bekendOp", morgen.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                        new XElement(ConsolidatieNs + "ontvangenOp", morgen.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                        new XElement(ConsolidatieNs + "BekendeToestand",
                            new XElement(ConsolidatieNs + "FRBRExpression", consolidatieFrbrExpression),
                            new XElement(ConsolidatieNs + "gerealiseerdeDoelen",
                                new XElement(ConsolidatieNs + "doel", doelId)),
                            new XElement(ConsolidatieNs + "geldigheid",
                                new XElement(ConsolidatieNs + "Geldigheidsperiode",
                                    new XElement(ConsolidatieNs + "juridischWerkendOp",
                                        new XElement(ConsolidatieNs + "Periode",
                                            new XElement(ConsolidatieNs + "vanaf", morgen.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                                        )
                                    ),
                                    new XElement(ConsolidatieNs + "geldigOp",
                                        new XElement(ConsolidatieNs + "Periode",
                                            new XElement(ConsolidatieNs + "vanaf", morgen.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                                        )
                                    )
                                )
                            ),
                            new XElement(ConsolidatieNs + "instrumentVersie", frbrExpression)
                        )
                    ),
                    new XElement(UitleveringNs + "RegelingVersie",
                        new XAttribute("schemaversie", "1.2.0"),
                        BuildExpressionIdentificatie(frbrWork, frbrExpression),
                        regelingVersieMetadata,
                        regelingVrijetekst ?? new XElement(StopTekstNs + "RegelingVrijetekst")
                    ),
                    BuildAnnotatieBijToestand(sourceZip, analysis, consolidatieFrbrWork, consolidatieFrbrExpression)
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

    private XElement? BuildAnnotatieBijToestand(ZipArchive sourceZip, ZipAnalysisResult analysis, string consolidatieFrbrWork, string consolidatieFrbrExpression)
    {
        var metadataEntry = sourceZip.GetEntry("Regeling/Metadata.xml");
        if (metadataEntry == null)
        {
            return null;
        }

        using var stream = metadataEntry.Open();
        var doc = XDocument.Load(stream);
        var metadataElement = doc.Descendants(StopDataNs + "RegelingMetadata").FirstOrDefault();
        if (metadataElement == null)
        {
            return null;
        }

        var annotatie = new XElement(UitleveringNs + "AnnotatieBijToestand",
            new XElement(StopDataNs + "ExpressionIdentificatie",
                new XAttribute(XNamespace.Xmlns + "data", StopDataNs),
                new XElement(StopDataNs + "FRBRWork", consolidatieFrbrWork),
                new XElement(StopDataNs + "FRBRExpression", consolidatieFrbrExpression),
                new XElement(StopDataNs + "soortWork", "/join/id/stop/work_006")
            ),
            new XElement(StopDataNs + "RegelingMetadata",
                new XAttribute(XNamespace.Xmlns + "data", StopDataNs),
                ConvertElementToNamespace(metadataElement, StopDataNs).Elements()
            )
        );

        return annotatie;
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

    private static XmlWriterSettings CreateXmlWriterSettings() => new()
    {
        Encoding = new UTF8Encoding(false),
        Indent = true,
        IndentChars = "  ",
        NewLineChars = "\n",
        NewLineHandling = NewLineHandling.Replace,
        OmitXmlDeclaration = false
    };

    private string GenerateDoelId(string bevoegdGezag, string? frbrWork)
    {
        // Generate doel ID based on pattern from testdata
        // Pattern: /join/id/proces/{bevoegdGezag}/{year}/Prog{programName}PPD{year}{year+4}
        var now = _timeProvider.Now;
        var year = now.Year;
        
        // Extract program name from FRBRWork if available
        // FRBRWork format: /akn/nl/bill/{bevoegdGezag}/{year}/Prg{ProgramName}
        var programName = "Klimaatadaptatie"; // Default
        if (!string.IsNullOrEmpty(frbrWork))
        {
            var parts = frbrWork.Split('/');
            if (parts.Length > 0)
            {
                var lastPart = parts[^1];
                if (lastPart.StartsWith("Prg", StringComparison.OrdinalIgnoreCase))
                {
                    programName = lastPart.Substring(3); // Remove "Prg" prefix
                }
            }
        }
        
        return $"/join/id/proces/{bevoegdGezag}/{year}/Prog{programName}PPD{year}{year + 4}";
    }

    private static string GenerateCvdrNumber(string frbrWork)
    {
        // Generate a deterministic 6-digit number from FRBRWork
        // Use a hash to ensure determinism while maintaining uniqueness
        if (string.IsNullOrEmpty(frbrWork))
        {
            return "000000";
        }

        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(frbrWork));
        // Take first 3 bytes and convert to 6-digit number
        var number = (hash[0] << 16) | (hash[1] << 8) | hash[2];
        // Ensure it's 6 digits (000000-999999)
        return (number % 1000000).ToString("D6", CultureInfo.InvariantCulture);
    }

    private static DateOnly GetNextMonday(DateOnly date)
    {
        // Bereken de volgende maandag
        var daysUntilMonday = ((int)DayOfWeek.Monday - (int)date.DayOfWeek + 7) % 7;
        if (daysUntilMonday == 0)
        {
            daysUntilMonday = 7; // Als het al maandag is, neem volgende week maandag
        }
        return date.AddDays(daysUntilMonday);
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

    public sealed record DoorleveringResult(
        byte[] ProefversieBesluitXml,
        byte[] ConsolidatiesXml,
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
