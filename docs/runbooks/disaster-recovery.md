# Disaster recovery runbook

Status: PROPOSED targets, no drill on record yet (audit-360 finding P0-3). This document becomes
real the day the first drill evidence is filed (section 7). Until then treat every RPO/RTO below as
a target, not a measurement.

Related: `docs/DEPLOY_ROLLBACK_RUNBOOK.md` (code rollback), `docs/PRODUCTION_HARDENING.md`,
`render.yaml` (app definition), `scripts/dr/` (tooling), `docs/runbooks/observability.md`.

## 1. Targets

| Layer | RPO (max data loss) | RTO (max time to restore) | Mechanism |
|---|---|---|---|
| Database (Neon) | 5 min | 2 h | Neon point-in-time restore (PITR) via branch from timestamp |
| Application (Render) | none (stateless image) | 30 min | Redeploy last good image / re-apply `render.yaml` blueprint |
| Frontend (Vercel) | none | 15 min | Promote or roll back a previous deployment |
| Documents (B2) | 24 h (nightly copy) | 4 h | Second-bucket copy + bucket versioning |

Preconditions the owner must confirm (UNVERIFIED in the repo, see audit-360 09-perf-ops):
- Neon plan and history retention. Free is about 6 h, Launch about 7 d, Scale up to 30 d. Set at least 7 d, and 30 d once payroll tenants are live. A 5 min RPO is only true if the restore window covers the incident; a corruption found after the window closes is unrecoverable from Neon alone, which is why section 3 includes a logical dump.
- Render plan is `starter`, single instance, 10 GB disk, no instance overlap: every deploy and every restart is a short outage (about 38 s for a good deploy).

## 2. What is backed up, where

| Asset | Where it lives | Backup | Not covered |
|---|---|---|---|
| Postgres data | Neon project, us-east-1 (pooled + direct endpoints) | Neon PITR (WAL history) | Retention window only; no second provider yet |
| Logical DB copy | n/a | Recommended: weekly `pg_dump` to the backup bucket (section 6.2) | Not automated yet |
| Employee/candidate documents | Backblaze B2 bucket (`Storage__Bucket`) | `scripts/dr/b2-backup.sh` to a second bucket | Source and backup both in US regions until the KSA bucket plan lands |
| App config | `render.yaml` in git | git history | Secret VALUES (`sync: false` keys) |
| Secrets | Render env, GitHub environment `production`, Vercel env | Owner password manager entry per secret (name, where used, rotation steps) | Nothing in the repo; that is intended |
| Frontend | Vercel deployments | Vercel keeps prior deployments | Vercel env values |
| Source | GitHub (public) | Clones | n/a |

Secrets inventory to keep in the owner's password manager (names only here): `ConnectionStrings__Default` (pooled), the direct DB URL used by CI migrations (`PROD_DATABASE_URL`), JWT signing keys, `Storage__AccessKey`/`Storage__SecretKey`, SMTP credentials, Render deploy hook, `NEON_API_KEY`. Recovery from a lost Render service is impossible without this list.

## 3. Roles

| Role | Who | Duty |
|---|---|---|
| Incident commander (IC) | Owner (or named delegate) | Declares the incident, owns decisions that lose data or touch production |
| Operator | Engineer on call | Runs the steps below, keeps the timeline |
| Scribe | Anyone | Timestamps every action in the incident doc (UTC and US Eastern) |
| Client comms | Owner | Customer notice; for personal-data breach the 72 h SDAIA/PDPL notification clock starts at awareness |

Agents and automation never run production-changing steps; the IC approves each one explicitly.

## 4. General rules for every scenario

1. Stop the bleeding first: if bad writes are still happening, pause traffic (Render: suspend the service, or set maintenance) before restoring.
2. Never restore over the primary in place. Restore to a NEW Neon branch, verify it, then promote or repoint (Neon "restore" can also be done in place, but only after a verified branch proves the target time is right).
3. Record the exact restore timestamp and why that instant. Prefer a time just before the first bad write, taken from the audit log (`audit_logs.created_at_utc`).
4. Take a Neon branch of the CURRENT primary before any rollback step, so the bad state is preserved for forensics.
5. After service returns: check `/health/ready` reports ready with `pendingMigrations` 0, log in, run the payroll checksum from `scripts/dr/verify.sql` section 4.

