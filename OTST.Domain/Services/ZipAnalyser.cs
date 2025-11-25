using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Xml.Linq;
using OTST.Domain.Models;

namespace OTST.Domain.Services;

/// <summary>
/// Provides functionality to analyse STOP-compliant ZIP archives and extract the metadata
/// required by the OTST workflow.
/// </summary>
public sealed class ZipAnalyser
{
    private static readonly XNamespace StopDataNs = "https://standaarden.overheid.nl/stop/imop/data/";
    private static readonly XNamespace StopTekstNs = "https://standaarden.overheid.nl/stop/imop/tekst/";

    /// <summary>
    /// Analyse a STOP ZIP archive from a stream.
    /// </summary>
    /// <param name="zipStream">Stream pointing to a ZIP archive. Ownership is not transferred.</param>
    public async Task<ZipAnalysisResult> AnalyseAsync(Stream zipStream)
    {
        if (zipStream is null) throw new ArgumentNullException(nameof(zipStream));
        if (!zipStream.CanRead) throw new ArgumentException("Stream must be readable", nameof(zipStream));

        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        return await AnalyseAsync(archive).ConfigureAwait(false);
    }

    /// <summary>
    /// Analyse a STOP ZIP archive from a file on disk.
    /// </summary>
    public async Task<ZipAnalysisResult> AnalyseAsync(string zipPath)
    {
        if (string.IsNullOrWhiteSpace(zipPath)) throw new ArgumentException("Value cannot be null or whitespace.", nameof(zipPath));
        await using var fileStream = File.OpenRead(zipPath);
        return await AnalyseAsync(fileStream).ConfigureAwait(false);
    }

    /// <summary>
    /// Analyse an already opened <see cref="ZipArchive"/>.
    /// </summary>
    internal Task<ZipAnalysisResult> AnalyseAsync(ZipArchive archive)
    {
        if (archive is null) throw new ArgumentNullException(nameof(archive));

        var resultBuilder = new ZipAnalysisResultBuilder();

        PopulateRegelingMetadata(archive, resultBuilder);
        PopulateIoFolders(archive, resultBuilder);
        PopulateTekstMetadata(archive, resultBuilder);

        var result = resultBuilder.Build();
        return Task.FromResult(result);
    }

