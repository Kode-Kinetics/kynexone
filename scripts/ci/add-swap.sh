#!/usr/bin/env bash
# Adds swap on the runner's scratch disk before a backend compile.
#
# WHY: compiling Zayra.Api needs more than 11 GB of live compiler heap (EF migration designers are ~93% of its
# 4M lines of C#), and a hosted runner has 16 GB and 3 GB of swap. A run that peaked at 16.2 GB was shut down by the
# runner ("received a shutdown signal"). Extra swap makes a spike slow instead of fatal.
# Non-fatal by design: if swap cannot be added the build still runs exactly as before.
set -u
size_gb="${1:-12}"
file=/mnt/ci-swapfile
if [ "$(uname -s)" != "Linux" ] || ! command -v sudo >/dev/null 2>&1; then echo "[ci-swap] not a Linux runner, skipped"; exit 0; fi
avail_kb=$(df --output=avail /mnt 2>/dev/null | tail -n 1 | tr -d ' ')
need_kb=$(( (size_gb + 1) * 1024 * 1024 ))
if [ -z "${avail_kb:-}" ] || [ "$avail_kb" -lt "$need_kb" ]; then echo "[ci-swap] /mnt has too little space, skipped"; exit 0; fi
sudo fallocate -l "${size_gb}G" "$file" && sudo chmod 600 "$file" && sudo mkswap "$file" >/dev/null && sudo swapon "$file" \
  && echo "[ci-swap] added ${size_gb}G swap" || echo "[ci-swap] could not add swap, continuing without"
free -m | sed 's/^/[ci-swap] /'
exit 0
