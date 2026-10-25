# Shared by the KynexOne client scripts (Windows Server). Every script runs from the kit directory (the one holding
# docker-compose.yml and .env), whatever directory it was started from. Same steps as the .sh scripts.
$ErrorActionPreference = 'Stop'
$KitDir = Split-Path -Parent $PSScriptRoot
Set-Location $KitDir

function Say([string]$Text)  { Write-Host "==> $Text" -ForegroundColor Cyan }
function Warn([string]$Text) { Write-Warning $Text }
function Die([string]$Text)  { Write-Host "ERROR: $Text" -ForegroundColor Red; exit 1 }

function Invoke-Compose {
    & docker compose --env-file .env -f docker-compose.yml @args
    if ($LASTEXITCODE -ne 0) { throw "docker compose $($args -join ' ') failed (exit $LASTEXITCODE)." }
}

# Like Invoke-Compose, but returns the exit code instead of throwing (probes and checks).
function Test-Compose {
    & docker compose --env-file .env -f docker-compose.yml @args *> $null
    return ($LASTEXITCODE -eq 0)
}

function Assert-Docker {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { Die 'Docker is not installed.' }
    & docker info *> $null
    if ($LASTEXITCODE -ne 0) { Die 'Docker is not running.' }
}

function Assert-Env { if (-not (Test-Path .env)) { Die "No .env here ($KitDir). Run scripts\install.ps1 first." } }

function Get-EnvValue([string]$Key) {
    $line = Get-Content .env | Where-Object { $_ -match "^$Key=" } | Select-Object -Last 1
    if ($line) { return $line.Substring($Key.Length + 1) } else { return '' }
}

function Set-EnvValue([string]$Key, [string]$Value) {
    $lines = @(Get-Content .env)
    $found = $false
    $lines = $lines | ForEach-Object { if ($_ -match "^$Key=") { $found = $true; "$Key=$Value" } else { $_ } }
    if (-not $found) { $lines += "$Key=$Value" }
    # No BOM: docker compose reads .env as plain UTF-8.
    [IO.File]::WriteAllLines((Join-Path $KitDir '.env'), [string[]]$lines, (New-Object Text.UTF8Encoding $false))
}

function New-Secret([int]$Length) {
    $chars = [char[]]'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'
    $bytes = New-Object byte[] $Length
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
}

function Test-BackendRunning {
    $id = & docker compose --env-file .env -f docker-compose.yml ps --status running -q backend 2>$null
    return [bool]$id
}

# /health/ready is 200 only once the API can serve: database reachable, every migration this image carries applied.
function Wait-Ready([int]$Tries = 60) {
    Say 'Waiting for KynexOne to report ready'
    for ($i = 0; $i -lt $Tries; $i++) {
        if (Test-Compose exec -T proxy wget -q -O /dev/null http://backend:5117/health/ready) { Say 'KynexOne is ready.'; return $true }
        Start-Sleep -Seconds 5
    }
    return $false
}

function Invoke-SmokeCheck {
    $info = & docker compose --env-file .env -f docker-compose.yml exec -T proxy wget -q -O - http://backend:5117/api/public/installation 2>$null
    if ($LASTEXITCODE -ne 0) { Warn 'GET /api/public/installation failed.'; return $false }
    if ("$info" -notmatch '"setupComplete":true') { Warn "The organisation is not set up: $info"; return $false }
    if ("$info" -match '"state":"Missing"') { Warn 'No licence is installed yet.' }
    if (-not (Test-Compose exec -T proxy wget -q -O /dev/null http://web:5173/build-info)) { Warn 'The web front end is not answering.'; return $false }
    Say 'Smoke checks passed.'
    return $true
}

function Test-Bundle([string]$Dir) {
    foreach ($line in Get-Content (Join-Path $Dir 'CHECKSUMS.sha256')) {
        if ($line -notmatch '^([0-9a-f]{64}) [ *]?(.+)$') { continue }
        $actual = (Get-FileHash (Join-Path $Dir $Matches[2]) -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $Matches[1]) { return $false }
    }
    return $true
}

# One-shot API commands (--maintenance, --setup-organisation, --import-licence, --licence-status).
function Invoke-Api { Invoke-Compose run --rm --no-deps backend @args }