    private static void PopulateRegelingMetadata(ZipArchive archive, ZipAnalysisResultBuilder builder)
    {
        // Regeling/Identificatie.xml
        if (TryLoadDocument(archive, "Regeling/Identificatie.xml", out var identificatieDoc))
        {
            builder.FrbrWork = FirstValue(identificatieDoc, StopDataNs + "FRBRWork")
                              ?? FirstValue(identificatieDoc, "FRBRWork");
            builder.FrbrExpression = FirstValue(identificatieDoc, StopDataNs + "FRBRExpression")
                                     ?? FirstValue(identificatieDoc, "FRBRExpression");
        }

        // Regeling/Momentopname.xml
        if (TryLoadDocument(archive, "Regeling/Momentopname.xml", out var momentopnameDoc))
        {
            builder.Doel = FirstValue(momentopnameDoc, StopDataNs + "doel")
                           ?? FirstValue(momentopnameDoc, "doel");
        }

        // Regeling/Metadata.xml
        if (TryLoadDocument(archive, "Regeling/Metadata.xml", out var metadataDoc))
        {
            var makerValue = FirstValue(metadataDoc, StopDataNs + "maker")
                             ?? FirstValue(metadataDoc, "maker");
            if (!string.IsNullOrWhiteSpace(makerValue))
            {
                var parts = makerValue.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length >= 2)
                {
                    var type = parts[^2];
                    var code = parts[^1];
                    if (IsValidAuthorityType(type))
                    {
                        builder.BevoegdGezag = code;
                    }
                }
            }
        }
    }

    private static void PopulateIoFolders(ZipArchive archive, ZipAnalysisResultBuilder builder)
    {
        var ioEntries = archive.Entries
            .Where(e => e.FullName.StartsWith("IO-", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var ioFolderNames = ioEntries
            .Select(e => e.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length > 0)
            .Select(parts => parts[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        long totalGmlSize = ioEntries
            .Where(e => e.FullName.EndsWith(".gml", StringComparison.OrdinalIgnoreCase))
            .Sum(e => e.Length);

        builder.AantalInformatieObjecten = ioFolderNames.Count;
        builder.TotaleGmlBestandsgrootte = totalGmlSize;

        foreach (var ioFolder in ioFolderNames)
        {
            builder.AddInformatieObject(BuildInformatieObject(archive, ioFolder));
        }
    }

    private static ZipAnalysisResult.InformatieObjectInfo BuildInformatieObject(ZipArchive archive, string ioFolder)
    {
        string folderPrefix = ioFolder.EndsWith('/') ? ioFolder : ioFolder + "/";
        folderPrefix = folderPrefix.StartsWith("IO-", StringComparison.OrdinalIgnoreCase) ? folderPrefix : "IO-" + folderPrefix;

        // Identificatie
        string? frbrWork = null;
        string? frbrExpression = null;
        if (TryLoadDocument(archive, folderPrefix + "Identificatie.xml", out var identificatieDoc))
        {
            frbrWork = FirstValue(identificatieDoc, StopDataNs + "FRBRWork")
                       ?? FirstValue(identificatieDoc, "FRBRWork");
            frbrExpression = FirstValue(identificatieDoc, StopDataNs + "FRBRExpression")
                             ?? FirstValue(identificatieDoc, "FRBRExpression");
        }

        // VersieMetadata
        string? officieleTitel = null;
        if (TryLoadDocument(archive, folderPrefix + "VersieMetadata.xml", out var versieMetadataDoc))
        {
            officieleTitel = FirstValue(versieMetadataDoc, StopDataNs + "officieleTitel")
                             ?? FirstValue(versieMetadataDoc, "officieleTitel");
        }

        // Locate GML/PDF and hash
        string? bestandsNaam = null;
        string? bestandHash = null;
        var fileEntry = archive.Entries
            .Where(e => e.FullName.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(e =>
                e.FullName.EndsWith(".gml", StringComparison.OrdinalIgnoreCase) ||
                e.FullName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

        if (fileEntry is not null)
        {
            bestandsNaam = Path.GetFileName(fileEntry.FullName);
            using var entryStream = fileEntry.Open();
            bestandHash = ComputeSha512(entryStream);
        }

        return new ZipAnalysisResult.InformatieObjectInfo(
            Folder: ioFolder,
            FrbrWork: frbrWork,
            FrbrExpression: frbrExpression,
            ExtIoRefEId: null, // populated later after Tekst.xml inspection
            Bestandsnaam: bestandsNaam,
            BestandHash: bestandHash,
            OfficieleTitel: officieleTitel);
    }

    private static void PopulateTekstMetadata(ZipArchive archive, ZipAnalysisResultBuilder builder)
    {
        if (!TryLoadDocument(archive, "Regeling/Tekst.xml", out var tekstDoc))
        {
            return;
        }

        var extIoRefs = tekstDoc.Descendants(StopTekstNs + "ExtIoRef")
            .Select(element => new ZipAnalysisResult.ExtIoRefInfo(
                element.Attribute("ref")?.Value ?? string.Empty,
                element.Attribute("eId")?.Value))
            .ToList();

        builder.ExtIoRefs.AddRange(extIoRefs);

        // Map ExtIoRef eIds back to IO entries using FRBR expression or work
        // Use GroupBy to handle duplicates - take first occurrence
        var infosByExpression = builder.InformatieObjecten
            .Where(io => !string.IsNullOrWhiteSpace(io.FrbrExpression))
            .GroupBy(io => io.FrbrExpression!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var infosByWork = builder.InformatieObjecten
            .Where(io => !string.IsNullOrWhiteSpace(io.FrbrWork))
            .GroupBy(io => io.FrbrWork!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var extIo in builder.ExtIoRefs)
        {
            if (string.IsNullOrWhiteSpace(extIo.Ref) || string.IsNullOrWhiteSpace(extIo.EId))
            {
                continue;
            }

            if (infosByExpression.TryGetValue(extIo.Ref, out var ioByExpression))
            {
                ioByExpression.ExtIoRefEId = extIo.EId;
            }
            else if (infosByWork.TryGetValue(extIo.Ref, out var ioByWork))
            {
                ioByWork.ExtIoRefEId = extIo.EId;
            }
        }
    }

    private static bool TryLoadDocument(ZipArchive archive, string entryPath, out XDocument document)
    {
        var entry = archive.GetEntry(entryPath);
        if (entry is null)
        {
            document = default!;
            return false;
        }

        using var entryStream = entry.Open();
        document = XDocument.Load(entryStream, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        return true;
    }

    private static string? FirstValue(XDocument doc, XName elementName) =>
        doc.Descendants(elementName).FirstOrDefault()?.Value?.Trim();

    private static string? FirstValue(XDocument doc, string localName) =>
        doc.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))?.Value?.Trim();

    private static string ComputeSha512(Stream stream)
    {
        using var sha = SHA512.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsValidAuthorityType(string type) =>
        type.Equals("gemeente", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("provincie", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("ministerie", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("waterschap", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Builder used to collect data while traversing the ZIP archive.
    /// </summary>
    private sealed class ZipAnalysisResultBuilder
    {
        public string? FrbrWork { get; set; }
        public string? FrbrExpression { get; set; }
        public string? Doel { get; set; }
        public string? BevoegdGezag { get; set; }
        public int AantalInformatieObjecten { get; set; }
        public long TotaleGmlBestandsgrootte { get; set; }
        public List<MutableIoInfo> InformatieObjecten { get; } = new();
        public List<ZipAnalysisResult.ExtIoRefInfo> ExtIoRefs { get; } = new();

        public void AddInformatieObject(ZipAnalysisResult.InformatieObjectInfo info)
        {
            InformatieObjecten.Add(new MutableIoInfo(info));
        }

        public ZipAnalysisResult Build()
        {
            return new ZipAnalysisResult
            {
                FrbrWork = FrbrWork,
                FrbrExpression = FrbrExpression,
                Doel = Doel,
                BevoegdGezag = BevoegdGezag,
                AantalInformatieObjecten = AantalInformatieObjecten,
                TotaleGmlBestandsgrootte = TotaleGmlBestandsgrootte,
                InformatieObjecten = InformatieObjecten
                    .Select(io => new ZipAnalysisResult.InformatieObjectInfo(
                        io.Folder,
                        io.FrbrWork,
                        io.FrbrExpression,
                        io.ExtIoRefEId,
                        io.Bestandsnaam,
                        io.BestandHash,
                        io.OfficieleTitel))
                    .ToList(),
                ExtIoRefs = ExtIoRefs
            };
        }

        public sealed class MutableIoInfo
        {
            public MutableIoInfo(ZipAnalysisResult.InformatieObjectInfo info)
            {
                Folder = info.Folder;
                FrbrWork = info.FrbrWork;
                FrbrExpression = info.FrbrExpression;
                ExtIoRefEId = info.ExtIoRefEId;
                Bestandsnaam = info.Bestandsnaam;
                BestandHash = info.BestandHash;
                OfficieleTitel = info.OfficieleTitel;
            }

            public string Folder { get; }
            public string? FrbrWork { get; }
            public string? FrbrExpression { get; }
            public string? ExtIoRefEId { get; set; }
            public string? Bestandsnaam { get; }
            public string? BestandHash { get; }
            public string? OfficieleTitel { get; }

            public static implicit operator ZipAnalysisResult.InformatieObjectInfo(MutableIoInfo info) =>
                new(info.Folder, info.FrbrWork, info.FrbrExpression, info.ExtIoRefEId, info.Bestandsnaam, info.BestandHash, info.OfficieleTitel);
        }
    }
}

