param(
    [string]$WorkerUrl = "https://extremeeditor-together-bundle.stopping-bypass.workers.dev",
    [string]$EnvFile,
    [switch]$NoBuild
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$service = Join-Path $root "cloudflare\together-bundle"
$testProject = Join-Path $root "src\ExtremeEditor.Core.Tests\ExtremeEditor.Core.Tests.csproj"
$testDll = Join-Path $root "src\ExtremeEditor.Core.Tests\bin\Release\net8.0\ExtremeEditor.Core.Tests.dll"
$tokenScript = Join-Path $service "scripts\room-token.mjs"
$variableNames = @(
    "TOGETHER_BUNDLE_MASTER_KEY",
    "EXTREMEEDITOR_TOGETHER_CLOUD_E2E_ONLY",
    "EXTREMEEDITOR_TOGETHER_E2E_URL",
    "EXTREMEEDITOR_TOGETHER_E2E_HOST_TOKEN",
    "EXTREMEEDITOR_TOGETHER_E2E_GUEST_TOKEN"
)
$previous = @{}
foreach ($name in $variableNames) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
}

function Read-LocalSecret([string]$path) {
    # Never print the file's contents or the extracted secret.
    foreach ($line in [IO.File]::ReadAllLines($path)) {
        if ($line -match '^\s*(?:export\s+)?TOGETHER_BUNDLE_MASTER_KEY\s*=\s*(.*?)\s*$') {
            $value = $Matches[1].Trim()
            if ($value.Length -ge 2 -and
                (($value.StartsWith('"') -and $value.EndsWith('"')) -or
                 ($value.StartsWith("'") -and $value.EndsWith("'")))) {
                $value = $value.Substring(1, $value.Length - 2)
            }
            return $value
        }
    }
    throw "TOGETHER_BUNDLE_MASTER_KEY was not found in the selected .env file."
}

try {
    if (-not $EnvFile) {
        $candidates = @(@(
            (Join-Path $service ".env"),
            (Join-Path $root ".env")
        ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
        if ($candidates.Count -ne 1) {
            throw "Expected exactly one .env in the repo root or cloudflare/together-bundle. Pass -EnvFile <path> to select explicitly."
        }
        $EnvFile = $candidates[0]
    }
    if (-not (Test-Path -LiteralPath $EnvFile -PathType Leaf)) {
        throw "Specified .env file does not exist."
    }
    $master = Read-LocalSecret $EnvFile
    if ($master.Length -lt 32) {
        throw "TOGETHER_BUNDLE_MASTER_KEY must be at least 32 characters."
    }

    $base = [Uri]$WorkerUrl
    if ($base.Scheme -ne "https" -or $base.UserInfo -or $base.Query -or $base.Fragment -or
        $base.AbsolutePath -ne "/" ) {
        throw "WorkerUrl must be an HTTPS origin without credentials, query or path."
    }
    if ($base.Host -ne "extremeeditor-together-bundle.stopping-bypass.workers.dev") {
        throw "Public E2E is restricted to the known dedicated ExtremeEditor Worker."
    }

    if (-not $NoBuild) {
        & dotnet build $testProject -c Release
        if ($LASTEXITCODE -ne 0) { throw "Core E2E test build failed." }
    }
    if (-not (Test-Path -LiteralPath $testDll -PathType Leaf)) {
        throw "Core test DLL not found. Build without -NoBuild first."
    }

    # The master stays only in this process and its short-lived Node child.
    $env:TOGETHER_BUNDLE_MASTER_KEY = $master
    $master = $null
    $room = "dev" + [Guid]::NewGuid().ToString("N").Substring(0, 20)
    $hostToken = (& node $tokenScript $room host host 1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $hostToken) { throw "Unable to issue host token." }
    $guestToken = (& node $tokenScript $room guest reader 1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $guestToken) { throw "Unable to issue guest token." }

    $env:EXTREMEEDITOR_TOGETHER_CLOUD_E2E_ONLY = "1"
    $env:EXTREMEEDITOR_TOGETHER_E2E_URL = $WorkerUrl.TrimEnd("/") + "/rooms/$room/"
    $env:EXTREMEEDITOR_TOGETHER_E2E_HOST_TOKEN = $hostToken
    $env:EXTREMEEDITOR_TOGETHER_E2E_GUEST_TOKEN = $guestToken
    Write-Host "Testing the deployed ExtremeEditor Worker with an isolated synthetic room..."
    & dotnet $testDll
    if ($LASTEXITCODE -ne 0) { throw "Public cloud E2E failed." }
    Write-Host "PASS: deployed Cloudflare two-client end-to-end test."
} finally {
    foreach ($name in $variableNames) {
        [Environment]::SetEnvironmentVariable($name, $previous[$name], "Process")
    }
}
