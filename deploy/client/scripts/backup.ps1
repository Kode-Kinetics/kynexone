# Back up KynexOne (Windows Server). Same backup folder layout as backup.sh, so either restore script reads it.
# database.dump includes the data-protection keys: without them encrypted MFA secrets cannot be read.
#
#   .\scripts\backup.ps1 [-To D:\kynexone-backups]
param([string]$To)
. "$PSScriptRoot\_common.ps1"

Assert-Docker; Assert-Env
if (-not $To) { $To = Join-Path $KitDir 'backups' }
New-Item -ItemType Directory -Force $To | Out-Null
$To = (Resolve-Path $To).Path
$stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'")
$out = Join-Path $To $stamp
New-Item -ItemType Directory -Force $out | Out-Null

# Binary output must not pass through the PowerShell pipeline (it re-encodes text): write inside the container and
# copy the file out.
Say 'Dumping the database'
Invoke-Compose exec -T postgres pg_dump -U kynexone -d kynexone -Fc -f /tmp/kynexone.dump
$pg = (& docker compose --env-file .env -f docker-compose.yml ps -q postgres).Trim()
& docker cp "${pg}:/tmp/kynexone.dump" (Join-Path $out 'database.dump')
Invoke-Compose exec -T postgres rm -f /tmp/kynexone.dump
if ((Get-Item (Join-Path $out 'database.dump')).Length -eq 0) { Die 'The database dump is empty.' }

$provider = Get-EnvValue STORAGE_PROVIDER
if ($provider -eq 's3') {
    Warn "Documents are in an external S3 store ($(Get-EnvValue STORAGE_ENDPOINT)); back it up with that store's own tools."
} else {
    Say 'Archiving the documents'
    & docker run --rm -v kynexone_documents:/documents:ro -v "${out}:/backup" postgres:16-alpine tar czf /backup/documents.tar.gz -C /documents .
    if ($LASTEXITCODE -ne 0) { Die 'Archiving the documents failed.' }
}

Copy-Item .env (Join-Path $out 'env.backup')
$manifest = @("created_utc=$stamp", "kynexone_version=$(Get-EnvValue KYNEXONE_VERSION)", "domain=$(Get-EnvValue KYNEXONE_DOMAIN)", "storage_provider=$provider")
foreach ($f in 'database.dump', 'documents.tar.gz', 'env.backup') {
    $p = Join-Path $out $f
    if (Test-Path $p) { $manifest += "$((Get-FileHash $p -Algorithm SHA256).Hash.ToLowerInvariant())  $f" }
}
[IO.File]::WriteAllLines((Join-Path $out 'manifest.txt'), [string[]]$manifest, (New-Object Text.UTF8Encoding $false))
[IO.File]::WriteAllText((Join-Path $To 'LATEST'), $out)
Say "Backup written to $out"
