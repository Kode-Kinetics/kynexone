# Repository and Deployment Governance

**Established:** 2026-08-26 (Wave 1, Phase A)
**Applies from:** `main` @ `f23a0009` (Wave 0 merge)
**Status:** Repository protection ACTIVE and proven. Production deployment gated. Two known gaps recorded below.

> This document records controls that are **configured and tested**, not intended. Every "active" claim
> below was verified against the live GitHub API or by attempting the action and being refused.

---

## 1. Why this exists

Before Wave 1, a merge to `main` did three things with no human in the loop and no way to tell whether
they worked:

1. Applied EF migrations directly to the **production database**.
2. Fired a Render deploy hook.
3. Reported **success** — because the deploy job POSTed a webhook and exited `0`.

That third point is the one that mattered. A green "Deploy Backend (Render)" meant *"Render accepted the
request"*, never *"the new code is serving traffic"*. A failed build, a crash loop, or a deploy that never
started were all indistinguishable from success.

Wave 1's baseline verification caught exactly that: `main` was green, every check passed, and production
was still running **older code**. See §5.

---

## 2. Branch protection — ACTIVE, PROVEN

Repository ruleset **`main-protection`** (id `21530403`), enforcement `active`, targeting the default
branch, with **zero bypass actors** — it applies to repository administrators too.

| Control | Rule | State |
|---|---|---|
| Pull request required | `pull_request` | active |
| Direct pushes blocked | implied by `pull_request` | **proven** — see below |
| Force pushes blocked | `non_fast_forward` | active |
| Branch deletion blocked | `deletion` | active |
| Conversation resolution required | `required_review_thread_resolution: true` | active |
| Stale approvals dismissed on new commits | `dismiss_stale_reviews_on_push: true` | active |
| Required status checks (8) | `required_status_checks`, `strict: true` | active |
| Admins cannot bypass | `bypass_actors: []` | active |
| Merge commits permitted | `allowed_merge_methods: [merge, squash, rebase]` | active — engineering evidence references individual commits |

**Required status checks (all 8 must pass, and the branch must be up to date):**

- Backend Tests (security gate)
- Schema Gates (drift, fresh deploy, upgrade)
- Frontend Production Build
- Frontend Typecheck
- Secret Scan (gitleaks)
- Dependency Vulnerability Scan
- Analyze (csharp) — CodeQL
- Analyze (javascript-typescript) — CodeQL

`Chrome Security Gate` is to be added to this list the moment it exists (Wave 1 Gate 0, Phase B3).

### Proof, not assertion

A real commit was created and pushed directly at `main` by an account with `admin: true`:

```
remote: - Changes must be made through a pull request.
remote: - 8 of 8 required status checks are expected.
 ! [remote rejected] HEAD -> main (push declined due to repository rule violations)
```

The probe commit was then discarded and the worktree restored to `f23a0009`.

### Deliberate deviation: zero required approvals

`required_approving_review_count` is **0**, not 1.

This is a conscious trade-off, recorded rather than hidden. The repository currently has a single
maintainer. Requiring one approving review would make it impossible for that person to merge their own
pull requests, which in practice leads to the protection being switched off — a control that blocks all
work does not survive contact with a deadline.

Everything that does not depend on a second human is enforced: no direct pushes, no force pushes, no
deletion, all eight gates green, branch up to date, threads resolved.

**Owner action before pilot:** add a second reviewer and raise
`required_approving_review_count` to `1`. Tracked as a release blocker in §6.

---

## 3. Production deployment — GATED

### Target model

```
pull request
  → tests / security / browser gates
  → merge to main
  → immutable image build (GHCR)
  → [staging deploy + smoke]        ← NOT YET IMPLEMENTED, see §6
  → protected `production` environment
  → explicit human approval
  → migration
  → deploy
  → health and commit verification
```

### Protected environment — ACTIVE

GitHub environment **`Production`** (GitHub matches environment names case-insensitively, so
`environment: production` in the workflow resolves to it):

| Control | State |
|---|---|
| `required_reviewers` | `kodekinetics79` |
| `deployment_branch_policy` | `protected_branches: true` — only protected branches may deploy |
| Production secrets scoped to this environment | see §6, owner action |

Two jobs now declare `environment: production` and therefore **cannot run without approval**:

- `migrate-backend` — writes to the production database
- `deploy-backend` — ships the release

### Pull requests never deploy production

Both jobs are additionally guarded by
`if: github.ref == 'refs/heads/main' && github.event_name == 'push'`. A pull request cannot reach either
one, regardless of environment approval.

### Deployment now tells the truth

`deploy-backend` gained three steps:

