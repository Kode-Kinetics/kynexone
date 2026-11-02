#!/usr/bin/env python3
"""Balance integration test classes deterministically; fail closed on discovery changes."""
import re
import subprocess
import sys


def shard_filter(listing, shard, total):
    marker = 'The following Tests are available:'
    lines = listing.splitlines()
    if lines.count(marker) != 1:
        raise ValueError('expected exactly one recognized VSTest discovery header')
    counts = {}
    for line in lines[lines.index(marker) + 1:]:
        if not line.strip():
            continue
        # Theory arguments are display-only. Do not accept warning/error text as test names.
        name = line.strip().split('(', 1)[0]
        if not line.startswith('    ') or not re.fullmatch(r'(?:[A-Za-z_]\w*\.){2,}[A-Za-z_]\w*', name):
            raise ValueError('unrecognized entry in VSTest discovery output')
        cls = name.rsplit('.', 1)[0]
        counts[cls] = counts.get(cls, 0) + 1
    if len(counts) < total:
        raise ValueError('integration discovery has too few classes to populate every shard')
    # VSTest '~' is substring matching: reject namespace/class suffix collisions.
    if any(cls != other and (cls + '.').casefold() in (other + '.').casefold() for cls in counts for other in counts):
        raise ValueError('ambiguous class substrings would overlap integration shards')
    loads = [0] * total
    selected = []
    for cls in sorted(counts, key=lambda cls: (-counts[cls], cls)):
        owner = min(range(total), key=lambda index: loads[index])
        loads[owner] += counts[cls]
        if owner == shard - 1:
            selected.append('FullyQualifiedName~' + cls + '.')
    if not selected:
        raise ValueError('requested integration shard has no test classes')
    return '|'.join(selected)


def main():
    if len(sys.argv) != 4 or not all(re.fullmatch(r'[1-9][0-9]*', n) for n in sys.argv[2:]):
        raise ValueError('usage: shard-tests.sh <test.csproj> <shard 1..N> <N> (positive integers)')
    project, shard, total = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
    if shard > total or total > 1024:
        raise ValueError('shard must be within 1..N; N must not exceed 1024')
    result = subprocess.run(['dotnet', 'test', project, '--no-build', '--no-restore', '-c', 'Release',
                             '--filter', 'Category=Integration', '--list-tests'],
                            capture_output=True, text=True, check=False)
    if result.returncode:
        raise ValueError('dotnet integration discovery failed (exit %d)' % result.returncode)
    print(shard_filter(result.stdout, shard, total))


if __name__ == '__main__':
    try:
        main()
    except (ValueError, OSError) as error:
        print('::error::' + str(error), file=sys.stderr)
        sys.exit(1)
