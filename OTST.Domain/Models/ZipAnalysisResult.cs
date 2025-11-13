using System.Collections.Generic;

namespace OTST.Domain.Models;

/// <summary>
/// Result of analysing an OTST-compliant STOP ZIP archive.
/// Mirrors the data collected by the original Java implementation.
/// </summary>
public sealed class ZipAnalysisResult
{
    public string? FrbrWork { get; init; }
    public string? FrbrExpression { get; init; }
    public string? Doel { get; init; }
    public string? BevoegdGezag { get; init; }
    public int AantalInformatieObjecten { get; init; }
    public long TotaleGmlBestandsgrootte { get; init; }
    public IReadOnlyList<InformatieObjectInfo> InformatieObjecten { get; init; } = new List<InformatieObjectInfo>();
    public IReadOnlyList<ExtIoRefInfo> ExtIoRefs { get; init; } = new List<ExtIoRefInfo>();

    public sealed record InformatieObjectInfo(
        string Folder,
        string? FrbrWork,
        string? FrbrExpression,
        string? ExtIoRefEId,
        string? Bestandsnaam,
        string? BestandHash,
        string? OfficieleTitel);

    public sealed record ExtIoRefInfo(string Ref, string? EId);
}

