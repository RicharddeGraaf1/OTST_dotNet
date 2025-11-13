using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using OTST.Domain.Services;

namespace OTST.Domain.Tests;

public class ZipAnalyserTests
{
    [Fact]
    public async Task AnalyseAsync_ReturnsExpectedMetadata()
    {
        // Arrange
        await using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            AddEntry(archive, "Regeling/Identificatie.xml", """
                <data:Regeling xmlns:data="https://standaarden.overheid.nl/stop/imop/data/">
                  <data:FRBRWork>/akn/nl/bill/0001/2024/work_1</data:FRBRWork>
                  <data:FRBRExpression>/akn/nl/bill/0001/2024/exp_1</data:FRBRExpression>
                </data:Regeling>
                """);

            AddEntry(archive, "Regeling/Momentopname.xml", """
                <data:Momentopname xmlns:data="https://standaarden.overheid.nl/stop/imop/data/">
                  <data:doel>/toepassingsprofiel/doel/publicatie</data:doel>
                </data:Momentopname>
                """);

            AddEntry(archive, "Regeling/Metadata.xml", """
                <data:Metadata xmlns:data="https://standaarden.overheid.nl/stop/imop/data/">
                  <data:maker>/join/id/stop/organisatie/gemeente/GM0001</data:maker>
                </data:Metadata>
                """);

            AddEntry(archive, "Regeling/Tekst.xml", """
                <tekst:Tekst xmlns:tekst="https://standaarden.overheid.nl/stop/imop/tekst/">
                  <tekst:ExtIoRef ref="/akn/nl/bill/0001/2024/io_1" eId="io_eid_1"/>
                </tekst:Tekst>
                """);

            AddEntry(archive, "IO-0001/Identificatie.xml", """
                <data:Informatieobject xmlns:data="https://standaarden.overheid.nl/stop/imop/data/">
                  <data:FRBRWork>/akn/nl/bill/0001/2024/io_work_1</data:FRBRWork>
                  <data:FRBRExpression>/akn/nl/bill/0001/2024/io_1</data:FRBRExpression>
                </data:Informatieobject>
                """);

            AddEntry(archive, "IO-0001/VersieMetadata.xml", """
                <data:VersieMetadata xmlns:data="https://standaarden.overheid.nl/stop/imop/data/">
                  <data:officieleTitel>Voorbeeld titel</data:officieleTitel>
                </data:VersieMetadata>
                """);

            AddEntry(archive, "IO-0001/bestand.gml", "<gml>content</gml>");
        }

        zipStream.Position = 0;
        var analyser = new ZipAnalyser();

        // Act
        var result = await analyser.AnalyseAsync(zipStream);

        // Assert
        result.FrbrWork.Should().Be("/akn/nl/bill/0001/2024/work_1");
        result.FrbrExpression.Should().Be("/akn/nl/bill/0001/2024/exp_1");
        result.Doel.Should().Be("/toepassingsprofiel/doel/publicatie");
        result.BevoegdGezag.Should().Be("GM0001");
        result.AantalInformatieObjecten.Should().Be(1);
        result.TotaleGmlBestandsgrootte.Should().BeGreaterThan(0);

        result.InformatieObjecten.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Folder = "IO-0001",
            FrbrExpression = "/akn/nl/bill/0001/2024/io_1",
            OfficieleTitel = "Voorbeeld titel",
            ExtIoRefEId = "io_eid_1",
            Bestandsnaam = "bestand.gml"
        }, options => options.ExcludingMissingMembers());
    }

    private static void AddEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}

