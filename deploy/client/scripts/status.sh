#!/usr/bin/env bash
# What is running, whether it is healthy, and the licence: the first thing to send to support.
source "$(dirname "$0")/_common.sh"
require_docker; require_env

say "Containers"
compose ps

say "Version and health"
compose exec -T caddy wget -q -O - http://api:5117/health/live 2>/dev/null && echo || warn "The API is not answering."
if compose exec -T caddy wget -q -O /dev/null http://api:5117/health/ready 2>/dev/null; then
  echo "ready: yes"
else
  echo "ready: NO (database unreachable, migrations pending, or starting up)"
fi

say "Licence"
compose exec -T api dotnet Zayra.Api.dll --licence-status || warn "Could not read the licence status."

say "Disk"
df -h "$KIT_DIR" | tail -n1
docker system df
