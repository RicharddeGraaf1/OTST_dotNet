using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using OTST.Domain.Abstractions;
using OTST.Domain.Models;

namespace OTST.Domain.Services.Validation;

internal sealed class BesluitGenerator
{
    private static readonly XNamespace AanleveringNs = "https://standaarden.overheid.nl/lvbb/stop/aanlevering/";
    private static readonly XNamespace DataNs = "https://standaarden.overheid.nl/stop/imop/data/";
    private static readonly XNamespace TekstNs = "https://standaarden.overheid.nl/stop/imop/tekst/";
    private static readonly XNamespace XsiNs = "http://www.w3.org/2001/XMLSchema-instance";

    private static readonly string[] RegelingFiles =
    {
        "Regeling/Identificatie.xml",
        "Regeling/VersieMetadata.xml",
        "Regeling/Metadata.xml",
        "Regeling/Momentopname.xml"
    };

    private readonly ITimeProvider _timeProvider;

    public BesluitGenerator(ITimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public BesluitResult Generate(ZipArchive archive, ZipAnalysisResult analysis, bool isValidation)
    {
        var now = _timeProvider.Now;
        var today = _timeProvider.Today;
        var tomorrow = GetNextWorkingDay(today);

        var huidigJaartal = now.Year.ToString(CultureInfo.InvariantCulture);
        var datumTijd = now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var datum = today.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        var besluitXml = GenerateBesluitXml(archive, analysis, huidigJaartal, datumTijd, datum, tomorrow);
        var opdrachtXml = GenerateOpdrachtXml(analysis, datumTijd, tomorrow, isValidation);

        return new BesluitResult(besluitXml, opdrachtXml);
    }

    private byte[] GenerateBesluitXml(ZipArchive archive, ZipAnalysisResult analysis, string huidigJaartal, string datumTijd, string datum, DateOnly tomorrow)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", "no"),
            new XElement(AanleveringNs + "AanleveringBesluit",
                new XAttribute("schemaversie", "1.2.0"),
                new XAttribute(XNamespace.Xmlns + "xsi", XsiNs.NamespaceName),
                new XAttribute(XsiNs + "schemaLocation", "https://standaarden.overheid.nl/lvbb/stop/aanlevering https://standaarden.overheid.nl/lvbb/1.2.0/lvbb-stop-aanlevering.xsd"),
                new XElement(AanleveringNs + "BesluitVersie",
                    BuildExpressionIdentificatie(analysis, huidigJaartal, datumTijd, datum),
                    BuildBesluitMetadata(archive, analysis),
                    BuildProcedureverloop(tomorrow),
                    BuildConsolidatieInformatie(analysis, tomorrow),
                    BuildBesluitCompact(archive, analysis),
                    BuildRegelingVersieInformatie(archive)
                )
            )
        );

