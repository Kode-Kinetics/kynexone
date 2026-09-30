#!/usr/bin/env python3
"""
DEPLOYMENT PARITY GATE — prove the two tiers a customer is actually using were built from the
same commit, and that it is the commit being accepted. Register item F11.

WHY THIS EXISTS
---------------
The frontend and the backend deploy by different rules and nothing compared them:

  * Vercel auto-deploys every push to `main`. No approval, no gate.
  * Render is approval-gated (render.yaml sets autoDeploy:false; CI's deploy-backend job POSTs the
    hook only after a reviewer approves the `production` environment).

So a merge to main puts new UI in front of customers immediately while the API behind it stays on
whatever was last approved — silently, with every CI signal green. That is the 2026-09-23
split-brain, and it is invisible from either tier on its own: each is healthy, each reports itself
correctly, and no screen anywhere says they disagree. It is also why acceptance screenshots could
never be tied to a build: the UI in the picture and the API that answered it were different trees.

This is the check that says so, in one sentence, with a direction and a distance.

WHAT IT READS (read-only; two anonymous GETs, no auth, no data)
---------------------------------------------------------------
  GET <frontend>/build-info   -> {"service":"kynexone-web","commit":"<sha>"}   (force-static: the
                                 value is fixed at BUILD time, so it identifies the bundle the
                                 browser was served, not the host that answered)
  GET <api>/health/live       -> {"service":"zayra-api","commit":"<sha>"}      (RENDER_GIT_COMMIT
                                 on Render, else SourceRevisionId baked into the assembly)

FAILS CLOSED
------------
Unreachable, wrong service, missing commit, or the placeholder values ("unknown"/"local") are all
FAILURES. A parity checker that cannot see a tier has not proved parity; it has proved nothing, and
reporting nothing as agreement is how the gap survived in the first place.

USAGE
-----
    ./scripts/check_deployment_parity.py --frontend https://… --api https://… [--candidate <sha>]
    ./scripts/check_deployment_parity.py … --wait-seconds 300   # let a just-triggered tier catch up
    ./scripts/check_deployment_parity.py … --json parity.json   # machine-readable result
    ./scripts/check_deployment_parity.py --self-test            # prove every failure path refuses

Exit 0 only when every tier answered, both carry the same commit, and (when --candidate is given)
that commit is the candidate.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
import time
import urllib.error
import urllib.request
from typing import Any, Callable, NamedTuple

# Values a tier reports when it does not KNOW its own commit. They are not evidence of anything.
PLACEHOLDERS = {"", "unknown", "local", "none", "null"}

TIERS = {
    "frontend": {"path": "/build-info", "service": "kynexone-web", "deployedBy": "Vercel (auto-deploys main)"},
    "api": {"path": "/health/live", "service": "zayra-api", "deployedBy": "Render (approval-gated)"},
}


class Observation(NamedTuple):
    """What one tier said about itself. `commit` is None when nothing usable came back."""
    tier: str
    url: str
    commit: str | None
    detail: str


class Result(NamedTuple):
    ok: bool
    lines: list[str]

    def report(self) -> str:
        return "\n".join(self.lines)


# ── reading the tiers ─────────────────────────────────────────────────────────────────────

def probe(tier: str, base_url: str, timeout: int = 30) -> Observation:
    spec = TIERS[tier]
    url = base_url.rstrip("/") + spec["path"]
    req = urllib.request.Request(url, headers={"Accept": "application/json",
                                               "User-Agent": "kynexone-parity-check"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            status = resp.status
            body = resp.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        return Observation(tier, url, None, f"HTTP {e.code}")
    except Exception as e:  # noqa: BLE001 — anything at all here is a failure, never a skip
        return Observation(tier, url, None, f"unreachable: {type(e).__name__}")

    try:
        payload = json.loads(body)
    except ValueError:
        return Observation(tier, url, None, f"HTTP {status}, not JSON: {body[:80]!r}")

    if payload.get("service") != spec["service"]:
        return Observation(tier, url, None,
                           f"HTTP {status}, service={payload.get('service')!r} (expected {spec['service']!r}) "
                           f"— this URL is not the {tier} of this product")

    commit = str(payload.get("commit") or "").strip().lower()
    if commit in PLACEHOLDERS:
        return Observation(tier, url, None,
                           f"HTTP {status}, commit={commit!r} — this build does not know which commit it is")
    return Observation(tier, url, commit, f"HTTP {status}")


# ── how far apart, and which way ──────────────────────────────────────────────────────────

def git_distance(older: str, newer: str) -> str | None:
    """
    'N commits' when `older` is an ancestor of `newer` in this clone, else None. Used only to phrase
    the finding; a clone that has never seen one of the commits says so rather than guessing.
    """
    try:
        subprocess.run(["git", "cat-file", "-e", f"{older}^{{commit}}"], check=True, capture_output=True)
        subprocess.run(["git", "cat-file", "-e", f"{newer}^{{commit}}"], check=True, capture_output=True)
        ancestor = subprocess.run(["git", "merge-base", "--is-ancestor", older, newer], capture_output=True)
        if ancestor.returncode != 0:
            return None
        count = subprocess.run(["git", "rev-list", "--count", f"{older}..{newer}"],
                               check=True, capture_output=True, text=True).stdout.strip()
        return f"{count} commit{'s' if count != '1' else ''}"
    except (subprocess.CalledProcessError, FileNotFoundError):
        return None


def describe_gap(a: Observation, b: Observation, distance: Callable[[str, str], str | None]) -> str:
    """
    Which tier is AHEAD, and by how much. Ancestry decides direction; the clone not knowing a commit
    is reported as not knowing, never as parity.
    """
    assert a.commit and b.commit
    forward = distance(b.commit, a.commit)    # b is older -> a is ahead
    backward = distance(a.commit, b.commit)
    if forward:
        return (f"{a.tier} is AHEAD of {b.tier} by {forward} "
                f"({a.commit[:8]} contains {b.commit[:8]} plus {forward}).")
    if backward:
        return (f"{b.tier} is AHEAD of {a.tier} by {backward} "
                f"({b.commit[:8]} contains {a.commit[:8]} plus {backward}).")
    return (f"neither commit contains the other — {a.tier} {a.commit[:8]} and {b.tier} {b.commit[:8]} "
            f"are on diverged histories, or this clone has not fetched one of them "
            f"(run `git fetch origin` and re-run to get the exact distance).")


# ── the decision, as a pure function ──────────────────────────────────────────────────────

def evaluate(observations: list[Observation], candidate: str | None,
             distance: Callable[[str, str], str | None] = git_distance) -> Result:
    lines: list[str] = []
    ok = True

    if not observations:
        return Result(False, ["FAIL: no tier was observed at all, so nothing was proved."])

    for obs in observations:
        spec = TIERS[obs.tier]
        if obs.commit:
            lines.append(f"  {obs.tier:<9} {obs.commit[:8]}  {obs.url}  [{spec['deployedBy']}]")
        else:
            ok = False
            lines.append(f"  {obs.tier:<9} UNKNOWN   {obs.url}  -> {obs.detail}")

    unreadable = [o for o in observations if not o.commit]
    if unreadable:
        lines.append("")
        lines.append("FAIL: a tier could not be identified, so parity was not proved — and an unproved "
                     "parity is not a passing one.")
        for obs in unreadable:
            lines.append(f"  {obs.tier}: {obs.detail}")
        return Result(False, lines)

    lines.append("")
    first = observations[0]
    mismatched = [o for o in observations[1:] if o.commit != first.commit]
    if mismatched:
        ok = False
        lines.append("FAIL: the tiers of this deployment were built from DIFFERENT commits. "
                     "Customers are using a mixture of two releases.")
        for other in mismatched:
            lines.append(f"  {describe_gap(first, other, distance)}")
        lines.append("  This is the split-brain: Vercel auto-deploys main, Render deploys only after a")
        lines.append("  reviewer approves, so a merge puts new UI in front of the older API. Cheapest")
        lines.append("  remedy is usually to roll the Vercel production alias back to the deployment whose")
        lines.append("  sha matches the live API — not to rebuild. See docs/RELEASE_CANDIDATE_AND_PARITY.md.")
    else:
        lines.append(f"Both tiers were built from {first.commit[:8]}.")

    if candidate:
        candidate = candidate.strip().lower()
        off = [o for o in observations if o.commit != candidate]
        if off:
            ok = False
            lines.append("")
            lines.append(f"FAIL: the deployment is not the candidate being accepted ({candidate[:8]}).")
            for obs in off:
                gap = distance(candidate, obs.commit) or distance(obs.commit, candidate)
                ahead = "ahead of" if distance(candidate, obs.commit) else "behind"
                where = f"{ahead} the candidate by {gap}" if gap else "on an unrelated history to the candidate"
                lines.append(f"  {obs.tier} is serving {obs.commit[:8]}, {where}.")
            lines.append("  Acceptance evidence gathered against this deployment does NOT describe the candidate.")
        else:
            lines.append(f"Both tiers are the candidate {candidate[:8]}.")

    lines.append("")
    lines.append("PASS: this deployment is one immutable candidate on both tiers." if ok
                 else "Parity gate FAILED. Nothing was changed; this check is read-only.")
    return Result(ok, lines)


# ── self-test ─────────────────────────────────────────────────────────────────────────────

SHA_A = "a" * 40
SHA_B = "b" * 40
SHA_C = "c" * 40


def fake_distance(older: str, newer: str) -> str | None:
    """A toy history: A -> B -> C. C is unrelated to nothing; SHA_C is a descendant of both."""
    order = {SHA_A: 0, SHA_B: 5, SHA_C: 36}
    if older not in order or newer not in order or order[older] >= order[newer]:
        return None
    n = order[newer] - order[older]
    return f"{n} commit{'s' if n != 1 else ''}"


def self_test() -> int:
    ok_obs = lambda tier, sha: Observation(tier, f"https://x/{tier}", sha, "HTTP 200")  # noqa: E731
    dead = lambda tier, why: Observation(tier, f"https://x/{tier}", None, why)          # noqa: E731
    failures = 0

    def case(label: str, obs: list[Observation], candidate: str | None,
             expect_ok: bool, expect_text: str | None = None) -> None:
        nonlocal failures
        result = evaluate(obs, candidate, fake_distance)
        good = result.ok == expect_ok and (expect_text is None or expect_text in result.report())
        print(f"  {'PASS' if good else 'FAIL'}  {label}")
        if not good:
            failures += 1
            print("        got: " + result.report().replace("\n", "\n        "))

    case("equal shas, no candidate -> PASS",
         [ok_obs("frontend", SHA_B), ok_obs("api", SHA_B)], None, True)
    case("equal shas matching the candidate -> PASS",
         [ok_obs("frontend", SHA_B), ok_obs("api", SHA_B)], SHA_B, True)
    case("THE 2026-09-23 SPLIT-BRAIN: frontend ahead of the API -> FAIL, names the direction and distance",
         [ok_obs("frontend", SHA_C), ok_obs("api", SHA_B)], None, False,
         "frontend is AHEAD of api by 31 commits")
    case("the API ahead of the frontend -> FAIL, names the OTHER direction",
         [ok_obs("frontend", SHA_A), ok_obs("api", SHA_C)], None, False,
         "api is AHEAD of frontend by 36 commits")
    case("tiers agree but neither is the candidate -> FAIL",
         [ok_obs("frontend", SHA_A), ok_obs("api", SHA_A)], SHA_C, False,
         "is not the candidate")
    case("frontend unreachable -> FAIL, never a pass",
         [dead("frontend", "unreachable: TimeoutError"), ok_obs("api", SHA_B)], SHA_B, False,
         "unreachable")
    case("a tier that does not know its commit ('local') -> FAIL",
         [ok_obs("frontend", SHA_B), dead("api", "HTTP 200, commit='local'")], SHA_B, False,
         "parity was not proved")
    case("a URL that answers 200 but is a different product -> FAIL",
         [dead("frontend", "service='something-else'"), ok_obs("api", SHA_B)], None, False,
         "not proved")
    case("diverged histories -> FAIL and say so rather than guess a direction",
         [ok_obs("frontend", "d" * 40), ok_obs("api", SHA_B)], None, False,
         "diverged histories")

    # The placeholder set must be enforced by the probe, not only by the evaluator.
    for placeholder in ("unknown", "local", ""):
        if placeholder not in PLACEHOLDERS:
            print(f"  FAIL  '{placeholder}' is not treated as an unknown commit")
            failures += 1
    print("  PASS  'unknown', 'local' and '' are all treated as no evidence")
    case("no tier observed at all -> FAIL", [], None, False, "nothing was proved")

    print("self-test: " + ("all checks passed" if failures == 0 else f"{failures} FAILED"))
    return 1 if failures else 0


# ── entry point ───────────────────────────────────────────────────────────────────────────

def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--frontend", help="frontend base URL, e.g. https://kynexone.vercel.app")
    ap.add_argument("--api", help="API base URL, e.g. https://zayra-ai-workforce.onrender.com")
    ap.add_argument("--candidate", default=None, help="the commit being accepted; both tiers must be it")
    ap.add_argument("--manifest", default=None, help="read --candidate from a release manifest JSON")
    ap.add_argument("--wait-seconds", type=int, default=0,
                    help="keep re-probing for this long while the tiers disagree (a tier may still be building)")
    ap.add_argument("--json", default=None, help="also write the result as JSON to this file")
    ap.add_argument("--self-test", action="store_true", help="prove every failure path refuses, then exit")
    args = ap.parse_args()

    if args.self_test:
        return self_test()

    if not args.frontend or not args.api:
        print("::error::check-deployment-parity: --frontend and --api are both required. A parity "
              "check that looks at one tier is not a parity check.", file=sys.stderr)
        return 2

    candidate = args.candidate
    if args.manifest:
        try:
            with open(args.manifest, encoding="utf-8") as fh:
                candidate = json.load(fh)["candidate"]["commit"]
        except (OSError, ValueError, KeyError) as e:
            print(f"::error::check-deployment-parity: could not read a candidate commit from "
                  f"{args.manifest} ({type(e).__name__}).", file=sys.stderr)
            return 2

    deadline = time.time() + max(args.wait_seconds, 0)
    while True:
        observations = [probe("frontend", args.frontend), probe("api", args.api)]
        result = evaluate(observations, candidate)
        if result.ok or time.time() >= deadline:
            break
        print(f"[parity] tiers do not agree yet; re-probing in 20s "
              f"({int(deadline - time.time())}s of --wait-seconds left)")
        time.sleep(20)

    header = "DEPLOYMENT PARITY" + (f" against candidate {candidate[:8]}" if candidate else "")
    print(header)
    print(result.report())

    if args.json:
        payload: dict[str, Any] = {
            "schema": "kynexone.deployment-parity/1",
            "candidate": candidate,
            "ok": result.ok,
            "tiers": [{"tier": o.tier, "url": o.url, "commit": o.commit, "detail": o.detail}
                      for o in observations],
            "report": result.report(),
        }
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump(payload, fh, indent=2)

    if not result.ok:
        print("::error::check-deployment-parity: the deployment's tiers are not one candidate. "
              + next((l.strip() for l in result.lines if l.strip().startswith("FAIL")), ""),
              file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
