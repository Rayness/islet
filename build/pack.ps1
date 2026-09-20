# Собирает то, что раздаётся людям: установщик Setup.exe, портативный zip и пакет обновления.
# Нужны .NET 10 SDK и Velopack CLI: dotnet tool install -g vpk
param(
    [string]$Version = "",
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\Islet\Islet.csproj"
$publish = Join-Path $root "build\publish-$Runtime"
$releases = Join-Path $root "build\releases"

# Версия по умолчанию — та, что записана в проекте.
if (-not $Version) {
    $xml = [xml](Get-Content $project)
    $Version = ($xml.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
}
if (-not $Version) { throw "Не удалось определить версию: передайте -Version" }

# Каналы Velopack: x64 остаётся в "win" — на него смотрят уже установленные копии,
# ARM64 живёт отдельным каналом, иначе второй прогон затрёт файлы первого.
$channel = if ($Runtime -eq "win-x64") { "win" } else { $Runtime }

Write-Host "Islet $Version ($Runtime, канал $channel)" -ForegroundColor Cyan

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

# --self-contained: на машине не нужен установленный .NET.
dotnet publish $project -c Release -r $Runtime --self-contained -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с кодом $LASTEXITCODE" }

# Без PRI приложения WinUI не найдёт скомпилированный XAML и exe упадёт на первом окне.
if (-not (Test-Path (Join-Path $publish "Islet.pri"))) { throw "В $publish нет Islet.pri — собирать установщик нельзя" }

# vpk собирает Setup.exe, портативный zip и nupkg, которым обновляются установленные копии.
vpk pack `
    --packId Islet `
    --packVersion $Version `
    --packDir $publish `
    --mainExe Islet.exe `
    --channel $channel `
    --runtime $Runtime `
    --packTitle Islet `
    --packAuthors Rayness `
    --icon (Join-Path $root "src\Islet\Assets\islet.ico") `
    --outputDir $releases
if ($LASTEXITCODE -ne 0) { throw "vpk pack завершился с кодом $LASTEXITCODE" }

Write-Host ""
Write-Host "Готово: $releases" -ForegroundColor Green
Get-ChildItem $releases | Select-Object Name, @{ n = "МБ"; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
