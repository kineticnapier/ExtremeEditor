$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "src\ExtremeEditor.Wpf\ExtremeEditor.Wpf.csproj"

& dotnet build $project --configuration Release

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
