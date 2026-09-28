# Локальный NuGet-источник (offline-загрузка)

`NuGet.config` ссылается на эту папку как на источник `local` — рядом с nuget.org.
На машине без интернета сюда нужно положить `.nupkg` пакетов, перечисленных в
`src/PathRouter.App/PathRouter.App.csproj` и их транзитивных зависимостей:

- Microsoft.WindowsAppSDK 1.8.260921001 (+ зависимости: foundation, winui, dwrite, AI, runtime и пр.)
- Microsoft.Graphics.Win2D 1.4.0
- Microsoft.Data.Sqlite 8.0.10 (для Core/SmokeTest)

Сами `.nupkg` в git не коммитятся (≈480 МБ) — папка заполняется на конкретной машине.
С интернетом просто `dotnet restore PathRouter.sln` — всё подтянется с nuget.org.