## 5. Scenarios

### 5.1 Bad migration

Symptom: deploy fails readiness (`/health/ready` 503), 42P01/42703 errors, or data shape wrong after a migration.
1. Do not redeploy from the Render dashboard (known to fail readiness for a migration-bearing build; approve or fix via CI). Do not diagnose by redeploying: each failed deploy costs minutes of downtime.
2. Create a Neon branch of the primary now (forensics).
3. If the migration is additive and the old code ignores it: roll the app back per `docs/DEPLOY_ROLLBACK_RUNBOOK.md` step 1; leave the schema.
4. If the migration is harmful: apply its tested `Down()` per the same runbook (step 2). If `Down()` refuses or data was damaged, restore the database to the instant before the migration ran (5.2 steps 2-6).
5. Evidence: migration id, timeline, which of the two paths was used.
Target: 30 min for app rollback, within the 2 h DB RTO for restore.

### 5.2 Data corruption or accidental delete

1. IC confirms scope (tenant, tables) and the last good instant `T` from the audit log. If only one tenant or a few rows are wrong, prefer a targeted repair from a restored branch (step 5) over restoring everything.
2. Run the drill script against `T` to get a verified read-only restore:
   `NEON_PROJECT_ID=... TARGET_TIMESTAMP=T NEON_API_KEY=... scripts/dr/restore-drill.sh --keep-branch --yes` (or dispatch the workflow; use `--keep-branch` so the branch survives for step 5).
3. Check the report: migration id equals production's, hash chain PASS, payroll checksum matches close evidence.
4. Full restore: in the Neon console restore the primary branch to `T` (Neon keeps the pre-restore state as a backup branch). Wait for the endpoint, then restart the Render service so pooled connections reset.
5. Targeted repair: from the kept branch, export the affected rows (`psql ... \copy`) and re-insert into the primary inside a transaction reviewed by the IC. Writes after `T` for other tenants are preserved this way.
6. Verify (rule 5), delete the drill branch, tell affected tenants what window of data was lost, if any.
Data loss: up to 5 min before the incident for a full restore; none for a targeted repair.

### 5.3 Neon region outage (us-east-1)

Neon PITR lives in the same region, so a regional loss is the one case PITR cannot cover.
1. Confirm on Neon's status page; do not restart the API repeatedly (OOM and connection storms made the last incident worse).
2. Put the app in a clear degraded state (readiness will be 503; communicate to customers).
3. If the outage is expected under 2 h, wait. Otherwise restore from the most recent logical dump (section 6.2) into a new Postgres in another region (a new Neon project in another region, or any managed Postgres 16), then update `ConnectionStrings__Default` in Render and the CI direct URL, and run `dotnet ef database update` (owner approval gate in CI) to be sure of the schema.
4. Expected loss: since the last dump (up to 7 d until the dump is made daily). This gap is why the nightly logical dump is recommended before the first live-data client. Decide per customer whether that is acceptable; it is an owner decision.

### 5.4 Render outage or service loss

1. Render status page. If the region is down and long, create the same service in another Render region from `render.yaml` (Blueprint), supply secrets from the password manager, and point the frontend `NEXT_PUBLIC_API_BASE_URL` rewrite at the new hostname. Note the hostname does not follow the service name (comment in `render.yaml`).
2. If only the service is broken: redeploy the last good image through the CI deploy hook.
3. Confirm `AllowedHosts`, `Proxy__KnownProxies`, storage and DB settings; `/health/ready` ready.
Target RTO 30 min if secrets are at hand; this is the scenario that fails without the secrets inventory.

### 5.5 B2 loss or corruption

1. Identify the damage: missing keys, deleted bucket, or overwritten objects. Check the bucket's file versions first (versioning restores overwrites and deletes: B2 lifecycle keeps hidden versions).
2. Restore from versions if possible. Otherwise copy back from the backup bucket:
   `rclone copy DST:<backup-bucket> SRC:<documents-bucket> --checksum` using the same env mapping as `scripts/dr/b2-backup.sh` with source and destination swapped (use a key allowed to write the documents bucket).
