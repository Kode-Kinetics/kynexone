# KynexOne HRM enterprise readiness handoff

Revalidated: 2026-08-16 (America/New_York)

Environment: isolated Docker Compose project `zayra-browser-audit`; production-built Next.js frontend on `http://127.0.0.1:55173`; Release-built ASP.NET API on `http://127.0.0.1:55117`; fresh migration-built PostgreSQL and Redis volumes; `SEED_DEMO_DATA=true`; `SEED_ENTERPRISE_TEST_DATA=true`.

## Verdict

**GO for a controlled enterprise client demo/pilot using the documented sandbox stack.**

**Production launch remains gated by customer/hosting configuration and independent regulatory/operational sign-off.** The application fails closed where durable storage, trusted proxy, or live-provider configuration is required; this report does not manufacture credentials, legal certification, backup evidence, or Saudi government connectivity.

## Final executable evidence

- Backend: **1,711 passed, 0 failed, 0 skipped** in the final full run.
- Browser: independent real-login client-pilot lane **1/1 passed**; complete production-mode Chromium gate **130 passed, 0 skipped, 0 unexpected, 0 flaky**.
- Browser scope includes accessibility, all platform routes, tenant/group/company personas, company switcher and export isolation, feature and subscription enforcement, Saudi readiness, real positive/negative authentication, tenant isolation, and setup/cleanup.
- Frontend: optimized Next.js production build passed; **59/59 static pages** generated; `tsc --noEmit` passed.
- API: Release build passed with **0 warnings and 0 errors**.
- Data: fresh zero-to-latest migration boot passed; readiness reports `pendingMigrations=0`; EF reports no pending model changes.
- Dependencies: npm audit found **0 vulnerabilities**; NuGet direct/transitive scan found no vulnerable packages.
- Hygiene: Docker Compose configuration and `git diff --check` passed.

## Fresh-state and restart proof

- The final database was recreated from empty disposable volumes before the last browser gate.
- Six intended active tenants exist: bootstrap single-company, Ras Al-Manar, IntelliFlow, and the three enterprise group fixtures (Almarai, Tata, Emaar). The temporary Evostel fixture is purged by browser teardown.
- Each enterprise group contains five legal entities and one real payroll run per entity, enabling authorization checks against existing forbidden financial artifacts rather than random IDs.
- Restart invariants matched exactly before/after: tenant count, IntelliFlow tenant ID, IntelliFlow admin ID, 12 active employees, one payroll run, and one locked run.
- Readiness after restart: database healthy, Redis configured, all five workers healthy, zero missing/stale/failed workers, empty QIWA/notification/report dead-letter or due queues.

## Runtime and load evidence

- Final browser/runtime window produced **zero HTTP 5xx** and no unhandled or Redis connection exceptions.
- Readiness load: **1,000/1,000 HTTP 200** at concurrency 50; 228 ms average and 640 ms maximum response time. ApacheBench length variance was caused only by live readiness timestamps/worker fields; connect, receive, and exception failures were zero.
- Authenticated dashboard load: **500/500 HTTP 200** at concurrency 25; 120 ms average and 390 ms maximum response time.

## Closed application risks

- Redis URI parsing and dashboard permanent-skeleton failure.
- Balanced two-sided payroll GL, authoritative SAR currency, and lock-time accounting validation.
- Effective-dated salary coverage shared by payroll, YTD, GOSI, and readiness.
- Canonical leave routing through Approval Center with transactional CAS, maker-checker, balance, notification, and replay safety.
- Transactional exactly-once recruitment offer acceptance and onboarding conversion.
- Stable employee public identity across compliance, onboarding, finance, imports, and ESS.
- Tenant-safe provider-neutral document reads and S3-compatible storage boundaries.
- Refresh-token family replay/concurrency protection, absolute family lifetime, revocable platform and tenant sessions, and current role/permission/entity-scope checks.
- Multiple legitimate tenant logins no longer revoke one another; sub-threshold bad-password attempts cannot revoke an existing session.
- Permission Deny/custom roles, SSO fail-closed behavior, central audit hash serialization, QIWA distributed leases/idempotency, report execution, worker heartbeats, proxy trust, MaxAdminUsers concurrency, and import serialization.
- Attendance/leave/time ownership, calendar, lock, accrual, cross-year, roster-rest, comp-off, overtime, encashment-to-payroll, and generic approval transactional invariants.
- Performance, offer-decline, offboarding, contracts, final settlement, payment lifecycle, compliance reminder delivery, and EOSB company/jurisdiction/wage-basis validation.
- Production container retains migrations; readiness fails closed on schema drift and operational worker health.
- CI now runs both the independent client-pilot lane and the complete enterprise Chromium gate with deterministic enterprise fixtures.

## External production gates

1. Provision real durable S3 bucket credentials and approved region; production intentionally refuses ephemeral document storage.
2. Configure the trusted reverse-proxy CIDR and verify the service is reachable only through that edge.
3. Configure and verify SMTP and any required SMS/WhatsApp/push providers.
4. Provision live QIWA credentials and execute provider certification/idempotency tests; the current environment uses the sandbox adapter.
5. Obtain current Saudi payroll/GOSI/WPS/QIWA legal and payroll-specialist sign-off before live filing.
6. Complete a real encrypted backup/PITR restore drill with approved RPO/RTO and retained evidence.
7. Configure production observability, alert routing, capacity targets, and a representative high-volume soak/chaos program for the intended workforce size.
8. If commercial self-service billing is required, integrate and certify a payment processor/webhook lifecycle; current billing is an operator-managed ledger.

Historical before-state screenshots and root-cause evidence remain in [REPORT.md](REPORT.md); the earlier business-continuity screenshots remain in [FINAL-REVALIDATION.md](FINAL-REVALIDATION.md).
