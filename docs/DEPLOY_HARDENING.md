# Deploy hardening — the parts only a human can do

Everything else in this branch is code: `render.yaml` now declares the infrastructure the live
service actually has, `scripts/check_render_env.py` compares the blueprint and the service in
**both** directions, and `scripts/check_secret_scope.py` measures whether the production
credentials really need an approval. Those changes ship themselves.

The four things below cannot be done from a branch. Each one needs somebody with the Render
dashboard or the GitHub repository settings open, and three of them are destructive or
irreversible if done in the wrong order.

Facts here were read from the live service and the repository on **2026-09-23**, read-only.
Service `srv-d8slkb77f7vs73d2k92g`, workspace `tea-d8ral9m7r5hc73e4t4n0`.

---

## 1. Move the three production secrets onto the `production` environment

### What is wrong

`.github/workflows/ci.yml` puts `environment: production` on `migrate-backend` (which runs
`dotnet ef database update` against the production database) and on `deploy-backend` (which POSTs
the Render deploy hook). The `production` environment requires a named reviewer. The natural
reading — *a human approves before those credentials can be used* — is false.

**An environment approval gates the job, not the credential.** `PROD_DATABASE_URL`,
`RENDER_DEPLOY_HOOK_URL` and `RENDER_API_KEY` are **repository** secrets; `gh secret list --env
production` returns nothing. Any job, in any workflow, on any branch can reference them with no
approval whatsoever.

Three further facts turn that from untidy into exposed:

| Fact | Consequence |
|---|---|
| `Kode-Kinetics/kynexone` is **public** and forking is enabled | anyone can read the workflow and see exactly which secret names to ask for |
| `ci.yml` has `on: pull_request` | a **same-repo** branch PR runs *that branch's* version of the workflow — including edits the branch makes to the workflow itself |
| the `main` ruleset requires **0** approving reviews | nothing forces a human to look first |

A branch that adds one step with no `environment:` is handed the production database credential
before any human sees the pull request. (Fork PRs are *not* the hole — GitHub withholds secrets
from fork runs. The hole is same-repo branches, i.e. anyone with write access, plus anything that
obtains write access.)

The new `secret-scope-gate` job in `ci.yml` measures this from a deliberately unprotected job and
is **red today**. It goes green when this section is done, and stays green only while it stays
done.

### The click-path

Do these in order. The order matters; see below.

**Step 1 — add each secret to the environment (additive, nothing breaks).**

1. `https://github.com/Kode-Kinetics/kynexone` → **Settings**
2. left sidebar → **Environments** → **production**
3. **Environment secrets** → **Add environment secret**
4. Name: `PROD_DATABASE_URL`. Value: paste the same value the repository secret holds.
   You cannot read the existing repository secret back out of GitHub, so take the value from
   wherever it was originally generated — the Neon dashboard for `PROD_DATABASE_URL`, the Render
   service's **Settings → Deploy Hook** for `RENDER_DEPLOY_HOOK_URL`, and **Account Settings →
   API Keys** for `RENDER_API_KEY`. Do not paste any of them into a terminal, a commit, an issue
   or a chat window.
5. Repeat for `RENDER_DEPLOY_HOOK_URL` and `RENDER_API_KEY`.

**Step 2 — prove the environment copies work, before deleting anything.**

Merge something small to `main`, approve the `production` gate, and watch `migrate-backend` and
`deploy-backend` go green. At this point both copies exist; the environment one wins inside those
jobs, so a green run proves the environment copies are correct.

**Step 3 — delete the repository copies.**

1. **Settings** → **Secrets and variables** → **Actions**
2. **Repository secrets** tab
3. Delete `PROD_DATABASE_URL`, `RENDER_DEPLOY_HOOK_URL`, `RENDER_API_KEY`.

**Step 4 — confirm.**

```bash
gh secret list                          # must NOT list the three
gh secret list --env production         # must list all three
```

The next CI run's `secret-scope-gate` job should go green on its own.

**Step 5 — re-scope any other workflow that used them.** Search first:

```bash
grep -rn "PROD_DATABASE_URL\|RENDER_DEPLOY_HOOK_URL\|RENDER_API_KEY" .github/workflows/
```

At the time of writing only `ci.yml` references them, and only inside jobs that already declare
`environment: production`. If that is still true, nothing else needs changing.

**Step 6 — make it stick.** Once `secret-scope-gate` is green, add `secret-scope-gate` to the
`needs:` list of `migrate-backend` in `ci.yml`. It is deliberately left out today: while it is red
for a reason only an admin can fix, making it block would freeze all releases. Once it is green,
blocking is exactly what you want.

