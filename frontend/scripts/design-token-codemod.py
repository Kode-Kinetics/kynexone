#!/usr/bin/env python3
"""One-shot, mechanical migration of off-scale styling onto the design tokens.
Usage: python3 scripts/design-token-codemod.py <file> [<file> ...]
Only touches Tailwind class tokens; never rewrites logic. Re-run safely (idempotent)."""
import re, sys

SIZE = {'9': 'caption', '10': 'caption', '10.5': 'caption', '11': 'caption', '11.5': 'meta', '12': 'meta', '12.5': 'meta',
        '13': 'body-sm', '13.5': 'body-sm', '14': 'body', '15': 'body', '16': 'h4', '18': 'h3'}

def radius(m):
    side, px = m.group(1) or '', float(m.group(2))
    if px <= 5: r = ''
    elif px <= 10: r = '-lg'
    elif px <= 13: r = '-xl'
    else: r = '-2xl'
    return f'rounded{side}{r}'

RULES = [
    (re.compile(r'\btext-\[(\d+(?:\.\d+)?)px\]'), lambda m: f"text-{SIZE[m.group(1)]}" if m.group(1) in SIZE else m.group(0)),
    (re.compile(r'(?<=-)gray-(?=\d)'), lambda m: 'slate-'),
    (re.compile(r'\bfont-(?:bold|extrabold|black)\b'), lambda m: 'font-semibold'),
    (re.compile(r'\bfont-(?:thin|extralight|light)\b'), lambda m: 'font-normal'),
    (re.compile(r'\brounded(-[tbselr]{1,2})?-\[(\d+(?:\.\d+)?)px\]'), radius),
    (re.compile(r'\brounded(-[tbselr]{1,2})?-md\b'), lambda m: f'rounded{m.group(1) or ""}-lg'),
    (re.compile(r'\brounded(-[tbselr]{1,2})?-sm\b'), lambda m: f'rounded{m.group(1) or ""}'),
    (re.compile(r'\brounded(-[tbselr]{1,2})?-3xl\b'), lambda m: f'rounded{m.group(1) or ""}-2xl'),
    (re.compile(r'\bshadow-2xl\b'), lambda m: 'shadow-e3'),
    (re.compile(r'\s?\b(?:dark:)?shadow-(?:sapphire|blue|indigo|violet|purple|emerald|rose|red|amber|cyan|midnight)(?:-\d{2,3})?/\d+'), lambda m: ''),
    # dark-surface and brand-text literals
    (re.compile(r'dark:(group-hover:)?text-\[#7AABFF\]', re.I), lambda m: f'dark:{m.group(1) or ""}text-primary-fg'),
    (re.compile(r'dark:bg-\[#0(?:c1120|d1221|d1225)\]', re.I), lambda m: 'dark:bg-surface'),
    (re.compile(r'dark:ring-\[#0D1221\]', re.I), lambda m: 'dark:ring-surface'),
]

for path in sys.argv[1:]:
    src = open(path).read()
    out = src
    for rx, fn in RULES:
        out = rx.sub(fn, out)
    if out != src:
        open(path, 'w').write(out)
        print('migrated', path)
