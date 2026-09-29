#!/usr/bin/env python3
"""
MIGRATION-TARGET GATE — prove that the database CI is about to migrate is the database the
web service actually connects to.

WHY THIS EXISTS
---------------
The production database URL is stored TWICE, maintained separately, by different people, in
different systems:

  * CI's `dotnet ef database update` reads `secrets.PROD_DATABASE_URL` — a repository-wide
    GitHub secret (.github/workflows/ci.yml).
  * The running app reads `ConnectionStrings__Default` — set only in the Render dashboard
    (render.yaml marks it `sync: false`, so the blueprint never supplies a value).

Until this script existed, nothing compared them. `scripts/check_render_env.py` verifies that
the key EXISTS; it never reads the value.

On 2026-09-23 that gap cost three consecutive production deploys and ~47 minutes of 502.
Production had just been cut over to a new database (`kynexone_clean`, with `neondb` kept as the
rollback). CI migrated one of them and printed "Done." — truthfully. The service connected to
the other, computed `pendingMigrations = 2`, and `/health/ready` returned 503 until Render timed
out and reverted. Every signal on the CI side was green. The failure was silent, deterministic,
and invisible from both ends.

A migration that reports success against the wrong database is worse than one that fails: it
leaves a green pipeline, a broken release, and no evidence anywhere.

WHAT IT COMPARES
----------------
Host and database name only. Never the password, and never the user.

The pooled and direct Neon endpoints are the SAME database — migrations must use the direct host
while normal traffic uses the pooled one (see the runbook), so `-pooler` is normalised away
before comparing. That difference is expected and must not fail the gate; a different database
NAME, or a different Neon project, must.

FAILS CLOSED
------------
A missing API key, an API error, an unparseable value or a missing variable all BLOCK the deploy.
A gate that switches itself off when its credential is missing is a gate that is off, and nobody
notices which.
"""
from __future__ import annotations

import argparse
import os
import re
import sys
import urllib.error
import urllib.request
from typing import NamedTuple

RENDER_API = "https://api.render.com/v1"


class Target(NamedTuple):
    host: str
    database: str

    def describe(self) -> str:
        """Safe for CI logs: host and database only — no user, no password."""
        return f"host={self.host} database={self.database}"


def normalise_host(host: str) -> str:
    """
    Neon's pooled endpoint (`<ep>-pooler.<region>.aws.neon.tech`) and its direct endpoint
    (`<ep>.<region>.aws.neon.tech`) address the SAME database. Migrations require the direct
    host; the app uses the pooled one. Treat them as equal.
    """
    host = host.strip().lower().rstrip(".")
    return re.sub(r"-pooler(?=\.|$)", "", host)


def parse_target(value: str, source: str) -> Target:
    """
    Accept both shapes this repo uses:
      ADO.NET  Host=h;Port=5432;Database=d;Username=u;Password=p;SSL Mode=Require;
      URL      postgresql://u:p@h:5432/d?sslmode=require
    """
    value = (value or "").strip()
    if not value:
        fail(f"{source} is empty — cannot prove the migration target. Deploy blocked (fail-closed).")

    if "://" in value:
        # Do not use urlsplit's netloc blindly: passwords routinely contain '@' and '/'.
        without_scheme = value.split("://", 1)[1]
        authority, _, path = without_scheme.partition("/")
        host_part = authority.rsplit("@", 1)[-1]          # strip user:password
        host = host_part.rsplit(":", 1)[0] if ":" in host_part and "]" not in host_part else host_part
        database = path.split("?", 1)[0]
    else:
        pairs = {}
        for chunk in value.split(";"):
            key, sep, val = chunk.partition("=")
            if sep:
                pairs[key.strip().lower()] = val.strip()
        host = pairs.get("host") or pairs.get("server") or ""
        # "Host=h:5432" is legal in some drivers; Port is usually its own key.
        if ":" in host:
            host = host.rsplit(":", 1)[0]
        database = pairs.get("database") or pairs.get("initial catalog") or ""

    if not host or not database:
        fail(
            f"{source} could not be parsed into a host and a database name. "
            "Deploy blocked (fail-closed). The value was NOT logged."
        )
    return Target(normalise_host(host), database.strip().lower())


def render_connection_string(service_id: str, api_key: str) -> str:
    req = urllib.request.Request(
        f"{RENDER_API}/services/{service_id}/env-vars?limit=100",
        headers={"Authorization": f"Bearer {api_key}", "Accept": "application/json"},
    )
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            import json

            payload = json.load(resp)
    except urllib.error.HTTPError as e:
        fail(f"Render API returned HTTP {e.code} for service {service_id}. Deploy blocked (fail-closed).")
    except Exception as e:  # noqa: BLE001 — any failure here must block, not skip
        fail(f"Could not reach the Render API ({type(e).__name__}). Deploy blocked (fail-closed).")

    for row in payload:
        var = row.get("envVar") or row
        if var.get("key") == "ConnectionStrings__Default":
            value = var.get("value")
            if not value:
                fail(
                    "Render returned ConnectionStrings__Default with no readable value (it may be a "
                    "secret file or the API key may lack permission). Deploy blocked (fail-closed)."
                )
            return value
    fail(
        f"Service {service_id} has no ConnectionStrings__Default env var. The app cannot be "
        "connecting to any database. Deploy blocked (fail-closed)."
    )


