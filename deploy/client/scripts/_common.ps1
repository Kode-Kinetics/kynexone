# Shared by the KynexOne client scripts (Windows). Every script runs from the kit directory (the one holding
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

function Wait-Ready([int]$Tries = 60) {
    Say 'Waiting for KynexOne to report ready'
    for ($i = 0; $i -lt $Tries; $i++) {
        & docker compose --env-file .env -f docker-compose.yml exec -T caddy wget -q -O /dev/null http://api:5117/health/ready *> $null
        if ($LASTEXITCODE -eq 0) { Say 'KynexOne is ready.'; return $true }
        Start-Sleep -Seconds 5
    }
    return $false
}

function Invoke-Api { Invoke-Compose run --rm --no-deps api @args }
