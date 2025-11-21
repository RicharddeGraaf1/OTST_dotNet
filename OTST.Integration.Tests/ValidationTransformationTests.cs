using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using OTST.Domain.Abstractions;
using OTST.Domain.Services.Validation;

namespace OTST.Integration.Tests;

public class ValidationTransformationTests : IAsyncLifetime
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "otst-validation-tests", Guid.NewGuid().ToString("N"));

    public record TestCase(string Dataset, string InputZip, string ExpectedFolder, string ExpectedOutputName, bool IsValidation, DateTimeOffset Timestamp);

    public static IEnumerable<object[]> TestCases() =>
        new[]
        {
            new object[] { new TestCase("validatie", "gm9920_input.zip", "gm9920_output", "validatieOpdracht_initieel.zip", true, new DateTimeOffset(2025, 10, 16, 09, 34, 40, TimeSpan.Zero)) },
            new object[] { new TestCase("validatie", "pv30_input.zip", "pv30_output", "validatieOpdracht_initieel.zip", true, new DateTimeOffset(2025, 10, 16, 10, 45, 13, TimeSpan.Zero)) }
        };

    [Theory]
    [MemberData(nameof(TestCases))]
    public async Task Transform_Validation_Snapshots_Match(TestCase testCase)
    {
        var timeProvider = new FixedTimeProvider(testCase.Timestamp);
        var service = new ValidationTransformationService(timeProvider);

        var inputZip = GetDatasetPath(testCase.Dataset, Path.Combine("input", testCase.InputZip));
        var outputZip = Path.Combine(_tempDirectory, $"{testCase.ExpectedFolder}_{testCase.ExpectedOutputName}");

        var result = service.TransformValidation(inputZip, outputZip, testCase.IsValidation);

        File.Exists(result.OutputZipPath).Should().BeTrue("transformatie moet een ZIP genereren");
        File.Exists(result.ReportPath).Should().BeTrue("rapport moet aanwezig zijn");

        var extractDirectory = Path.Combine(_tempDirectory, $"{testCase.ExpectedFolder}_actual");
        Directory.CreateDirectory(extractDirectory);
        ZipFile.ExtractToDirectory(result.OutputZipPath, extractDirectory, overwriteFiles: true);

        var expectedDirectory = GetDatasetPath(testCase.Dataset, Path.Combine("expected", testCase.ExpectedFolder));
        var expectedFiles = Directory.GetFiles(expectedDirectory).Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal)!;
        var actualFiles = Directory.GetFiles(extractDirectory).Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal)!;

        actualFiles.Should().Contain(expectedFiles, "alle verwachte bestanden moeten aanwezig zijn");

        foreach (var file in expectedFiles)
        {
            var expectedPath = Path.Combine(expectedDirectory, file!);
            var actualPath = Path.Combine(extractDirectory, file!);
            var extension = Path.GetExtension(file) ?? string.Empty;

            if (!IsXmlLike(extension))
            {
                var expectedBytes = await File.ReadAllBytesAsync(expectedPath);
                var actualBytes = await File.ReadAllBytesAsync(actualPath);
                actualBytes.Should().BeEquivalentTo(expectedBytes, $"Bestand {file} moet overeenkomen met referentie");
                continue;
            }

            var expectedContent = await File.ReadAllTextAsync(expectedPath, Encoding.UTF8);
            var actualContent = await File.ReadAllTextAsync(actualPath, Encoding.UTF8);

            if (file.Equals("besluit.xml", StringComparison.OrdinalIgnoreCase))
            {
                AssertBesluit(actualContent, expectedContent);
            }
            else if (file.Equals("opdracht.xml", StringComparison.OrdinalIgnoreCase))
            {
                AssertOpdracht(actualContent, expectedContent);
            }
            else if (file.Equals("manifest.xml", StringComparison.OrdinalIgnoreCase))
            {
                AssertManifest(actualContent, expectedContent);
            }
            else if (file.StartsWith("IO-", StringComparison.OrdinalIgnoreCase))
            {
                AssertIo(actualContent, expectedContent);
            }
            else if (file.Equals("manifest-ow.xml", StringComparison.OrdinalIgnoreCase))
            {
                AssertManifestOw(actualContent, expectedContent);
            }
            else
            {
                Canonicalize(SanitizeGenericXml(actualContent)).Should().Be(Canonicalize(SanitizeGenericXml(expectedContent)), $"Bestand {file} moet overeenkomen met referentie");
            }
        }
    }

    private static bool IsXmlLike(string extension) =>
        extension.Equals(".xml", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".gml", StringComparison.OrdinalIgnoreCase);

    private static string Canonicalize(string value)
    {
        var doc = XDocument.Parse(value);
        if (doc.Root is not null)
        {
            SortAttributes(doc.Root);
        }
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    private static void SortAttributes(XElement element)
    {
        var orderedAttributes = element.Attributes()
            .OrderBy(a => a.Name.NamespaceName, StringComparer.Ordinal)
            .ThenBy(a => a.Name.LocalName, StringComparer.Ordinal)
            .ToList();

        element.RemoveAttributes();
        foreach (var attribute in orderedAttributes)
        {
            element.Add(attribute);
        }

        foreach (var child in element.Elements())
        {
            SortAttributes(child);
        }
    }

    private static void AssertBesluit(string actualContent, string expectedContent)
    {
        var actual = XDocument.Parse(actualContent);
        var expected = XDocument.Parse(expectedContent);
        var dataNs = (XNamespace)"https://standaarden.overheid.nl/stop/imop/data/";
        var tekstNs = (XNamespace)"https://standaarden.overheid.nl/stop/imop/tekst/";

        string Actual(XNamespace ns, string name) => actual.Descendants(ns + name).First().Value;
        string Expected(XNamespace ns, string name) => expected.Descendants(ns + name).First().Value;

        Actual(dataNs, "FRBRWork").Should().Be(Expected(dataNs, "FRBRWork"));
        Actual(dataNs, "FRBRExpression").Should().Be(Expected(dataNs, "FRBRExpression"));
        Actual(dataNs, "soortWork").Should().Be(Expected(dataNs, "soortWork"));
        Actual(dataNs, "bekendOp").Should().Be(Expected(dataNs, "bekendOp"));

        var actualDoel = Actual(dataNs, "doel");
        var expectedDoel = Expected(dataNs, "doel");
        GetPrefix(actualDoel).Should().Be(GetPrefix(expectedDoel), "Doel prefix moet gelijk zijn");

        Actual(tekstNs, "Al").Should().Be(Expected(tekstNs, "Al"));
    }

    private static void AssertIo(string actualContent, string expectedContent)
    {
        var normalizedActual = NormalizeIo(SanitizeIoXml(actualContent));
        var normalizedExpected = NormalizeIo(SanitizeIoXml(expectedContent));
        normalizedActual.Should().Be(normalizedExpected, "Informatieobject (hash genegeerd) moet overeenkomen met referentie");
    }

    private static void AssertOpdracht(string actualContent, string expectedContent)
    {
        var actual = XDocument.Parse(actualContent);
        var expected = XDocument.Parse(expectedContent);

        string Actual(string name) => actual.Descendants().First(e => e.Name.LocalName == name).Value;
        string Expected(string name) => expected.Descendants().First(e => e.Name.LocalName == name).Value;

        Actual("idBevoegdGezag").Should().Be(Expected("idBevoegdGezag"));
        Actual("idAanleveraar").Should().Be(Expected("idAanleveraar"));
        Actual("publicatie").Should().Be(Expected("publicatie"));
        Actual("datumBekendmaking").Should().Be(Expected("datumBekendmaking"));

        var expectedPrefix = GetPrefix(Expected("idLevering"));
        var actualPrefix = GetPrefix(Actual("idLevering"));
        actualPrefix.Should().Be(expectedPrefix);
    }

    private static void AssertManifest(string actualContent, string expectedContent)
    {
        var actual = XDocument.Parse(actualContent);
        var expected = XDocument.Parse(expectedContent);

        var actualEntries = actual.Root!.Elements().Select(e => new
        {
            File = e.Element(e.Name.Namespace + "bestandsnaam")!.Value,
            Type = e.Element(e.Name.Namespace + "contentType")!.Value
        }).OrderBy(e => e.File, StringComparer.Ordinal).ToList();

        var expectedEntries = expected.Root!.Elements().Select(e => new
        {
            File = e.Element(e.Name.Namespace + "bestandsnaam")!.Value,
            Type = e.Element(e.Name.Namespace + "contentType")!.Value
        }).OrderBy(e => e.File, StringComparer.Ordinal).ToList();

        actualEntries.Should().BeEquivalentTo(expectedEntries, options => options.WithStrictOrdering(), "manifest moet dezelfde bestanden bevatten");
    }

    private static void AssertManifestOw(string actualContent, string expectedContent)
    {
        var actual = XDocument.Parse(actualContent);
        var expected = XDocument.Parse(expectedContent);
        var ns = actual.Root!.Name.Namespace;

        string Actual(string name) => actual.Descendants(ns + name).First().Value;
        string Expected(string name) => expected.Descendants(ns + name).First().Value;

        var actualDoel = Actual("DoelID");
        var expectedDoel = Expected("DoelID");
        GetPrefix(actualDoel).Should().Be(GetPrefix(expectedDoel), "DoelID prefix moet gelijk zijn");

        Actual("WorkIDRegeling").Should().Be(Expected("WorkIDRegeling"));

        var actualBestanden = actual.Descendants(ns + "Bestand")
            .Select(e => new
            {
                Naam = e.Element(ns + "naam")!.Value,
                ObjectType = e.Element(ns + "objecttype")!.Value
            })
            .OrderBy(x => x.Naam, StringComparer.Ordinal)
            .ToList();

        var expectedBestanden = expected.Descendants(ns + "Bestand")
            .Select(e => new
            {
                Naam = e.Element(ns + "naam")!.Value,
                ObjectType = e.Element(ns + "objecttype")!.Value
            })
            .OrderBy(x => x.Naam, StringComparer.Ordinal)
            .ToList();

        actualBestanden.Should().BeEquivalentTo(expectedBestanden, options => options.WithStrictOrdering(), "manifest-ow moet dezelfde bestand entries bevatten");
    }

    private static readonly XNamespace IoAanleveringNs = "https://standaarden.overheid.nl/lvbb/stop/aanlevering/";
    private static readonly XNamespace IoDataNs = "https://standaarden.overheid.nl/stop/imop/data/";
    private static readonly HashSet<string> DataElementNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ExpressionIdentificatie",
        "FRBRWork",
        "FRBRExpression",
        "soortWork",
        "InformatieObjectVersieMetadata",
        "heeftGeboorteregeling",
        "heeftBestanden",
        "heeftBestand",
        "Bestand",
        "bestandsnaam",
        "hash",
        "InformatieObjectMetadata",
        "alternatieveTitels",
        "alternatieveTitel",
        "eindverantwoordelijke",
        "formaatInformatieobject",
        "maker",
        "naamInformatieObject",
        "officieleTitel",
        "publicatieinstructie"
    };

    private static string NormalizeIo(string content)
    {
        var doc = XDocument.Parse(content);
        if (doc.Root is not null)
        {
            NormalizeIoElement(doc.Root, parentIsData: false);
            SortAttributes(doc.Root);
        }

        return doc.ToString(SaveOptions.DisableFormatting);
    }

    private static string SanitizeIoXml(string value)
    {
        var doc = XDocument.Parse(value);
        foreach (var hashElement in doc.Descendants().Where(e => e.Name.LocalName.Equals("hash", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            hashElement.Remove();
        }
        foreach (var element in doc.Root!.DescendantsAndSelf())
        {
            var namespaceAttributes = element.Attributes().Where(a => a.IsNamespaceDeclaration).ToList();
            foreach (var attr in namespaceAttributes)
            {
                attr.Remove();
            }
        }
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    private static string SanitizeGenericXml(string value)
    {
        var doc = XDocument.Parse(value);
        foreach (var element in doc.Root!.DescendantsAndSelf())
        {
            var namespaceAttributes = element.Attributes().Where(a => a.IsNamespaceDeclaration).ToList();
            foreach (var attr in namespaceAttributes)
            {
                attr.Remove();
            }
        }
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    private static void NormalizeIoElement(XElement element, bool parentIsData)
    {
        var isCurrentlyData = element.Name.Namespace == IoDataNs;
        var isData = isCurrentlyData || parentIsData || DataElementNames.Contains(element.Name.LocalName);
        element.Name = (isData ? IoDataNs : IoAanleveringNs) + element.Name.LocalName;

        foreach (var attr in element.Attributes().Where(a => a.IsNamespaceDeclaration).ToList())
        {
            attr.Remove();
        }

        foreach (var child in element.Elements())
        {
            NormalizeIoElement(child, isData);
        }
    }

    private static string GetDatasetPath(string dataset, string relative)
    {
        var baseDir = AppContext.BaseDirectory ?? throw new InvalidOperationException("Base directory not available.");
        return Path.Combine(baseDir, "TestData", dataset, relative);
    }

    private static string GetPrefix(string value)
    {
        var index = value.LastIndexOf('_');
        return index > 0 ? value[..index] : value;
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDirectory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Environment.GetEnvironmentVariable("OTST_KEEP_TEMP") == "1")
        {
            return Task.CompletedTask;
        }

        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private sealed class FixedTimeProvider : ITimeProvider
    {
        private readonly DateTimeOffset _timestamp;

        public FixedTimeProvider(DateTimeOffset timestamp)
        {
            _timestamp = timestamp;
        }

        public DateTimeOffset Now => _timestamp;
        public DateOnly Today => DateOnly.FromDateTime(_timestamp.Date);
    }
}

