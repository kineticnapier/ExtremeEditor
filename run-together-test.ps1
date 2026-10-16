param(
    [switch]$NoBuild,
    [int]$Port = 8787
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$service = Join-Path $root "cloudflare\together-bundle"
$exe = Join-Path $root "src\ExtremeEditor.Wpf\bin\Release\net8.0-windows\ExtremeEditor.Wpf.exe"
$vars = Join-Path $service ".dev.vars"
if (Test-Path -LiteralPath $vars) {
    throw "Existing .dev.vars found. Back it up or remove it manually before running this isolated local test launcher."
}
if (-not (Test-Path (Join-Path $service "node_modules\wrangler"))) {
    throw "Wrangler is missing. Run npm install in cloudflare\together-bundle."
}
if (-not $NoBuild) {
    & (Join-Path $root "build-fast.ps1")
    if ($LASTEXITCODE -ne 0) { throw "WPF Release build failed." }
}
if (-not (Test-Path $exe)) { throw "Release exe not found: $exe" }
$room = "dev" + [Guid]::NewGuid().ToString("N").Substring(0,20)
$member = "host"
$keyBytes = New-Object byte[] 40
[System.Security.Cryptography.RandomNumberGenerator]::Fill($keyBytes)
$key = [Convert]::ToHexString($keyBytes)
$oldMaster = [Environment]::GetEnvironmentVariable("TOGETHER_BUNDLE_MASTER_KEY", "Process")
$oldUrl = [Environment]::GetEnvironmentVariable("EXTREMEEDITOR_TOGETHER_DEV_URL", "Process")
$oldMember = [Environment]::GetEnvironmentVariable("EXTREMEEDITOR_TOGETHER_DEV_MEMBER", "Process")
$oldToken = [Environment]::GetEnvironmentVariable("EXTREMEEDITOR_TOGETHER_DEV_TOKEN", "Process")
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

    $server = Start-Process -FilePath (Join-Path $service "node_modules\.bin\wrangler.cmd") `
        -ArgumentList @("dev", "--local", "--port", "$Port") `
        -WorkingDirectory $service -PassThru
    $ready = $false
    for ($attempt = 0; $attempt -lt 35; $attempt++) {
        if ($server.HasExited) { throw "Wrangler exited before becoming ready." }
        try {
            $response = Invoke-WebRequest -Uri "$($env:EXTREMEEDITOR_TOGETHER_DEV_URL)bundle" `
                -Method GET -SkipHttpErrorCheck -TimeoutSec 2
            if ($response.StatusCode -eq 401) { $ready = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 350
    }
    if (-not $ready) { throw "Local Wrangler did not become ready on port $Port." }
    Write-Host "Together test server ready. Room: $room ; participant: $member"
    Write-Host "Open Together > details; development fields are prefilled."
    & $exe
}
finally {
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $vars) { Remove-Item -LiteralPath $vars -Force }
    [Environment]::SetEnvironmentVariable("TOGETHER_BUNDLE_MASTER_KEY", $oldMaster, "Process")
    [Environment]::SetEnvironmentVariable("EXTREMEEDITOR_TOGETHER_DEV_URL", $oldUrl, "Process")
    [Environment]::SetEnvironmentVariable("EXTREMEEDITOR_TOGETHER_DEV_MEMBER", $oldMember, "Process")
    [Environment]::SetEnvironmentVariable("EXTREMEEDITOR_TOGETHER_DEV_TOKEN", $oldToken, "Process")
}
