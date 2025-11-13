using System;

namespace OTST.Domain.Abstractions;

public sealed class SystemTimeProvider : ITimeProvider
{
    public static SystemTimeProvider Instance { get; } = new();

    private SystemTimeProvider()
    {
    }

    public DateTimeOffset Now => DateTimeOffset.Now;
    public DateOnly Today => DateOnly.FromDateTime(DateTime.Today);
}

