[CmdletBinding()]
param(
    [ValidateRange(1, 64)]
    [int]$Jobs = 6,
    [switch]$NoRestore,
    [switch]$NoBuild,
    [switch]$Native
)

$ErrorActionPreference = "Stop"
$params = @{
    Mode = "Full"
    Jobs = $Jobs
    NoRestore = $NoRestore
    NoBuild = $NoBuild
}

& (Join-Path $PSScriptRoot "test-fast.ps1") @params
$result = $LASTEXITCODE
if ($result -ne 0) { exit $result }

if ($Native) {
    $nativeBuild = Join-Path $PSScriptRoot "build\native-renderer"
    if (-not (Test-Path -LiteralPath $nativeBuild -PathType Container)) {
        throw "No native test build found. Run .\build-full.ps1 first."
    }
    & ctest --test-dir $nativeBuild -C Release --output-on-failure --parallel $Jobs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
