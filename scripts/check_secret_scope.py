#!/usr/bin/env python3
"""GATE: production credentials must be readable ONLY inside the `production` environment.

WHAT IS WRONG TODAY
-------------------
Two jobs in .github/workflows/ci.yml say `environment: production`, and both carry a comment
explaining that this makes them safe. `migrate-backend` writes to the production database.
`deploy-backend` triggers the Render deploy hook. The `production` environment requires a named
reviewer's approval, so the reading is: a human approves before either credential can be used.

That reading is wrong, and the comments in ci.yml already admit it. An environment approval gates
the JOB, not the CREDENTIAL. `PROD_DATABASE_URL`, `RENDER_DEPLOY_HOOK_URL` and `RENDER_API_KEY`
are REPOSITORY secrets -- `gh secret list --env production` returns nothing -- so ANY job, in ANY
workflow, on ANY branch can reference them with no approval at all.

Which matters here because of three facts that only bite in combination:

  1. `Kode-Kinetics/kynexone` is a PUBLIC repository and forking is enabled.
  2. ci.yml runs `on: pull_request`, and a pull request from a SAME-REPO branch runs the version
     of the workflow on that branch -- including any edits the branch makes to the workflow.
  3. The `main` ruleset requires 0 approving reviews.

So a branch that adds one step to ci.yml -- a step with no `environment:` -- gets
`PROD_DATABASE_URL` handed to it by GitHub, with no approval, before any human looks at the pull
request. The database credential's only real protection is that nobody has tried.

(Fork PRs are NOT the exposure: GitHub withholds secrets from `pull_request` runs originating in
a fork. That is also why this gate cannot observe anything on a fork PR -- see CONTEXTS.)

WHAT THIS GATE DOES
-------------------
It runs in a job that deliberately has NO `environment:` key, and asks GitHub for each watched
secret. If GitHub hands one over, that secret is readable outside `production` and the gate
fails, naming it. Once the secrets are moved onto the `production` environment and the repository
copies are deleted, this job sees nothing and passes -- and it keeps passing only for as long as
that stays true. Re-adding a repository copy turns the gate red on the next run.

It never prints, logs, compares, hashes or measures a secret VALUE. The only thing it computes
from a secret is whether it is the empty string, and the only thing it prints is the NAME.

CONTEXTS
--------
A run can be in one of three positions, and only two of them can see anything:

    push            authoritative. Secrets are available; if one is visible here, it is
                    repository-scoped. This is the run to trust.
    same-repo-pr    authoritative, and the exact shape of the exposure described above.
    fork-pr         NOT authoritative. GitHub withholds ALL secrets from fork pull requests, so
                    every probe reads empty whatever the real configuration is. The gate says so
                    in as many words and exits 0, because failing every fork PR would teach
                    people to ignore it. It does not claim to have passed.

USAGE
    scripts/check_secret_scope.py --self-test
        Runs the tests below, including the 2026-09-23 misconfiguration. No network, no secrets.

    SCOPE_CONTEXT=push PROBE_PROD_DATABASE_URL=... scripts/check_secret_scope.py
        The real check. The workflow supplies one PROBE_<NAME> per watched secret; see
        WATCHED_SECRETS.

EXIT CODES
    0  no watched secret was readable outside `production` (or: a fork PR, which is stated)
    1  at least one watched secret IS readable outside `production` (the names are printed)
    2  the gate could not run -- unknown context, or a probe the workflow forgot to wire.
       Never a silent pass.
"""

from __future__ import annotations

import argparse
import os
import sys

# Secret name -> why it must not be readable without an approval.
#
# Add a secret here when it can change or read production. Do NOT add a secret here that a job
# outside `production` legitimately needs -- that job would break, and the honest fix is to
# narrow the job, not to drop the secret from this list.
WATCHED_SECRETS: dict[str, str] = {
    "PROD_DATABASE_URL": (
        "full read/write credential for the production database. Used by migrate-backend to run "
        "`dotnet ef database update`; anything holding it can also drop every tenant's data."
    ),
    "RENDER_DEPLOY_HOOK_URL": (
        "a bearer URL: POSTing to it deploys whatever is on the service's branch. autoDeploy is "
        "off precisely so that CI is the only trigger, and this URL is that trigger."
    ),
    "RENDER_API_KEY": (
        "Render API access to srv-d8slkb77f7vs73d2k92g. check_render_env.py reads env-var NAMES "
        "with it, but the key itself is not read-only -- it can rewrite the service."
    ),
}

