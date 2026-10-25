#!/usr/bin/env python3
"""Dependency licence inventory (implementation prompt WP14.3).

    scripts/licence_report.py <report-dir>

Reads the inventories the CI job produced in <report-dir> — nuget.json (nuget-license) and npm-*.json
(license-checker-rseidelsohn) — writes <report-dir>/licences.md, a single table of every shipped
dependency and its licence, and exits non-zero when a dependency carries a strong-copyleft licence
(GPL or AGPL, any version) that scripts/licence-allowlist.txt has not approved. KynexOne is installed
on client servers: a GPL/AGPL component would put obligations on that distribution, so taking one on
is a decision someone signs, not something a dependency bump does quietly. LGPL and MPL are reported
but not refused.
"""
import json
import re
import sys
from pathlib import Path

STRONG_COPYLEFT = re.compile(r"\bA?GPL\b|GNU (AFFERO )?GENERAL PUBLIC", re.IGNORECASE)
WEAK_ONLY = re.compile(r"\bLGPL\b|LESSER GENERAL PUBLIC", re.IGNORECASE)


def allowlist(root: Path) -> set[str]:
    path = root / "scripts" / "licence-allowlist.txt"
    if not path.exists():
        return set()
    names = set()
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.split("#", 1)[0].strip()
        if line:
            names.add(line.lower())
    return names


def nuget_rows(path: Path) -> list[tuple[str, str, str, str]]:
    rows = []
    for entry in json.loads(path.read_text(encoding="utf-8")):
        licence = entry.get("License") or entry.get("LicenseExpression") or entry.get("LicenseUrl") or "UNKNOWN"
        rows.append(("nuget", entry.get("PackageId", "?"), entry.get("PackageVersion", "?"), str(licence)))
    return rows


def npm_rows(path: Path) -> list[tuple[str, str, str, str]]:
    rows = []
    app = path.stem.removeprefix("npm-")
    for key, entry in json.loads(path.read_text(encoding="utf-8")).items():
        name, _, version = key.rpartition("@")
        licences = entry.get("licenses", "UNKNOWN")
        if isinstance(licences, list):
            licences = " OR ".join(licences)
        rows.append((f"npm:{app}", name or key, version, str(licences)))
    return rows


def main() -> int:
    if len(sys.argv) != 2:
        print(__doc__)
        return 2
    report = Path(sys.argv[1])
    root = Path(__file__).resolve().parent.parent
    rows: list[tuple[str, str, str, str]] = []
    if (report / "nuget.json").exists():
        rows += nuget_rows(report / "nuget.json")
    for path in sorted(report.glob("npm-*.json")):
        rows += npm_rows(path)
    if not rows:
        print(f"No inventories found in {report}.", file=sys.stderr)
        return 2

    approved = allowlist(root)
    refused = [r for r in rows
               if STRONG_COPYLEFT.search(r[3]) and not WEAK_ONLY.search(r[3]) and r[1].lower() not in approved]
    # A dual licence that offers a permissive option ("MIT OR GPL-2.0") is not a copyleft obligation.
    refused = [r for r in refused if not re.search(r"\bOR\b", r[3]) or all(
        STRONG_COPYLEFT.search(part) for part in re.split(r"\bOR\b", r[3]))]

    lines = ["# Dependency licences", "", f"{len(rows)} dependencies.", "",
             "| Source | Package | Version | Licence |", "|---|---|---|---|"]
    lines += [f"| {s} | {n} | {v} | {l} |" for s, n, v, l in sorted(rows)]
    (report / "licences.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"Wrote {report / 'licences.md'} ({len(rows)} dependencies).")

    if refused:
        print("Strong-copyleft dependencies not approved in scripts/licence-allowlist.txt:", file=sys.stderr)
        for s, n, v, l in refused:
            print(f"  {s} {n}@{v}: {l}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
