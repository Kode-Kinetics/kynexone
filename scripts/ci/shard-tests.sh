#!/usr/bin/env bash
# A discovery error or an empty shard is a failed populated CI lane, never an unmatched filter.
set -euo pipefail
exec python3 "$(dirname "$0")/shard-tests.py" "$@"
