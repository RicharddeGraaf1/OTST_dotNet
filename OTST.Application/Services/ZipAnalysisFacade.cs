using System.Threading.Tasks;
using OTST.Domain.Models;
using OTST.Domain.Services;

namespace OTST.Application.Services;

/// <summary>
/// Application-layer façade around the domain-level <see cref="ZipAnalyser"/>.
/// Provides a stable surface for UI and CLI callers.
/// </summary>
public sealed class ZipAnalysisFacade
{
    private readonly ZipAnalyser _zipAnalyser;

    public ZipAnalysisFacade()
        : this(new ZipAnalyser())
    {
    }

    public ZipAnalysisFacade(ZipAnalyser zipAnalyser)
    {
        _zipAnalyser = zipAnalyser;
    }

    public Task<ZipAnalysisResult> AnalyseAsync(string zipPath) =>
        _zipAnalyser.AnalyseAsync(zipPath);
}