VALID_CONTEXTS = ("push", "same-repo-pr", "fork-pr")

PROBE_PREFIX = "PROBE_"


def classify(context: str, probes: dict[str, str | None]) -> tuple[int, list[str], list[str]]:
    """Return (exit_code, leaked, unwired).

    `probes` maps a watched secret name to the raw probe value, or None when the workflow did not
    set the probe variable at all. GitHub always sets a wired probe -- to the empty string when
    the secret does not exist -- so None means the WORKFLOW is wrong, which is a cannot-run, not
    a pass.
    """
    if context not in VALID_CONTEXTS:
        return 2, [], []

    unwired = sorted(name for name, value in probes.items() if value is None)
    if unwired:
        return 2, [], unwired

    if context == "fork-pr":
        return 0, [], []

    leaked = sorted(name for name, value in probes.items() if (value or "").strip())
    return (1 if leaked else 0), leaked, []


def read_probes() -> dict[str, str | None]:
    return {name: os.environ.get(PROBE_PREFIX + name) for name in WATCHED_SECRETS}


def run(context: str, probes: dict[str, str | None]) -> int:
    code, leaked, unwired = classify(context, probes)

    print(f"Context             : {context}")
    print(f"Watched secrets     : {len(WATCHED_SECRETS)}")
    print("This job declares no `environment:`, so anything visible here needs no approval.")
    print("")

    if code == 2 and context not in VALID_CONTEXTS:
        print(f"::error::check_secret_scope: SCOPE_CONTEXT is {context!r}; expected one of "
              f"{', '.join(VALID_CONTEXTS)}. Cannot tell whether secrets are observable in this "
              f"run, so the result would be meaningless. Failing closed.", file=sys.stderr)
        return 2

    if unwired:
        print("::error::check_secret_scope: the workflow did not set a probe variable for "
              "every watched secret, so their scope is UNKNOWN, not clean. Failing closed.",
              file=sys.stderr)
        for name in unwired:
            print(f"    NOT WIRED: {PROBE_PREFIX}{name}", file=sys.stderr)
        print("", file=sys.stderr)
        print("  Each watched secret needs a line in the job's `env:` block:", file=sys.stderr)
        print(f"      {PROBE_PREFIX}<NAME>: ${{{{ secrets.<NAME> }}}}", file=sys.stderr)
        return 2

    if context == "fork-pr":
        print("::notice::check_secret_scope: this is a pull request from a FORK. GitHub "
              "withholds every secret from fork runs, so each probe reads empty regardless of "
              "how the secrets are really scoped. This run has verified NOTHING. The push-to-main "
              "and same-repo-PR runs are the authoritative ones.")
        return 0

    if not leaked:
        print("PASS - no watched secret was readable from a job without `environment: production`.")
        print("")
        for name in sorted(WATCHED_SECRETS):
            print(f"    scoped: {name}")
        return 0

    print("", file=sys.stderr)
    print(f"::error::check_secret_scope: {len(leaked)} production credential(s) were handed to a "
          f"job that NO ONE APPROVED. Repository-scoped, not environment-scoped.", file=sys.stderr)
    print("", file=sys.stderr)
    for name in leaked:
        print(f"    READABLE WITHOUT APPROVAL: {name}", file=sys.stderr)
        print(f"        {WATCHED_SECRETS[name]}", file=sys.stderr)
    print("", file=sys.stderr)
    print("  ci.yml puts `environment: production` on migrate-backend and deploy-backend, and", file=sys.stderr)
    print("  that approval gates the JOB. It does not gate the CREDENTIAL. While these secrets", file=sys.stderr)
    print("  live at repository scope, any job in any workflow on any branch can read them --", file=sys.stderr)
    print("  including a workflow edited by the same pull request that runs it, in a PUBLIC repo", file=sys.stderr)
    print("  whose main ruleset requires zero approving reviews.", file=sys.stderr)
    print("", file=sys.stderr)
    print("  FIX (order matters -- see docs/DEPLOY_HARDENING.md):", file=sys.stderr)
    print("    1. Settings -> Environments -> production -> Add environment secret, for each name.", file=sys.stderr)
    print("    2. Only then Settings -> Secrets and variables -> Actions -> delete the", file=sys.stderr)
    print("       repository-level copy of each.", file=sys.stderr)
    print("  Doing 2 before 1 leaves migrate-backend and deploy-backend with no credential at", file=sys.stderr)
    print("  all, which strands main: merged, migrated nowhere, deployed nowhere.", file=sys.stderr)
    print("", file=sys.stderr)
    return 1


