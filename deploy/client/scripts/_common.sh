#!/usr/bin/env bash
# Shared by the KynexOne client scripts. Every script runs from the kit directory (the one holding
# docker-compose.yml and .env), whatever directory it was started from.
set -euo pipefail

KIT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$KIT_DIR"

say()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33mWARNING:\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31mERROR:\033[0m %s\n' "$*" >&2; exit 1; }

compose() { docker compose --env-file .env -f docker-compose.yml "$@"; }

require_docker() {
  command -v docker >/dev/null 2>&1 || die "Docker is not installed (https://docs.docker.com/engine/install/)."
  docker compose version >/dev/null 2>&1 || die "The Docker Compose plugin is not installed."
  docker info >/dev/null 2>&1 || die "Docker is not running, or this user may not use it (try sudo)."
}

require_env() { [ -f .env ] || die "No .env here ($KIT_DIR). Run scripts/install.sh first."; }

env_get() { grep -E "^$1=" .env | tail -n1 | cut -d= -f2-; }

env_set() {
  if grep -qE "^$1=" .env; then
    local tmp; tmp="$(mktemp)"
    awk -v k="$1" -v v="$2" 'BEGIN{FS=OFS="="} $1==k {print k"="v; next} {print}' .env > "$tmp" && cat "$tmp" > .env && rm -f "$tmp"
  else
    printf '%s=%s\n' "$1" "$2" >> .env
  fi
}

random_secret() { # $1 = length, alphanumeric so it is safe in a connection string and in .env
  LC_ALL=C tr -dc 'A-Za-z0-9' < /dev/urandom | head -c "$1"
}

# The API answers /health/ready only once it can serve: database reachable and every migration this image
# carries applied. Asked from inside the caddy container, so it works before DNS and certificates do.
wait_ready() {
  local tries="${1:-60}"
  say "Waiting for KynexOne to report ready"
  for _ in $(seq 1 "$tries"); do
    if compose exec -T caddy wget -q -O /dev/null http://api:5117/health/ready 2>/dev/null; then
      say "KynexOne is ready."; return 0
    fi
    sleep 5
  done
  return 1
}

api_command() { compose run --rm --no-deps api "$@"; }
