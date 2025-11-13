using System.Collections.Generic;

namespace OTST.Domain.Models;

public sealed record TransformationResult(
    string OutputZipPath,
    string ReportPath,
    IReadOnlyCollection<string> GeneratedFiles);

