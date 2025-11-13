## OTST (.NET)

### Lokale development
- `dotnet build` – compileer alle projecten.
- `dotnet test` – voer unit-tests uit (xUnit + FluentAssertions).
- `dotnet run --project OTST.App/OTST.App.csproj` – start de WPF UI (analysefunctie beschikbaar, meer flows volgen).
- `dotnet publish OTST.App/OTST.App.csproj -c Release -r win-x64 --self-contained false` – publiceer de WPF-app (zelfde commando wordt gebruikt door de CI workflow).

> 💡 Target framework staat momenteel op `net9.0-windows` omdat alleen de .NET 9 SDK aanwezig is. Pas de projectbestanden aan zodra een `net8.0-windows` SDK beschikbaar is.