### What breaks if the order is wrong

**Deleting the repository copies before adding the environment copies strands `main`.** The
symptom is specific and worth recognising:

- `migrate-backend`'s "Gate — render.yaml and the Render service declare the same env keys" step
  exits **2** with `RENDER_API_KEY is not set … failing closed`.
- If you got past that, "Apply EF Core migrations" exits 1 with `PROD_DATABASE_URL secret is not
  set — cannot gate the deploy on DB migrations`.
- `deploy-backend` exits 1 with `RENDER_DEPLOY_HOOK_URL secret is not set and autoDeploy is false
  — no deploy will occur`.
- `release-outcome` then fails, which is correct: main has merged and production did not ship.

Nothing is destroyed, but production sits behind `main` until the environment copies are added —
and `autoDeploy` is `false`, so nothing self-heals.

**What does *not* break:** `deploy-preflight`. That job runs only the three gates' `--self-test`
modes, all offline. Verified against `ci.yml` on 2026-09-23 — the job has no `environment:`, no
job-level `env:`, and no step-level `env:`; its three steps are exactly
`./scripts/check_render_env.py --self-test`, `./scripts/check_migration_target.py --self-test` and
`./scripts/check_secret_scope.py --self-test`. It needs no key and is unaffected by this change in
either order.

### While you are in that screen: three undeclared keys on the service

`check_render_env.py` now reports the other direction too, and production currently has three env
vars that `render.yaml` has never mentioned:

| Key | What reads it | What to do |
|---|---|---|
| `PLATFORM_ADMIN_EMAIL` | `PlatformOwnerBootstrap` | **declare it** in `render.yaml` |
| `PLATFORM_ADMIN_PASSWORD` | `PlatformOwnerBootstrap` — mints the platform **owner** | **declare it** `sync: false` |
| `Storage__AllowEphemeral` | *nothing in production code*; only a test fixture names it | **delete it from the service** |

The first two are the ones that matter. A credential governing the most privileged principal in
the system has been live for months while `render.yaml` — the file anyone reads to answer "what
configuration does production have?" — did not know it existed. Declaring it does not weaken
anything (`sync: false` keeps the value in the dashboard); it makes the key reviewable.

`Storage__AllowEphemeral` is a dead escape hatch. `DocumentStorageRegistration.ResolveAndValidate`
does not read it — it fails closed on anything that is not `Storage__Provider=s3`, full stop. A
flag that looks like it can disable a P0 storage guard, set on production, read by nothing, is
worth removing before someone wires it back up.

---

## 2. The 10 GB disk

### Is detaching it safe?

**Probably, but nobody can say so from the repository, and the check costs two minutes.**

What is known:

- The service mounts a 10 GB disk (`dsk-damq6otg1s2s73duicf0`, name `disk`) at `/var/data`.
- **No application code references that path.** `grep -rn "var/data"` over the repository returns
  nothing outside `render.yaml`'s own new comment.
- Uploaded documents go to S3: `Storage__Provider: s3`, and
  `DocumentStorageRegistration.ResolveAndValidate` throws at boot in any non-Development
  environment if the provider is not `s3`. There is no fallback to local disk in production.
- DataProtection keys persist to Postgres, not to the filesystem.

So nothing *should* be on it. "Should" is not evidence, and detaching a Render disk **destroys it
and its contents immediately, with no undo and no snapshot**.

What the disk costs while it stays:

> **Render will not run two instances of a service that share a disk.** The old instance is
> stopped before the new one starts. Every deploy therefore has a hard 502 window — about 38
> seconds when the deploy is healthy, and about 16 minutes when the new instance never becomes
> healthy and Render eventually reverts. Zero-downtime deploys are impossible while the disk is
> attached. This is the single largest cause of user-visible downtime in the current pipeline.

### What a human must check first

```bash
render ssh srv-d8slkb77f7vs73d2k92g
# then, on the instance:
ls -la /var/data
du -sh /var/data
find /var/data -type f | head -50
```

Read the result literally:

- **Empty, or only `lost+found`** → nothing is there. Proceed.
- **Anything else** → stop. Copy it off the instance first (`scp`, or S3), decide what it is, and
  only then come back. Once the disk is detached there is no second chance and no support ticket
  that recovers it.

Capture the `ls -la` output in the change record. It is the only evidence that will exist
afterwards.

### The detach

