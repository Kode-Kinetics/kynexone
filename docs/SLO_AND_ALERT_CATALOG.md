# SLO and Alert Catalog

**Wave 1 G3.** Status: **targets defined, instrumentation partially in place, alerts NOT yet firing.**

> Read the status column honestly. An SLO with no instrument behind it is an intention, not an
> objective, and this catalog marks which is which. Nothing here is claimed to be alerting today —
> there is no collector configured in production (`Observability:OtlpEndpoint` is unset), so metrics
> are collected and not shipped.

---

## 1. Why these numbers

This is a payroll product. The failure that matters is not "the site is slow" — it is **an employee is
not paid, or is paid the wrong amount, and nobody notices until they complain.** The objectives below
are weighted accordingly: availability and latency targets are ordinary, and the payroll, job and
notification objectives are strict, because those are the ones that turn into money and into a labour
dispute.

Error budgets are stated monthly (30 days).

---

## 2. Service level objectives

| # | SLO | Target | Budget / 30d | Measured by | Status |
|---|---|---|---|---|---|
| S1 | API availability — non-5xx on authenticated endpoints | 99.5% | 3h 39m | `http.server.request.duration` count by status (ASP.NET Core instrumentation) | **Instrumented** |
| S2 | API error rate — 5xx share of all requests | < 0.5% | — | same | **Instrumented** |
| S3 | API latency — p95 across authenticated endpoints | < 800 ms | — | `http.server.request.duration` histogram | **Instrumented** |
| S4 | API latency — p99 | < 2500 ms | — | same | **Instrumented** |
| S5 | Authorization-denial anomaly — cross-company denials per hour | < 5, and no 10× hour-on-hour jump | `zayra.authorization.denials` | **Instrument defined, not yet emitted** |
| S6 | Payroll run completion — runs reaching a terminal state | 99.9% | — | `zayra.payroll.runs_requested` vs terminal outcome | **Instrument defined, not yet emitted** |
| S7 | Payroll validation failure rate | < 2% of runs | — | `zayra.payroll.validation_failures` | **Instrument defined, not yet emitted** |
| S8 | Tenant provisioning success | 99% | — | not yet instrumented | **GAP-G3-2** |
| S9 | Job queue delay — oldest queued job age | < 5 min p95 | — | requires G2 | **Blocked on G2** |
| S10 | Notification delivery lag — queued → sent | < 10 min p95 | — | `zayra.notifications.delivered` + ledger timestamps | **Partially instrumented** |
| S11 | Document availability — authorized download succeeds | 99.9% | — | `zayra.documents.operations` | **Instrument defined; storage work is G1** |
| S12 | Database readiness — `/health/ready` returns 200 | 99.9% | — | existing readiness endpoint | **Live** |
| S13 | Migration parity — `pendingMigrations == 0` | 100% | zero tolerance | `/health/ready`, `zayra.database.pending_migrations` | **Live** |
| S14 | Backup age | < 24h | zero tolerance | requires G4 | **Blocked on G4** |
| S15 | Restore drill | passes monthly | zero tolerance | requires G4 | **Blocked on G4** |

---

## 3. Alerts

Severity: **P1** page immediately · **P2** notify on-call within the hour · **P3** ticket.

