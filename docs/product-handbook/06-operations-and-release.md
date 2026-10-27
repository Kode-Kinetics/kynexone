# Operations and Release Runbook

## Release identity and ownership

A release is one version number (`KYNEXONE_VERSION`) that names a matching API image, web image and database schema, built from one pinned source SHA by the `release` workflow and shipped as an offline bundle (`kynexone-<version>.tar.gz` with its `.sha256`) or pushed to the image registry. The API and web are always released together, so there is no tier-parity question at an installation: the API's `/health/live` and the web's `/build-info` report the same version and commit.

KynexOne's supplier owns building, signing and publishing a release and issuing licences. The organisation's IT team (or the implementation partner) owns installing it, upgrading, backups and the server. Merge, CI success, release publication and an installation's upgrade are distinct events.

## Before publishing a release

1. Identify the source SHA and the modules and behaviour it changes. Generate the release manifest for that exact candidate (`scripts/release_manifest.py --commit <sha> --out release-manifest.json`) and check the required CI conclusions, including `client-install` (a clean install and an upgrade with the kit) and `licence-report` (third-party licences against the allow-list).
2. Confirm migration visibility. Reconcile the expected migration manifest to the migration history; model drift and undiscoverable migrations are failures. Migrations must be additive, so the previous version keeps running on the upgraded database (the rollback path below depends on it).
3. Review financial and statutory changes with the responsible domain reviewer, including any effect on open payroll runs at an installation.
4. Check that the release notes say which settings in `.env` are new or changed, and which optional features stay off by default.

## Installing and upgrading

Installation and upgrade are done with the kit; see the [client installation guide](../CLIENT_INSTALLATION.md) §2 and §12. An upgrade:

1. verifies the bundle's checksums and refuses when the licence's support period has ended;
2. takes a backup (database, documents, settings);
3. switches maintenance mode on — users see a "being updated" notice and background workers pause;
4. loads the new images, applies the database migrations and waits for readiness, which refuses a build whose expected schema is missing;
5. runs smoke checks and switches maintenance off.

After an upgrade, confirm the version on **Organisation Admin → Licence** or `scripts/status.sh`, then run the accepted cross-role smoke journey, including a fresh persisted read and a critical authorization refusal. The mobile app and KynexBridge are released separately and must stay compatible with the server versions in support.

## Monitoring and triage

`scripts/status.sh` reports the containers, readiness, the version, the licence and its usage. Monitor API liveness and readiness separately, failed authentication, business-request errors, migration failures, background job status, document retrieval, disk space, backup success and integration reconciliation. No measured availability or contractual SLA is established here; those come from the organisation's support agreement.

For an incident, record time, version, company, sanitized correlation IDs, expected versus actual result and financial exposure. Determine whether the fault is access, licence, configuration, schema, data, client build, API behavior, server resources or an external provider before changing the system. For financial duplicates or uncertain bank outcomes, pause the affected operation and reconcile external evidence before retrying.

Preserve relevant logs (`docker compose logs`) and immutable record history. Do not delete financial evidence or broaden access to make a failed process pass. A user-facing error should name a useful next step while excluding internal secrets and unrelated personnel information.

## Recovery and rollback

If an upgrade fails, KynexOne stays in maintenance and the script prints the two ways back:

1. **Previous version**: set `KYNEXONE_VERSION` back and run `docker compose up -d backend web`. Because migrations are additive, the previous version runs on the upgraded database.
2. **Restore** the backup the upgrade took, which also returns the version. A restore replaces the database (including the data-protection keys), the documents and the settings, and refuses while the application runs.

Prefer a forward corrective release where a restore would lose work entered after the upgrade. Never run a down migration at an installation. After recovery, check readiness, the version and the impacted business and negative authorization journeys, and reconcile payroll/GL/payment records affected during the incident.

## Mobile release and support boundary

The mobile README documents Expo/EAS channels and fingerprint-based runtime compatibility. The same store app serves every installation: on first start it asks for the organisation's server (QR code or typed HTTPS address). Native dependencies or configuration changes require a new store binary; a JavaScript update cannot repair a missing native module or reach an incompatible runtime. Push notifications are off unless the installation switches them on.

Before public distribution, accept physical iOS/Android behavior, server selection, MFA screen protection, connectivity recovery, upgrades and Arabic layout. Store approval, real-device results and supported OS versions must be recorded rather than inferred from an application ID.

## Sources

- [Client installation guide](../CLIENT_INSTALLATION.md)
- [Installation kit](../../deploy/client/README.md)
- [Release bundle builder](../../deploy/release/make-bundle.sh)
- [Release identity and parity](../RELEASE_CANDIDATE_AND_PARITY.md)
- [Mobile release procedure](../../mobile/README.md)
