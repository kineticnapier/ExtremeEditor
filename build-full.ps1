[CmdletBinding()]
param(
    [ValidateRange(1, 64)]
    [int]$Jobs = 6,
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"
$nativeSource = Join-Path $PSScriptRoot "src\ExtremeEditor.NativeRenderer"
$nativeBuild = Join-Path $PSScriptRoot "build\native-renderer"
$project = Join-Path $PSScriptRoot "src\ExtremeEditor.Wpf\ExtremeEditor.Wpf.csproj"
$watch = [System.Diagnostics.Stopwatch]::StartNew()

# A full native build also compiles the regression executables. Ordinary
# build-fast only compiles the DLL required for running the WPF editor.
& cmake -S $nativeSource -B $nativeBuild -A x64
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& cmake --build $nativeBuild --config Release --parallel $Jobs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$buildArgs = @(
    "build", $project, "--configuration", "Release",
    "-m:$Jobs", "-p:NativeRendererParallelJobs=$Jobs"
)
if ($NoRestore) { $buildArgs += "--no-restore" }

& dotnet @buildArgs
$result = $LASTEXITCODE
$watch.Stop()
Write-Host ("build-full: {0:N2}s (jobs: {1})" -f $watch.Elapsed.TotalSeconds, $Jobs)
if ($result -ne 0) { exit $result }
