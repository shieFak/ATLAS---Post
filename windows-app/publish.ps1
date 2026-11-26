$ErrorActionPreference = 'Stop'
$projectFile = Join-Path $PSScriptRoot 'ChelbasketDesktop/ChelbasketDesktop.csproj'
$outputDirectory = Join-Path $PSScriptRoot 'publish'

dotnet publish $projectFile `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    --output $outputDirectory

if ($LASTEXITCODE -ne 0) {
    throw 'Не удалось собрать Windows-приложение.'
}

$executable = Join-Path $outputDirectory 'Atlas.exe'
if (-not (Test-Path -LiteralPath $executable)) {
    throw "EXE не найден после сборки: $executable"
}

Write-Host "Готово: $executable"
