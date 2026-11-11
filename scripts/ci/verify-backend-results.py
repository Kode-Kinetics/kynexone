#!/usr/bin/env python3
"""The required gate independently verifies every expected lane's TRX for this commit and run.

Result artifacts are shared across attempts of a run (a re-run lane overwrites its own); the workflow gates on every
lane job's CURRENT result as well, so an artifact left by an earlier attempt belongs to a lane that passed there."""
import os
from pathlib import Path
import re
import subprocess
import sys


def main():
    if len(sys.argv) != 2:
        raise ValueError('usage: verify-backend-results.py <downloaded-artifacts-directory>')
    sha, run, attempt = (os.environ.get(key, '') for key in ('GITHUB_SHA', 'GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT'))
    if not re.fullmatch(r'[0-9a-f]{40}', sha) or any(not re.fullmatch(r'[1-9][0-9]*', value) for value in (run, attempt)):
        raise ValueError('missing exact workflow identity')
    root = Path(sys.argv[1])
    expected = {'backend-results-%s-%s-%s' % (lane, sha, run): lane
                for lane in ('unit', 'shard-1', 'shard-2', 'shard-3', 'shard-4')}
    if not root.is_dir() or {path.name for path in root.iterdir()} != expected.keys():
        raise ValueError('required unit and four shard artifacts are absent or unexpected')
    for folder, lane in expected.items():
        subprocess.run([sys.executable, str(Path(__file__).with_name('verify-test-results.py')),
                        str(root / folder / (lane + '.trx'))], check=True)
    print('Verified unit and all four integration shards for this checkout/run')


if __name__ == '__main__':
    try:
        main()
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        print('::error::' + str(error), file=sys.stderr)
        sys.exit(1)
