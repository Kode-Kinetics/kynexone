#!/usr/bin/env python3
"""GATE: every `sync: false` key in render.yaml must actually exist on the Render service.

WHY THIS EXISTS
---------------
`sync: false` in a Render blueprint means "this key is deliberately not in the file; set it in
the dashboard". It is a note to a human. Render does not enforce it, no CI job checked it, and
nothing anywhere compared the blueprint's list of required keys against the service's real
environment. The blueprint could name fourteen required keys, the service could have thirteen,
and every tool in the pipeline would report success.

That is what happened. `Proxy__KnownNetworks` was declared `sync: false` and was never set.
Because `Proxy__TrustForwardedHeaders` was "true", the app's own fail-closed proxy guard threw at
startup — so the Render pre-deploy job `dotnet Zayra.Api.dll --migrate` died ~10s in, twice,
before reaching the database. The backend could neither ship nor roll back. The missing key was a
single dashboard field, and the blueprint had said it was required all along.

`Proxy__KnownNetworks` was not special. It was one of fourteen keys with exactly the same
non-guarantee behind them. This gate turns the comment into a check.

USAGE
    scripts/check_render_env.py --self-test
        Runs the unit tests below, including the exact historical input. No network, no secrets.

    scripts/check_render_env.py --service-id srv-xxxx
        Compares render.yaml against the live service. Needs RENDER_API_KEY in the environment.

    scripts/check_render_env.py --present-from FILE
        Compares render.yaml against a newline-separated list of key names (offline/manual
        checklist mode).

EXIT CODES
    0  every required key is present
    1  one or more required keys are missing (they are named)
    2  the gate could not run (bad args, no credential, API error) -- never a silent pass
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import urllib.error
import urllib.request

RENDER_API = "https://api.render.com/v1"


# ──────────────────────────────────────────────────────────────────────────────
# Parsing
# ──────────────────────────────────────────────────────────────────────────────
def parse_required_keys(render_yaml: str) -> list[str]:
    """Return the keys declared `sync: false` in a Render blueprint, in file order.

    Deliberately a small line scanner rather than a YAML parse: this must run on a bare CI
    runner with no pip install, and PyYAML is not in the toolchain. The shape it matches is the
    only shape Render accepts for an env var entry:

        - key: SOME_NAME
          sync: false

    A `key:` with a `value:` is supplied by the blueprint itself and is NOT required in the
    dashboard, so it is not returned. Comments and blank lines between the two may appear.
    """
    required: list[str] = []
    pending_key: str | None = None

    for raw in render_yaml.splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue

        # Strip an inline trailing comment. render.yaml really does write
        #     - key: Storage__Endpoint          # R2: https://... ; empty for AWS
        # and the gate's own self-test caught this: without stripping, Storage__Endpoint and
        # Storage__Region were silently dropped from the required list -- the exact
        # under-reporting failure this gate exists to prevent, inside the gate itself.
        # Values here are unquoted key names, so a bare '#' cannot be part of one.
        line = re.sub(r"\s+#.*$", "", line).strip()
        if not line:
            continue

        m = re.match(r"^-\s*key:\s*(\S+)\s*$", line)
        if m:
            pending_key = m.group(1).strip("\"'")
            continue

        m = re.match(r"^key:\s*(\S+)\s*$", line)
        if m:
            pending_key = m.group(1).strip("\"'")
            continue

        if pending_key is None:
            continue

        if re.match(r"^sync:\s*false\s*$", line):
            if pending_key not in required:
                required.append(pending_key)
            pending_key = None
        elif re.match(r"^(value|fromService|fromGroup|fromDatabase|generateValue):", line):
            # Supplied by the blueprint; not a dashboard obligation.
            pending_key = None

    return required


def find_missing(required: list[str], present: set[str]) -> list[str]:
    """Required keys absent from the service. Order follows render.yaml so the message is stable."""
    return [k for k in required if k not in present]


# ──────────────────────────────────────────────────────────────────────────────
# Render API
# ──────────────────────────────────────────────────────────────────────────────
def fetch_present_keys(service_id: str, api_key: str) -> set[str]:
    """Key NAMES only. This gate never reads, logs or returns a secret VALUE."""
    keys: set[str] = set()
    cursor = None
    while True:
        url = f"{RENDER_API}/services/{service_id}/env-vars?limit=100"
        if cursor:
            url += f"&cursor={cursor}"
        req = urllib.request.Request(url, headers={
            "Authorization": f"Bearer {api_key}",
            "Accept": "application/json",
        })
        with urllib.request.urlopen(req, timeout=30) as resp:
            page = json.loads(resp.read().decode())
        if not page:
            break
        for item in page:
            env_var = item.get("envVar") or {}
            if env_var.get("key"):
                keys.add(env_var["key"])
        cursor = page[-1].get("cursor")
        if not cursor or len(page) < 100:
            break
    return keys


# ──────────────────────────────────────────────────────────────────────────────
# Reporting
# ──────────────────────────────────────────────────────────────────────────────
def report(required: list[str], present: set[str], source: str) -> int:
    missing = find_missing(required, present)
    print(f"Required by render.yaml (sync: false) : {len(required)}")
    print(f"Present on {source:<26}: {len(required) - len(missing)} of {len(required)}")

    if not required:
        print("::error::check_render_env: render.yaml declares ZERO `sync: false` keys. "
              "That is almost certainly a parse failure, not a blueprint with no secrets. "
              "Refusing to pass vacuously.", file=sys.stderr)
        return 2

    if not missing:
        print("PASS - every key render.yaml marks `sync: false` is set on the service.")
        return 0

    print("", file=sys.stderr)
    print(f"::error::check_render_env: {len(missing)} required environment "
          f"variable(s) are NOT set on the target service. Deploy blocked.", file=sys.stderr)
    print("", file=sys.stderr)
    for key in missing:
        print(f"    MISSING: {key}", file=sys.stderr)
    print("", file=sys.stderr)
    print("  render.yaml declares each of these `sync: false`, which means the blueprint expects", file=sys.stderr)
    print("  a human to have set it in the Render dashboard (Environment -> Environment Variables).", file=sys.stderr)
    print("  Until then the service is missing configuration it has been told it requires --", file=sys.stderr)
    print("  and a missing key here has already taken the backend down: an unset", file=sys.stderr)
    print("  Proxy__KnownNetworks killed the pre-deploy migration job before it reached the DB.", file=sys.stderr)
    print("", file=sys.stderr)
    return 1


# ──────────────────────────────────────────────────────────────────────────────
# Self-test -- the gate must be seen to FAIL, on the input that actually occurred
# ──────────────────────────────────────────────────────────────────────────────
# The live key list read from the Render API on 2026-09-21, with Proxy__KnownNetworks removed:
# the exact state of service srv-d8slkb77f7vs73d2k92g during the incident.
HISTORICAL_PRESENT_KEYS = {
    "AI_MODEL", "AI_PROVIDER", "ASPNETCORE_ENVIRONMENT", "ASPNETCORE_URLS", "AllowedHosts",
    "CORS_EXTRA_ORIGINS", "ConnectionStrings__Default", "DEDICATED_DEPLOYMENT",
    "Database__RunMigrationsOnStartup", "Jwt__Issuer", "Jwt__PlatformAudience", "Jwt__SigningKey",
    "Jwt__TenantAudience", "OLLAMA_API_KEY", "OLLAMA_BASE_URL", "PLATFORM_ADMIN_EMAIL",
    "PLATFORM_ADMIN_PASSWORD", "PORT", "Proxy__TrustForwardedHeaders", "SeedAdmin__Email",
    "SeedAdmin__Password", "SeedAdmin__SeedDemoData", "SeedAdmin__TenantName",
    "SeedAdmin__TenantSlug", "Storage__AccessKey", "Storage__AllowEphemeral", "Storage__Bucket",
    "Storage__Endpoint", "Storage__Provider", "Storage__Region", "Storage__SecretKey",
    # "Proxy__KnownNetworks",  <- THE INCIDENT: declared `sync: false`, never set.
}

SAMPLE_BLUEPRINT = """\
services:
  - type: web
    name: kynexone
    envVars:
      - key: ASPNETCORE_ENVIRONMENT
        value: Production
      # A comment between the key and its sync flag must not break the scan.
      - key: Proxy__KnownNetworks
        sync: false
      - key: Jwt__SigningKey
        sync: false
      - key: Storage__Endpoint          # inline trailing comment
        sync: false
