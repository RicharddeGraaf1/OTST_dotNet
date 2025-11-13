using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using OTST.Domain.Abstractions;
using OTST.Domain.Services;

namespace OTST.Integration.Tests;

public class IntrekkingTransformationTests : IAsyncLifetime
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "otst-tests", Guid.NewGuid().ToString("N"));

    public record TestCase(string Dataset, string InputZip, string ExpectedFolder, string ExpectedOutputName, bool IsValidation, DateTimeOffset Timestamp);

    public static IEnumerable<object[]> TestCases() =>
        new[]
        {
            new object[] { new TestCase("intrekkingvalidatie", "helloDSO_input.zip", "helloDSO_output", "intrekkingValidatieOpdracht_initieel.zip", true, new DateTimeOffset(2025, 7, 23, 16, 40, 52, TimeSpan.Zero)) },
            new object[] { new TestCase("intrekking", "gm9920_input.zip", "gm9920_output", "intrekkingOpdracht_initieel.zip", false, new DateTimeOffset(2025, 5, 22, 16, 40, 52, TimeSpan.Zero)) }
        };

    [Theory]
    [MemberData(nameof(TestCases))]
    public async Task Transform_Intrekking_Snapshots_Match(TestCase testCase)
    {
        var timeProvider = new FixedTimeProvider(testCase.Timestamp);
        var service = new IntrekkingTransformationService(timeProvider);

        var inputZip = GetDatasetPath(testCase.Dataset, Path.Combine("input", testCase.InputZip));
        var outputZip = Path.Combine(_tempDirectory, testCase.ExpectedOutputName);

        var result = await service.TransformIntrekkingAsync(inputZip, outputZip, testCase.IsValidation);

        File.Exists(result.OutputZipPath).Should().BeTrue("transformatie moet een ZIP genereren");
        File.Exists(result.ReportPath).Should().BeTrue("rapport moet aanwezig zijn");

        var extractDirectory = Path.Combine(_tempDirectory, $"{Path.GetFileNameWithoutExtension(testCase.ExpectedOutputName)}_actual");
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
            var extension = Path.GetExtension(file);

            if (!extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
            {
                var expectedBytes = File.ReadAllBytes(expectedPath);
                var actualBytes = File.ReadAllBytes(actualPath);
                actualBytes.Should().BeEquivalentTo(expectedBytes, $"Bestand {file} moet overeenkomen met referentie");
                continue;
            }

            var expectedContent = File.ReadAllText(expectedPath, Encoding.UTF8);
            var actualContent = File.ReadAllText(actualPath, Encoding.UTF8);

            if (file.Equals("intrekkingsbesluit.xml", StringComparison.OrdinalIgnoreCase))
            {
                AssertIntrekkingsbesluit(actualContent, expectedContent);
            }
            else if (file.Equals("opdracht.xml", StringComparison.OrdinalIgnoreCase))
            {
                AssertOpdracht(actualContent, expectedContent, testCase.IsValidation);
            }
            else if (file.Equals("manifest.xml", StringComparison.OrdinalIgnoreCase))
            {
                AssertManifest(actualContent, expectedContent);
            }
            else if (file.Equals("manifest-ow.xml", StringComparison.OrdinalIgnoreCase))
            {
                AssertManifestOw(actualContent, expectedContent);
            }
            else
            {
                Canonicalize(actualContent).Should().Be(Canonicalize(expectedContent), $"Bestand {file} moet overeenkomen met referentie");
            }
        }
    }

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n").Trim();

    private static string Canonicalize(string value)
    {
        var document = XDocument.Parse(value);
        if (document.Root is not null)
        {
            SortAttributes(document.Root);
        }
        return document.ToString(SaveOptions.DisableFormatting);
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

    private static void AssertIntrekkingsbesluit(string actualContent, string expectedContent)
    {
        var actual = XDocument.Parse(actualContent);
        var expected = XDocument.Parse(expectedContent);

        var dataNs = (XNamespace)"https://standaarden.overheid.nl/stop/imop/data/";
        var tekstNs = (XNamespace)"https://standaarden.overheid.nl/stop/imop/tekst/";

        actual.Root.Should().NotBeNull();
        expected.Root.Should().NotBeNull();

        string ActualValue(XName name) => actual.Descendants(name).First().Value;
        string ExpectedValue(XName name) => expected.Descendants(name).First().Value;

        ActualValue(dataNs + "FRBRWork").Should().Be(ExpectedValue(dataNs + "FRBRWork"));
        ActualValue(dataNs + "FRBRExpression").Should().Be(ExpectedValue(dataNs + "FRBRExpression"));
        ActualValue(dataNs + "soortWork").Should().Be(ExpectedValue(dataNs + "soortWork"));
        ActualValue(dataNs + "bekendOp").Should().Be(ExpectedValue(dataNs + "bekendOp"));

        var actualDoel = ActualValue(dataNs + "doel");
        var expectedDoel = ExpectedValue(dataNs + "doel");
        var expectedPrefix = GetPrefix(expectedDoel);
        var actualPrefix = GetPrefix(actualDoel);
        actualPrefix.Should().Be(expectedPrefix, "Doel prefix moet gelijk zijn");

        ActualValue(tekstNs + "Al").Should().Be(ExpectedValue(tekstNs + "Al"));
    }

    private static void AssertOpdracht(string actualContent, string expectedContent, bool isValidation)
    {
        var actual = XDocument.Parse(actualContent);
        var expected = XDocument.Parse(expectedContent);

        string Actual(string name) => actual.Descendants().First(e => e.Name.LocalName == name).Value;
        string Expected(string name) => expected.Descendants().First(e => e.Name.LocalName == name).Value;

        Actual("idBevoegdGezag").Should().Be(Expected("idBevoegdGezag"));
        Actual("idAanleveraar").Should().Be(Expected("idAanleveraar"));
        Actual("publicatie").Should().Be(Expected("publicatie"));
        Actual("datumBekendmaking").Should().Be(Expected("datumBekendmaking"));

        var prefix = isValidation ? "OTST_val_intr_" : "OTST_pub_intr_";
        Actual("idLevering").Should().MatchRegex($"{prefix}.*_\\d{{8}}_\\d{{6}}");
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

    private static void AssertManifestOw(string actualContent, string expectedContent)
    {
        var actual = XDocument.Parse(actualContent);
        var expected = XDocument.Parse(expectedContent);
        var ns = actual.Root!.Name.Namespace;

        string Actual(string name) => actual.Descendants(ns + name).First().Value;
        string Expected(string name) => expected.Descendants(ns + name).First().Value;

        Actual("WorkIDRegeling").Should().Be(Expected("WorkIDRegeling"));

        var actualDoel = Actual("DoelID");
        var expectedDoel = Expected("DoelID");
        GetPrefix(actualDoel).Should().Be(GetPrefix(expectedDoel), "DoelID prefix moet gelijk zijn");

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

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDirectory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
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
