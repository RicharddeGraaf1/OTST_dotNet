using System;

namespace OTST.Domain.Abstractions;

public interface ITimeProvider
{
    DateTimeOffset Now { get; }
    DateOnly Today { get; }
}

