[CmdletBinding()]
param(
    [ValidateSet("Quick", "Full")]
    [string]$Mode = "Quick",
    [ValidateRange(1, 64)]
    [int]$Jobs = 6,
    [switch]$NoRestore,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$watch = [System.Diagnostics.Stopwatch]::StartNew()
$tests = @("Core", "Audio")
if ($Mode -eq "Full") { $tests += "WPF" }
Write-Host ("Selected {0} test suites: {1}" -f $Mode, ($tests -join ", "))

$projects = @{
    Core = Join-Path $PSScriptRoot "src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj"
    Audio = Join-Path $PSScriptRoot "src\ExtremeEditor.Audio.Tests\ExtremeEditor.Audio.Tests.csproj"
    WPF = Join-Path $PSScriptRoot "src\ExtremeEditor.Wpf.Tests\ExtremeEditor.Wpf.Tests.csproj"
}
$assemblies = @{
    Core = Join-Path $PSScriptRoot "src\ExtremeEditor.Core.Tests\bin\Release\net8.0\ExtremeEditor.Core.Tests.dll"
    Audio = Join-Path $PSScriptRoot "src\ExtremeEditor.Audio.Tests\bin\Release\net8.0-windows\ExtremeEditor.Audio.Tests.dll"
    WPF = Join-Path $PSScriptRoot "src\ExtremeEditor.Wpf.Tests\bin\Release\net8.0-windows\ExtremeEditor.Wpf.Tests.dll"
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

# Build outputs are shared; only test execution is parallel. Launch real
# System.Diagnostics.Process objects rather than Start-Process -PassThru,
# which did not expose reliable ExitCode values on the reporter's machine.
$started = @()
$failed = $false
try {
    foreach ($name in $tests) {
        $info = [System.Diagnostics.ProcessStartInfo]::new()
        $info.FileName = "dotnet"
        # Audio test module initializers resolve source files relative to the repository root.
        # PowerShell / ProcessStartInfo may otherwise inherit a profile directory.
        $info.WorkingDirectory = $PSScriptRoot
        $info.Arguments = 'exec "' + $assemblies[$name] + '"'
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true

        # Do not mutate the invoking PowerShell session's environment.
        $info.EnvironmentVariables.Remove("EXTREMEEDITOR_COLLABORATION_REMOVAL_ONLY")
        $info.EnvironmentVariables.Remove("EXTREMEEDITOR_AUDIO_DENSE_ONLY")
        if ($name -eq "Core" -and $Mode -ne "Full") {
            $info.EnvironmentVariables["EXTREMEEDITOR_COLLABORATION_REMOVAL_ONLY"] = "1"
        }
        if ($name -eq "Audio" -and $Mode -ne "Full") {
            $info.EnvironmentVariables["EXTREMEEDITOR_AUDIO_DENSE_ONLY"] = "1"
        }

        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $info
        if (-not $process.Start()) {
            $process.Dispose()
            throw "Could not start $name test process."
        }
        # Drain both pipes while both tests run, even when a test produces
        # enough output to fill its pipe before WaitForExit.
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $started += [pscustomobject]@{
            Name = $name
            Process = $process
            Stdout = $stdoutTask
            Stderr = $stderrTask
        }
        Write-Host "Started $name tests (PID $($process.Id))."
    }

    foreach ($job in $started) {
        $job.Process.WaitForExit()
        $stdout = $job.Stdout.GetAwaiter().GetResult()
        $stderr = $job.Stderr.GetAwaiter().GetResult()
        [int]$exitCode = $job.Process.ExitCode
        Write-Host ("--- {0} tests (exit {1}) ---" -f $job.Name, $exitCode)
        if (-not [string]::IsNullOrEmpty($stdout)) { Write-Host $stdout.TrimEnd() }
        if (-not [string]::IsNullOrEmpty($stderr)) {
            [Console]::Error.WriteLine($stderr.TrimEnd())
        }
        if ($exitCode -ne 0) { $failed = $true }
    }
}
finally {
    foreach ($job in $started) {
        try {
            if (-not $job.Process.HasExited) { $job.Process.WaitForExit() }
        }
        finally { $job.Process.Dispose() }
    }
}

$watch.Stop()
Write-Host ("test-fast: {0:N2}s ({1}, {2} suite(s))" -f $watch.Elapsed.TotalSeconds, $Mode, $tests.Count)
if ($failed) { exit 1 }
exit 0
