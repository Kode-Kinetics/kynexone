# First installation of KynexOne on this server (Windows). Same steps as install.sh.
#
#   .\scripts\install.ps1 -Domain hr.example.local [-Images kynexone-1.0.0.tar] [-Licence kynexone.licence]
param([string]$Domain, [string]$Images, [string]$Licence)
. "$PSScriptRoot\_common.ps1"

Assert-Docker

if (Test-Path .env) {
    Say 'Keeping the existing .env'
} else {
    if (-not $Domain) { $Domain = Read-Host 'Address people will use to reach KynexOne (e.g. hr.example.local)' }
    if (-not $Domain) { Die 'An address is required.' }
    Say 'Writing .env with new secrets'
    Copy-Item .env.example .env
    Set-EnvValue KYNEXONE_DOMAIN $Domain
    Set-EnvValue POSTGRES_PASSWORD (New-Secret 40)
    Set-EnvValue JWT_AUDIENCE ("kynexone-" + (New-Secret 12).ToLowerInvariant())
    Set-EnvValue JWT_SIGNING_KEY (New-Secret 96)
    # Only the account running the installation (and administrators) may read the secrets.
    & icacls .env /inheritance:r /grant:r "$($env:USERNAME):(R,W)" /grant:r 'Administrators:(F)' | Out-Null
}

foreach ($key in 'POSTGRES_PASSWORD', 'JWT_AUDIENCE', 'JWT_SIGNING_KEY') {
    if ((Get-EnvValue $key) -eq 'GENERATED') { Die "$key in .env is still GENERATED. Delete .env and run install.ps1 again." }
}

if ($Images) { Say "Loading images from $Images"; & docker load -i $Images }
else { Say "Pulling images for version $(Get-EnvValue KYNEXONE_VERSION)"; Invoke-Compose pull }

Say 'Starting the database'
Invoke-Compose up -d --wait postgres

Say 'Creating the database schema'
Invoke-Api --migrate

Say 'Creating the organisation and its first administrator'
Invoke-Api --setup-organisation

if ($Licence) {
    if (-not (Test-Path $Licence)) { Die "Licence file not found: $Licence" }
    Say 'Importing the licence'
    $abs = (Resolve-Path $Licence).Path
    Invoke-Compose run --rm --no-deps -v "${abs}:/tmp/kynexone.licence:ro" api --import-licence /tmp/kynexone.licence
}

Say 'Starting KynexOne'
Invoke-Compose up -d
if (-not (Wait-Ready 60)) { Die 'KynexOne did not become ready. See: docker compose logs api' }

Write-Host ""
Write-Host "KynexOne is installed: https://$(Get-EnvValue KYNEXONE_DOMAIN)"
Write-Host "  - Sign in as the administrator you just created."
Write-Host "  - No licence yet? Upload it in Tenant Admin -> Licence."
Write-Host "  - Schedule scripts\backup.ps1 (Task Scheduler) and copy the backups off this server."
Write-Host "  - Keep .env safe: it holds the database password and the token signing key."
