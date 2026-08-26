# KynexOne HRM final pilot revalidation

Revalidated: 2026-08-15 (America/New_York)

Environment: isolated Docker Compose project `zayra-browser-audit`; frontend `http://127.0.0.1:55173`; API `http://127.0.0.1:55117`; fresh migration-built PostgreSQL and Redis volumes; `SEED_DEMO_DATA=true`.

## Verdict

**GO for an enterprise client demo/pilot in the documented sandbox environment.**

The original audit's release-blocking application failures were fixed and revalidated in a real Chromium browser against a clean PostgreSQL-backed deployment. Live Saudi government filing remains an external production gate until real QIWA credentials are provisioned and current statutory rates receive qualified legal/payroll sign-off.

## Final clean-state proof

- Exactly 3 intended tenants exist and all are active: `zayra`, `rasalmanar`, and `intelliflow`.
- IntelliFlow has exactly one payroll run: July 2026 `Locked`; no synthetic August draft exists.
- Readiness reports `pendingMigrations=0`, database healthy, and Redis configured.
- A backend restart preserved the same IntelliFlow tenant ID, tenant count, payroll data, active login session, and populated dashboard.
- The correctly parameterized `tenant-pilot` Chromium lane loaded the dashboard and every core module without API 5xx responses: 1/1 passed in 31.8 seconds.

## Browser-verified business continuity

### Payroll, statutory deductions, and GL

- Dashboard: 12 active employees; July 2026 locked payroll; gross SAR 205,200.00; deductions SAR 10,603.13; net SAR 194,596.87.
- PostgreSQL: 12/12 slips have current-run-inclusive YTD; employee statutory deductions total SAR 10,603.13; employer contributions total SAR 14,403.13; all 12 employees are represented.
- Browser GL journal: total debits SAR 219,603.13; total credits SAR 219,603.13; authoritative currency SAR; explicit balanced status.
- Saudi Compliance: 100/100; QIWA readiness 12/12; WPS locked with no blockers; GOSI 12 ready / 0 blocked.
- Evidence: [balanced GL journal](21-payroll-gl-final.png), [Saudi compliance](20-saudi-compliance-final.png).

### Leave to canonical Approval Center

- Admin submitted a real Annual Leave request for Yaser Al-Ghamdi: HTTP 201.
- The request appeared in the Leave approval list and the HR Manager's role-owned Approval Center queue.
- HR Manager approved through the global Approval Center: HTTP 200.
- The pending queue cleared and the Leave module showed `Approved` with the exact entered Aug 27, 2026 date.
- PostgreSQL retry execution, maker-checker rollback, projection/balance/notification consistency, CAS, and replay protection are covered by a real-PostgreSQL integration test.
- Evidence: [HR Manager queue after decision](24-hr-manager-approved-final.png), [approved Leave record](25-leave-approved-final.png).

## Engineering gates

- Backend: 1,657 passed, 0 failed, 0 skipped.
- Frontend: optimized production build passed; 59 routes generated; standalone `tsc --noEmit` passed.
- Browser: critical IntelliFlow tenant lane passed first attempt with a non-flaky 90-second whole-flow budget and unchanged per-route timeouts.
- Security: `npm audit` found 0 vulnerabilities; NuGet direct/transitive vulnerable-package scan found none.
- Data: EF reports no pending model changes; clean schema has all migrations applied; model-versus-migration schema parity gate is in CI.
- Infrastructure: Docker Compose validation and `git diff --check` passed.

## Material fixes included

- Redis URI parsing and dashboard failure state.
- One-time invitation consumption and refresh-token family replay defense with PostgreSQL concurrency tests.
- Durable Data Protection key storage.
- Canonical employee public identity across compliance, onboarding, finance, imports, and ESS.
- Provider-neutral, tenant-safe document storage and S3 reads.
- Transactional exactly-once recruitment offer acceptance.
- Canonical leave approval routing, role scopes, PostgreSQL retry-safe transactions, balance transitions, and notifications.
- Effective-dated payroll salary source shared by payroll, coverage, YTD, and GOSI.
- Balanced two-sided payroll GL with lock-time validation and SAR currency authority.
- Migration repair/parity gates and migration-on-startup support for the isolated Development stack.
- Idempotent clean demo startup that preserves tenant identity, sessions, and payroll across restarts.
- CI browser-pilot deployment gate with real seeded data.

## External production gates (not fabricated)

- QIWA production API credentials are not configured. Sandbox readiness is 12/12, but live sync cannot be claimed without customer/government credentials.
- SMTP is not configured in the isolated browser environment; in-app notifications were verified.
- Default GOSI/statutory rules carry an explicit qualified-review warning. Obtain current Saudi payroll/legal sign-off before a real filing.
- Refresh-token replay defense is implemented, but an independent absolute session-family lifetime/device binding remains a defense-in-depth hardening item.