| # | Alert | Condition | Sev | Runbook summary |
|---|---|---|---|---|
| A1 | API unavailable | `/health/live` fails 3 consecutive probes | P1 | Check Render service state and recent deploy. If a deploy is in flight, the deploy job's own verification should already have failed — see `docs/DEPLOY_ROLLBACK_RUNBOOK.md`. |
| A2 | Error-rate spike | 5xx > 2% over 5 min | P1 | Group by route and `zayra.failure_category`. A single route means a code path; spread across routes means a dependency — check A6 first. |
| A3 | Latency regression | p95 > 2s over 10 min | P2 | Compare against deploy time. Check DB latency in `/health/ready`. |
| A4 | **Migration mismatch** | `pendingMigrations > 0` for > 5 min | **P1** | The running code and the schema disagree. `/health/ready` returns 503 and Render will not route to the instance, so this presents as an outage. Run the migration job; do NOT roll the schema back under a running app. |
| A5 | Payroll run stuck | a run in a non-terminal state > 60 min | P1 | Find the run by `zayra.payroll_run_id` on the span, then the correlation id. Until G2 there is no durable job record, so recovery is manual — this is why G2 exists. |
| A6 | Database unreachable | `/health/ready` DB probe unhealthy 2 consecutive | P1 | Neon status, connection count, credentials. Readiness already gates traffic. |
| A7 | **Cross-company authorization-denial anomaly** | `zayra.authorization.denials{failure_category="authorization"}` 10× hour-on-hour | **P1 security** | Someone is probing entity boundaries. Identify the module from the span, then the tenant from the trace — never from a metric label, which deliberately does not carry one. |
| A8 | Notification failure spike | failure share > 20% over 15 min | P2 | Provider outage vs configuration. `not_configured` is not a failure — see §5. |
| A9 | Queue backlog | oldest queued job > 15 min | P2 | Blocked on G2. |
| A10 | Storage unavailable | document op failure > 5% over 10 min | P2 | Blocked on G1. |
| A11 | Backup stale | newest backup > 24h | P1 | Blocked on G4. |
| A12 | Restore drill failed | monthly drill fails | P1 | Blocked on G4. |
| A13 | Authentication failure spike | `zayra.authentication.failures` 10× baseline over 10 min | P2 security | Credential stuffing vs a broken client. The login limiter (10/60s) should already be shedding. |

---

## 4. The sixty-second diagnosis contract

An operator holding **one correlation id** must reach, within about a minute: what failed, which module
and operation, which tenant and company, the trace, the run or job id, the failure category, the retry
state, the affected record count, and the recovery action.

The correlation id is returned on **every** response in `X-Correlation-ID`, so a customer or a frontend
engineer can quote it from the network tab without access to any tooling.

**This contract is not yet proven.** The four drills the brief requires — failed payroll operation,
database dependency failure, notification provider failure, and unauthorized cross-company request —
are **not** executed in this change. Claiming a diagnosis time without running the drill against a live
collector would be exactly the kind of unearned assertion this programme exists to stop. Recorded as
**GAP-G3-4**; `docs/G3_OBSERVABILITY_CERTIFICATION.md` is deliberately not written yet.

---

## 5. Rules that keep this catalog honest

**Optional providers are reported truthfully, not as failures.** `/health/ready` distinguishes
`configured` from `not_configured` for Redis, Qiwa and SMTP. An unconfigured provider must never mark
the application unhealthy — nor be quietly reported as healthy. It is a third state and is shown as one.

**No identifier is ever a metric label.** Tenant, company, employee, request, run and correlation ids
belong on spans, where they are one searchable field. As a metric label each multiplies the time-series
count without bound. Enforced by `ObservabilityTelemetryTests.NoIdentifierIsEverPermittedAsAMetricLabel`.

**No PII reaches telemetry.** No salary, IBAN, bank detail, identity number, document content, email
body, token or employee name in a span attribute, metric label or log scope. Failure categories are a
closed vocabulary rather than exception text, because an exception message is one interpolation away
from carrying an IBAN. Enforced by `NoSpanAttributeNamesAPersonOrACredential` and
`FailureCategoriesAreAClosedVocabulary_NotFreeText`.

**Health endpoints leak nothing.** No connection strings, no driver detail, no secret. `/health/ready`
is public because a load balancer must reach it.

---

## 6. Gaps

| # | Gap |
|---|---|
| **GAP-G3-1** | **EF Core instrumentation deliberately absent.** The package is prerelease-only and removed the `SetDbStatementForText` switch that disables SQL capture. On this schema a captured statement carries salaries and IBANs, so a beta whose redaction behaviour cannot be pinned is the wrong dependency. Query timings are covered by the domain histogram until a stable release restores an explicit switch. |
| **GAP-G3-2** | Tenant-provisioning metrics (S8) not instrumented. |
| **GAP-G3-3** | Domain instruments are **defined but not yet emitted** — the counters exist and no call site increments them. Wiring them into payroll, notifications and document paths is the next slice. |
| **GAP-G3-4** | The four failure drills and the 60-second diagnosis proof are **not executed**. No certification document is written. |
| **GAP-G3-5** | No collector is configured in production, so nothing is exported today. Set `Observability:OtlpEndpoint`. |
| **GAP-G3-6** | Dashboards are not built. The metric and attribute vocabulary they need is defined here. |
| **GAP-G3-7** | Trace continuity through background workers is unproven — the workers predate this change and do not yet start spans. |
