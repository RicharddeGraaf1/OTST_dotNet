using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OTST.Domain.Abstractions;
using OTST.Domain.Models;
using OTST.Domain.Services.Intrekking;

namespace OTST.Domain.Services;

public sealed class IntrekkingTransformationService
{
    private readonly ZipAnalyser _zipAnalyser;
    private readonly IntrekkingProcessor _processor;
    private readonly ITimeProvider _timeProvider;

    public IntrekkingTransformationService(ITimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? SystemTimeProvider.Instance;
        _zipAnalyser = new ZipAnalyser();
        _processor = new IntrekkingProcessor(_timeProvider);
    }

    public static string GetDefaultOutputPath(string sourceZipPath, bool isValidation)
    {
        var directory = Path.GetDirectoryName(sourceZipPath)
                       ?? throw new InvalidOperationException("Kan doelmap niet bepalen.");
        var fileName = isValidation ? "intrekkingValidatieOpdracht_initieel.zip" : "intrekkingOpdracht_initieel.zip";
        return Path.Combine(directory, fileName);
    }

    public static string GetReportPath(string outputZipPath)
    {
        var directory = Path.GetDirectoryName(outputZipPath) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(outputZipPath);
        return Path.Combine(directory, $"{baseName}_rapport.txt");
    }

    public async Task<TransformationResult> TransformIntrekkingAsync(string sourceZipPath, string outputZipPath, bool isValidation)
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

        var intrekkingResult = _processor.CreateIntrekking(sourceArchive, analysis, isValidation);
        AddEntry(targetArchive, "intrekkingsbesluit.xml", intrekkingResult.BesluitXml, addedFiles, fileOrder);
        AddEntry(targetArchive, "opdracht.xml", intrekkingResult.OpdrachtXml, addedFiles, fileOrder);

        foreach (var modified in intrekkingResult.ModifiedFiles)
        {
            AddEntry(targetArchive, modified.Key, modified.Value, addedFiles, fileOrder);
        }

        CopyRemainingEntries(sourceArchive, targetArchive, addedFiles, fileOrder);

        var manifestFiles = fileOrder.Concat(new[] { "manifest.xml" });
        var manifestBytes = ManifestBuilder.BuildManifest(manifestFiles, isIntrekking: true);
        AddEntry(targetArchive, "manifest.xml", manifestBytes, addedFiles, fileOrder);

        var reportPath = GetReportPath(outputZipPath);
        WriteReport(reportBuilder, analysis, outputZipPath, intrekkingResult.DoelId, addedFiles);
        await File.WriteAllTextAsync(reportPath, reportBuilder.ToString(), Encoding.UTF8).ConfigureAwait(false);

        return new TransformationResult(outputZipPath, reportPath, addedFiles.ToArray());
    }

    private static void AddEntry(ZipArchive archive, string entryName, byte[] content, ISet<string> addedFiles, IList<string> fileOrder)
    {
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

            if (entry.FullName.StartsWith("IO-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(Path.GetFileName(entry.FullName), "pakbon.xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var targetName = entry.FullName;

            if (entry.FullName.StartsWith("Regeling/", StringComparison.OrdinalIgnoreCase))
            {
                // Intrekking resultaten bevatten de Regeling bestanden niet
                continue;
            }

            if (entry.FullName.StartsWith("OW-bestanden/", StringComparison.OrdinalIgnoreCase))
            {
                targetName = Path.GetFileName(entry.FullName);
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