        return Encoding.UTF8.GetBytes(doc.ToString(SaveOptions.DisableFormatting));
    }

    private static XElement BuildExpressionIdentificatie(ZipAnalysisResult analysis, string huidigJaartal, string datumTijd, string datum)
    {
        var bevoegdGezag = analysis.BevoegdGezag ?? "onbekend";

        return new XElement(DataNs + "ExpressionIdentificatie",
            new XElement(DataNs + "FRBRWork", $"/akn/nl/bill/{bevoegdGezag}/{huidigJaartal}/OTSTgegenereerd{datumTijd}"),
            new XElement(DataNs + "FRBRExpression", $"/akn/nl/bill/{bevoegdGezag}/{huidigJaartal}/OTSTgegenereerd{datumTijd}/nld@{datum};1"),
            new XElement(DataNs + "soortWork", "/join/id/stop/work_003")
        );
    }

    private XElement BuildBesluitMetadata(ZipArchive archive, ZipAnalysisResult analysis)
    {
        var metadataElement = new XElement(DataNs + "BesluitMetadata");

        foreach (var element in LoadRegelingMetadata(archive))
        {
            metadataElement.Add(element);
        }

        metadataElement.Add(new XElement(DataNs + "soortProcedure", "/join/id/stop/proceduretype_definitief"));

        if (analysis.InformatieObjecten.Count > 0)
        {
            var refs = new XElement(DataNs + "informatieobjectRefs");
            foreach (var io in analysis.InformatieObjecten)
            {
                if (!string.IsNullOrWhiteSpace(io.FrbrExpression))
                {
                    refs.Add(new XElement(DataNs + "informatieobjectRef", io.FrbrExpression));
                }
            }
            metadataElement.Add(refs);
        }

        if (!metadataElement.Elements(DataNs + "maker").Any() && !string.IsNullOrWhiteSpace(analysis.BevoegdGezag))
        {
            metadataElement.Add(new XElement(DataNs + "maker", $"/tooi/id/gemeente/{analysis.BevoegdGezag}"));
        }

        if (!metadataElement.Elements(DataNs + "eindverantwoordelijke").Any() && !string.IsNullOrWhiteSpace(analysis.BevoegdGezag))
        {
            metadataElement.Add(new XElement(DataNs + "eindverantwoordelijke", $"/tooi/id/gemeente/{analysis.BevoegdGezag}"));
        }

        return metadataElement;
    }

    private IEnumerable<XElement> LoadRegelingMetadata(ZipArchive archive)
    {
        var metadataEntry = archive.GetEntry("Regeling/Metadata.xml");
        if (metadataEntry is null)
        {
            yield break;
        }

        using var stream = metadataEntry.Open();
        var doc = XDocument.Load(stream);
        var root = doc.Root;
        if (root is null)
        {
            yield break;
        }

        foreach (var child in root.Elements())
        {
            if (string.Equals(child.Name.LocalName, "soortRegeling", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(child.Name.LocalName, "overheidsdomeinen", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(child.Name.LocalName, "onderwerpen", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(child.Name.LocalName, "rechtsgebieden", StringComparison.OrdinalIgnoreCase))
            {
                var list = new XElement(DataNs + child.Name.LocalName);
                foreach (var sub in child.Elements())
                {
                    list.Add(new XElement(DataNs + sub.Name.LocalName, sub.Value.Trim()));
                }
                yield return list;
            }
            else
            {
                yield return ConvertElementToNamespace(child, DataNs);
            }
        }
    }

    private XElement BuildProcedureverloop(DateOnly tomorrow)
    {
        var dateText = tomorrow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return new XElement(DataNs + "Procedureverloop",
            new XElement(DataNs + "bekendOp", dateText),
            new XElement(DataNs + "procedurestappen",
                new XElement(DataNs + "Procedurestap",
                    new XElement(DataNs + "soortStap", "/join/id/stop/procedure/stap_003"),
                    new XElement(DataNs + "voltooidOp", dateText)
                )
            )
        );
    }

    private XElement BuildConsolidatieInformatie(ZipAnalysisResult analysis, DateOnly tomorrow)
    {
        var dateText = tomorrow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var consolidatie = new XElement(DataNs + "ConsolidatieInformatie",
            new XElement(DataNs + "BeoogdeRegelgeving",
                new XElement(DataNs + "BeoogdeRegeling",
                    new XElement(DataNs + "doelen",
                        new XElement(DataNs + "doel", analysis.Doel ?? string.Empty)
                    ),
                    new XElement(DataNs + "instrumentVersie", analysis.FrbrExpression ?? string.Empty),
                    new XElement(DataNs + "eId", "art_besluit1")
                )
            ),
            new XElement(DataNs + "Tijdstempels",
                new XElement(DataNs + "Tijdstempel",
                    new XElement(DataNs + "doel", analysis.Doel ?? string.Empty),
                    new XElement(DataNs + "soortTijdstempel", "juridischWerkendVanaf"),
                    new XElement(DataNs + "datum", dateText),
                    new XElement(DataNs + "eId", "art_besluit2")
                )
            )
        );

        var beoogdeRegelgeving = consolidatie.Element(DataNs + "BeoogdeRegelgeving");
        if (beoogdeRegelgeving is not null)
        {
            foreach (var io in analysis.InformatieObjecten)
            {
                if (string.IsNullOrWhiteSpace(io.FrbrExpression) || string.IsNullOrWhiteSpace(io.ExtIoRefEId))
                {
                    continue;
                }

                beoogdeRegelgeving.Add(
                    new XElement(DataNs + "BeoogdInformatieobject",
                        new XElement(DataNs + "doelen",
                            new XElement(DataNs + "doel", analysis.Doel ?? string.Empty)
                        ),
                        new XElement(DataNs + "instrumentVersie", io.FrbrExpression),
                        new XElement(DataNs + "eId", $"!main#{io.ExtIoRefEId}")
                    )
                );
            }
        }

        return consolidatie;
    }

    private XElement BuildBesluitCompact(ZipArchive archive, ZipAnalysisResult analysis)
    {
        var compact = new XElement(TekstNs + "BesluitCompact",
            new XAttribute(XNamespace.Xmlns + "tekst", TekstNs.NamespaceName),
            new XElement(TekstNs + "RegelingOpschrift",
                new XAttribute("eId", "longTitle"),
                new XAttribute("wId", "longTitle"),
                new XElement(TekstNs + "Al", "Officiele titel van de aanlevering")
            ),
            new XElement(TekstNs + "Aanhef",
                new XAttribute("eId", "formula_1"),
                new XAttribute("wId", "formula_1"),
                new XElement(TekstNs + "Al", "Aanhef van het besluit")
            ),
            new XElement(TekstNs + "Lichaam",
                new XAttribute("eId", "body"),
                new XAttribute("wId", "body"),
                new XElement(TekstNs + "WijzigArtikel",
                    new XAttribute("eId", "art_besluit1"),
                    new XAttribute("wId", $"{analysis.BevoegdGezag}__art_besluit1"),
                    new XElement(TekstNs + "Kop",
                        new XElement(TekstNs + "Label", "Artikel"),
                        new XElement(TekstNs + "Nummer", "I")
                    ),
                    new XElement(TekstNs + "Wat",
                        "Wijzigingen zoals opgenomen in ",
                        new XElement(TekstNs + "IntRef", new XAttribute("ref", "cmp_besluit"), "Bijlage A"),
                        " worden vastgesteld."
                    )
                ),
                new XElement(TekstNs + "Artikel",
                    new XAttribute("eId", "art_besluit2"),
                    new XAttribute("wId", $"{analysis.BevoegdGezag}__art_besluit2"),
                    new XElement(TekstNs + "Kop",
                        new XElement(TekstNs + "Label", "Artikel"),
                        new XElement(TekstNs + "Nummer", "II")
                    ),
                    new XElement(TekstNs + "Inhoud",
                        new XElement(TekstNs + "Al", $"Dit besluit treedt in werking per {GetNextWorkingDay(_timeProvider.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}")
                    )
                )
            ),
            new XElement(TekstNs + "Sluiting",
                new XAttribute("eId", "formula_2"),
                new XAttribute("wId", "formula_2"),
                new XElement(TekstNs + "Al", "Sluiting van het besluit"),
                new XElement(TekstNs + "Ondertekening",
                    new XElement(TekstNs + "Al", "Ondertekening van het besluit")
                )
            )
        );

        var wijzigBijlage = new XElement(TekstNs + "WijzigBijlage",
            new XAttribute("eId", "cmp_besluit"),
            new XAttribute("wId", $"{analysis.BevoegdGezag}__cmp_besluit")
        );

        wijzigBijlage.Add(
            new XElement(TekstNs + "Kop",
                new XElement(TekstNs + "Label", "Bijlage"),
                new XElement(TekstNs + "Nummer", "A"),
                new XElement(TekstNs + "Opschrift", "Bijlage bij artikel I")
            )
        );

        var tekstEntry = archive.GetEntry("Regeling/Tekst.xml");
        if (tekstEntry != null)
        {
            using var stream = tekstEntry.Open();
            var tekstDoc = XDocument.Load(stream);
            var importedRoot = ConvertElementToNamespace(tekstDoc.Root!, TekstNs);
            importedRoot.SetAttributeValue("wordt", analysis.FrbrExpression ?? string.Empty);
            importedRoot.SetAttributeValue("componentnaam", "main");
            wijzigBijlage.Add(importedRoot);
        }

        compact.Add(wijzigBijlage);
        return compact;
    }

    private XElement BuildRegelingVersieInformatie(ZipArchive archive)
    {
        var regelingVersieInfo = new XElement("RegelingVersieInformatie");

        foreach (var file in RegelingFiles)
        {
            var entry = archive.GetEntry(file);
            if (entry is null)
            {
                continue;
            }

            using var stream = entry.Open();
            var doc = XDocument.Load(stream);
            var root = doc.Root;
            if (root is null)
            {
                continue;
            }

            regelingVersieInfo.Add(ConvertElementToNamespace(root, DataNs));
        }

        return regelingVersieInfo;
    }

    private byte[] GenerateOpdrachtXml(ZipAnalysisResult analysis, string datumTijd, DateOnly datumBekendmaking, bool isValidation)
    {
        var rootName = isValidation ? "validatieOpdracht" : "publicatieOpdracht";
        var rootNs = XNamespace.Get("http://www.overheid.nl/2017/lvbb");
        var datePart = datumTijd.Substring(0, 8);
        var timePart = datumTijd.Substring(8);
        var aanbodType = isValidation ? "OTST_val" : "OTST_pub";

        var bevoegdGezag = analysis.BevoegdGezag ?? "onbekend";

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(rootNs + rootName,
                new XElement(rootNs + "idLevering", $"{aanbodType}_{bevoegdGezag}_{datePart}_{timePart}"),
                new XElement(rootNs + "idBevoegdGezag", "00000001003214345000"),
                new XElement(rootNs + "idAanleveraar", "00000001003214345000"),
                new XElement(rootNs + "publicatie", "besluit.xml"),
                new XElement(rootNs + "datumBekendmaking", datumBekendmaking.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            )
        );

        return Encoding.UTF8.GetBytes(doc.ToString(SaveOptions.DisableFormatting));
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

    private static DateOnly GetNextWorkingDay(DateOnly date)
    {
        var next = date.AddDays(1);
        while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            next = next.AddDays(1);
        }
        return next;
    }

    internal sealed record BesluitResult(byte[] BesluitXml, byte[] OpdrachtXml);
}

