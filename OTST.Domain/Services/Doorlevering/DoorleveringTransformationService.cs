using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using OTST.Domain.Abstractions;
using OTST.Domain.Models;
using OTST.Domain.Services.Doorlevering;

namespace OTST.Domain.Services;

public sealed class DoorleveringTransformationService
{
    private readonly ZipAnalyser _zipAnalyser;
    private readonly DoorleveringProcessor _processor;
    private readonly ITimeProvider _timeProvider;

    public DoorleveringTransformationService(ITimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? SystemTimeProvider.Instance;
        _zipAnalyser = new ZipAnalyser();
        _processor = new DoorleveringProcessor(_timeProvider);
    }

    public static string GetDefaultOutputPath(string sourceZipPath, bool isValidation)
    {
        var directory = Path.GetDirectoryName(sourceZipPath)
                       ?? throw new InvalidOperationException("Kan doelmap niet bepalen.");
        var fileName = isValidation ? "doorleveringValidatieOpdracht_initieel.zip" : "doorleveringOpdracht_initieel.zip";
        return Path.Combine(directory, fileName);
    }

    public static string GetReportPath(string outputZipPath)
    {
        var directory = Path.GetDirectoryName(outputZipPath) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(outputZipPath);
        return Path.Combine(directory, $"{baseName}_rapport.txt");
    }

    public TransformationResult TransformDoorlevering(string sourceZipPath, string outputZipPath, bool isValidation)
    {
        return TransformDoorleveringAsync(sourceZipPath, outputZipPath, isValidation).GetAwaiter().GetResult();
    }

    public async Task<TransformationResult> TransformDoorleveringAsync(string sourceZipPath, string outputZipPath, bool isValidation)
    {
        if (!File.Exists(sourceZipPath))
        {
            throw new FileNotFoundException("Bron ZIP is niet gevonden.", sourceZipPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputZipPath)!);

        using var sourceArchive = ZipFile.OpenRead(sourceZipPath);
        var analysis = await _zipAnalyser.AnalyseAsync(sourceArchive).ConfigureAwait(false);

        using var outputStream = File.Create(outputZipPath);
        using var targetArchive = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: true);

        var addedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileOrder = new List<string>();
        var reportBuilder = new StringBuilder();

        var doorleveringResult = _processor.CreateDoorlevering(sourceArchive, analysis, isValidation);
        AddEntry(targetArchive, "proefversiebesluit.xml", doorleveringResult.ProefversieBesluitXml, addedFiles, fileOrder);
        AddEntry(targetArchive, "consolidaties.xml", doorleveringResult.ConsolidatiesXml, addedFiles, fileOrder);

        foreach (var modified in doorleveringResult.ModifiedFiles)
        {
            AddEntry(targetArchive, modified.Key, modified.Value, addedFiles, fileOrder);
        }

        CopyRemainingEntries(sourceArchive, targetArchive, addedFiles, fileOrder);

        // Update manifest-ow.xml with all OW files that were copied
        UpdateManifestOw(targetArchive, addedFiles, fileOrder, doorleveringResult.DoelId, analysis.FrbrWork);

        var manifestFiles = fileOrder.Concat(new[] { "manifest.xml" });
        var manifestBytes = ManifestBuilder.BuildManifest(manifestFiles, isIntrekking: false);
        AddEntry(targetArchive, "manifest.xml", manifestBytes, addedFiles, fileOrder);

        var reportPath = GetReportPath(outputZipPath);
        WriteReport(reportBuilder, analysis, outputZipPath, doorleveringResult.DoelId, addedFiles);
        await File.WriteAllTextAsync(reportPath, reportBuilder.ToString(), Encoding.UTF8).ConfigureAwait(false);