1. Confirm `/var/data` is empty (above), and paste the output into the PR or change ticket.
2. Remove the `disk:` block from `render.yaml`. Keep the comment above it, rewritten to say when
   it was removed and what `ls` showed.
3. Merge to `main`. **A blueprint sync applies this to the live service** — that is the mechanism,
   not a side effect. Render records it as a `trigger=service_updated` deploy. Two such deploys
   are already in this service's history from 2026-09-23 at 20:27 and 20:28, and *both ended
   `update_failed`* — so expect the sync itself to be a deploy that can fail, and watch it.
4. Watch the deploy.

### Expected evidence afterwards

This is the part worth being precise about, because it is what distinguishes "the disk is gone"
from "overlapping deploys now work", and only the second one is the benefit.

In the Render logs for the *next* deploy after the detach, the new instance's
`Now listening on: http://0.0.0.0:8080` must appear **before** the old instance's
`Application is shutting down...`.

```bash
render logs --resources srv-d8slkb77f7vs73d2k92g --tail --confirm --output text \
  --text "Now listening,Application is shutting down"
```

- **New "Now listening" precedes old "Application is shutting down"** → instances overlapped.
  Zero-downtime deploys are working. This is the success condition.
- **Old "shutting down" still precedes new "Now listening"** → the disk was not the only thing
  serialising deploys. Do not claim the fix; find what else is (a `numInstances` of 1 alone does
  not do this — Render overlaps during a deploy — so look at the health check and the start-up
  time).

Independently confirm from the outside: poll `/health/live` every second across the deploy and
count non-200s. Before the detach that count is non-zero by construction; afterwards it should be
zero.

### If you decide to keep it

That is a legitimate choice — a disk is also the only place a future need for local scratch or a
cache would go. But then the ~38 s 502 window per deploy is a **deliberate, accepted** cost and
should be written down as one, not rediscovered during every incident review.

---

## 3. Manual Deploy

### Can it be disabled or restricted?

**No.** There is no per-service setting that turns off the dashboard's **Manual Deploy** button.
The service's own configuration has no such field. `autoDeploy: false` and
`autoDeployTrigger: off` — both confirmed on the live service — govern *automatic* deploys from
the repository. Neither touches the button; that is precisely why the button is a bypass.

The nearest thing Render offers is workspace member roles, and it does not help here:

- This workspace (`tea-d8ral9m7r5hc73e4t4n0`, "My Workspace") is a single-owner workspace. There
  is one Admin, and that Admin is the person who would be clicking the button.
- Role-based restrictions are an Organization/Enterprise feature; environment protection applies
  to services inside a Render **Project**, and this service is not in one.

So the control cannot be technical. It has to be a written rule plus detection.

### The written rule

> **Manual Deploy is for one situation only: production is down and CI cannot ship the fix.**
>
> Manual Deploy bypasses *every* gate in `ci.yml` — the test suite, the security gates, the
> schema gates, the `production` approval, and the `migrate-backend` job that applies EF
> migrations. A manual deploy therefore ships code against **whatever schema the database
> happens to have**. That is exactly what happened on 2026-09-23 — un-migrated code reached
> production this way — and the deploy history below shows the button was used four times in
> twelve hours, not once in an emergency.
>
> Before clicking it:
> 1. Say out loud which migration state the database is in. If you cannot, do not click.
> 2. Post in the incident channel: what you are deploying, which commit, and why CI cannot.
>
> After clicking it:
> 3. Open an issue the same day recording the commit, the reason, and what has to change so the
>    next occurrence goes through CI.

### Detection — this part *is* automatable

Every Render deploy record carries a `trigger` field, and it distinguishes the paths cleanly:

```bash
render deploys list srv-d8slkb77f7vs73d2k92g --confirm --output json
```

`trigger=deploy_hook` is CI. `trigger=manual` is the button. `trigger=service_updated` is a
blueprint or settings change.

Read on 2026-09-23. The six most recent deploys (timestamps are UTC, so the newest fall on the
24th) contain **four** manual ones, and the currently `live` deploy is one of them:

| time (UTC) | trigger | status | commit |
|---|---|---|---|
| 2026-09-24 01:16 | **manual** | live | `5b74943` |
| 2026-09-24 00:45 | **manual** | deactivated | `5b74943` |
| 2026-09-23 23:38 | **manual** | deactivated | `5b74943` |
| 2026-09-23 22:13 | **manual** | deactivated | `8e3b3a3` |
| 2026-09-23 22:02 | deploy_hook | deactivated | `a0564d8` |
| 2026-09-23 20:28 | service_updated | update_failed | `a0564d8` |

