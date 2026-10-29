#!/usr/bin/env python3
"""Run a compiler with bounded periodic numeric diagnostics; preserve its exit status."""
from pathlib import Path
import signal
import os
import subprocess
import sys

SNAPSHOT = Path(__file__).with_name('build-resource-snapshot.sh')


def snapshot(phase):
    try:
        subprocess.run(['bash', str(SNAPSHOT), 'build', phase], timeout=10, check=False)
        subprocess.run(['df', '-Pk', '.'], timeout=10, check=False)
    except (OSError, subprocess.TimeoutExpired):
        print('[ci-resource] diagnostic unavailable', flush=True)


def main():
    if len(sys.argv) < 2:
        return 2
    snapshot('before')
    child = subprocess.Popen(sys.argv[1:], start_new_session=True)
    interrupted_signal = None
    def forward(signum, _frame):
        nonlocal interrupted_signal
        if interrupted_signal is None:
            interrupted_signal = signum
        try:
            os.killpg(child.pid, signum)
        except ProcessLookupError:
            pass
    signal.signal(signal.SIGTERM, forward)
    signal.signal(signal.SIGINT, forward)
    samples = 0
    while True:
        try:
            code = child.wait(timeout=30)
            break
        except subprocess.TimeoutExpired:
            if interrupted_signal is not None:
                try:
                    os.killpg(child.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
            if samples < 120:
                snapshot('during')
                samples += 1
    if interrupted_signal is not None:
        # MSBuild can exit on TERM while a GC-bound compiler child remains alive.
        try:
            os.killpg(child.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
    snapshot('after')
    if interrupted_signal is not None:
        return 128 + interrupted_signal
    return code if code >= 0 else 128 - code


if __name__ == '__main__':
    sys.exit(main())