# ──────────────────────────────────────────────────────────────────────────────
# Self-test -- the gate must be seen to FAIL on the configuration that exists today
# ──────────────────────────────────────────────────────────────────────────────
def self_test() -> int:
    failures: list[str] = []

    def check(name: str, got, want) -> None:
        if got != want:
            failures.append(f"{name}\n      expected: {want!r}\n      got     : {got!r}")
        else:
            print(f"  ok  {name}")

    print("check_secret_scope self-test")
    print("")

    all_scoped = {name: "" for name in WATCHED_SECRETS}

    # 1. THE REGRESSION TEST. The 2026-09-23 state: the secrets are repository-wide, so an
    #    unapproved job receives them. If this ever stops failing, the gate has stopped working.
    todays_state = dict(all_scoped)
    todays_state["PROD_DATABASE_URL"] = "postgres://redacted-by-the-test"
    todays_state["RENDER_DEPLOY_HOOK_URL"] = "https://redacted-by-the-test"
    check("CATCHES TODAY: repository-scoped secrets reach an unapproved job",
          classify("push", todays_state),
          (1, ["PROD_DATABASE_URL", "RENDER_DEPLOY_HOOK_URL"], []))

    # 2. And passes once they are environment-scoped -- otherwise it is a gate that can only fail.
    check("passes once every secret is environment-scoped",
          classify("push", all_scoped), (0, [], []))

    # 3. A same-repo PR is the exposure this gate exists for, and is equally authoritative.
    check("a same-repo PR is authoritative and fails the same way",
          classify("same-repo-pr", todays_state)[0], 1)

    # 4. A fork PR can observe nothing. It must not be reported as a pass of the check, but it
    #    must not fail either: GitHub withholds secrets from fork runs by design.
    check("a fork PR observes nothing and does not fail",
          classify("fork-pr", todays_state), (0, [], []))

    # 5. Whitespace is not a value. A secret set to "   " is still set, but a probe carrying only
    #    the runner's own whitespace must not be read as a leak.
    check("a whitespace-only probe is not a leak",
          classify("push", {**all_scoped, "RENDER_API_KEY": "  \n "})[0], 0)

    # 6. FAIL CLOSED: a probe the workflow forgot is UNKNOWN scope, not clean scope. This is the
    #    failure mode that turns a gate off silently -- someone adds a secret to WATCHED_SECRETS
    #    and not to the workflow, and the gate goes green having checked one fewer thing.
    half_wired = dict(all_scoped)
    half_wired["RENDER_API_KEY"] = None
    check("CANNOT RUN when a probe is unwired (not a pass)",
          classify("push", half_wired), (2, [], ["RENDER_API_KEY"]))

    # 7. FAIL CLOSED: an unrecognised context. A typo in the workflow expression must not
    #    silently become "fork-pr, nothing to see".
    check("CANNOT RUN on an unknown context", classify("", all_scoped), (2, [], []))
    check("CANNOT RUN on a misspelled context", classify("pull_request", all_scoped), (2, [], []))

    # 8. Every watched secret carries a reason. An entry with no reason is an entry nobody can
    #    evaluate when it starts failing.
    check("every watched secret states why it is watched",
          sorted(n for n, why in WATCHED_SECRETS.items() if not why.strip()), [])

    # 9. The gate must never emit a value. Exercise the full reporting path with a distinctive
    #    fake and assert it appears in NOTHING the gate wrote.
    import io
    import contextlib
    canary = "CANARY-VALUE-MUST-NEVER-BE-PRINTED"
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        run("push", {**all_scoped, "PROD_DATABASE_URL": canary})
    printed = out.getvalue() + err.getvalue()
    check("the gate never prints a secret value", canary in printed, False)
    check("but it does name the secret", "PROD_DATABASE_URL" in printed, True)

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
    ap.add_argument("--self-test", action="store_true",
                    help="prove the gate can fail, then exit. No network, no secrets.")
    ap.add_argument("--context", default=os.environ.get("SCOPE_CONTEXT"),
                    help=f"one of {', '.join(VALID_CONTEXTS)} (default: $SCOPE_CONTEXT)")
    args = ap.parse_args()

    if args.self_test:
        return self_test()

    if args.context is None:
        print("::error::check_secret_scope: no --context and no SCOPE_CONTEXT. The gate cannot "
              "tell whether secrets are observable in this run. Failing closed.", file=sys.stderr)
        return 2

    return run(args.context, read_probes())


if __name__ == "__main__":
    sys.exit(main())
