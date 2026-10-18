[CmdletBinding()]
param(
    [ValidateSet("Quick", "Full", "Packages", "AudioCut")]
    [string]$Mode = "Quick",
    [ValidateRange(1, 64)]
    [int]$Jobs = 6,
    [switch]$NoRestore,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$watch = [System.Diagnostics.Stopwatch]::StartNew()
$tests = @()
if ($Mode -in @("Quick", "Full", "Packages")) { $tests += "Core" }
if ($Mode -in @("Quick", "Full", "AudioCut")) { $tests += "Audio" }

$projects = @{
    Core = Join-Path $PSScriptRoot "src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj"
    Audio = Join-Path $PSScriptRoot "src\ExtremeEditor.Audio.Tests\ExtremeEditor.Audio.Tests.csproj"
}
$assemblies = @{
    Core = Join-Path $PSScriptRoot "src\ExtremeEditor.Core.Tests\bin\Release\net8.0\ExtremeEditor.Core.Tests.dll"
    Audio = Join-Path $PSScriptRoot "src\ExtremeEditor.Audio.Tests\bin\Release\net8.0-windows\ExtremeEditor.Audio.Tests.dll"
}

# Sequential builds avoid competing writes to Core's shared bin/obj. Tests
# have separate output directories and can run as independent processes.
if (-not $NoBuild) {
    foreach ($name in $tests) {
        $buildArgs = @("build", $projects[$name], "--configuration", "Release", "-m:$Jobs")
        if ($NoRestore) { $buildArgs += "--no-restore" }
        Write-Host "Building $name tests..."
        & dotnet @buildArgs
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}
foreach ($name in $tests) {
    if (-not (Test-Path -LiteralPath $assemblies[$name] -PathType Leaf)) {
        throw "Test assembly is missing: $($assemblies[$name]). Run without -NoBuild first."
    }
}

$logDir = Join-Path ([IO.Path]::GetTempPath()) ("extremeeditor-parallel-tests-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $logDir | Out-Null
$jobsStarted = @()
$previousCore = [Environment]::GetEnvironmentVariable("EXTREMEEDITOR_TOGETHER_PACKAGES_ONLY", "Process")
$previousAudio = [Environment]::GetEnvironmentVariable("EXTREMEEDITOR_AUDIO_CUT_ONLY", "Process")
$failed = $false
try {
    foreach ($name in $tests) {
        # Set environment before process creation; each child inherits its own
        # independent values. Restore the caller's environment in finally.
        $coreOnly = if ($name -eq "Core" -and $Mode -ne "Full") { "1" } else { $null }
        $audioOnly = if ($name -eq "Audio" -and $Mode -ne "Full") { "1" } else { $null }
        [Environment]::SetEnvironmentVariable("EXTREMEEDITOR_TOGETHER_PACKAGES_ONLY", $coreOnly, "Process")
        [Environment]::SetEnvironmentVariable("EXTREMEEDITOR_AUDIO_CUT_ONLY", $audioOnly, "Process")

        $output = Join-Path $logDir ($name + ".stdout.txt")
        $errors = Join-Path $logDir ($name + ".stderr.txt")
        $quotedAssembly = '"' + $assemblies[$name] + '"'
        $process = Start-Process -FilePath "dotnet" -ArgumentList @("exec", $quotedAssembly) -NoNewWindow -PassThru -RedirectStandardOutput $output -RedirectStandardError $errors
        $jobsStarted += [pscustomobject]@{
            Name = $name
            Process = $process
            Output = $output
            Errors = $errors
        }
        Write-Host "Started $name tests (PID $($process.Id))."
    }

    foreach ($job in $jobsStarted) {
        $job.Process.WaitForExit()
        Write-Host ("--- {0} tests (exit {1}) ---" -f $job.Name, $job.Process.ExitCode)
        if (Test-Path -LiteralPath $job.Output) {
            Get-Content -LiteralPath $job.Output | Out-Host
        }
        if (Test-Path -LiteralPath $job.Errors) {
            Get-Content -LiteralPath $job.Errors | Out-Host
        }
        if ($job.Process.ExitCode -ne 0) { $failed = $true }
    }
}
finally {
    [Environment]::SetEnvironmentVariable("EXTREMEEDITOR_TOGETHER_PACKAGES_ONLY", $previousCore, "Process")
    [Environment]::SetEnvironmentVariable("EXTREMEEDITOR_AUDIO_CUT_ONLY", $previousAudio, "Process")
    foreach ($job in $jobsStarted) {
        if (-not $job.Process.HasExited) { $job.Process.WaitForExit() }
        $job.Process.Dispose()
    }
    if (Test-Path -LiteralPath $logDir) {
        Remove-Item -LiteralPath $logDir -Recurse -Force
    }
}

$watch.Stop()
Write-Host ("test-fast: {0:N2}s ({1}, {2} suite(s))" -f $watch.Elapsed.TotalSeconds, $Mode, $tests.Count)
if ($failed) { exit 1 }
