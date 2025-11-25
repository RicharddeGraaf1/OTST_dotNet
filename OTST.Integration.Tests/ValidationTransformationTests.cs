using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using OTST.Domain.Abstractions;
using OTST.Domain.Services;
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

    public static IEnumerable<object[]> DoorleveringTestCases() =>
        new[]
        {
            new object[] { new TestCase("doorlevering", "gm0590_input.zip", "gm0590_programma_output", "doorleveringOpdracht_initieel.zip", false, new DateTimeOffset(2025, 11, 18, 19, 06, 09, TimeSpan.Zero)) },
            new object[] { new TestCase("doorlevering", "gm0687_input.zip", "gm0687_voorbeschermingsregels_output", "doorleveringOpdracht_initieel.zip", false, new DateTimeOffset(2025, 11, 18, 19, 06, 09, TimeSpan.Zero)) },
            new object[] { new TestCase("doorlevering", "gm0938_input.zip", "gm0938_omgevingsvisie_output", "doorleveringOpdracht_initieel.zip", false, new DateTimeOffset(2025, 11, 18, 19, 06, 09, TimeSpan.Zero)) }
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

    private static void AssertManifestOwObjectTypes(string actualManifestOwContent, string expectedManifestOwContent, string extractDirectory)
    {
        var actual = XDocument.Parse(actualManifestOwContent);
        var expected = XDocument.Parse(expectedManifestOwContent);
        var ns = actual.Root!.Name.Namespace;
        var slNs = (XNamespace)"http://www.geostandaarden.nl/bestanden-ow/standlevering-generiek";

        // Haal alle verwachte objecttypen uit manifest-ow.xml
        var expectedObjectTypes = expected.Descendants(ns + "objecttype")
            .Select(ot => ot.Value)
            .OrderBy(ot => ot, StringComparer.Ordinal)
            .ToList();

        // Haal alle objecttypen uit de daadwerkelijke OW-bestanden
        var actualObjectTypes = new List<string>();
        var actualBestanden = actual.Descendants(ns + "Bestand")
            .Select(e => e.Element(ns + "naam")?.Value ?? string.Empty)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList();

        foreach (var bestandsnaam in actualBestanden)
        {
            var bestandPath = Path.Combine(extractDirectory, bestandsnaam);
            if (File.Exists(bestandPath))
            {
                var bestandContent = File.ReadAllText(bestandPath, Encoding.UTF8);
                var bestandDoc = XDocument.Parse(bestandContent);
                var objectTypesInBestand = bestandDoc.Descendants(slNs + "objectType")
                    .Select(ot => ot.Value)
                    .ToList();
                actualObjectTypes.AddRange(objectTypesInBestand);
            }
        }

        var actualObjectTypesSorted = actualObjectTypes.OrderBy(ot => ot, StringComparer.Ordinal).ToList();
        actualObjectTypesSorted.Should().BeEquivalentTo(expectedObjectTypes, 
            $"Alle objecttypen uit manifest-ow.xml moeten aanwezig zijn in de OW-bestanden. " +
            $"Verwacht: [{string.Join(", ", expectedObjectTypes)}], " +
            $"Gevonden: [{string.Join(", ", actualObjectTypesSorted)}]");
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
                Naam = e.Element(ns + "naam")?.Value ?? string.Empty,
                ObjectTypes = e.Elements(ns + "objecttype").Select(ot => ot.Value).OrderBy(ot => ot, StringComparer.Ordinal).ToList()
            })
            .OrderBy(x => x.Naam, StringComparer.Ordinal)
            .ToList();

        var expectedBestanden = expected.Descendants(ns + "Bestand")
            .Select(e => new
            {
                Naam = e.Element(ns + "naam")?.Value ?? string.Empty,
                ObjectTypes = e.Elements(ns + "objecttype").Select(ot => ot.Value).OrderBy(ot => ot, StringComparer.Ordinal).ToList()
            })
            .OrderBy(x => x.Naam, StringComparer.Ordinal)
            .ToList();

        // Filter to only check expected files (some input zips may have extra OW files)
        var expectedFileNames = expectedBestanden.Select(b => b.Naam).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var filteredActual = actualBestanden.Where(b => expectedFileNames.Contains(b.Naam)).ToList();
        
        // Debug: if no matches, show what we have
        if (filteredActual.Count == 0 && actualBestanden.Count > 0)
        {
            var actualNames = string.Join(", ", actualBestanden.Select(b => $"'{b.Naam}'"));
            var expectedNames = string.Join(", ", expectedBestanden.Select(b => $"'{b.Naam}'"));
            throw new InvalidOperationException($"Geen overeenkomende bestandsnamen. Actual bestanden: [{actualNames}]. Expected bestanden: [{expectedNames}]");
        }
        
        filteredActual.Should().HaveCount(expectedBestanden.Count, $"manifest-ow moet alle verwachte bestanden bevatten. Gevonden: {actualBestanden.Count}, verwacht: {expectedBestanden.Count}");
        for (int i = 0; i < expectedBestanden.Count; i++)
        {
            var expectedFile = expectedBestanden[i];
            var actualFile = filteredActual.FirstOrDefault(a => string.Equals(a.Naam, expectedFile.Naam, StringComparison.OrdinalIgnoreCase));
            actualFile.Should().NotBeNull($"Bestand {expectedFile.Naam} moet aanwezig zijn");
            actualFile!.ObjectTypes.Should().BeEquivalentTo(expectedFile.ObjectTypes, $"Bestand {expectedFile.Naam} objecttypes moeten overeenkomen");
        }
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
            SortElements(doc.Root);
        }

        return doc.ToString(SaveOptions.DisableFormatting);
    }

    private static void SortElements(XElement element)
    {
        var sortedChildren = element.Elements()
            .OrderBy(e => e.Name.LocalName, StringComparer.Ordinal)
            .ToList();
        
        element.RemoveNodes();
        foreach (var child in sortedChildren)
        {
            SortElements(child);
            element.Add(child);
        }
    }

    private static string SanitizeIoXml(string value)
    {
        var doc = XDocument.Parse(value);
        foreach (var hashElement in doc.Descendants().Where(e => e.Name.LocalName.Equals("hash", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            hashElement.Remove();
        }
        // Remove schemaLocation attributes
        foreach (var element in doc.Root!.DescendantsAndSelf())
        {
            var schemaLocationAttrs = element.Attributes()
                .Where(a => a.Name.LocalName.Equals("schemaLocation", StringComparison.OrdinalIgnoreCase) ||
                           a.Name.LocalName.Equals("schemaLocation", StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var attr in schemaLocationAttrs)
            {
                attr.Remove();
            }
            // Remove schemaversie from InformatieObjectMetadata if present
            if (element.Name.LocalName.Equals("InformatieObjectMetadata", StringComparison.OrdinalIgnoreCase))
            {
                var schemaversieAttr = element.Attribute("schemaversie");
                if (schemaversieAttr != null)
                {
                    schemaversieAttr.Remove();
                }
            }
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
        var xsiNs = XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance");
        foreach (var element in doc.Root!.DescendantsAndSelf())
        {
            var namespaceAttributes = element.Attributes().Where(a => a.IsNamespaceDeclaration).ToList();
            foreach (var attr in namespaceAttributes)
            {
                attr.Remove();
            }
            // Remove schemaLocation attributes
            var schemaLocationAttr = element.Attribute(xsiNs + "schemaLocation");
            if (schemaLocationAttr != null)
            {
                schemaLocationAttr.Remove();
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

    [Theory]
    [MemberData(nameof(DoorleveringTestCases))]
    public async Task Transform_Doorlevering_Snapshots_Match(TestCase testCase)
    {
        var timeProvider = new FixedTimeProvider(testCase.Timestamp);
        var service = new DoorleveringTransformationService(timeProvider);

        var inputZip = GetDatasetPath(testCase.Dataset, Path.Combine("input", testCase.InputZip));
        var outputZip = Path.Combine(_tempDirectory, $"{testCase.ExpectedFolder}_{testCase.ExpectedOutputName}");

        var result = service.TransformDoorlevering(inputZip, outputZip, testCase.IsValidation);

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

            if (file.Equals("proefversiebesluit.xml", StringComparison.OrdinalIgnoreCase))
            {
                AssertProefversieBesluit(actualContent, expectedContent);
            }
            else if (file.Equals("consolidaties.xml", StringComparison.OrdinalIgnoreCase))
            {
                AssertConsolidaties(actualContent, expectedContent);
            }
            else if (file.Equals("manifest.xml", StringComparison.OrdinalIgnoreCase))
            {
                AssertManifest(actualContent, expectedContent);
            }
            else if (file.StartsWith("IO-", StringComparison.OrdinalIgnoreCase) || 
                     (file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && 
                      !file.Equals("proefversiebesluit.xml", StringComparison.OrdinalIgnoreCase) &&
                      !file.Equals("consolidaties.xml", StringComparison.OrdinalIgnoreCase) &&
                      !file.Equals("manifest.xml", StringComparison.OrdinalIgnoreCase) &&
                      !file.Equals("manifest-ow.xml", StringComparison.OrdinalIgnoreCase) &&
                      (file.Contains("gio-", StringComparison.OrdinalIgnoreCase) || 
                       file.Contains("consolideren-", StringComparison.OrdinalIgnoreCase) ||
                       file.Contains("publiceren-", StringComparison.OrdinalIgnoreCase))))
            {
                AssertIo(actualContent, expectedContent);
            }
            else if (file.Equals("manifest-ow.xml", StringComparison.OrdinalIgnoreCase))
            {
                AssertManifestOw(actualContent, expectedContent);
            }
            else if (file.Equals("divisies.xml", StringComparison.OrdinalIgnoreCase) || 
                     file.Equals("divisieaanduidingen.xml", StringComparison.OrdinalIgnoreCase) ||
                     file.Equals("regelingsgebied.xml", StringComparison.OrdinalIgnoreCase) ||
                     file.Equals("owRegelingsgebied.xml", StringComparison.OrdinalIgnoreCase) ||
                     file.Equals("tekstdelen.xml", StringComparison.OrdinalIgnoreCase) ||
                     file.Equals("gebieden.xml", StringComparison.OrdinalIgnoreCase) ||
                     file.Equals("owGebied.xml", StringComparison.OrdinalIgnoreCase) ||
                     file.StartsWith("ow", StringComparison.OrdinalIgnoreCase) && file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                // Voor OW-bestanden zijn dataset, gebied en leveringsId variabel
                // en hoeven niet exact overeen te komen - controle gebeurt al via manifest-ow.xml objecttypen
                // Alleen controleren als het bestand ook daadwerkelijk bestaat in de actual output
                if (File.Exists(actualPath))
                {
                    AssertOwBestandIgnoringVariableFields(actualContent, expectedContent, file);
                }
            }
            else if (!criticalFiles.Contains(file!, StringComparer.OrdinalIgnoreCase) && 
                     !file!.StartsWith("IO-", StringComparison.OrdinalIgnoreCase) &&
                     !file.Contains("gio-", StringComparison.OrdinalIgnoreCase) &&
                     !file.Contains("consolideren-", StringComparison.OrdinalIgnoreCase) &&
                     !file.Contains("publiceren-", StringComparison.OrdinalIgnoreCase))
            {
                // Voor andere bestanden (afbeeldingen, etc.) controleren we alleen als ze bestaan
                if (File.Exists(actualPath))
                {
                    Canonicalize(SanitizeGenericXml(actualContent)).Should().Be(Canonicalize(SanitizeGenericXml(expectedContent)), $"Bestand {file} moet overeenkomen met referentie");
                }
            }
        }
    }

    private static void AssertOwBestandIgnoringVariableFields(string actualContent, string expectedContent, string fileName)
    {
        // Voor OW-bestanden zijn dataset, gebied en leveringsId variabel
        // Normaliseer deze velden naar een vaste waarde voor vergelijking
        var actual = XDocument.Parse(actualContent);
        var expected = XDocument.Parse(expectedContent);
        
        var slNs = (XNamespace)"http://www.geostandaarden.nl/bestanden-ow/standlevering-generiek";
        
        // Vervang variabele velden in beide documenten
        foreach (var elem in actual.Descendants(slNs + "dataset").Concat(actual.Descendants(slNs + "gebied")).Concat(actual.Descendants(slNs + "leveringsId")))
        {
            elem.Value = "VARIABLE";
        }
        
        foreach (var elem in expected.Descendants(slNs + "dataset").Concat(expected.Descendants(slNs + "gebied")).Concat(expected.Descendants(slNs + "leveringsId")))
        {
            elem.Value = "VARIABLE";
        }
        
        Canonicalize(SanitizeGenericXml(actual.ToString())).Should().Be(Canonicalize(SanitizeGenericXml(expected.ToString())), $"Bestand {fileName} moet overeenkomen met referentie (variabele velden genegeerd)");
    }

    private static void AssertProefversieBesluit(string actualContent, string expectedContent)
    {
        var actual = XDocument.Parse(actualContent);
        var expected = XDocument.Parse(expectedContent);
        var dataNs = (XNamespace)"https://standaarden.overheid.nl/stop/imop/data/";
        var consolidatieNs = (XNamespace)"https://standaarden.overheid.nl/stop/imop/consolidatie/";

        string Actual(XNamespace ns, string name) => actual.Descendants(ns + name).First().Value;
        string Expected(XNamespace ns, string name) => expected.Descendants(ns + name).First().Value;

        Actual(dataNs, "FRBRWork").Should().Be(Expected(dataNs, "FRBRWork"));
        Actual(dataNs, "FRBRExpression").Should().Be(Expected(dataNs, "FRBRExpression"));
        Actual(dataNs, "soortWork").Should().Be(Expected(dataNs, "soortWork"));
        Actual(dataNs, "bekendOp").Should().Be(Expected(dataNs, "bekendOp"));

        var actualDoel = Actual(consolidatieNs, "doel");
        var expectedDoel = Expected(consolidatieNs, "doel");
        GetPrefix(actualDoel).Should().Be(GetPrefix(expectedDoel), "Doel prefix moet gelijk zijn");
    }

    private static void AssertConsolidaties(string actualContent, string expectedContent)
    {
        var actual = XDocument.Parse(actualContent);
        var expected = XDocument.Parse(expectedContent);
        var consolidatieNs = (XNamespace)"https://standaarden.overheid.nl/stop/imop/consolidatie/";

        string Actual(XNamespace ns, string name) => actual.Descendants(ns + name).First().Value;
        string Expected(XNamespace ns, string name) => expected.Descendants(ns + name).First().Value;

        var actualDoel = Actual(consolidatieNs, "doel");
        var expectedDoel = Expected(consolidatieNs, "doel");
        GetPrefix(actualDoel).Should().Be(GetPrefix(expectedDoel), "Doel prefix moet gelijk zijn");

        // CVDR-nummer is variabel en hoeft niet exact overeen te komen
        var actualFrbrWork = Actual(consolidatieNs, "FRBRWork");
        var expectedFrbrWork = Expected(consolidatieNs, "FRBRWork");
        // Controleer alleen het prefix (zonder CVDR-nummer)
        var actualPrefix = actualFrbrWork.Substring(0, actualFrbrWork.LastIndexOf("CVDR") + 4);
        var expectedPrefix = expectedFrbrWork.Substring(0, expectedFrbrWork.LastIndexOf("CVDR") + 4);
        actualPrefix.Should().Be(expectedPrefix, "FRBRWork prefix moet gelijk zijn");
        // Controleer dat het een 6-cijferig nummer is
        var actualNumber = actualFrbrWork.Substring(actualFrbrWork.LastIndexOf("CVDR") + 4);
        actualNumber.Length.Should().Be(6, "CVDR-nummer moet 6 cijfers zijn");
        actualNumber.All(char.IsDigit).Should().BeTrue("CVDR-nummer moet alleen cijfers bevatten");
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

