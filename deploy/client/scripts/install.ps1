# First installation of KynexOne on this server (Windows Server). Same steps as install.sh.
#
#   .\scripts\install.ps1 [-Domain hr.example.local] [-Licence organisation.licence] [-ForceReconfigure]
#
# Run from an extracted offline bundle it loads the bundle's images after verifying CHECKSUMS.sha256; otherwise it
# pulls them. Unattended: set the OrganisationSetup__* environment variables and nothing is asked.
param([string]$Domain, [string]$Licence, [switch]$ForceReconfigure)
. "$PSScriptRoot\_common.ps1"

# 1. Prerequisites
Assert-Docker
if ((Test-Path .env) -and -not $ForceReconfigure) {
    Die 'KynexOne is already configured here (.env exists). Use scripts\upgrade.ps1 to upgrade, or -ForceReconfigure to change the address and TLS settings (the secrets are kept).'
}
foreach ($port in 80, 443) {
    if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) {
        if (-not (Test-Compose ps -q proxy)) { Die "Port $port is already in use on this server." }
    }
}

# 2. Settings
if (-not (Test-Path .env)) {
    Copy-Item .env.example .env
    Say 'Generating secrets'
    Set-EnvValue POSTGRES_PASSWORD (New-Secret 40)
    Set-EnvValue JWT_AUDIENCE ("kynexone-" + (New-Secret 12).ToLowerInvariant())
    Set-EnvValue JWT_SIGNING_KEY (New-Secret 96)
    Set-EnvValue PROXY_CLIENT_IP_SECRET (New-Secret 48)
    # Only the installing account and administrators may read the secrets.
    & icacls .env /inheritance:r /grant:r "$($env:USERNAME):(R,W)" /grant:r 'Administrators:(F)' | Out-Null
}
if (-not $Domain) { $Domain = Read-Host "Address people will use to reach KynexOne [$(Get-EnvValue KYNEXONE_DOMAIN)]" }
if (-not $Domain) { $Domain = Get-EnvValue KYNEXONE_DOMAIN }
if (-not $Domain -or $Domain -eq 'hr.example.local') { Die 'Give the real address with -Domain.' }
Set-EnvValue KYNEXONE_DOMAIN $Domain
$tls = Read-Host "TLS: internal | an email address (Let's Encrypt) | /certs/cert.pem /certs/key.pem [$(Get-EnvValue CADDY_TLS)]"
if ($tls) { Set-EnvValue CADDY_TLS $tls }
foreach ($key in 'POSTGRES_PASSWORD', 'JWT_AUDIENCE', 'JWT_SIGNING_KEY', 'PROXY_CLIENT_IP_SECRET') {
    if ((Get-EnvValue $key) -eq 'GENERATED') { Die "$key in .env is still GENERATED. Delete .env and run install.ps1 again." }
}

# 3. Images
$bundleDir = (Resolve-Path (Join-Path $KitDir '..\..')).Path
$imagesTar = Join-Path $bundleDir 'images\kynexone-images.tar'
if (Test-Path $imagesTar) {
    Say 'Verifying the offline bundle'
    if (-not (Test-Bundle $bundleDir)) { Die "The bundle's checksums do not match. Download it again." }
    Set-EnvValue KYNEXONE_VERSION ((Get-Content (Join-Path $bundleDir 'VERSION') -Raw).Trim())
    Say 'Loading images'
    & docker load -i $imagesTar
} else {
    Say "Pulling images for version $(Get-EnvValue KYNEXONE_VERSION)"
    Invoke-Compose pull postgres redis migrate backend web proxy
}

# 4. Database and storage
Say 'Starting the database and cache'
Invoke-Compose up -d --wait postgres redis
Say 'Applying the database migrations'
Invoke-Compose up migrate --exit-code-from migrate

# 5. Organisation
$organisations = (& docker compose --env-file .env -f docker-compose.yml exec -T postgres psql -U kynexone -d kynexone -tAc 'SELECT count(*) FROM tenants').Trim()
if ($organisations -ne '0') {
    Say 'The organisation is already set up; keeping it'
} else {
    Say 'Creating the organisation and its first administrator'
    $setupArgs = @()
    foreach ($var in 'OrganisationName', 'LegalName', 'HomeCountry', 'AdminEmail', 'AdminFullName', 'AdminPassword') {
        $value = [Environment]::GetEnvironmentVariable("OrganisationSetup__$var")
        if ($value) { $setupArgs += @('-e', "OrganisationSetup__$var=$value") }
    }
    Invoke-Compose run --rm --no-deps @setupArgs backend --setup-organisation
}

# 6. Licence
if ($Licence) {
    if (-not (Test-Path $Licence)) { Die "Licence file not found: $Licence" }
    Say 'Importing the licence'
    $abs = (Resolve-Path $Licence).Path
    Invoke-Compose run --rm --no-deps -T -v "${abs}:/tmp/kynexone.licence:ro" backend --import-licence /tmp/kynexone.licence
} else {
    $status = & docker compose --env-file .env -f docker-compose.yml run --rm --no-deps -T backend --licence-status
    $id = ($status | Where-Object { $_ -match '^Installation id' }) -replace '^Installation id\s*:\s*', ''
    Warn "No licence imported. Send this installation id to your KynexOne supplier: $id"
    Warn 'Then upload the licence in Tenant Admin -> Licence. Until then the API answers 402 licence_missing.'
}

# 7. Start
Say 'Starting KynexOne'
Invoke-Compose up -d
if (-not (Wait-Ready 60)) { Die 'KynexOne did not become ready. See: docker compose logs backend' }
if (-not (Invoke-SmokeCheck)) { Warn 'Smoke checks reported a problem (above).' }

Write-Host ""
Write-Host "KynexOne is installed: https://$(Get-EnvValue KYNEXONE_DOMAIN)"
Write-Host '  - Sign in as the administrator you just created.'
Write-Host '  - Schedule scripts\backup.ps1 (Task Scheduler) and copy the backups off this server.'
Write-Host '  - Keep .env safe: it holds the database password and the token signing key.'
