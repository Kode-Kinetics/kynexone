# What is running, whether it is healthy, the version and the licence (Windows Server): the first thing to send to
# support.
. "$PSScriptRoot\_common.ps1"
Assert-Docker; Assert-Env
$ErrorActionPreference = 'Continue'
$compose = @('compose', '--env-file', '.env', '-f', 'docker-compose.yml')

Say 'Containers'
& docker @compose ps

Say 'Version'
& docker @compose exec -T proxy wget -q -O - http://backend:5117/health/live
Write-Host ""

Say 'Readiness'
if (Test-Compose exec -T proxy wget -q -O /dev/null http://backend:5117/health/ready) { Write-Host 'ready: yes' }
else { Write-Host 'ready: NO (database unreachable, migrations pending, or starting up)' }

Say 'Maintenance'
& docker @compose exec -T postgres psql -U kynexone -d kynexone -tAc "SELECT key || ' = ' || value FROM platform_config_entries WHERE key IN ('maintenance_mode','maintenance_message')"

Say 'Licence'
& docker @compose exec -T backend dotnet Zayra.Api.dll --licence-status

Say 'Disk'
Get-PSDrive -Name (Split-Path -Qualifier $KitDir).TrimEnd(':') | Format-Table Name, Used, Free
& docker system df