1. **Record currently-deployed commit** — captured *before* the release goes out, because afterwards
   nothing else can answer "what was running a minute ago?". Render builds from the branch, so there is
   no image digest on its side to roll back to. The value is written to the job summary as the rollback
   target.
2. **Trigger the hook** — unchanged, still `curl -sf` so an HTTP error is a non-zero exit.
3. **Verify the deployed commit is actually serving traffic** — polls `/health/live` for up to 15 minutes
   until its `commit` field equals `github.sha`, then requires `/health/ready` to return 200. A timeout,
   a mismatch, or a non-ready service **fails the job**.

This depends on `/health/live` exposing the deployed commit via `RENDER_GIT_COMMIT`, which is present in
`main` @ `f23a0009` but was **not** present in the code running in production at the time of writing —
which is how the stale deployment in §5 was detected in the first place.

### Rollback

`docs/DEPLOY_ROLLBACK_RUNBOOK.md` covers the procedure. `build-image` now also records the immutable
image digest in the job summary so a rollback can name an exact artifact rather than a tag that has since
moved.

---

## 4. Repository variables and secrets

| Name | Kind | Purpose |
|---|---|---|
| `PRODUCTION_BASE_URL` | repository **variable** (public, non-secret) | Target for deploy verification and health probes |
| `RENDER_DEPLOY_HOOK_URL` | secret | Render deploy trigger |

No secret value is echoed by any step. The verification steps print only the commit SHA being waited for
and the SHA observed.

---

## 5. INCIDENT — production was running stale code while `main` was green

**Detected:** 2026-08-26 ~04:33Z, during Wave 1 Phase A1 baseline verification.

**Observed.** `main` @ `f23a0009`, CI and both CodeQL analyses green, `Apply DB Migrations` green,
`Deploy Backend (Render)` green. Live service responses:

| Endpoint | Committed `main` produces | Production actually returned |
|---|---|---|
| `/health/live` | `{status, utc, service, commit}` | `{status, utc, service}` — **no `commit`** |
| `/health/ready` | `ReadinessEvidence` incl. `pendingMigrations`, `queues`, `workers` | `{status, utc, dependencies, tenants, activeTenants}` — **none of those fields** |

The response *shapes* do not match the committed source, so production was running an older build. The
migration job had already applied `20260816200703_AddLiveSeparationUniqueIndex` to the production
database, so at that moment **the schema was ahead of the code** — which is the intended direction, and
why `/health/ready` gating on pending migrations matters.

**Root cause.** `deploy-backend` fired the webhook (`deploy id dep-da76i4942hec73afcm90`) and exited
successfully without waiting for or verifying the outcome. Whether the Render build was still running,
had failed, or had never started is not distinguishable from the job's own output.

**Fix.** The verification step in §3. With it, this incident would have failed the job rather than
reporting green.

**Residual.** The Render build outcome for `dep-da76i4942hec73afcm90` cannot be read from CI. Confirming
it requires the Render dashboard or an API key, which is an owner action — see §6.

---

## 6. Known gaps — none of these are claimed as complete

| # | Gap | Impact | Owner action |
|---|---|---|---|
| G-1 | **No staging environment.** The target model calls for staging auto-deploy plus smoke before production. No staging service exists. | Production approval is the only gate between merge and customers. | Provision a staging Render service + database; add a `staging` environment with no approval requirement. |
| G-2 | **Image is built, not deployed.** `build-image` publishes an immutable GHCR image per commit, but `render.yaml` builds from the branch. The artifact that is verified is not the artifact that runs. | "Deploy the exact image whose migration passed" is not achievable today. | Switch the Render service to deploy the GHCR image by digest, or accept branch builds and drop the image job. |
| G-3 | **Required approvals = 0.** See §2. | A single maintainer can merge unreviewed. | Add a second reviewer, set count to 1. |
| G-4 | **Production secret scoping unverified.** `RENDER_DEPLOY_HOOK_URL` is a repository secret, not an environment secret, so jobs outside `production` could reference it. | Weakens the environment boundary. | Move production secrets to the `Production` environment scope. |
| G-5 | **Render deploy status not observable from CI.** No Render API key is available to the pipeline. | Verification is indirect (health polling) rather than authoritative. | Add `RENDER_API_KEY` as a `Production` environment secret and poll the deploy status API directly. |
| G-6 | **Chrome security gate not yet a required check.** Being built in Wave 1 Gate 0 Phase B3. | Role/isolation regressions are not blocked at merge. | Add to the ruleset once the job name is stable. |

---

## 7. Release tag

`wave0-engineering-baseline-2026-08-26` — annotated, on the Wave 0 merge commit.

The tag message states explicitly that this is an **engineering baseline only**: not pilot-certified,
not production-certified, and that Wave 1 operational durability is incomplete.
