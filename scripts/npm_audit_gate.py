#!/usr/bin/env python3
"""GATE: fail on any High/Critical npm advisory, except a named, dated, justified exception.

WHY THIS EXISTS
---------------
`npm audit --audit-level=high` has no way to accept one advisory. In September 2026 two High
advisories landed against mobile's Expo build tooling with NO patched release anywhere:

    GHSA-vfj7-8cjw-p6xm  braces <= 3.0.3      (3.0.3 is the latest published version)
    GHSA-86w9-cpqp-85rv  node-forge <= 1.4.0  (1.4.0 is the latest published version)

No upgrade or `overrides` pin can clear them, and `npm audit fix --force` "fixes" them by
downgrading Expo to v44. The plain command therefore failed every PR in the repo, whatever it
touched. The two choices were to lower the threshold (blind to every future High) or accept
these two by ID. This gate does the second, and nothing broader:

  * an exception matches one GHSA id AND one package name, never a package or a severity;
  * every exception carries an `expires` date, after which it stops applying and the gate fails;
  * every applied exception is printed as a ::warning:: on every run, so it stays visible;
  * an exception that no longer matches anything is reported, so the list cannot quietly rot;
  * unreadable or error-shaped audit output fails the gate rather than passing it.

USAGE
    scripts/npm_audit_gate.py --dir mobile --exceptions .github/npm-audit-exceptions/mobile.json
    scripts/npm_audit_gate.py --self-test
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import subprocess
import sys

BLOCKING = {"high", "critical"}


def ghsa_of(url: str) -> str:
    return url.rstrip("/").rsplit("/", 1)[-1]


def advisories(audit: dict) -> list[dict]:
    """Root advisories from `npm audit --json` (v7+). Chained entries (`via` is a string) are
    the same advisory seen from a dependent, so only dict entries are counted."""
    if not isinstance(audit, dict) or "error" in audit or "vulnerabilities" not in audit:
        raise ValueError(f"unrecognised npm audit output: {json.dumps(audit)[:400]}")
    seen: dict[tuple[str, str], dict] = {}
    for vuln in audit["vulnerabilities"].values():
        for via in vuln.get("via", []):
            if isinstance(via, dict) and via.get("url"):
                key = (ghsa_of(via["url"]), via["name"])
                seen[key] = {
                    "ghsa": key[0],
                    "package": key[1],
                    "severity": via.get("severity", "unknown"),
                    "title": via.get("title", ""),
                    "range": via.get("range", ""),
                }
    return sorted(seen.values(), key=lambda a: (a["package"], a["ghsa"]))


def evaluate(audit: dict, exceptions: list[dict], today: dt.date) -> tuple[list[str], list[str]]:
    """Returns (errors, warnings). Any error fails the gate."""
    errors: list[str] = []
    warnings: list[str] = []
    active: dict[tuple[str, str], dict] = {}
    for ex in exceptions:
        for field in ("ghsa", "package", "expires", "reason"):
            if not ex.get(field):
                errors.append(f"exception {ex!r} is missing '{field}'")
        if any(not ex.get(f) for f in ("ghsa", "package", "expires", "reason")):
            continue
        expires = dt.date.fromisoformat(ex["expires"])
        if expires < today:
            errors.append(
                f"exception for {ex['ghsa']} ({ex['package']}) expired on {ex['expires']}; "
                "re-check upstream for a patched release, then fix or renew it deliberately"
            )
            continue
        active[(ex["ghsa"], ex["package"])] = ex

    used: set[tuple[str, str]] = set()
    for adv in advisories(audit):
        if adv["severity"] not in BLOCKING:
            continue
        key = (adv["ghsa"], adv["package"])
        if key in active:
            used.add(key)
            warnings.append(
                f"ACCEPTED {adv['severity']} {adv['ghsa']} {adv['package']} {adv['range']} "
                f"until {active[key]['expires']}: {active[key]['reason']}"
            )
        else:
            errors.append(
                f"{adv['severity']} {adv['ghsa']} {adv['package']} {adv['range']} — {adv['title']}"
            )
    for key in active.keys() - used:
        warnings.append(
            f"exception for {key[0]} ({key[1]}) no longer matches any finding; remove it"
        )
    return errors, warnings


def run_audit(directory: str) -> dict:
    # npm audit exits 1 whenever it finds anything, so the exit code is not the signal; the
    # parsed JSON is. A failure to produce JSON at all is surfaced, not treated as clean.
    proc = subprocess.run(
        ["npm", "audit", "--omit=dev", "--json"],
        cwd=directory,
        capture_output=True,
        text=True,
    )
    try:
        return json.loads(proc.stdout)
    except json.JSONDecodeError:
        raise ValueError(f"npm audit produced no JSON (exit {proc.returncode}): {proc.stderr[:400]}")


def self_test() -> int:
    today = dt.date(2026, 10, 4)
    # The real shape `npm audit --json` emitted for mobile on 2026-10-04, trimmed.
    audit = {
        "auditReportVersion": 2,
        "vulnerabilities": {
            "braces": {"via": [{"name": "braces", "severity": "high", "range": "<=3.0.3",
                                "title": "stack exhaustion",
                                "url": "https://github.com/advisories/GHSA-vfj7-8cjw-p6xm"}]},
            "micromatch": {"via": ["braces"]},
            "node-forge": {"via": [{"name": "node-forge", "severity": "high", "range": "<=1.4.0",
                                    "title": "PKCS#1 v1.5",
                                    "url": "https://github.com/advisories/GHSA-86w9-cpqp-85rv"}]},
            "uuid": {"via": [{"name": "uuid", "severity": "moderate", "range": "<11.1.1",
                              "title": "bounds", "url": "https://github.com/advisories/GHSA-w5hq-g745-h8pq"}]},
        },
    }
    both = [
        {"ghsa": "GHSA-vfj7-8cjw-p6xm", "package": "braces", "expires": "2026-11-03", "reason": "r"},
        {"ghsa": "GHSA-86w9-cpqp-85rv", "package": "node-forge", "expires": "2026-11-03", "reason": "r"},
    ]
    cases = [
        ("no exceptions: both Highs fail", [], 2),
        ("both accepted: passes, moderate ignored", both, 0),
        ("one accepted: the other still fails", both[:1], 1),
        ("expired exception fails", [dict(both[0], expires="2026-10-01"), both[1]], 2),
        ("right GHSA, wrong package does not match", [dict(both[0], package="micromatch"), both[1]], 1),
        ("missing reason is rejected", [dict(both[0], reason=""), both[1]], 2),
    ]
    failed = 0
    for name, exc, want in cases:
        errors, _ = evaluate(audit, exc, today)
        ok = len(errors) == want
        failed += not ok
        print(f"{'ok  ' if ok else 'FAIL'} {name} (errors={len(errors)}, want {want})")
    _, warns = evaluate({"vulnerabilities": {}}, both, today)
    stale_ok = sum("no longer matches" in w for w in warns) == 2
    failed += not stale_ok
    print(f"{'ok  ' if stale_ok else 'FAIL'} unused exceptions are reported")
    for bad in ({"error": {"code": "ENOLOCK"}}, {}, []):
        try:
            evaluate(bad, both, today)
            failed += 1
            print(f"FAIL malformed output {bad!r} was accepted")
        except ValueError:
            print(f"ok   malformed output {bad!r} is refused")
    return 1 if failed else 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--dir")
    parser.add_argument("--exceptions")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        return self_test()
    if not args.dir:
        parser.error("--dir is required")

    exceptions = []
    if args.exceptions:
        with open(args.exceptions) as f:
            exceptions = json.load(f)["exceptions"]
    try:
        errors, warnings = evaluate(run_audit(args.dir), exceptions, dt.date.today())
    except ValueError as e:
        print(f"::error::{e}")
        return 1
    for w in warnings:
        print(f"::warning::{args.dir}: {w}")
    for e in errors:
        print(f"::error::{args.dir}: {e}")
    if errors:
        return 1
    print(f"{args.dir}: no unaccepted High/Critical advisories.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
