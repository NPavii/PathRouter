# ============================================================
#  Караван (PathRouter) — сборка standalone-релиза
#  Результат: publish\ — папка с Караван.exe, НЕ требующая .NET Runtime
# ============================================================
#  Почему такая сложность (три шага):
#  1) XAML-компилятор Windows App SDK ломает XBF при проектной сборке —
#     рабочий XBF получается ТОЛЬКО через сборку solution (см. README).
#  2) Self-contained publish возможен только на уровне проекта и снова
#     портит XBF/.pri.
#  3) Поэтому: solution build (хорошие XBF/pri) -> publish (рантайм и apphost)
#     -> аккуратная подмена XBF и Караван.pri из solution build.
#  Плюс: убран <RuntimeIdentifiers> из csproj — с ним apphost получался
#  «framework-dependent» и standalone-запуск падал с «You must install .NET».
# ============================================================

param(
    [string]$Output = "publish"
)

$ErrorActionPreference = "Stop"
$Root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$Bin     = Join-Path $Root "src\PathRouter.App\bin\x64\Release\net8.0-windows10.0.19041.0"
$GoodXbf = @("App.xbf", "MainWindow.xbf", "GraphCanvas.xbf", "ForceGraphView.xbf")

Push-Location $Root
try {
    # dotnet может не лежать в PATH у внешнего вызывающего — подхватываем стандартные места
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        $env:PATH = "$env:PATH;C:\Program Files\dotnet"
    }

    Write-Host "== restore ==" -ForegroundColor Cyan
    dotnet restore PathRouter.sln

    Write-Host "== build (solution — правильный XBF) ==" -ForegroundColor Cyan
    dotnet build PathRouter.sln -c Release --no-restore

    # запущенный экземпляр держал бы файлы вывода — останавливаем
    Get-Process -Name "Караван" -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500

    Write-Host "== publish (self-contained win-x64) ==" -ForegroundColor Cyan
    dotnet publish src\PathRouter.App -c Release -r win-x64 --self-contained `
        -p:Platform=x64 -o $Output --no-restore

    Write-Host "== подмена XBF и .pri (обход ловушки WinApp SDK) ==" -ForegroundColor Cyan
    foreach ($f in $GoodXbf) {
        Copy-Item (Join-Path $Bin $f) (Join-Path $Output $f) -Force
    }
    Copy-Item (Join-Path $Bin "Караван.pri") (Join-Path $Output "Караван.pri") -Force

    Write-Host ""
    Write-Host "Готово: $Output\Караван.exe (standalone, .NET Runtime не нужен)" -ForegroundColor Green
    Write-Host "Smoke-тест логики: dotnet run --project src/PathRouter.SmokeTest -c Release"
}
finally {
    Pop-Location
}
