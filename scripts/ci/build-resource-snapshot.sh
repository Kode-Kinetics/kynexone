#!/usr/bin/env bash
# Read-only, bounded host metrics. No credentials, process arguments, or file payloads.
# Diagnostic failure is non-fatal; the caller's real compiler/test exit code is untouched.
set -u
project=${1:-unknown}
phase=${2:-unknown}
printf '[ci-resource] phase=%s project=%s utc=%s\n' "$phase" "$project" "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
if [ -r /proc/meminfo ]; then
  awk '/^(MemTotal|MemAvailable|SwapTotal|SwapFree|Committed_AS):/ {print "[ci-resource] " $0}' /proc/meminfo
fi
if [ -r /proc/pressure/memory ]; then
  head -n 2 /proc/pressure/memory | sed 's/^/[ci-resource] memory-pressure /'
fi
# Account for both host and namespaced cgroup-v2 roots without printing cgroup paths.
cgroup=/sys/fs/cgroup
if [ -r /proc/self/cgroup ]; then
  relative=$(awk -F: '$1 == "0" {print $3; exit}' /proc/self/cgroup)
  if [ -n "${relative:-}" ] && [[ "$relative" != *'..'* ]] && [ -d "/sys/fs/cgroup$relative" ]; then
    cgroup="/sys/fs/cgroup$relative"
  fi
fi
for metric in memory.current memory.max memory.peak memory.events; do
  if [ -r "$cgroup/$metric" ]; then
    head -n 12 "$cgroup/$metric" | sed "s/^/[ci-resource] $metric /"
  fi
done
# Only numeric residency/elapsed time and the executable basename, never its arguments.
printf '[ci-resource] dotnet pid rss_kib elapsed_seconds executable (largest first)\n'
ps -C dotnet -o pid=,rss=,etimes=,comm= --sort=-rss 2>/dev/null | head -n 12 | sed 's/^/[ci-resource] /' || true
exit 0
