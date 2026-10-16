param(
    [switch]$NoBuild,
    [switch]$E2E,
    [int]$Port = 8787
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$service = Join-Path $root "cloudflare\together-bundle"
$exe = Join-Path $root "src\ExtremeEditor.Wpf\bin\Release\net8.0-windows\ExtremeEditor.Wpf.exe"
$testProject = Join-Path $root "src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj"
$testDll = Join-Path $root "src\ExtremeEditor.Core.Tests\bin\Release\net8.0\ExtremeEditor.Core.Tests.dll"
$vars = Join-Path $service ".dev.vars"
$previousDevVars = if (Test-Path -LiteralPath $vars -PathType Leaf) {
    [IO.File]::ReadAllBytes($vars)
} else { $null }
if (-not (Test-Path (Join-Path $service "node_modules\wrangler"))) {
    throw "Wrangler is missing. Run npm install in cloudflare\together-bundle."
}
if (-not $NoBuild) {
    if ($E2E) {
        & dotnet build $testProject -c Release
        if ($LASTEXITCODE -ne 0) { throw "Together Core E2E test build failed." }
    } else {
        & (Join-Path $root "build-fast.ps1")
        if ($LASTEXITCODE -ne 0) { throw "WPF Release build failed." }
    }
}
if ($E2E -and -not (Test-Path $testDll)) { throw "Core tests executable not found: $testDll" }
if (-not $E2E -and -not (Test-Path $exe)) { throw "Release exe not found: $exe" }
$room = "dev" + [Guid]::NewGuid().ToString("N").Substring(0,20)
$member = "host"
$keyBytes = New-Object byte[] 40
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($keyBytes) } finally { $rng.Dispose() }
$key = [BitConverter]::ToString($keyBytes).Replace("-", "")
$oldMaster = [Environment]::GetEnvironmentVariable("TOGETHER_BUNDLE_MASTER_KEY", "Process")
$oldUrl = [Environment]::GetEnvironmentVariable("EXTREMEEDITOR_TOGETHER_DEV_URL", "Process")
$oldMember = [Environment]::GetEnvironmentVariable("EXTREMEEDITOR_TOGETHER_DEV_MEMBER", "Process")
$oldToken = [Environment]::GetEnvironmentVariable("EXTREMEEDITOR_TOGETHER_DEV_TOKEN", "Process")
$e2eNames = @(
    "EXTREMEEDITOR_TOGETHER_CLOUD_E2E_ONLY",
    "EXTREMEEDITOR_TOGETHER_E2E_URL",
    "EXTREMEEDITOR_TOGETHER_E2E_HOST_TOKEN",
    "EXTREMEEDITOR_TOGETHER_E2E_GUEST_TOKEN"
)
$e2ePrevious = @{}
foreach ($name in $e2eNames) {
    $e2ePrevious[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
}
$server = $null
try {
    # This key is for one test session only; never publish it.
    [IO.File]::WriteAllText($vars, 'TOGETHER_BUNDLE_MASTER_KEY="' + $key + '"' + [Environment]::NewLine)
    $env:TOGETHER_BUNDLE_MASTER_KEY = $key
    $token = (& node (Join-Path $service "scripts\room-token.mjs") $room $member host 24 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $token) { throw "Could not generate development host token." }
    $env:EXTREMEEDITOR_TOGETHER_DEV_URL = "http://127.0.0.1:${Port}/rooms/$room/"
    $env:EXTREMEEDITOR_TOGETHER_DEV_MEMBER = $member
    $env:EXTREMEEDITOR_TOGETHER_DEV_TOKEN = $token
    if ($E2E) {
        $guestToken = (& node (Join-Path $service "scripts\room-token.mjs") $room guest reader 24 | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or -not $guestToken) { throw "Could not generate development guest token." }
        $env:EXTREMEEDITOR_TOGETHER_CLOUD_E2E_ONLY = "1"
        $env:EXTREMEEDITOR_TOGETHER_E2E_URL = $env:EXTREMEEDITOR_TOGETHER_DEV_URL
        $env:EXTREMEEDITOR_TOGETHER_E2E_HOST_TOKEN = $token
        $env:EXTREMEEDITOR_TOGETHER_E2E_GUEST_TOKEN = $guestToken
    }

    $server = Start-Process -FilePath (Join-Path $service "node_modules\.bin\wrangler.cmd") `
        -ArgumentList @("dev", "--local", "--port", "$Port") `
        -WorkingDirectory $service -PassThru
    $ready = $false
    for ($attempt = 0; $attempt -lt 35; $attempt++) {
        if ($server.HasExited) { throw "Wrangler exited before becoming ready." }
        try {
            $response = Invoke-WebRequest -Uri "$($env:EXTREMEEDITOR_TOGETHER_DEV_URL)bundle" `
                -Method GET -UseBasicParsing -TimeoutSec 2
        } catch [System.Net.WebException] {
            $httpResponse = $_.Exception.Response
            if ($null -ne $httpResponse -and [int]$httpResponse.StatusCode -eq 401) {
                $ready = $true
                $httpResponse.Close()
                break
            }
        } catch { }
        Start-Sleep -Milliseconds 350
    }
    if (-not $ready) { throw "Local Wrangler did not become ready on port $Port." }
    Write-Host "Together test server ready. Room: $room ; participant: $member"
    if ($E2E) {
        Write-Host "Running automated host/guest cloud E2E (no GUI interaction required)."
        & dotnet $testDll
        if ($LASTEXITCODE -ne 0) { throw "Together live cloud E2E failed." }
    } else {
        Write-Host "Open Together > details; development fields are prefilled."
        & $exe
    }
}
finally {
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
    if ($null -ne $previousDevVars) { [IO.File]::WriteAllBytes($vars, $previousDevVars) }
    elseif (Test-Path -LiteralPath $vars) { Remove-Item -LiteralPath $vars -Force }
    [Environment]::SetEnvironmentVariable("TOGETHER_BUNDLE_MASTER_KEY", $oldMaster, "Process")
    [Environment]::SetEnvironmentVariable("EXTREMEEDITOR_TOGETHER_DEV_URL", $oldUrl, "Process")
    [Environment]::SetEnvironmentVariable("EXTREMEEDITOR_TOGETHER_DEV_MEMBER", $oldMember, "Process")
    [Environment]::SetEnvironmentVariable("EXTREMEEDITOR_TOGETHER_DEV_TOKEN", $oldToken, "Process")
    foreach ($name in $e2eNames) {
        [Environment]::SetEnvironmentVariable($name, $e2ePrevious[$name], "Process")
    }
}