Two things follow. The button is not an emergency tool here, it is the normal path — four of the
last six deploys. And **production is running `5b74943`, which is 20 commits behind
`origin/main` (`acc990a`)**. Anyone reading CI would conclude production is current. It is not.

A scheduled job that lists deploys and opens an issue on any `trigger=manual` would make each
occurrence self-reporting. That is the honest substitute for a setting that does not exist, and it
is not in this branch — it needs the `RENDER_API_KEY` to be environment-scoped first (§1), because
a scheduled workflow is exactly the kind of unapproved job that should not be able to read it
today.

---

## 4. Residency: the service runs in Oregon

### The exposure, plainly

`render.yaml` instructs, in its own words, that the document bucket be pinned to "an APPROVED
GCC/KSA (or adequacy) jurisdiction" because the product holds personal data under PDPL — offer
letters, national IDs, contracts, payroll.

**The compute that processes all of it runs in `oregon`, in the United States.** So does the 10 GB
disk, which is regional and lives in the same place. Both are now declared in `render.yaml` rather
than being invisible, which is the change this branch makes — it does not change where they are.

Even with the bucket correctly pinned, personal data is *processed* in the US: it is decrypted in
memory in Oregon, written to Oregon-resident logs, and reachable by a US-jurisdiction provider.
Under PDPL, pinning storage while processing elsewhere does not satisfy a residency requirement.
The current arrangement is therefore either non-compliant or operating on an explicit transfer
basis, and there is no record in this repository of such a basis existing.

**This is a commercial and legal decision, not an engineering one.** It is written down here so
that it is a decision rather than an oversight.

### The constraint that shapes every option

**A Render service's region is fixed at creation and cannot be changed.** There is no migration,
no setting, no support path. Moving regions means creating a *new service*, which means a new
`srv-` id, a new `.onrender.com` hostname, new environment variables, a new deploy hook, a new
disk, and a DNS cutover.

And the harder constraint: **Render has no Middle East region at all.** Its regions are US
(Oregon, Ohio, Virginia), EU (Frankfurt) and Asia-Pacific (Singapore). No amount of Render
configuration produces GCC or KSA residency. *(Verify against render.com/docs/regions before
acting on this — region availability is the kind of fact that changes.)*

### The options

1. **Accept the exposure explicitly.** Record the US processing location, the transfer basis
   relied on, and the customers it has been disclosed to. Cheapest, and honest — but it must be
   an actual written decision by whoever carries the risk, not silence. If Evostel's pilot data
   is real personal data, this decision cannot wait.

2. **Move to Frankfurt.** New service, EU processing, GDPR adequacy machinery. Still not GCC, so
   it does not satisfy a KSA-residency clause — it only shortens the argument. Costs a full
   service rebuild and a cutover.

3. **Leave Render for the backend.** A KSA/GCC region means a provider that has one — AWS
   Bahrain (`me-south-1`), AWS UAE (`me-central-1`), Oracle Jeddah, STC Cloud, or similar. This
   is the only option that actually satisfies the requirement `render.yaml` already writes down.
   It is a genuine migration: container platform, Postgres, object storage, CI, secrets.

4. **Split the plane.** Keep Render for anything without personal data; run the tenant data plane
   in-region. Doubles the operational surface and is usually worse than (3) unless the split is
   already natural.

### What to do first, regardless of which option wins

Pin the **bucket** now. `Storage__Region` and `Storage__Endpoint` are `sync: false` and their
current values are unverified. Whatever is decided about compute, storage residency is a dashboard
field and should not be left on a default. Check it in the same session as §1 — you will already
be in the environment variables screen.

---

## Change record

| Item | Where |
|---|---|
| `region`, `numInstances`, `disk` declared | `render.yaml` |
| Bidirectional blueprint↔service key check | `scripts/check_render_env.py` (`parse_declared_keys`, `find_undeclared`, `UNDECLARED_ALLOWLIST`) |
| Secret-scope measurement | `scripts/check_secret_scope.py`, job `secret-scope-gate` in `.github/workflows/ci.yml` |
| Regression guards | `backend-dotnet/Zayra.Api.Tests/DeployHardeningTests.cs` — `RenderYaml_DeclaresTheInfrastructureTheLiveServiceActuallyHas`, `Ci_SecretScopeGateRunsWithoutAnEnvironment` |

Related: `docs/DEPLOY_ROLLBACK_RUNBOOK.md`, `docs/REPOSITORY_AND_DEPLOYMENT_GOVERNANCE.md`.
