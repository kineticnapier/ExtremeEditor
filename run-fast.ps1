$ErrorActionPreference = "Stop"
$executable = Join-Path $PSScriptRoot "src\ExtremeEditor.Wpf\bin\Release\net8.0-windows\ExtremeEditor.Wpf.exe"

if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    [Console]::Error.WriteLine("ExtremeEditor Release executable was not found: $executable")
    [Console]::Error.WriteLine("Run .\build-fast.ps1 first.")
    exit 1
}

& $executable @args

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
