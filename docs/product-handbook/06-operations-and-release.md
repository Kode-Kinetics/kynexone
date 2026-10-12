# Operations and Release Runbook

## Release identity and ownership

A release is a pinned source candidate plus compatible schema, tier builds and configuration. The release owner coordinates engineering, database, security and customer acceptance. Merge, CI success, deployment and tenant enablement are distinct events. At the documented October 11 observation, the reviewed source candidate was newer than the deployed revision; this must be resolved before the candidate is represented as live.

The repository's existing deployment and parity runbooks remain the authoritative engineering procedures. This guide explains their operating gates without assuming that all current hosting settings or credentials have been independently inspected.

## Before promotion

1. Identify the source SHA, accepted module scope and affected tenant/company behavior. Generate or retrieve the release manifest for that exact candidate and check required CI conclusions.
2. Confirm compatibility and migration visibility. Reconcile the expected migration manifest to database history; model drift and undiscoverable migrations are failures, not documentation exceptions.
3. Review financial and statutory changes. The latest GL release requires a read-only inventory of affected overtime rules, company overrides, reserved account codes and open Saudi payroll runs before promotion. Verify actual policies with the responsible client/domain reviewer.
4. Confirm a tested recoverable database checkpoint and document-store recovery arrangement. The existence of a managed database service does not prove a usable backup or restore time.
5. Check environment variable presence and applicable feature gates privately. Record names and validation results, never credential values. Off-by-default connections remain off until their separate acceptance is complete.
6. Agree the monitoring owner, user communications, decision window and rollback trigger for the release. Production promotion follows the authorized release process.

## Migration and deployment sequence

The documented pipeline runs backend/security and frontend checks, then applies production EF migrations through the gated migration job before triggering Render deployment. Render readiness should refuse a candidate whose expected schema is missing. Vercel can deploy the frontend on a main push while the backend waits for its production gate, which creates a compatibility risk even if each tier is individually healthy.

Use the existing release scripts in a prepared repository environment:

```bash
./scripts/release_manifest.py --commit <accepted-sha> --out release-manifest.json
./scripts/check_deployment_parity.py --frontend https://kynexone.vercel.app --api https://zayra-ai-workforce.onrender.com --candidate <accepted-sha>
```

Confirm readiness, source parity and configured tenant gates after promotion. Then run the accepted cross-role smoke journey, including a fresh persisted read and critical authorization refusal. Mobile and workers are outside the two-tier parity check and require their own compatible release records.

## Monitoring and triage

Monitor API liveness and readiness separately, frontend/API build identities, failed authentication, business-request errors, migration failures, background job status, document retrieval and integration reconciliation. Define alert thresholds from observed operating behavior and the contracted service scope; no measured production availability or contractual SLA is established here.

For an incident, record time, affected release, tenant/company, sanitized correlation IDs, expected versus actual result and financial exposure. Determine whether the fault is access, configuration, schema, data, client build, API behavior or external provider failure before changing the system. Avoid repeated redeployments as diagnosis. For financial duplicates or uncertain bank outcomes, pause the affected operation and reconcile external evidence before retrying.

Preserve relevant logs and immutable record history. Do not delete financial evidence or broaden access to make a failed process pass. A user-facing error should name a useful next step while excluding internal secrets and unrelated personnel information.

## Recovery and rollback

Start with deployment parity. If the UI is ahead of a known-good backend, the existing procedure can restore a matching Vercel deployment by promoting its previous alias rather than rebuilding. Backend rollback uses the approved previous image and requires readiness verification. Schema rollback is a separate decision: additive structures can usually remain, while destructive downgrade may be refused once real financial evidence exists.

Before any schema reversal, take an approved recovery checkpoint, confirm the currently serving code no longer uses structures to be removed, and test the exact down migration against representative nonproduction data. Preserve loan, approval, ERP confirmation and benefit histories. Prefer a forward corrective release where downgrade would lose evidence or remove policy enforcement.

After recovery, rerun parity, readiness, impacted business and negative authorization journeys. Reconcile payroll/GL/payment records affected during the incident. Complete the incident record with cause, actual customer impact, corrective change and evidence of recovery.

## Mobile release and support boundary

The mobile README documents Expo/EAS production, preview and development channels and fingerprint-based runtime compatibility. Native dependencies or configuration changes require a new store binary. Supported OTA publishing uses the repository's profile-specific scripts; a JavaScript update cannot repair a missing native module or reach an incompatible runtime.

Before public distribution, accept physical iOS/Android behavior, MFA screen protection, push credentials, connectivity recovery, upgrades, rollback and Arabic layout. The README describes uncompleted biometric backend and offline replay work. Store approval, real-device results and supported OS versions must be recorded rather than inferred from an application ID.

## Sources

- [Deployment and rollback runbook](https://github.com/Kode-Kinetics/kynexone/blob/76985c559e9b51c246a4a48ab88e0e14eedc1ae1/docs/DEPLOY_ROLLBACK_RUNBOOK.md)
- [Release identity and parity](https://github.com/Kode-Kinetics/kynexone/blob/76985c559e9b51c246a4a48ab88e0e14eedc1ae1/docs/RELEASE_CANDIDATE_AND_PARITY.md)
- [October 11 financial/statutory rollout requirements](https://github.com/Kode-Kinetics/kynexone/pull/223)
- [Mobile release procedure](https://github.com/Kode-Kinetics/kynexone/blob/76985c559e9b51c246a4a48ab88e0e14eedc1ae1/mobile/README.md)
