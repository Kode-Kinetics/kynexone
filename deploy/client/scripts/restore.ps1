# Restore KynexOne from a backup folder (Windows Server). This REPLACES the current database and documents.
#
#   docker compose stop backend web      # restore refuses to run while the API is running
#   .\scripts\restore.ps1 backups\20261025T020000Z [-Yes]
param([Parameter(Mandatory = $true)][string]$Backup, [switch]$Yes)
. "$PSScriptRoot\_common.ps1"

$src = (Resolve-Path $Backup).Path
$dump = Join-Path $src 'database.dump'
if (-not (Test-Path $dump)) { Die "$dump is missing." }
Assert-Docker; Assert-Env
if (Test-BackendRunning) { Die 'The API is running. Stop it first: docker compose stop backend web' }

$manifestPath = Join-Path $src 'manifest.txt'
$backupVersion = ''
if (Test-Path $manifestPath) {
    foreach ($line in Get-Content $manifestPath) {
        if ($line -match '^kynexone_version=(.+)$') { $backupVersion = $Matches[1] }
        elseif ($line -match '^([0-9a-f]{64})  (.+)$') {
            $actual = (Get-FileHash (Join-Path $src $Matches[2]) -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actual -ne $Matches[1]) { Die "The backup's checksum does not match: $($Matches[2])" }
        }
    }
    Say 'Backup checksums verified'
} else {
    Warn "The backup has no manifest.txt; its files cannot be verified."
}

if (-not $Yes) {
    $answer = Read-Host "Replace ALL current KynexOne data with the backup from $(Split-Path -Leaf $src)? Type RESTORE"
    if ($answer -ne 'RESTORE') { Die 'Cancelled.' }
}

& docker compose --env-file .env -f docker-compose.yml stop proxy *> $null
if ($backupVersion -and $backupVersion -ne (Get-EnvValue KYNEXONE_VERSION)) {
    Say "Returning to version $backupVersion (the backup's version)"
    Set-EnvValue KYNEXONE_VERSION $backupVersion
}

Invoke-Compose up -d --wait postgres redis
Say 'Restoring the database'
Invoke-Compose exec -T postgres psql -U kynexone -d postgres -v ON_ERROR_STOP=1 `
    -c 'DROP DATABASE IF EXISTS kynexone WITH (FORCE);' -c 'CREATE DATABASE kynexone OWNER kynexone;'
$pg = (& docker compose --env-file .env -f docker-compose.yml ps -q postgres).Trim()
& docker cp $dump "${pg}:/tmp/kynexone.dump"
Invoke-Compose exec -T postgres pg_restore -U kynexone -d kynexone --no-owner --exit-on-error /tmp/kynexone.dump
Invoke-Compose exec -T postgres rm -f /tmp/kynexone.dump

if (Test-Path (Join-Path $src 'documents.tar.gz')) {
    Say 'Restoring the documents'
    & docker run --rm -v kynexone_documents:/documents -v "${src}:/backup:ro" postgres:16-alpine `
        sh -c 'find /documents -mindepth 1 -delete && tar xzf /backup/documents.tar.gz -C /documents && chown -R 1654 /documents'
    if ($LASTEXITCODE -ne 0) { Die 'Restoring the documents failed.' }
}

Say 'Starting KynexOne'
Invoke-Api --maintenance off
Invoke-Compose up -d
if (-not (Wait-Ready 60)) { Die 'KynexOne did not become ready after the restore. See: docker compose logs backend' }
if (-not (Invoke-SmokeCheck)) { Warn 'Smoke checks reported a problem (above).' }
Say 'Restore complete.'
