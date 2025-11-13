using OTST.Domain.Abstractions;
using OTST.Domain.Models;
using OTST.Domain.Services.Validation;

namespace OTST.Application.Services;

public sealed class ValidationTransformationFacade
{
    private readonly ValidationTransformationService _service;

    public ValidationTransformationFacade(ITimeProvider? timeProvider = null)
    {
        _service = new ValidationTransformationService(timeProvider);
    }

    public TransformationResult TransformValidation(string sourceZipPath, string? outputZipPath = null)
    {
        var targetPath = outputZipPath ?? ValidationTransformationService.GetDefaultOutputPath(sourceZipPath, isValidation: true);
        return _service.TransformValidation(sourceZipPath, targetPath, isValidation: true);
    }

    public TransformationResult TransformPublicatie(string sourceZipPath, string? outputZipPath = null)
    {
        var targetPath = outputZipPath ?? ValidationTransformationService.GetDefaultOutputPath(sourceZipPath, isValidation: false);
        return _service.TransformValidation(sourceZipPath, targetPath, isValidation: false);
    }
}