def fail(message: str) -> "NoReturn":  # type: ignore[valid-type]
    print(f"::error::check-migration-target: {message}", file=sys.stderr)
    sys.exit(1)


def self_test() -> int:
    """
    Prove the gate can FAIL. Every check here has been seen to fail at least once; a gate nobody
    has watched refuse is not known to work.
    """
    cases = [
        # (ci_value, render_value, expect_match, label)
        (
            "Host=ep-falling-frost-1.us-east-1.aws.neon.tech;Database=kynexone_clean;Username=u;Password=p;",
            "Host=ep-falling-frost-1-pooler.us-east-1.aws.neon.tech;Database=kynexone_clean;Username=u;Password=p;",
            True,
            "pooled vs direct endpoint, same database → MATCH (this is the expected normal state)",
        ),
        (
            "Host=ep-falling-frost-1.us-east-1.aws.neon.tech;Database=neondb;Username=u;Password=p;",
            "Host=ep-falling-frost-1-pooler.us-east-1.aws.neon.tech;Database=kynexone_clean;Username=u;Password=p;",
            False,
            "THE 2026-09-23 INCIDENT: CI migrates neondb, the app reads kynexone_clean → MISMATCH",
        ),
        (
            "postgresql://u:p%40ss@ep-a.us-east-1.aws.neon.tech:5432/kynexone_clean?sslmode=require",
            "Host=ep-a.us-east-1.aws.neon.tech;Database=kynexone_clean;Username=u;Password=p@ss;",
            True,
            "URL form vs ADO.NET form, same target, password containing '@' → MATCH",
        ),
        (
            "Host=ep-a.us-east-1.aws.neon.tech;Database=d;Username=u;Password=p;",
            "Host=ep-b.us-east-1.aws.neon.tech;Database=d;Username=u;Password=p;",
            False,
            "same database name on a DIFFERENT Neon endpoint → MISMATCH",
        ),
    ]
    failures = 0
    for ci_value, render_value, expect_match, label in cases:
        got = parse_target(ci_value, "self-test/ci") == parse_target(render_value, "self-test/render")
        ok = got == expect_match
        print(f"  {'PASS' if ok else 'FAIL'}  {label}")
        failures += 0 if ok else 1

    # Passwords must never reach a log, even on the failure path.
    t = parse_target("Host=h.example;Database=d;Username=u;Password=sup3rs3cret;", "self-test/redaction")
    if "sup3rs3cret" in t.describe() or "u" == t.describe().split("database=")[-1]:
        print("  FAIL  describe() leaked a credential")
        failures += 1
    else:
        print("  PASS  describe() emits host and database only — no user, no password")

    print("self-test: " + ("all checks passed" if failures == 0 else f"{failures} FAILED"))
    return 1 if failures else 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--service-id", help="Render service id whose ConnectionStrings__Default to read")
    ap.add_argument("--self-test", action="store_true", help="prove the gate can fail, then exit")
    args = ap.parse_args()

    if args.self_test:
        return self_test()

    if not args.service_id:
        fail("--service-id is required. Deploy blocked (fail-closed).")

    api_key = os.environ.get("RENDER_API_KEY", "").strip()
    if not api_key:
        fail(
            "RENDER_API_KEY is not set, so the migration target cannot be proved. Deploy blocked "
            "(fail-closed) — a gate that skips itself when its credential is missing is not a gate."
        )

    ci_target = parse_target(os.environ.get("PROD_DATABASE_URL", ""), "PROD_DATABASE_URL (what CI will migrate)")
    app_target = parse_target(
        render_connection_string(args.service_id, api_key),
        "ConnectionStrings__Default (what the service connects to)",
    )

    if ci_target != app_target:
        print(f"::error::check-migration-target: CI is about to migrate a DIFFERENT database "
              f"than the service connects to.\n"
              f"  CI  (PROD_DATABASE_URL)          {ci_target.describe()}\n"
              f"  APP (ConnectionStrings__Default) {app_target.describe()}\n"
              f"Migrating here would report success and leave the service on an un-migrated schema, "
              f"so /health/ready would return 503 until the deploy timed out. Deploy blocked.\n"
              f"Fix: point PROD_DATABASE_URL at the database the service uses — the DIRECT "
              f"(non-pooler) endpoint, since the pooler cannot run migrations.", file=sys.stderr)
        return 1

    print(f"check-migration-target: OK — CI and the service agree on {ci_target.describe()}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