        return new TransformationResult(outputZipPath, reportPath, addedFiles.ToArray());
    }

    private static void AddEntry(ZipArchive archive, string entryName, byte[] content, ISet<string> addedFiles, IList<string> fileOrder)
    {
        if (addedFiles.Contains(entryName))
        {
            return;
        }

        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using (var entryStream = entry.Open())
        {
            entryStream.Write(content, 0, content.Length);
        }
        addedFiles.Add(entryName);
        fileOrder.Add(entryName);
    }

    private static void CopyRemainingEntries(ZipArchive sourceArchive, ZipArchive targetArchive, ISet<string> addedFiles, IList<string> fileOrder)
    {
        foreach (var entry in sourceArchive.Entries)
        {
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            // IO-bestanden (GML) moeten worden gekopieerd voor doorlevering
            // Ze worden gekopieerd met alleen de bestandsnaam (zonder "IO-" prefix)
            // Skip metadata bestanden uit IO- directories
            if (entry.FullName.StartsWith("IO-", StringComparison.OrdinalIgnoreCase))
            {
                var ioFileName = Path.GetFileName(entry.FullName);
                // Skip metadata bestanden
                if (ioFileName is "Identificatie.xml" or "JuridischeBorgingVan.xml" or "Metadata.xml" or 
                    "Momentopname.xml" or "VersieMetadata.xml")
                {
                    continue;
                }
                
                // Kopieer alle GML en XML bestanden uit IO- directories (inclusief bestanden met lange namen)
                var ext = Path.GetExtension(ioFileName).ToLowerInvariant();
                if (ext is ".gml" or ".xml")
                {
                    if (addedFiles.Contains(ioFileName))
                    {
                        continue;
                    }

                    var ioEntry = targetArchive.CreateEntry(ioFileName, CompressionLevel.Optimal);
                    using var ioSourceStream = entry.Open();
                    using var ioTargetStream = ioEntry.Open();
                    ioSourceStream.CopyTo(ioTargetStream);
                    addedFiles.Add(ioFileName);
                    fileOrder.Add(ioFileName);
                }
                continue;
            }

            if (string.Equals(Path.GetFileName(entry.FullName), "pakbon.xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Skip divisies.xml als het niet in OW-bestanden/ zit (wordt alleen gebruikt voor gm0590)
            // divisies.xml wordt alleen gebruikt als het in OW-bestanden/ zit, anders wordt divisieaanduidingen.xml gebruikt
            var fileNameCheck = Path.GetFileName(entry.FullName);
            if (string.Equals(fileNameCheck, "divisies.xml", StringComparison.OrdinalIgnoreCase) &&
                !entry.FullName.StartsWith("OW-bestanden/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Skip manifest-ow.xml - wordt later bijgewerkt met alle OW-bestanden
            if (string.Equals(Path.GetFileName(entry.FullName), "manifest-ow.xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var targetName = entry.FullName;

            if (entry.FullName.StartsWith("Regeling/", StringComparison.OrdinalIgnoreCase))
            {
                // Regeling bestanden worden opgenomen in proefversiebesluit.xml en consolidaties.xml
                // Alleen afbeeldingen uit Regeling/ worden gekopieerd
                var fileName = Path.GetFileName(entry.FullName);
                var ext = Path.GetExtension(fileName).ToLowerInvariant();
                if (ext is ".jpg" or ".jpeg" or ".png")
                {
                    targetName = fileName;
                }
                else
                {
                    continue;
                }
            }
            else if (entry.FullName.StartsWith("OW-bestanden/", StringComparison.OrdinalIgnoreCase))
            {
                var fileName = Path.GetFileName(entry.FullName);
                // Map bestandsnamen voor doorlevering
                targetName = fileName switch
                {
                    "divisieteksten.xml" => "divisieaanduidingen.xml",
                    "regelingsgebieden.xml" => "regelingsgebied.xml",
                    "regelingsgebied.xml" => "owRegelingsgebied.xml",
                    "gebieden.xml" => "owGebied.xml",
                    "gebiedengroepen.xml" => "owGebiedengroep.xml",
                    "regelteksten.xml" => "owRegeltekst.xml",
                    "regelsvooriedereen.xml" => "owRegelVoorIedereen.xml",
                    "ambtsgebieden.xml" => "owAmbtsgebied.xml",
                    _ => fileName
                };
            }
            else
            {
                // Voor bestanden buiten OW-bestanden/ en Regeling/, gebruik alleen de bestandsnaam
                // Alleen specifieke hernoemingen voor bestanden die niet in OW-bestanden/ zitten
                var fileName = Path.GetFileName(entry.FullName);
                targetName = fileName switch
                {
                    // divisieteksten.xml wordt alleen hernoemd als het in OW-bestanden/ zit
                    // regelingsgebieden.xml wordt alleen hernoemd als het in OW-bestanden/ zit
                    // Andere bestanden worden niet hernoemd als ze niet in OW-bestanden/ zitten
                    // Bestanden met lange namen (zoals .gml en .xml) worden gekopieerd met hun originele naam
                    _ => fileName
                };
            }

            if (addedFiles.Contains(targetName))
            {
                continue;
            }

            var newEntry = targetArchive.CreateEntry(targetName, CompressionLevel.Optimal);
            using var sourceStream = entry.Open();
            using var targetStream = newEntry.Open();
            sourceStream.CopyTo(targetStream);
            addedFiles.Add(targetName);
            fileOrder.Add(targetName);
        }
    }

    private static void UpdateManifestOw(ZipArchive targetArchive, ISet<string> addedFiles, IList<string> fileOrder, string doelId, string? frbrWork)
    {
        var manifestOwNs = (XNamespace)"http://www.geostandaarden.nl/bestanden-ow/manifest-ow";
        
        // Maak een nieuw manifest-ow.xml document op basis van de toegevoegde bestanden
        // (We kunnen niet zoeken in Create mode, dus maken we het altijd opnieuw)
        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(manifestOwNs + "Aanleveringen",
                new XElement(manifestOwNs + "domein", "omgevingswet"),
                new XElement(manifestOwNs + "Aanlevering",
                    new XElement(manifestOwNs + "WorkIDRegeling", frbrWork ?? string.Empty),
                    new XElement(manifestOwNs + "DoelID", doelId)
                )
            )
        );
        
        // Update DoelID
        var doelElement = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "DoelID");
        if (doelElement != null)
        {
            doelElement.Value = doelId;
        }

        // Update WorkIDRegeling
        if (!string.IsNullOrEmpty(frbrWork))
        {
            var workIdElement = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "WorkIDRegeling");
            if (workIdElement != null)
            {
                workIdElement.Value = frbrWork;
            }
        }

        // Verzamel alle OW-bestanden die zijn toegevoegd (exclusief manifest-ow.xml zelf)
        var owFiles = addedFiles
            .Where(f => f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) 
                     && !f.Equals("manifest-ow.xml", StringComparison.OrdinalIgnoreCase)
                     && !f.Equals("manifest.xml", StringComparison.OrdinalIgnoreCase)
                     && !f.Equals("proefversiebesluit.xml", StringComparison.OrdinalIgnoreCase)
                     && !f.Equals("consolidaties.xml", StringComparison.OrdinalIgnoreCase)
                     && !f.StartsWith("IO-", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        // Verwijder bestaande Bestand elementen
        var aanlevering = document.Descendants(manifestOwNs + "Aanlevering").FirstOrDefault();
        if (aanlevering != null)
        {
            var existingBestanden = aanlevering.Elements(manifestOwNs + "Bestand").ToList();
            foreach (var bestand in existingBestanden)
            {
                bestand.Remove();
            }

            // Voeg nieuwe Bestand elementen toe
            foreach (var fileName in owFiles)
            {
                var bestand = new XElement(manifestOwNs + "Bestand",
                    new XElement(manifestOwNs + "naam", fileName));
                
                // Voeg objecttypes toe op basis van bestandsnaam
                if (fileName.Contains("divisies", StringComparison.OrdinalIgnoreCase))
                {
                    bestand.Add(new XElement(manifestOwNs + "objecttype", "Divisie"));
                    bestand.Add(new XElement(manifestOwNs + "objecttype", "Divisietekst"));
                }
                else if (fileName.Contains("regelingsgebied", StringComparison.OrdinalIgnoreCase))
                {
                    bestand.Add(new XElement(manifestOwNs + "objecttype", "Regelingsgebied"));
                }
                else if (fileName.Contains("tekstdelen", StringComparison.OrdinalIgnoreCase))
                {
                    bestand.Add(new XElement(manifestOwNs + "objecttype", "Tekstdeel"));
                }
                else if (fileName.Contains("ambtsgebieden", StringComparison.OrdinalIgnoreCase))
                {
                    bestand.Add(new XElement(manifestOwNs + "objecttype", "Ambtsgebied"));
                }

                aanlevering.Add(bestand);
            }
        }

        // Genereer de bijgewerkte inhoud
        var settings = new System.Xml.XmlWriterSettings
        {
            Encoding = Encoding.UTF8,
            Indent = false,
            OmitXmlDeclaration = false
        };

        using var ms = new MemoryStream();
        using (var writer = System.Xml.XmlWriter.Create(ms, settings))
        {
            document.Save(writer);
        }

        // Als manifest-ow.xml al bestaat, moeten we het vervangen
        // Omdat ZipArchive entries niet kunnen worden verwijderd, moeten we het overslaan
        // in CopyRemainingEntries en hier toevoegen. Als het al bestaat van ProcessOwFiles,
        // wordt het hier overschreven door een nieuwe entry met dezelfde naam.
        // (ZipArchive zal de laatste entry gebruiken)
        
        var newEntry = targetArchive.CreateEntry("manifest-ow.xml", CompressionLevel.Optimal);
        using (var newStream = newEntry.Open())
        {
            ms.Position = 0;
            ms.CopyTo(newStream);
        }
        
        if (!addedFiles.Contains("manifest-ow.xml"))
        {
            addedFiles.Add("manifest-ow.xml");
            fileOrder.Add("manifest-ow.xml");
        }
    }

    private static void WriteReport(StringBuilder builder, ZipAnalysisResult analysis, string outputZipPath, string doelId, IEnumerable<string> files)
    {
        builder.AppendLine("Rapport Omgevingswet Test Suite Tool (.NET)");
        builder.AppendLine("==========================================");
        builder.AppendLine();
        builder.AppendLine($"Output: {outputZipPath}");
        builder.AppendLine($"FRBR Work: {analysis.FrbrWork ?? "onbekend"}");
        builder.AppendLine($"FRBR Expression: {analysis.FrbrExpression ?? "onbekend"}");
        builder.AppendLine($"Bevoegd gezag: {analysis.BevoegdGezag ?? "onbekend"}");
        builder.AppendLine($"Aantal informatieobjecten: {analysis.AantalInformatieObjecten}");
        builder.AppendLine($"Doel-ID: {doelId}");
        builder.AppendLine();
        builder.AppendLine("Bestanden in resultaat:");
        foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- {file}");
        }
    }
}
