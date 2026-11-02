# Production observability plan

Status: plan for the owner to switch on. Audit-360 P1-4: today production logs go to the Render
console only, OpenTelemetry registers only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (it is not),
there is no error tracker and no alert. Cost target: 0 to 30 USD per month.

Related: `docs/runbooks/disaster-recovery.md`, `render.yaml`.

## 1. What already exists (do not rebuild)

- API: JSON console logs with tenant and trace scope; `/health/live` and `/health/ready` (ready is 503 while migrations are pending or the instance is draining; it is Render's health check and is anonymous).
- OpenTelemetry in the API activates by environment variable only (`Infrastructure/Operations/OpenTelemetryRegistration.cs`); frontend OTEL is opt-in too (`frontend/instrumentation.ts`).
- Mobile: an error-reporting adapter (`reportError`) is meant to be the single seam for a crash reporter; verify its location in `mobile/` before wiring (not found under `mobile/src` by name search in this worktree, so confirm where it lives).

## 2. Step 1: uptime check (15 min, free)

Monitor `GET https://<api-host>/health/ready` every 60 s, alert after 2 consecutive failures (about 2 min). Also monitor the frontend home page and `/health/live` (separates "process down" from "not ready").
Options:
- Free uptime service (Better Stack, UptimeRobot, or Grafana Cloud synthetic): create account, add 3 HTTP monitors, add alert contacts (owner email + phone SMS/push).
- Fallback with no account: a scheduled GitHub Action that curls the endpoint and fails the run. GitHub emails the repo owner on failed scheduled runs. Weaker (5 min minimum cadence, delays under load, burns minutes) so prefer a real uptime service.
Expect brief 502/503 during deploys (about 38 s); set the alert threshold above that or mute during the deploy window.
Do not use the API hostname in any public doc; the owner enters it in the monitor only.

## 3. Step 2: error tracking (Sentry, free tier to start)

Status: the code is merged-ready and DORMANT. With no DSN configured, no Sentry code is registered or loaded, nothing initialises, nothing is sent and there is no startup error. Create the accounts (section 6) and set the variables below to switch it on.

### 3.1 Environment variables

| Platform | Where | Variable | Meaning |
|----------|-------|----------|---------|
| API (.NET) | Render dashboard | `SENTRY_DSN` | Project DSN. Unset = off. |
| API | Render | `SENTRY_ENVIRONMENT` | Optional; defaults to the ASP.NET environment name. |
| API | Render | `SENTRY_TRACES_SAMPLE_RATE` | Optional, 0 to 1; unset or invalid = 0 (no performance data). |
| Web server | Vercel (runtime) | `SENTRY_DSN`, `SENTRY_ENVIRONMENT`, `SENTRY_TRACES_SAMPLE_RATE` | Same meanings, for server rendering and route errors. |
| Web browser | Vercel (build time) | `NEXT_PUBLIC_SENTRY_DSN`, `NEXT_PUBLIC_SENTRY_ENVIRONMENT`, `NEXT_PUBLIC_SENTRY_TRACES_SAMPLE_RATE`, `NEXT_PUBLIC_SENTRY_RELEASE` | Inlined into the bundle at build, so a change needs a redeploy. Unset = the SDK is not even loaded. |
| Web source maps | Vercel (build time) | `SENTRY_AUTH_TOKEN`, `SENTRY_ORG`, `SENTRY_PROJECT` | Source maps upload only when `SENTRY_AUTH_TOKEN` is set. A failed upload warns and never fails the build. |
| Mobile | EAS env, per build profile | `EXPO_PUBLIC_SENTRY_DSN`, `EXPO_PUBLIC_SENTRY_TRACES_SAMPLE_RATE` | Unset = off. `SENTRY_ORG`, `SENTRY_PROJECT`, `SENTRY_AUTH_TOKEN` enable build-time source maps (see `mobile/docs/CRASH_REPORTING.md`). |

The API keys are deliberately NOT declared in `render.yaml`: a `sync: false` key is a hard requirement of `scripts/check_render_env.py` and would block deploys until set. Add them in the Render dashboard when ready.

### 3.2 Switching on and off

- On: set the DSN for that environment (API: Render env var then the service restarts; web: Vercel env var then redeploy; mobile: set for the EAS profile then a new store build, because `@sentry/react-native` is a native module).
- Off, per environment: remove the DSN (leave it unset or empty) and restart/redeploy. This is the data-residency switch: where a client requires KSA residency ("on client order"), run that environment with no DSN and nothing leaves it. Use a separate Sentry project per environment so one can be off while another is on.
- Tracing stays off (rate 0) unless a rate is set. Session replay, screenshots and view-hierarchy attachments are not enabled on any platform.

### 3.3 What is guaranteed, and the tests that prove it

Each platform has exactly one scrub point (`BeforeSend` or the equivalent). It:
- sets `SendDefaultPii` false (no IP, user, cookies);
- drops request bodies, cookies, headers and query strings, and the user object;
- drops breadcrumbs that carry a URL with a query string or any personal-data shape (and console breadcrumbs on web and mobile);
- redacts, in every remaining string, bearer tokens, JWTs, emails, IBANs, Saudi national / iqama ids (10 digits starting 1 or 2) money amounts (any decimal amount, and a whole number after a pay word in English or Arabic) and other runs of 6+ digits;
- tags a tenant only as an opaque GUID (`tenant_id`); a tenant or company name, user name or email is refused.

| Platform | Scrub | Tests |
|----------|-------|-------|
| API | `Infrastructure/Operations/SentryRegistration.cs` over `SentryPrivacy.cs` | `Zayra.Api.Tests/SentryErrorTrackingTests.cs`: an event holding an IBAN, national id, iqama, email, a salary in a payroll URL query and a Bearer token keeps none of them; DSN unset registers nothing; `Observability` names no Sentry type. |
| Web | `frontend/src/lib/errorTracking/scrub.ts` and `options.ts` | `frontend/unit/errorTrackingScrub.spec.ts` (same event shapes); `frontend/e2e/error-tracking-dormant.spec.ts` (lazy imports, no committed DSN, no source-map upload without a token). |
| Mobile | `mobile/src/observability/reportError.ts` (`scrubSentryEvent`, `scrubSentryBreadcrumb`), installed only by `reportError`'s adapter | `mobile/tests/reportError.test.ts`. |

API specifics: HTTP responses are unchanged (`ExceptionResponseMapper` still builds them). Errors the global handler maps below 500 (business-rule failures) are not reported. Failed outbound HTTP requests are not captured (their URLs carry ids). Web: with `OTEL_EXPORTER_OTLP_ENDPOINT` also set, Sentry skips its own OpenTelemetry setup and leaves tracing to `@vercel/otel`. Web has no script/connect Content-Security-Policy today (only `frame-ancestors`), so no ingest host needs allowing; if a `connect-src` is ever added it must list the ingest host only when a DSN is set (`error-tracking-dormant.spec.ts` fails to prompt that review).

### 3.4 Region and data location

Create the Sentry organization in the EU region and record it in the sub-processor register (PDPL transfer rule). The region cannot be changed later. If a client forbids it, run that environment with no DSN (3.2).

## 4. Step 3: logs and metrics

- Render log stream: Render dashboard > Settings > Log Streams, add a syslog/HTTPS endpoint from Better Stack, Papertrail, or Grafana Cloud Loki (any gives 7-30 d searchable retention; Render keeps only a short window). Logs are already JSON; filter on `level=Error`.
- Metrics/traces: set `OTEL_EXPORTER_OTLP_ENDPOINT` and `OTEL_EXPORTER_OTLP_HEADERS` on the Render service (values from Grafana Cloud free or Better Stack; the header contains a token, so enter it in the dashboard, never in `render.yaml`). Add `OTEL_SERVICE_NAME=kynexone-api`. Do the same for Vercel for the web.
- Keep personal data out of logs: before enabling the drain, grep a sample of the last day of logs for email, IBAN, and national id patterns.

## 5. Step 4: alerts and routing

| Alert | Condition | Severity | Route |
|---|---|---|---|
| API down | `/health/ready` fails 2 checks | Page | Phone push/SMS + email |
| Not ready after deploy | 503 for over 5 min | Page | Phone + email |
| Error spike | Sentry: more than 20 events/10 min or any new issue tagged payroll | High | Email + Slack/Teams webhook |
| 5xx rate | over 2 percent for 5 min (from logs/OTEL) | High | Email + chat |
| p95 latency | over 3 s for 10 min | Medium | Chat |
| Memory | over 80 percent for 10 min (Render metrics alert) | High | Email (OOM kills caused the earlier 502 incident) |
| Pending migrations | `pendingMigrations` above 0 in `/health/ready` for 10 min | High | Email |
| DR drill overdue | no drill artifact in 35 days | Low | Calendar reminder |
| Backup failure | `DR documents backup` workflow failed | High | GitHub email on failed run |

Routing: one primary (owner), one named backup, a shared inbox alias for the audit trail. Alerts go to chat for working hours and to phone push only for the two Page rows, to avoid fatigue. Owner timezone is US Eastern; clients are in Riyadh, so staff the Riyadh business-day morning explicitly.

## 6. What the owner must create (checklist)

Accounts and settings:
1. Uptime service account (Better Stack or UptimeRobot) with 3 monitors and alert contacts; phone app installed.
2. Sentry: create the organization in the EU region; create 3 projects (`kynexone-api` .NET, `kynexone-web` Next.js, `kynexone-mobile` React Native); set the DSNs per section 3.1 (Render, Vercel, EAS); optionally an auth token for Vercel source maps; accept the data-processing terms; record Sentry as a sub-processor (region, DPA link).
3. Log/metrics backend account (Grafana Cloud free or Better Stack), one log source, one OTLP token.
4. Render: add Log Stream destination; add env vars `SENTRY_DSN`, `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_HEADERS`, `OTEL_SERVICE_NAME`; enable Render's memory/CPU alert notifications to the owner email.
5. Vercel: env vars `NEXT_PUBLIC_SENTRY_DSN`, `SENTRY_DSN`, `SENTRY_AUTH_TOKEN`, and the OTEL variables.
6. Chat webhook (Slack or Teams) for non-page alerts.
7. GitHub: confirm notifications for failed workflow runs are on for the owner account.
8. Add the new vendors to the sub-processor register with region and DPA link.
9. Neon: enable consumption/limit alerts and note the PITR retention in the DR runbook.

The error-tracking code for API, web and mobile is in place and dormant (section 3); it needs only the DSNs. Until then steps 1, the Render log stream, and the Render memory alert already deliver most of the value and need no code.
