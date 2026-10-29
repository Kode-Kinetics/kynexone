#!/usr/bin/env python3
"""Reject missing, unsuccessful or zero-executed VSTest TRX results for populated CI lanes."""
from pathlib import Path
import sys
import xml.etree.ElementTree as ET


def verify(path):
    root = ET.parse(path).getroot()
    ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    summary = root.find('t:ResultSummary', ns)
    counters = root.find('t:ResultSummary/t:Counters', ns)
    results = root.findall('t:Results/t:UnitTestResult', ns)
    if summary is None or counters is None or summary.get('outcome') not in ('Completed', 'Passed'):
        raise ValueError('missing or unsuccessful TRX summary')
    def count(name, required=False):
        text = counters.get(name, None if required else '0')
        if text is None or not text.isascii() or not text.isdecimal():
            raise ValueError('invalid TRX counter: ' + name)
        return int(text)
    executed, passed, total = count('executed', True), count('passed', True), count('total', True)
    if executed < 1 or executed != passed or total < executed:
        raise ValueError('populated lane must execute and pass at least one test')
    # VSTest 17.14.1 emits notExecuted=0 even when skipped result entries exist.
    # Entries/total/executed below remain authoritative; reject other contradictory values.
    if count('notExecuted') not in (0, total - executed):
        raise ValueError('TRX skipped counter disagrees with total/executed counts')
    if any(count(key) for key in ('failed', 'error', 'timeout', 'aborted', 'notRunnable', 'disconnected')):
        raise ValueError('unsuccessful tests in TRX counters')
    if len(results) != total or sum(result.get('outcome') == 'Passed' for result in results) != executed:
        raise ValueError('TRX result entries disagree with execution counters')
    if any(result.get('outcome') not in ('Passed', 'NotExecuted') for result in results):
        raise ValueError('unsuccessful TRX result entry')
    return executed


if __name__ == '__main__':
    try:
        if len(sys.argv) != 2:
            raise ValueError('usage: verify-test-results.py <expected.trx>')
        print('Verified %d executed passing tests' % verify(Path(sys.argv[1])))
    except (ValueError, OSError, ET.ParseError) as error:
        print('::error::' + str(error), file=sys.stderr)
        sys.exit(1)