3. Point `Storage__Bucket`/keys at the restored bucket if a new bucket was needed; redeploy to pick up env.
4. Spot check: open five documents from five tenants including one payslip PDF.
Backup design, by the owner:
- Nightly: run `dr-documents-backup` (or schedule it, see 6.1).
- Destination bucket in a different provider or at least a different region, with versioning and OBJECT LOCK in compliance or governance mode, default retention 30 d (object lock must be enabled when the bucket is created; it cannot be added later on B2). Source key read-only, destination key write-only without delete, so a stolen app credential cannot erase the backup. Keep the backup bucket's admin key out of Render and GitHub.
- Residency: the backup of personal data must sit in an approved region too; do not satisfy RPO by copying KSA tenant documents to a non-approved jurisdiction.

### 5.6 Leaked credential

1. IC declares; note the time of exposure. Public repo: rotate first, investigate second.
2. Rotate in this order: Neon role password (and update Render + CI direct URL), JWT signing key (this signs everyone out), B2 application keys, SMTP, Render deploy hook, GitHub tokens, `NEON_API_KEY`.
3. Revoke sessions (bump the security stamp / sign out everywhere; note the known refresh-stamp gap, treat refresh tokens as live until the signing key is rotated).
4. Review `audit_logs` and provider access logs (Neon, B2, Render) for use of the credential between exposure and rotation. If personal data may have been read, start the 72 h notification clock.
5. Check git history for the leaked value (scan before pushing any fix; do not push write-ups of a live hole to the public repo).
Target: rotation under 1 h from awareness.

## 6. Routine

### 6.1 Schedules (owner turns on)
- Monthly: dispatch `DR restore drill` with a restore point about 24 h in the past (and every quarter, one deliberately 6 days in the past to test the retention edge).
- Nightly documents copy: after one reviewed dry run, add to `dr-documents-backup.yml` under `on:` `schedule: - cron: '30 2 * * *'` (02:30 UTC).
- Calendar the secret rotation review quarterly.

### 6.2 Logical dump (recommended before real client data)
`pg_dump --format=custom --no-owner "$DIRECT_DB_URL" | rclone rcat DST:<backup-bucket>/db/kynexone-$(date -u +%F).dump` from the direct (non-pooled) endpoint, run from CI or a trusted machine, keep 35 daily plus 12 monthly, encrypted at rest by the destination. Restore test: `pg_restore` into a scratch database quarterly.

## 7. Drill procedure and evidence

First drill (owner):
1. Create a Neon API key (Neon console > Account > API keys). Never paste it into chat or the repo.
2. Locally: `export NEON_API_KEY=... NEON_PROJECT_ID=... TARGET_TIMESTAMP=<24h ago, UTC, e.g. 2026-10-25T09:00:00Z> DR_OPERATOR="Your Name"`, then `bash scripts/dr/restore-drill.sh --dry-run`, then without `--dry-run`. Type `restore` at the prompt.
   Or in CI: add `NEON_API_KEY` (environment secret) and `NEON_PROJECT_ID` (environment variable) to the `production` environment, then run the `DR restore drill` workflow, approve the production gate.
3. The report (`dr-evidence-*.md` or the workflow artifact) contains time to a queryable restore, migration id, row counts, audit chain result, payroll checksum.
4. Complete the sign-off checklist in the report by hand and sign.

Evidence to file after EVERY drill or real recovery (store under `docs/evidence/dr/YYYY-MM-DD/`, no secrets or customer data in it; row counts and checksums only):
- the signed report; restore point and why; measured time to restore (RTO) and age of the data lost (RPO);
- Neon plan and retention window at the time; branch list screenshot showing the drill branch deleted;
- for real incidents: timeline, decisions, customer notices.

Drill log (append a row per drill):

| Date | Operator | Restore point | Time to restore | Result | Evidence |
|---|---|---|---|---|---|
| (none yet) | | | | | |

Pass criteria: restore branch queryable under 2 h (expect minutes), automated result PASS, migration id equal to production's at that instant, payroll checksum equal to close evidence.
Known limits of the automated check: it proves the database restores and its audit chain and payroll totals are intact; it does not restore B2 documents, secrets, or the app. Run a documents restore spot check (5.5 step 4) at least quarterly.
