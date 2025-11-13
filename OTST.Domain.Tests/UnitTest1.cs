using FluentAssertions;
using OTST.Domain;

namespace OTST.Domain.Tests;

public class DomainInfoTests
{
    [Fact]
    public void ProductName_IsStable()
    {
        DomainInfo.ProductName.Should().Be("OTST");
    }
}
