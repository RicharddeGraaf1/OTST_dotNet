using System.Threading.Tasks;
using OTST.Domain.Abstractions;
using OTST.Domain.Models;
using OTST.Domain.Services;

namespace OTST.Application.Services;

public sealed class DoorleveringTransformationFacade
{
    private readonly DoorleveringTransformationService _service;

    public DoorleveringTransformationFacade(ITimeProvider? timeProvider = null)
    {
        _service = new DoorleveringTransformationService(timeProvider);
    }

    public Task<TransformationResult> TransformValidatieDoorlevering(string sourceZipPath, string? outputZipPath = null)
    {
        var targetPath = outputZipPath ?? DoorleveringTransformationService.GetDefaultOutputPath(sourceZipPath, isValidation: true);
        return _service.TransformDoorleveringAsync(sourceZipPath, targetPath, isValidation: true);
    }

    public Task<TransformationResult> TransformDoorlevering(string sourceZipPath, string? outputZipPath = null)
    {
        var targetPath = outputZipPath ?? DoorleveringTransformationService.GetDefaultOutputPath(sourceZipPath, isValidation: false);
        return _service.TransformDoorleveringAsync(sourceZipPath, targetPath, isValidation: false);
    }
}