"""


def self_test() -> int:
    failures: list[str] = []

    def check(name: str, got, want) -> None:
        if got != want:
            failures.append(f"{name}\n      expected: {want!r}\n      got     : {got!r}")
        else:
            print(f"  ok  {name}")

    print("check_render_env self-test")
    print("")

    # 1. Parsing: `sync: false` keys are found; `value:` keys are not.
    check("parses sync:false keys and ignores value: keys",
          parse_required_keys(SAMPLE_BLUEPRINT),
          ["Proxy__KnownNetworks", "Jwt__SigningKey", "Storage__Endpoint"])

    # 2. THE REGRESSION TEST. The real render.yaml, against the real pre-incident key list.
    #    If this ever stops failing, the gate has stopped working.
    here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    with open(os.path.join(here, "render.yaml"), encoding="utf-8") as fh:
        real_required = parse_required_keys(fh.read())

    check("render.yaml still declares Proxy__KnownNetworks as sync:false",
          "Proxy__KnownNetworks" in real_required, True)

    missing_then = find_missing(real_required, HISTORICAL_PRESENT_KEYS)
    check("CATCHES THE INCIDENT: Proxy__KnownNetworks reported missing",
          missing_then, ["Proxy__KnownNetworks"])

    # 3. And passes once the key is set -- otherwise it is a gate that can only fail.
    missing_now = find_missing(real_required, HISTORICAL_PRESENT_KEYS | {"Proxy__KnownNetworks"})
    check("passes once Proxy__KnownNetworks is set", missing_now, [])

    # 4. Fail-closed on an empty parse rather than reporting a vacuous pass.
    check("empty blueprint yields no required keys (caller treats as error)",
          parse_required_keys("services: []"), [])

    print("")
    if failures:
        for f in failures:
            print(f"  FAIL  {f}", file=sys.stderr)
        print(f"\n{len(failures)} self-test failure(s).", file=sys.stderr)
        return 1
    print("All self-tests passed.")
    return 0


# ──────────────────────────────────────────────────────────────────────────────
def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--render-yaml", default=None, help="path to render.yaml")
    ap.add_argument("--service-id", default=os.environ.get("RENDER_SERVICE_ID"),
                    help="Render service id, e.g. srv-xxxx")
    ap.add_argument("--present-from", default=None,
                    help="file of key names already set (offline mode, instead of the API)")
    ap.add_argument("--self-test", action="store_true", help="run the gate's own tests and exit")
    args = ap.parse_args()

    if args.self_test:
        return self_test()

    repo_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    path = args.render_yaml or os.path.join(repo_root, "render.yaml")
    if not os.path.isfile(path):
        print(f"::error::check_render_env: render.yaml not found at {path}", file=sys.stderr)
        return 2

    with open(path, encoding="utf-8") as fh:
        required = parse_required_keys(fh.read())

    if args.present_from:
        if not os.path.isfile(args.present_from):
            print(f"::error::check_render_env: --present-from file not found: {args.present_from}",
                  file=sys.stderr)
            return 2
        with open(args.present_from, encoding="utf-8") as fh:
            present = {ln.strip() for ln in fh if ln.strip() and not ln.startswith("#")}
        return report(required, present, "the supplied key list")

    if not args.service_id:
        print("::error::check_render_env: no --service-id (or RENDER_SERVICE_ID). "
              "Cannot verify the service's environment. Failing closed.", file=sys.stderr)
        return 2

    api_key = os.environ.get("RENDER_API_KEY")
    if not api_key:
        # Fail closed, loudly. A gate that skips itself when its credential is absent is a gate
        # that is off, and nobody notices which.
        print("::error::check_render_env: RENDER_API_KEY is not set, so the required environment "
              "variables CANNOT be verified. Failing closed -- add the RENDER_API_KEY secret to "
              "the `production` GitHub environment.", file=sys.stderr)
        return 2

    try:
        present = fetch_present_keys(args.service_id, api_key)
    except urllib.error.HTTPError as e:
        print(f"::error::check_render_env: Render API returned HTTP {e.code} for service "
              f"{args.service_id}. Cannot verify; failing closed.", file=sys.stderr)
        return 2
    except Exception as e:  # noqa: BLE001 - any failure here must fail closed, not pass
        print(f"::error::check_render_env: could not reach the Render API "
              f"({type(e).__name__}). Cannot verify; failing closed.", file=sys.stderr)
        return 2

    if not present:
        print(f"::error::check_render_env: the Render API returned ZERO env vars for "
              f"{args.service_id}. That is not a service that could boot; treating as an error.",
              file=sys.stderr)
        return 2

    return report(required, present, f"service {args.service_id}")


if __name__ == "__main__":
    sys.exit(main())
