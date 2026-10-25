#!/usr/bin/env bash
# What is running, whether it is healthy, the version and the licence: the first thing to send to support.
source "$(dirname "$0")/_common.sh"
require_docker; require_env

say "Containers"
compose ps

say "Version"
probe /health/live - && echo || warn "The API is not answering."

say "Readiness"
if probe /health/ready; then echo "ready: yes"; else echo "ready: NO (database unreachable, migrations pending, or starting up)"; fi

say "Maintenance"
compose exec -T postgres psql -U kynexone -d kynexone -tAc \
  "SELECT key || ' = ' || value FROM platform_config_entries WHERE key IN ('maintenance_mode','maintenance_message')" 2>/dev/null \
  || warn "Could not read the maintenance state."

say "Licence"
compose exec -T backend dotnet Zayra.Api.dll --licence-status || warn "Could not read the licence status."

say "Disk"
df -h "$KIT_DIR" | tail -n1
docker system df
