# Upgrade KynexOne (Windows Server). Same steps as upgrade.sh.
#
#   .\scripts\upgrade.ps1 kynexone-1.2.0.tar.gz      an offline bundle
#   .\scripts\upgrade.ps1 1.2.0                      pull that version's images
param([Parameter(Mandatory = $true)][string]$Target)
. "$PSScriptRoot\_common.ps1"

Assert-Docker; Assert-Env
$current = Get-EnvValue KYNEXONE_VERSION

# 1. The new release
$bundle = $null
if (Test-Path $Target -PathType Leaf) {
    $work = Join-Path ([IO.Path]::GetTempPath()) ("kynexone-upgrade-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $work | Out-Null
    Say "Unpacking $(Split-Path -Leaf $Target)"
    & tar xzf $Target -C $work
    if ($LASTEXITCODE -ne 0) { Die 'Could not unpack the bundle.' }
    $bundle = (Get-ChildItem $work -Directory | Select-Object -First 1).FullName
    if (-not (Test-Path (Join-Path $bundle 'VERSION'))) { Die 'Not a KynexOne bundle (no VERSION).' }
    if (-not (Test-Bundle $bundle)) { Die "The bundle's checksums do not match. Download it again." }
    Say 'Bundle checksums verified'
    $new = (Get-Content (Join-Path $bundle 'VERSION') -Raw).Trim()
} else {
    $new = $Target
}
if ($new -notmatch '^\d+\.\d+\.\d+$') { Die "'$new' is not a MAJOR.MINOR.PATCH version." }
if ($current -eq $new) { Die "Already on $new." }
if ([version]$new -lt [version]$current) { Die "$new is older than $current. Going back is a restore (scripts\restore.ps1), not an upgrade." }

# 2. Support period
$status = & docker compose --env-file .env -f docker-compose.yml run --rm --no-deps -T backend --licence-status 2>$null
$support = (($status | Where-Object { $_ -match '^Support until' }) -replace '^Support until\s*:\s*', '').Trim()
if ($support -match '^\d{4}-\d{2}-\d{2}$' -and [datetime]$support -lt (Get-Date).ToUniversalTime().Date) {
    Die "The licence's support period ended on $support. Renew support with your KynexOne supplier to upgrade."
}

Say "Upgrading KynexOne $current -> $new"

# 3. Backup
& "$PSScriptRoot\backup.ps1"
if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne $null) { Die 'The backup failed; nothing was changed.' }
$backup = (Get-Content (Join-Path $KitDir 'backups\LATEST') -Raw).Trim()

function Show-Rollback {
    Warn "The upgrade to $new did not finish. KynexOne is in maintenance mode on the upgraded database."
    Warn "Roll back: 1. set KYNEXONE_VERSION=$current in .env; docker compose up -d backend web;"
    Warn '              docker compose run --rm --no-deps backend --maintenance off'
    Warn "           2. if $current does not start: docker compose stop backend web; .\scripts\restore.ps1 $backup -Yes"
}

# 4. Maintenance on
Say 'Switching maintenance mode on'
Invoke-Api --maintenance on --message "KynexOne is being upgraded to $new. Please try again in a few minutes."

try {
    # 5. Images and kit
    if ($bundle) {
        Say 'Loading images'
        & docker load -i (Join-Path $bundle 'images\kynexone-images.tar')
        if ($LASTEXITCODE -ne 0) { throw 'docker load failed.' }
        Say 'Updating the kit files (your .env, certs and backups are kept)'
        foreach ($f in 'docker-compose.yml', 'Caddyfile', '.env.example', 'README.md') { Copy-Item (Join-Path $bundle "deploy\client\$f") (Join-Path $KitDir $f) -Force }
        Copy-Item (Join-Path $bundle 'deploy\client\scripts\*') (Join-Path $KitDir 'scripts') -Force
    }
    Set-EnvValue KYNEXONE_VERSION $new
    if (-not $bundle) { Invoke-Compose pull migrate backend web }

    # 6. Migrations
    Say "Applying the $new database changes"
    Invoke-Compose up migrate --exit-code-from migrate

    # 7-9. Start, wait, check
    Say "Starting $new"
    Invoke-Compose up -d
    if (-not (Wait-Ready 60)) { throw "$new did not become ready." }
    if (-not (Invoke-SmokeCheck)) { throw 'The smoke checks failed.' }
} catch {
    Warn "$_"
    Show-Rollback
    exit 1
}

# 10. Maintenance off
Invoke-Api --maintenance off
Say "KynexOne is now on $new. Pre-upgrade backup: $backup"
