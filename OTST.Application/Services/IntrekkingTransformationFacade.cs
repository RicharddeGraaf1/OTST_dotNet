using System.Threading.Tasks;
using OTST.Domain.Abstractions;
using OTST.Domain.Models;
using OTST.Domain.Services;

namespace OTST.Application.Services;

public sealed class IntrekkingTransformationFacade
{
    private readonly IntrekkingTransformationService _service;

    public IntrekkingTransformationFacade(ITimeProvider? timeProvider = null)
    {
        _service = new IntrekkingTransformationService(timeProvider);
    }

    public Task<TransformationResult> TransformIntrekkingValidatieAsync(string sourceZipPath, string? outputZipPath = null)
    {
        var targetPath = outputZipPath ?? IntrekkingTransformationService.GetDefaultOutputPath(sourceZipPath, isValidation: true);
        return _service.TransformIntrekkingAsync(sourceZipPath, targetPath, isValidation: true);
    }

    public Task<TransformationResult> TransformIntrekkingPublicatieAsync(string sourceZipPath, string? outputZipPath = null)
    {
        var targetPath = outputZipPath ?? IntrekkingTransformationService.GetDefaultOutputPath(sourceZipPath, isValidation: false);
        return _service.TransformIntrekkingAsync(sourceZipPath, targetPath, isValidation: false);
    }
}

