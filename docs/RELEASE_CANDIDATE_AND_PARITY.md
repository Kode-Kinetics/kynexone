# Release Candidate, Deployment Parity & Two-Tier Rollback (F11)

A release is **one commit on both tiers**. This document says how to record what a commit is, how
to prove a deployment is that commit end to end, and how to get back when it is not.

Backend-only rollback mechanics (migrations, `/health/ready`, the Render manual deploy) live in
[`DEPLOY_ROLLBACK_RUNBOOK.md`](DEPLOY_ROLLBACK_RUNBOOK.md) and are not repeated here.

## The problem this closes

Acceptance evidence used to span different source trees and different local stacks, so nobody could
show that a client-facing build contained a given fix, or that the UI and the API in front of a
customer were the same accepted version.

That is not a paperwork problem. **The two tiers deploy by different rules:**

| Tier | Deploys when | Gate |
|---|---|---|
| Frontend (Vercel) | every push to `main` | none — automatic |
| Backend (Render) | CI's `deploy-backend` POSTs the hook | a reviewer must approve the `production` environment |

So a merge to `main` puts new UI in front of customers **immediately**, while the API behind it
stays on whatever was last approved. Each tier is healthy. Each reports itself correctly. Nothing
says they disagree. That is the 2026-09-23 split-brain, and as this is written it is the live state
of the pilot: the frontend serves `64bab568` and the API serves `d5346758`, 36 commits behind,
because the Batch E+F backend deploy is still awaiting approval.

## The two artefacts

### `scripts/release_manifest.py` — what a candidate IS

```bash
./scripts/release_manifest.py --commit <sha> --out release-manifest.json
./scripts/release_manifest.py --self-test        # proves the redaction guard refuses
```

One JSON document per commit: source commit and subject; the backend image ref and immutable
digest; the assembly informational version `/health/live` will report; the frontend's expected
`/build-info` commit; the full EF migration set the build contains and its latest id; the **names**
of every switch that gates customer-visible behaviour; and GitHub's own record of the required
checks' conclusions for that exact sha.

Everything is read from `git` at the commit, never from the worktree, so a dirty checkout cannot
leak into a candidate record.

**Config names, never config values.** Gate values live in the deployment's environment. The
generator records the name, what it gates, the build's own default and the file that proves the
name still exists — and `assert_no_secrets` refuses to emit a document carrying a credential
rather than emitting a suspect one. The gate registry is re-verified against the tree on every run,
so a rename fails generation instead of quietly leaving a live switch unnamed.

CI runs this in `build-image`, where the digest exists, and uploads
`release-manifest-<sha>` for 90 days.

### `scripts/check_deployment_parity.py` — whether a deployment IS that candidate

```bash
./scripts/check_deployment_parity.py \
  --frontend https://kynexone.vercel.app \
  --api https://zayra-ai-workforce.onrender.com \
  --candidate <sha>
```

Two anonymous GETs, no auth, no data: `<frontend>/build-info` and `<api>/health/live`. Both values
are fixed at **build** time, so they identify the bundle the browser was served and the image the
API is running — not the host that answered.

It exits non-zero and names the direction and the distance:

```
FAIL: the tiers of this deployment were built from DIFFERENT commits.
  frontend is AHEAD of api by 36 commits (64bab568 contains d5346758 plus 36 commits).
```

Unreachable, wrong service, or a build that reports `unknown`/`local` are all **failures**. A check
that cannot see a tier has not proved parity; it has proved nothing.

CI runs it at the end of `deploy-backend`, after the backend commit is verified, with
`--wait-seconds 300` so a frontend still building is not called a mismatch. It needs
`vars.PRODUCTION_FRONTEND_URL`; without it the step fails closed rather than passing blind.

## Rollback

### Which tier is wrong?

Run the parity checker first. It answers the only question that matters at 2am — *which* tier is
ahead, and by how much — before anything is changed.

### Frontend (Vercel)

**Do not rebuild.** Every previous production build is still deployed and addressable; rolling back
is re-pointing the production alias, which takes seconds and cannot fail on a build error.

1. Find the deployment whose commit matches the live API:
   `vercel ls <project>` (or Vercel dashboard → Deployments; each row shows its commit).
2. Promote it: Vercel dashboard → that deployment → **Instant Rollback** / **Promote to
   Production**, or `vercel promote <deployment-url>`.
3. Re-run the parity checker. It must now pass with `--candidate <the live API's commit>`.

Note that `main` will re-deploy the newer frontend on the next push, so a rollback here is a
holding action while the backend is approved, not a fix.

### Backend (Render)

See [`DEPLOY_ROLLBACK_RUNBOOK.md`](DEPLOY_ROLLBACK_RUNBOOK.md) §Rollback: Render dashboard →
**Manual Deploy → a previous, known-good image**, then confirm `/health/ready` is
`{"status":"ready","pendingMigrations":0}`.

A deploy costs roughly 38 seconds of 502 (there is no instance overlap on the 10 GB disk) and a
*failed* one costs about 16 minutes. **Do not diagnose by redeploying.**

### What the migration means for a rollback

**Additive columns can stay.** Migrations are additive-only where possible, and the older code
ignores new nullable columns, so almost every rollback is a code rollback with the schema left
where it is. That is the cheap, safe path and it is the default.

Only reverse a migration when the new schema is *actively harmful* — a dropped or rewritten column,
a constraint the old code violates. Then:

```bash
dotnet ef migrations list --project backend-dotnet/Zayra.Api/Zayra.Api.csproj
dotnet ef database update <PreviousMigrationName> --project backend-dotnet/Zayra.Api/Zayra.Api.csproj
```

The manifest's `migrations.ids` is what tells you which migrations the outgoing build contained and
which the incoming one does not — `latestId` of the target candidate is the name to roll back *to*.

Schema leads code, in both directions: never promote an image whose migrations are not applied
(`/health/ready` returns 503 and blocks it), and never remove a column the currently-serving build
still writes.

### Cheapest remedy for a split-brain

**Roll the Vercel production alias back to the deployment whose sha matches the live backend.**

It is seconds, it needs no build, it touches no database, and it restores a matched pair rather
than creating a third state. Rebuilding, reverting `main`, or force-deploying the backend are all
slower, riskier and — when the backend deploy is merely *awaiting approval* — unnecessary. Approve
the backend deploy when it is ready, and let both tiers converge forward.

## What none of this proves

- That the candidate is **correct**, only that it is **identified**. The acceptance suites are
  separate evidence, recorded in the manifest as check conclusions.
- Anything about a tier's **configuration**. The manifest names the gates; it deliberately does not
  read their values, so "the right tenants are switched on" is still a human check against
  `docs/saudi-bank-export.md`.
- That a **browser** got what `/build-info` says. A CDN edge can serve a stale HTML document to one
  viewer; `/build-info` is `force-static` and travels with the build, but it is not a per-request
  attestation.
- Parity of anything other than the two tiers named. Workers, the mobile app and any other
  deployment are outside this check.
