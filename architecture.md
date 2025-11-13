## OTST .NET Architectuur

### 1. Doel en context
Deze architectuurschets beschrijft een nieuwe .NET (C#) implementatie van de bestaande **Omgevingswet Test Suite Tool (OTST)**. Het doel is functionele 1-op-1 pariteit met de huidige JavaFX/React + Java back-end applicatie (`C:\GIT\OmgevingswetTestSuiteTool`), maar gebouwd op het .NET-ecosysteem om onderhoud, uitbreidbaarheid en integratie met Windows-werkplekken te verbeteren.

### 2. Functionele scope (pariteit met OTST)
- ZIP-bestanden analyseren (FRBR, doel, bevoegd gezag, informatieobjecten).
- Transformaties uitvoeren voor publicatie-, validatie-, intrekkings- en doorleveringsopdrachten.
- IO-bestanden verwerken (genereren IO-XML, verplaatsen PDF/GML, wrappen van GML).
- Genereren van `besluit.xml`, `intrekkingsbesluit.xml`, `opdracht.xml`, `manifest.xml`.
- Metadata tonen, logging, voortgangsbalk en rapportage (`*_rapport.txt`).
- Ondersteunen van meerdere scenario’s: initiële publicatie, validatie, intrekking, validatie-intrekking, doorlevering, validatie-doorlevering.
- UI-gestuurd padbeheer (bron ZIP kiezen, doelpad bepalen, custom output).

### 3. Niet-functionele eisen
- Desktop-first Windows applicatie met moderne UX.
- Hergebruik van ZIP/XML/GML logica via testbare bibliotheken.
- Offline inzetbaar, geen externe services vereist.
- Uitvoerbare single-file distributie of MSIX (Self-contained .NET 8 runtime).
- Logging en foutenbeheer robuust, gericht op troubleshootbaarheid.
- Performance vergelijkbaar met Java-versie (transformaties op grote ZIP’s).

### 4. Hoog-over architectuur
```
┌───────────────────────────┐
│      Presentation (UI)    │  WPF (.NET 8) + MVVM
└───────────────┬───────────┘
                │ Commands/ViewModels
┌───────────────▼───────────┐
│      Application Core     │  Orchestrators, Use-cases
└───────────────┬───────────┘
                │ Domain Services
┌───────────────▼───────────┐
│         Domain Layer      │  ZIP analyser, transformers,
│                           │  manifest/besluit generators
└───────────────┬───────────┘
                │ Interfaces
┌───────────────▼───────────┐
│      Infrastructure       │  File system, ZIP IO, XML,
│                           │  configuration, logging
└───────────────────────────┘
```

### 5. Technische keuzes
- **Runtime**: .NET 8 (LTS) / C# 12.
- **UI Framework**: WPF met MVVM (CommunityToolkit.Mvvm).
- **IoC/DI**: `Microsoft.Extensions.DependencyInjection`.
- **ZIP**: `System.IO.Compression` + SharpZipLib (optioneel) voor geavanceerde features.
- **XML**: `System.Xml`, `System.Xml.Linq`, `System.Xml.XPath`.
- **Validatie/parsing**: eigen XSD-validaties met `XmlSchemaSet`.
- **Logging**: `Serilog` naar rolling file + in-memory sink voor UI-log.
- **Unit/integration tests**: xUnit + FluentAssertions; testdata via embedded resources of temp directories.
- **Packaging**: `dotnet publish -r win-x64 --self-contained true`.

### 6. Laag-voor-laag details
**Presentation (WPF UI)**
- Schermen: hoofdvenster met tabs (Analyse, Transformatie, Log), dialoogservices voor file/folder picker.
- `MainViewModel`: bindings naar padvelden, knoppen, progress, metadata-output en logs.
- Command pattern: `AnalyzeCommand`, `TransformCommand`, `ValidateCommand`, etc., die services aanspreken.
- Status- en foutmeldingen via notification-service (dialogen/toasts).

**Application Core**
- Use-case klassen: `AnalyzeZipUseCase`, `TransformZipUseCase`, `GenerateReportUseCase`.
- Coördineren sequenties: validaties, orchestratie van domain services, transacties (via `using` scopes).
- Mappen UI-input naar domain requests (`TransformRequest`).
- Publiceren van progress events (`IProgress<ProgressUpdate>`), log events naar UI.

**Domain Layer**
- Entiteiten/Value Objects: `ZipAnalyseResult`, `InformatieObject`, `BesluitDocument`, `ManifestEntry`.
- Services:
  - `ZipAnalyser`
  - `IoProcessor` (IO-XML, PDF/GML handling)
  - `IntrekkingProcessor`
  - `BesluitGenerator`
  - `ManifestBuilder`
  - `ReportFormatter`
- Domeinlogica is framework-onafhankelijk, testbaar, geen directe IO.

**Infrastructure Layer**
- Implementaties van interfaces voor bestands- en ZIP-operaties (`IZipArchiveFactory`, `IFileSystem`).
- XML parsing helpers die schema’s laden vanuit `Resources/Schemas`.
- Rapportwriter (`StreamWriter`) en loggers.
- Configuratie (`appsettings.json`) voor defaults (bijv. voorbeeld-zip pad).
- Facade naar 3rd-party libs (SharpZipLib) via eigen interfaces om unit-tests te vergemakkelijken.

### 7. Belangrijkste componenten
| Component | Rol |
|-----------|-----|
| `MainWindow`, `MainViewModel` | UI-interactie, binding, command routing |
| `TransformationCoordinator` | Hoofdorchestrator voor alle transformatietypes |
| `ZipAnalyser` | Leest `Regeling/*`, `IO-*` om FRBR, doel, bevoegd gezag, IO-data te extraheren |
| `IoProcessor` | Genereert IO-XML, wikkelt GML, verplaatst bestanden |
| `BesluitGenerator` | Bouwt `besluit.xml` en `opdracht.xml` o.b.v. analyse |
| `IntrekkingProcessor` | Verwerkt intrekkingsscenario’s, levert gewijzigde OW-bestanden |
| `DoorleveringProcessor` | Variant op intrekking/publicatie voor doorlevering |
| `ManifestBuilder` | Schrijft `manifest.xml` o.b.v. toegevoegde bestanden |
| `ReportService` | Genereert tekstueel rapport en levert metadata aan UI |
| `ProgressReporter` | Publiceert voortgang vanuit domain naar UI (`Progress<T>`) |

### 8. Use-case workflows
**Analyse ZIP**
1. UI valideert pad en roept `AnalyzeZipUseCase`.
2. Use-case opent ZIP via `IZipArchiveFactory`.
3. `ZipAnalyser` leest metadata en IO’s; retourneert `ZipAnalyseResult`.
4. Resultaat naar UI + logservice; metadata weergegeven in tekstveld.

**Transformatie Publicatie**
1. UI levert `TransformRequest` (bronpad, doelpad, modus flags).
2. `TransformZipUseCase` opent bron- en doelsystemen binnen `using` scope.
3. `ZipAnalyser` → basisdata.
4. `IoProcessor` verwerkt IO’s (PDF/GML naar root, IO-XML genereren).
5. `BesluitGenerator` produceert `besluit.xml` + `opdracht.xml`.
6. `ManifestBuilder` verzamelt alle bestanden en voegt `manifest.xml` toe.
7. `ReportService` schrijft rapport; events sturen status naar UI.

**Validatie / Intrekking / Doorlevering**
- Zelfde stappen als transformatie, maar andere flags.
- `IntrekkingProcessor` en `DoorleveringProcessor` passen specifieke logica toe (bijv. muteren OW-bestanden, andere bestandsnamen).
- Bestandsnaamlogica komt in `OutputFileNameService` om consistentie te borgen.

### 9. Data & configuratie
- **Models** (`/src/OTST.Domain/Models`): immutable classes voor FRBR-data, IO-info, opdrachtmetadata.
- **Configuratie**:
  - `AppConfig.DefaultInputZip`
  - Pad-templates voor `*_rapport.txt`, `publicatieOpdracht_initieel.zip` etc.
  - Schema-locaties voor XML-validatie.
- **Resources**:
  - XSD’s (STOP-standaard), voorbeeld-rapport sjablonen, voorbeeld-invoerzips voor demo.

### 10. Cross-cutting concerns
- **Logging**: Domain-services loggen via `ILogger`; UI toont recente logregels.
- **Foutafhandeling**: gecentraliseerde exceptionhandler → UI dialog + log; herstelt voortgangsbalk.
- **Progress**: domain rapporteert percentage of fase (`Analyze`, `Process IO`, `Generate Manifest`).
- **Internationalisatie**: Primair NL, maar resource dictionaries voorbereid voor EN.
- **Validation**: Input padvalidatie, consistentie-checks (bijv. verplichte documenten aanwezig).

### 11. Teststrategie
- **Unit tests** target domain-services met controlled ZIP’s (in-memory).
- **Integration tests** gebruiken temp directories en echte ZIP’s uit `InputVoorbeeld`.
- **UI tests** optioneel via WinAppDriver of Playwright (low priority).
- **Performance tests** scriptbaar via console-runner (zelfde domain assembly).

### 12. Projectstructuur (voorstel)
```
OTST.sln
  src/
    OTST.App/                 (WPF, Presentation)
    OTST.Application/         (Use-cases, DTO's)
    OTST.Domain/              (Business logic, models)
    OTST.Infrastructure/      (IO, XML, logging, DI bootstrap)
    OTST.Resources/           (XSD's, voorbeelddata)
  tests/
    OTST.Domain.Tests/
    OTST.Integration.Tests/
  docs/
    architecture.md
    decision-records/
```

### 13. Deploy & operations
- Build pipeline: GitHub Actions/Azure DevOps → `dotnet build`, tests, `dotnet publish --self-contained`.
- Artefact: MSIX of ZIP met exe + dependencies.
- Logging directory: `%LOCALAPPDATA%\OTST\logs`.
- Config override via `appsettings.json` in installatiedir, user-level overrides via `appsettings.user.json`.

### 14. Open vragen / beslispunten
- Moet de UI functionaliteit (kaartviewer, teksteditor) uit de huidige React UI worden gerepliceerd? Zo ja, kiezen voor WebView2 integratie of native componenten.
- Ondersteuning voor command-line modus gewenst?
- Welke XML-schema versies exact? (inventariseren uit Java-project / XSD’s).
- Moet validatie tegen externe services (bijv. STOP API’s) plaatsvinden?
- Eventuele toekomstige cloud-integratie (bijv. VTH systemen) meenemen?

### 15. Volgende stappen
1. Requirements bevestigen (UI scope, CLI, distributie).
2. Beslissingen vastleggen in ADR’s (UI framework, packaging).
3. MVP backlog definiëren (Analyse + Publicatie transformatie).
4. Start met domain-layer porting (ZipAnalyser tests).
5. UI-prototype opleveren voor stakeholdersfeedback.



