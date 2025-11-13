using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using OTST.Domain.Abstractions;
using OTST.Domain.Models;

namespace OTST.Domain.Services.Validation;

public sealed class ValidationTransformationService
{
    private readonly ZipAnalyser _zipAnalyser = new();
    private readonly IoProcessor _ioProcessor;
    private readonly BesluitGenerator _besluitGenerator;
    private readonly ITimeProvider _timeProvider;

    public ValidationTransformationService(ITimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? SystemTimeProvider.Instance;
        _ioProcessor = new IoProcessor(_timeProvider);
        _besluitGenerator = new BesluitGenerator(_timeProvider);
    }

    public static string GetDefaultOutputPath(string sourceZipPath, bool isValidation)
    {
        var directory = Path.GetDirectoryName(sourceZipPath) ?? Directory.GetCurrentDirectory();
        var fileName = isValidation ? "validatieOpdracht_initieel.zip" : "publicatieOpdracht_initieel.zip";
        return Path.Combine(directory, fileName);
    }

    public static string GetReportPath(string outputZipPath)
    {
        var directory = Path.GetDirectoryName(outputZipPath) ?? Directory.GetCurrentDirectory();
        var baseName = Path.GetFileNameWithoutExtension(outputZipPath);
        return Path.Combine(directory, $"{baseName}_rapport.txt");
    }

    public TransformationResult TransformValidation(string sourceZipPath, string outputZipPath, bool isValidation)
    {
        if (!File.Exists(sourceZipPath))
        {
            throw new FileNotFoundException("Bron ZIP is niet gevonden.", sourceZipPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputZipPath)!);

        using var sourceArchive = ZipFile.OpenRead(sourceZipPath);
        var analysis = _zipAnalyser.AnalyseAsync(sourceArchive).GetAwaiter().GetResult();

        using var outputStream = File.Create(outputZipPath);
        using var targetArchive = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: true);

        var addedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileOrder = new List<string>();
        var reportBuilder = new StringBuilder();

        foreach (var io in analysis.InformatieObjecten)
        {
            var result = _ioProcessor.Process(sourceArchive, analysis, io);
            AddEntry(targetArchive, result.IoXmlFileName, result.IoXmlContent, addedFiles, fileOrder);

            foreach (var file in result.AdditionalFiles)
            {
                AddEntry(targetArchive, file.FileName, file.Content, addedFiles, fileOrder);
            }
        }

        var besluitResult = _besluitGenerator.Generate(sourceArchive, analysis, isValidation);
        AddEntry(targetArchive, "besluit.xml", besluitResult.BesluitXml, addedFiles, fileOrder);
        AddEntry(targetArchive, "opdracht.xml", besluitResult.OpdrachtXml, addedFiles, fileOrder);

        CopyRemainingEntries(sourceArchive, targetArchive, addedFiles, fileOrder);

        var manifestBytes = ManifestBuilder.BuildManifest(fileOrder.Concat(new[] { "manifest.xml" }), isIntrekking: false);
        AddEntry(targetArchive, "manifest.xml", manifestBytes, addedFiles, fileOrder);

        var reportPath = GetReportPath(outputZipPath);
        WriteReport(reportBuilder, analysis, outputZipPath, fileOrder);
        File.WriteAllText(reportPath, reportBuilder.ToString(), Encoding.UTF8);

        return new TransformationResult(outputZipPath, reportPath, fileOrder);
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

            if (entry.FullName.StartsWith("IO-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(entry.FullName, "pakbon.xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string targetName = entry.FullName;

            if (entry.FullName.StartsWith("Regeling/", StringComparison.OrdinalIgnoreCase))
            {
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
                targetName = Path.GetFileName(entry.FullName);
            }

            if (string.IsNullOrEmpty(targetName) || addedFiles.Contains(targetName))
            {
                continue;
            }

            var newEntry = targetArchive.CreateEntry(targetName, CompressionLevel.Optimal);
            using var source = entry.Open();
            using var destination = newEntry.Open();
            source.CopyTo(destination);

            addedFiles.Add(targetName);
            fileOrder.Add(targetName);
        }
    }

    private static void WriteReport(StringBuilder builder, ZipAnalysisResult analysis, string outputZipPath, IEnumerable<string> files)
    {
        builder.AppendLine("Rapport Omgevingswet Test Suite Tool (.NET)");
        builder.AppendLine("==========================================");
        builder.AppendLine();
        builder.AppendLine($"Output: {outputZipPath}");
        builder.AppendLine($"FRBR Work: {analysis.FrbrWork ?? "onbekend"}");
        builder.AppendLine($"FRBR Expression: {analysis.FrbrExpression ?? "onbekend"}");
        builder.AppendLine($"Bevoegd gezag: {analysis.BevoegdGezag ?? "onbekend"}");
        builder.AppendLine($"Aantal informatieobjecten: {analysis.AantalInformatieObjecten}");
        builder.AppendLine();
        builder.AppendLine("Bestanden in resultaat:");
        foreach (var file in files)
        {
            builder.AppendLine($"- {file}");
        }
    }
}

