#!/usr/bin/env bash
# Print a `dotnet test --filter` fragment selecting shard K of N of the Integration test CLASSES.
#   scripts/ci/shard-tests.sh <test-csproj> <shard 1..N> <N>
# Needs a built project (run `dotnet build -c Release` first). Classes are assigned greedily by test
# count (largest first, to the lightest shard) so shards are balanced and a class never straddles two.
set -euo pipefail
proj="${1:?csproj}"; shard="${2:?shard}"; total="${3:?total}"

dotnet test "$proj" --no-build -c Release --filter "Category=Integration" --list-tests 2>/dev/null \
| awk -v shard="$shard" -v total="$total" '
  /^    / {
    name=$0; sub(/^ +/, "", name); sub(/\(.*/, "", name)      # drop theory arguments
    n=split(name, p, "."); cls=p[1]; for (i=2;i<n;i++) cls=cls "." p[i]  # drop the method segment
    count[cls]++
  }
  END {
    # order classes by descending size, then name (deterministic)
    m=0; for (c in count) keys[++m]=c
    for (i=2;i<=m;i++) { k=keys[i]; j=i-1
      while (j>0 && (count[keys[j]]<count[k] || (count[keys[j]]==count[k] && keys[j]>k))) { keys[j+1]=keys[j]; j-- }
      keys[j+1]=k }
    for (s=1;s<=total;s++) load[s]=0
    for (i=1;i<=m;i++) { best=1; for (s=2;s<=total;s++) if (load[s]<load[best]) best=s
      load[best]+=count[keys[i]]; owner[keys[i]]=best }
    out=""; for (i=1;i<=m;i++) if (owner[keys[i]]==shard) out=out (out==""?"":"|") "FullyQualifiedName~" keys[i] "."
    if (out=="") { print "FullyQualifiedName=__no_tests_in_this_shard__"; exit }
    print out
  }'
