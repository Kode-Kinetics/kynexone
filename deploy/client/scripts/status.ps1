# What is running, whether it is healthy, and the licence (Windows): the first thing to send to support.
. "$PSScriptRoot\_common.ps1"
Assert-Docker; Assert-Env
$ErrorActionPreference = 'Continue'

Say 'Containers'
& docker compose --env-file .env -f docker-compose.yml ps

Say 'Version and health'
& docker compose --env-file .env -f docker-compose.yml exec -T caddy wget -q -O - http://api:5117/health/live
Write-Host ""
& docker compose --env-file .env -f docker-compose.yml exec -T caddy wget -q -O /dev/null http://api:5117/health/ready *> $null
if ($LASTEXITCODE -eq 0) { Write-Host 'ready: yes' } else { Write-Host 'ready: NO (database unreachable, migrations pending, or starting up)' }

Say 'Licence'
& docker compose --env-file .env -f docker-compose.yml exec -T api dotnet Zayra.Api.dll --licence-status

Say 'Disk'
Get-PSDrive -Name (Split-Path -Qualifier $KitDir).TrimEnd(':') | Format-Table Name, Used, Free
& docker system df
