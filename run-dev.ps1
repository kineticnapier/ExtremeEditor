$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "src\ExtremeEditor.Wpf\ExtremeEditor.Wpf.csproj"

if ($args.Count -gt 0) {
    & dotnet run --project $project -- @args
}
else {
    & dotnet run --project $project
}

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
