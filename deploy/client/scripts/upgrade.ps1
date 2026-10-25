# Upgrade KynexOne to a new version (Windows). Same steps as upgrade.sh.
#
#   .\scripts\upgrade.ps1 1.2.0 [-Images kynexone-1.2.0.tar]
param([Parameter(Mandatory = $true)][string]$Version, [string]$Images)
. "$PSScriptRoot\_common.ps1"

if ($Version -notmatch '^\d+\.\d+\.\d+$') { Die 'Usage: .\scripts\upgrade.ps1 <MAJOR.MINOR.PATCH> [-Images file.tar]' }
Assert-Docker; Assert-Env

$current = Get-EnvValue KYNEXONE_VERSION
if ($current -eq $Version) { Die "Already on $Version." }
if ([version]$Version -lt [version]$current) {
    Die "$Version is older than $current. Going back is a restore (scripts\restore.ps1 <backup>), not an upgrade."
}

Say "Upgrading KynexOne $current -> $Version"
& "$PSScriptRoot\backup.ps1"
$backup = (Get-Content (Join-Path $KitDir 'backups\LATEST') -Raw).Trim()

try {
    Say 'Switching maintenance mode on'
    Invoke-Api --maintenance on --message "KynexOne is being upgraded to $Version. Please try again in a few minutes."

    if ($Images) { & docker load -i $Images; if ($LASTEXITCODE -ne 0) { throw 'docker load failed.' } }
    Set-EnvValue KYNEXONE_VERSION $Version
    if (-not $Images) { Invoke-Compose pull api web }

    Say "Applying the $Version database changes"
    Invoke-Api --migrate

    Say "Starting $Version"
    Invoke-Compose up -d
    if (-not (Wait-Ready 60)) { throw "$Version did not become ready." }

    Invoke-Api --maintenance off
} catch {
    Warn "The upgrade did not finish: $_"
    Warn 'KynexOne is still in maintenance mode. To return to the previous version with the data as it was:'
    Warn "  .\scripts\restore.ps1 $backup -Yes"
    exit 1
}
Say "KynexOne is now on $Version. Rollback point: $backup"
