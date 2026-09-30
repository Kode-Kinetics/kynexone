#!/usr/bin/env python3
"""
CANDIDATE MANIFEST — one machine-readable record of what a given commit IS, so that a
client-facing deployment can later be proved to contain it. Register item F11.

WHY THIS EXISTS
---------------
The recorded failure mode: acceptance evidence (screenshots, test runs, "we fixed that")
spanned different source trees and different local stacks. Nobody could show that a build a
customer was looking at contained a given fix, or that the frontend and the backend in front of
that customer were the same accepted version. `docs/saudi-bank-export.md` states the requirement
in as many words -- "record a clean candidate with matching UI/API versions" -- and nothing
produced such a record.

A candidate is a COMMIT. Everything here is derived from that commit (via `git`, so the dirty
worktree cannot leak into it) plus GitHub's own record of what CI concluded about it. Two things
are deliberately NOT here: anything a deployment holds (which tiers are actually running it --
that is `check_deployment_parity.py`), and any configuration VALUE (see below).

CONFIG NAMES, NEVER CONFIG VALUES
---------------------------------
The activation gates below are the switches that decide whether customer-visible behaviour is on.
Their values live in the deployment's environment -- Render's dashboard, Vercel's project -- and
this file records only their NAMES, what they gate, what the build's own default is, and the file
that proves each name still exists at this commit. A manifest is an artifact that gets attached to
PRs, uploaded to CI and pasted into tickets; a manifest that could carry a signing key or a
connection string would eventually carry one. `assert_no_secrets` enforces that on the way out and
fails the generator rather than emitting a suspect document.

The gate registry is checked against the tree at the candidate commit on every run. If a gate is
renamed or its defining file moves, generation FAILS -- a stale registry that quietly stops naming
a live switch is exactly the kind of drift this item exists to stop.

USAGE
-----
    ./scripts/release_manifest.py                              # HEAD, to stdout
    ./scripts/release_manifest.py --commit 64bab568            # a named candidate
    ./scripts/release_manifest.py --image-digest sha256:...    # CI, where the digest is known
    ./scripts/release_manifest.py --out release-manifest.json
    ./scripts/release_manifest.py --self-test                  # prove the redaction guard refuses

Network (GitHub check runs) is optional: `--offline` skips it and records that it was skipped.
Absent evidence is recorded as absent; it is never reported as a pass.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.request
from datetime import datetime, timezone
from typing import Any

SCHEMA = "kynexone.release-manifest/1"
DEFAULT_REPO = "Kode-Kinetics/kynexone"
MIGRATIONS_DIR = "backend-dotnet/Zayra.Api/Migrations"
API_CSPROJ = "backend-dotnet/Zayra.Api/Zayra.Api.csproj"

# The checks `main`'s ruleset requires before a merge (repo ruleset "main-protection",
# 2026-09-30). Declared here so the manifest can be produced without ruleset-read permission;
# cross-checked against the live ruleset when the token can see it, and any drift is recorded.
REQUIRED_CHECKS = [
    "Backend Tests (security gate)",
    "Schema Gates (drift, fresh deploy, upgrade)",
    "Frontend Production Build",
    "Frontend Typecheck",
    "Secret Scan (gitleaks)",
    "Dependency Vulnerability Scan",
    "Analyze (csharp)",
    "Analyze (javascript-typescript)",
]

# Every switch that decides whether customer-visible behaviour is ON. `evidence` is the file that
# defines the name; it must exist at the candidate commit or generation fails.
ACTIVATION_GATES = [
    {
        "name": "SaudiBankExportActivation:Enabled",
        "needles": ["SaudiBankExportActivation", "Enabled"],
        "env": "SaudiBankExportActivation__Enabled",
        "gates": "Whether Saudi bank payment-file export exists for anyone at all.",
        "buildDefault": "off (absent or unparseable is off)",
        "evidence": "backend-dotnet/Zayra.Api/Infrastructure/Payroll/SaudiBankExports/SaudiBankExportActivation.cs",
    },
    {
        "name": "SaudiBankExportActivation:Companies[n]:{TenantId,CompanyId}",
        "needles": ["SaudiBankExportActivation", "Companies"],
        "env": "SaudiBankExportActivation__Companies__<n>__TenantId / __CompanyId",
        "gates": "Which exact legal entities may export. No wildcard, no tenant-wide fallback; an "
                 "empty list denies everyone even with Enabled=true.",
        "buildDefault": "empty (nobody)",
        "evidence": "backend-dotnet/Zayra.Api/Infrastructure/Payroll/SaudiBankExports/SaudiBankExportActivation.cs",
    },
    {
        "name": "EmployeeEffectiveChanges:Enabled",
        "needles": ["EmployeeEffectiveChanges", "Enabled"],
        "env": "EmployeeEffectiveChanges__Enabled",
        "gates": "Whether approved employee changes are applied on their effective date. Off means "
                 "they wait in ApprovedPendingEffectiveDate and the health counter shows how many.",
        "buildDefault": "on",
        "evidence": "backend-dotnet/Zayra.Api/Infrastructure/Employees/EffectiveChangeOptions.cs",
    },
    {
        "name": "Email:DeliveryMode",
        "needles": ["Email:DeliveryMode", "AllowedRecipients"],
        "env": "Email__DeliveryMode",
        "gates": "Whether mail reaches real people. 'smtp'/'relay' sends; anything else captures. "
                 "Paired with Email:AllowedRecipients, which narrows a relay to named addresses.",
        "buildDefault": "smtp (sends)",
        "evidence": "backend-dotnet/Zayra.Api/Infrastructure/Email/EmailDelivery.cs",
    },
    {
        "name": "QIWA_USE_LIVE_ADAPTER",
        "needles": ["QIWA_USE_LIVE_ADAPTER"],
        "env": "QIWA_USE_LIVE_ADAPTER",
        "gates": "Whether Qiwa calls hit the real api.qiwa.tech adapter or the sandbox mock.",
        "buildDefault": "sandbox mock (anything but 'true')",
        "evidence": "backend-dotnet/Zayra.Api/Program.cs",
    },
    {
        "name": "AI_PROVIDER / AI_MODEL / OLLAMA_BASE_URL",
        "needles": ["AI_PROVIDER", "AI_MODEL", "OLLAMA_BASE_URL"],
        "env": "AI_PROVIDER, AI_MODEL, OLLAMA_BASE_URL",
        "gates": "Which model serves AI-assisted surfaces, and whether they are served at all.",
        "buildDefault": "declared per-service in render.yaml; no value committed",
        "evidence": "render.yaml",
    },
    {
        "name": "Database:RunMigrationsOnStartup",
        "needles": ["Database__RunMigrationsOnStartup"],
        "env": "Database__RunMigrationsOnStartup",
        "gates": "Whether the instance migrates its own database at boot. Production is false: CI's "
                 "migrate-backend job applies schema ahead of the deploy hook.",
        "buildDefault": "declared in render.yaml",
        "evidence": "render.yaml",
    },
    {
        "name": "DEDICATED_DEPLOYMENT",
        "needles": ["DEDICATED_DEPLOYMENT"],
        "env": "DEDICATED_DEPLOYMENT",
        "gates": "Single-tenant vs multi-tenant behaviour of the whole deployment.",
        "buildDefault": "declared in render.yaml",
        "evidence": "render.yaml",
    },
]

# If any of these environment variables has a value, that exact value must not appear anywhere in
# the emitted document. The cheapest possible proof that the generator did not read a secret into
# the thing it is about to publish.
SENSITIVE_ENV = [
    "ConnectionStrings__Default", "PROD_DATABASE_URL", "RENDER_DEPLOY_HOOK_URL", "RENDER_API_KEY",
    "GITHUB_TOKEN", "GH_TOKEN", "Jwt__SigningKey", "SeedAdmin__Password", "Storage__SecretKey",
    "Storage__AccessKey", "OLLAMA_API_KEY", "VERCEL_TOKEN",
]

# Shapes that mean "somebody pasted a credential in here".
SECRET_PATTERNS = [
    re.compile(r"(?i)\b(password|pwd|secret|signingkey|api[_-]?key|access[_-]?key|bearer)\s*[=:]\s*\S"),
    re.compile(r"://[^/\s\"]*:[^/@\s\"]+@"),          # user:password@host in any URL
    re.compile(r"(?i)\bgh[pousr]_[A-Za-z0-9]{20,}"),  # GitHub token forms
]


def fail(message: str) -> "NoReturn":  # type: ignore[valid-type]
    print(f"::error::release-manifest: {message}", file=sys.stderr)
    sys.exit(1)


# ── git ───────────────────────────────────────────────────────────────────────────────────

def git(*args: str) -> str:
    try:
        return subprocess.run(["git", *args], check=True, capture_output=True, text=True).stdout.strip()
    except subprocess.CalledProcessError as e:
        fail(f"git {' '.join(args)} failed: {e.stderr.strip() or e}")
    except FileNotFoundError:
        fail("git is not on PATH; the manifest is derived from the repository and cannot be faked.")


def resolve_commit(ref: str) -> str:
    sha = git("rev-parse", f"{ref}^{{commit}}")
    if not re.fullmatch(r"[0-9a-f]{40}", sha):
        fail(f"{ref} did not resolve to a commit id.")
    return sha


def tree_paths(commit: str, prefix: str) -> list[str]:
    out = git("ls-tree", "-r", "--name-only", commit, "--", prefix)
    return [line for line in out.splitlines() if line]


def blob(commit: str, path: str) -> str | None:
    try:
        return subprocess.run(["git", "show", f"{commit}:{path}"], check=True,
                              capture_output=True, text=True).stdout
    except subprocess.CalledProcessError:
        return None


# ── the parts of a candidate ──────────────────────────────────────────────────────────────

def migration_set(commit: str) -> dict[str, Any]:
    """
    The migration ids this build CONTAINS, derived exactly as ./Dockerfile derives
    Migrations.manifest: the filenames, minus .Designer.cs and the model snapshot, sorted. Reading
    the filenames rather than `dotnet ef migrations list` is deliberate and is the same choice the
    Dockerfile made -- three migrations written without a [Migration] attribute were invisible to
    EF, and therefore to every ef-based tool, for 70 days.
    """
    ids = sorted(
        os.path.basename(p)[: -len(".cs")]
        for p in tree_paths(commit, MIGRATIONS_DIR)
        if p.endswith(".cs") and not p.endswith(".Designer.cs") and not p.endswith("ModelSnapshot.cs")
    )
    if not ids:
        fail(f"no migrations found under {MIGRATIONS_DIR} at {commit[:8]} -- refusing to record an empty set.")
    return {
        "count": len(ids),
        "latestId": ids[-1],
        "ids": ids,
        "derivedFrom": f"{MIGRATIONS_DIR}/*.cs at this commit (the rule ./Dockerfile uses to write Migrations.manifest)",
        "embeddedResource": "Zayra.Api.Migrations.manifest",
        "note": "The runtime image strips Migrations/; /health/ready diffs this embedded list against "
                "__EFMigrationsHistory and fails closed when neither is readable.",
    }


def assembly_version(commit: str) -> str:
    """
    What /health/live reports off Render: MSBuild appends +<SourceRevisionId> to the informational
    version and CI passes the sha as SourceRevisionId, so the commit is baked into the assembly and
    cannot be claimed by whoever launches an older image. See Infrastructure/Operations/BuildInfo.cs.
    """
    csproj = blob(commit, API_CSPROJ) or ""
    m = re.search(r"<Version>\s*([^<]+?)\s*</Version>", csproj)
    return f"{(m.group(1) if m else '1.0.0')}+{commit}"


def verify_gate_registry(commit: str) -> list[dict[str, Any]]:
    """
    Every gate's defining file must exist at the candidate commit and must still contain the tokens
    that name the switch. Raises ValueError when it does not, so a rename cannot leave the manifest
    quietly naming a switch that no longer gates anything.
    """
    gates = []
    missing = []
    for gate in ACTIVATION_GATES:
        text = blob(commit, gate["evidence"])
        if text is None:
            missing.append(f"{gate['name']}: {gate['evidence']} does not exist at this commit")
            continue
        for needle in gate["needles"]:
            if needle not in text:
                missing.append(f"{gate['name']}: '{needle}' no longer appears in {gate['evidence']}")
        gates.append({k: v for k, v in gate.items() if k != "needles"}
                     | {"valueLocation": "deployment environment -- NOT recorded here"})
    if missing:
        raise ValueError(
            "the activation-gate registry is stale; a switch this manifest claims to name has moved "
            "or been renamed:\n  " + "\n  ".join(missing)
            + "\n  Update ACTIVATION_GATES in scripts/release_manifest.py. A manifest that silently "
              "stops naming a live switch is the drift F11 exists to prevent.")
    return gates


# ── GitHub: what CI concluded about this exact commit ─────────────────────────────────────

def github_api(path: str) -> Any | None:
    """GET a GitHub API path. Token from the environment, else the gh CLI. None on any failure."""
    token = (os.environ.get("GITHUB_TOKEN") or os.environ.get("GH_TOKEN") or "").strip()
    if token:
        req = urllib.request.Request(
            f"https://api.github.com{path}",
            headers={"Authorization": f"Bearer {token}", "Accept": "application/vnd.github+json",
                     "User-Agent": "kynexone-release-manifest"},
        )
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                return json.load(resp)
        except (urllib.error.URLError, OSError, ValueError):
            return None
    try:
        out = subprocess.run(["gh", "api", path], check=True, capture_output=True, text=True).stdout
        return json.loads(out)
    except (subprocess.CalledProcessError, FileNotFoundError, ValueError):
        return None


def test_evidence(repo: str, commit: str, offline: bool) -> dict[str, Any]:
    if offline:
        return {"source": "skipped (--offline)", "allRequiredChecksPassed": None,
                "requiredChecks": [{"name": n, "conclusion": "not looked up"} for n in REQUIRED_CHECKS]}

    payload = github_api(f"/repos/{repo}/commits/{commit}/check-runs?per_page=100")
    if not payload or "check_runs" not in payload:
        return {"source": "unavailable (no readable GitHub token, or the API did not answer)",
                "allRequiredChecksPassed": None,
                "requiredChecks": [{"name": n, "conclusion": "unknown"} for n in REQUIRED_CHECKS],
                "note": "Absent evidence. This is NOT a pass."}

    runs = payload["check_runs"]
    by_name: dict[str, dict[str, Any]] = {}
    for run in runs:
        prev = by_name.get(run["name"])
        # Keep the latest attempt per check name.
        if prev is None or (run.get("completed_at") or "") >= (prev.get("completed_at") or ""):
            by_name[run["name"]] = run

    required = []
    for name in REQUIRED_CHECKS:
        run = by_name.get(name)
        required.append({
            "name": name,
            "status": run.get("status") if run else "absent",
            "conclusion": (run.get("conclusion") if run else None) or "absent",
            "runId": run.get("id") if run else None,
        })
    others = sorted(
        ({"name": r["name"], "conclusion": r.get("conclusion") or r.get("status")}
         for r in by_name.values() if r["name"] not in set(REQUIRED_CHECKS)),
        key=lambda r: r["name"],
    )

    workflow_runs = []
    wf = github_api(f"/repos/{repo}/actions/runs?head_sha={commit}&per_page=20")
    for run in (wf or {}).get("workflow_runs", []):
        workflow_runs.append({
            "id": run.get("id"), "name": run.get("name"), "event": run.get("event"),
            "status": run.get("status"), "conclusion": run.get("conclusion"),
            "attempt": run.get("run_attempt"), "url": run.get("html_url"),
        })

    evidence: dict[str, Any] = {
        "source": f"GitHub check runs for {repo}@{commit[:8]}",
        "requiredChecksSource": "declared in scripts/release_manifest.py (ruleset 'main-protection')",
        "allRequiredChecksPassed": all(c["conclusion"] == "success" for c in required),
        "requiredChecks": required,
        "recordedWhile": "complete" if all(c["status"] == "completed" for c in required) else
                         "IN PROGRESS — a check that had not finished when this manifest was written "
                         "is recorded as it stood, not as a pass. Re-run the generator on this commit "
                         "for the settled picture.",
        "otherChecks": others,
        "workflowRuns": workflow_runs,
    }

    # Cross-check the declared list against the live ruleset when the token can see it, so the
    # constant above cannot drift away from what actually gates a merge without anyone noticing.
    rulesets = github_api(f"/repos/{repo}/rulesets")
    for entry in rulesets or []:
        detail = github_api(f"/repos/{repo}/rulesets/{entry.get('id')}")
        for rule in (detail or {}).get("rules", []):
            if rule.get("type") == "required_status_checks":
                live = sorted(c["context"] for c in rule["parameters"]["required_status_checks"])
                evidence["requiredChecksSource"] = f"ruleset '{entry.get('name')}' (verified live)"
                if live != sorted(REQUIRED_CHECKS):
                    evidence["requiredChecksDrift"] = {
                        "ruleset": live, "declared": sorted(REQUIRED_CHECKS),
                        "action": "update REQUIRED_CHECKS in scripts/release_manifest.py",
                    }
    return evidence


# ── redaction guard ───────────────────────────────────────────────────────────────────────

def assert_no_secrets(document: dict[str, Any], env: dict[str, str] | None = None) -> None:
    """
    Refuse to emit a manifest that carries a credential. Two independent checks: no value of a
    known-sensitive environment variable appears verbatim, and no string has the SHAPE of a pasted
    credential. Raises ValueError; the caller turns that into a non-zero exit and emits nothing.
    """
    env = os.environ if env is None else env
    text = json.dumps(document)

    for name in SENSITIVE_ENV:
        value = (env.get(name) or "").strip()
        if len(value) >= 8 and value in text:
            raise ValueError(f"the value of {name} appears in the manifest. Nothing was written.")

    for pattern in SECRET_PATTERNS:
        match = pattern.search(text)
        if match:
            raise ValueError(
                f"a string in the manifest has the shape of a credential ({pattern.pattern[:40]}…): "
                f"…{match.group(0)[:24]}…. Nothing was written."
            )


# ── assembly ──────────────────────────────────────────────────────────────────────────────

def build_manifest(commit: str, repo: str, image_digest: str | None, image_ref: str | None,
                   offline: bool) -> dict[str, Any]:
    subject = git("log", "-1", "--format=%s", commit)
    committed = git("log", "-1", "--format=%cI", commit)
    image = image_ref or f"ghcr.io/{repo.lower()}/backend:{commit}"
    return {
        "schema": SCHEMA,
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z"),
        "candidate": {
            "repository": repo,
            "commit": commit,
            "shortCommit": commit[:8],
            "subject": subject,
            "committedAtUtc": committed,
        },
        "backend": {
            "imageRef": image,
            "imageDigest": image_digest or "unresolved (pass --image-digest; CI's build-image job knows it)",
            "assemblyInformationalVersion": assembly_version(commit),
            "identityEndpoint": "/health/live",
            "expectedHealthLiveCommit": commit,
            "identityNote": "Off Render, the commit comes from SourceRevisionId baked into the assembly; "
                            "on Render, RENDER_GIT_COMMIT of the running instance. See BuildInfo.cs.",
        },
        "frontend": {
            "identityEndpoint": "/build-info",
            "expectedBuildInfoCommit": commit,
            "buildIdSources": ["BUILD_COMMIT", "VERCEL_GIT_COMMIT_SHA", "GITHUB_SHA"],
            "identityNote": "force-static: the value is fixed at build time, so it identifies the build "
                            "the browser was served, not the machine that answered. See frontend/app/build-info/route.ts.",
        },
        "migrations": migration_set(commit),
        "activationGates": {
            "policy": "NAMES ONLY. Values live in the deployment environment and are never recorded here.",
            "gates": verify_gate_registry(commit),
        },
        "testEvidence": test_evidence(repo, commit, offline),
        "reproduce": {
            "manifest": f"./scripts/release_manifest.py --commit {commit}",
            "backendBuild": f"docker build -f ./Dockerfile -t {image} .",
            "backendTests": "cd backend-dotnet && dotnet test Zayra.Api.Tests",
            "frontendTypecheck": "cd frontend && npx tsc --noEmit",
            "frontendBuild": f"cd frontend && BUILD_COMMIT={commit} npm run build",
            "schemaGates": "./scripts/schema-gates.sh",
            "parity": "./scripts/check_deployment_parity.py --frontend <url> --api <url> "
                      f"--candidate {commit}",
            "rollback": "docs/RELEASE_CANDIDATE_AND_PARITY.md",
        },
    }


def self_test() -> int:
    """Prove the redaction guard refuses. A guard nobody has watched refuse is not known to work."""
    failures = 0
    cases: list[tuple[str, dict[str, Any], dict[str, str], bool]] = [
        ("a clean manifest passes",
         {"a": "64bab568c9ce72665c605ef39da7467d15ac2d82", "b": "sha256:" + "d" * 64}, {}, True),
        ("a connection string pasted into a field is refused",
         {"db": "Host=h;Database=d;Username=u;Password=hunter2;"}, {}, False),
        ("a URL carrying user:password is refused",
         {"url": "postgresql://u:p4ssw0rd@ep-a.neon.tech:5432/db"}, {}, False),
        ("a GitHub token is refused",
         {"t": "ghp_" + "A" * 36}, {}, False),
        ("the live value of a sensitive env var is refused even in an innocent-looking field",
         {"note": "deployed against xK9-live-endpoint-secret-value"},
         {"PROD_DATABASE_URL": "xK9-live-endpoint-secret-value"}, False),
        ("a short env value is not treated as a secret (too weak to match on)",
         {"note": "mode is smtp"}, {"GH_TOKEN": "smtp"}, True),
    ]
    for label, doc, env, should_pass in cases:
        try:
            assert_no_secrets(doc, env)
            got = True
        except ValueError:
            got = False
        ok = got == should_pass
        print(f"  {'PASS' if ok else 'FAIL'}  {label}")
        failures += 0 if ok else 1

    # The registry must be live, not a comment: every evidence file must exist at HEAD.
    try:
        verify_gate_registry(resolve_commit("HEAD"))
        print("  PASS  every activation gate in the registry still exists at HEAD")
    except ValueError as e:
        print(f"  FAIL  {e}")
        failures += 1

    print("self-test: " + ("all checks passed" if failures == 0 else f"{failures} FAILED"))
    return 1 if failures else 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--commit", default="HEAD", help="the candidate (default: HEAD)")
    ap.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY") or DEFAULT_REPO)
    ap.add_argument("--image-digest", default=None, help="immutable backend image digest, if known")
    ap.add_argument("--image-ref", default=None, help="backend image reference, if not the CI default")
    ap.add_argument("--out", default="-", help="file to write, or - for stdout")
    ap.add_argument("--offline", action="store_true", help="skip the GitHub lookup and record that it was skipped")
    ap.add_argument("--self-test", action="store_true", help="prove the redaction guard refuses, then exit")
    args = ap.parse_args()

    if args.self_test:
        return self_test()

    try:
        manifest = build_manifest(resolve_commit(args.commit), args.repo, args.image_digest,
                                  args.image_ref, args.offline)
    except ValueError as e:
        fail(str(e))
    try:
        assert_no_secrets(manifest)
    except ValueError as e:
        fail(str(e))

    text = json.dumps(manifest, indent=2) + "\n"
    if args.out == "-":
        sys.stdout.write(text)
    else:
        with open(args.out, "w", encoding="utf-8") as fh:
            fh.write(text)
        print(f"release-manifest: wrote {args.out} for {manifest['candidate']['shortCommit']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
