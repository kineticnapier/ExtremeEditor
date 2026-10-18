[CmdletBinding()]
param(
    [ValidateRange(1, 64)]
    [int]$Jobs = 6,
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "src\ExtremeEditor.Wpf\ExtremeEditor.Wpf.csproj"

# MSBuild handles managed project dependencies. The WPF project incrementally
# builds only the NativeRenderer DLL and the AssetExtractor when their inputs
# changed, rather than rebuilding every C++ test target on every edit.
$buildArgs = @(
    "build", $project, "--configuration", "Release",
    "-m:$Jobs", "-p:NativeRendererParallelJobs=$Jobs"
)
if ($NoRestore) {
    $buildArgs += "--no-restore"
}

$watch = [System.Diagnostics.Stopwatch]::StartNew()
& dotnet @buildArgs
$result = $LASTEXITCODE
$watch.Stop()
Write-Host ("build-fast: {0:N2}s (MSBuild max nodes: {1}; CMake jobs: {1})" -f $watch.Elapsed.TotalSeconds, $Jobs)

if ($result -ne 0) {
    exit $result
}
