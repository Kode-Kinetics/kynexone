# Mandatory MFA for privileged users

Code: `backend-dotnet/Zayra.Api/Infrastructure/Auth/PrivilegedMfaPolicy.cs`.
Tests: `Zayra.Api.Tests/Security/PrivilegedMfaEnforcementTests.cs`, `MfaStatusHttpTests.cs`.

## Who

- **Every platform operator** (all six platform roles).
- **Tenant users holding any privileged permission** — through a role, an override or a company
  grant's role. Every permission is privileged **by default**; only an explicit list is not:
  self-service/own-record (`profile.*`, `ess.*`, `loans.self`, `attendance.kiosk`, `leave.write`,
  `overtime.write`), read-only views (`*.read`, `ai.query`, `ai.insights_view`) and a line manager's
  decisions on their own team (`manager.approve`, `approvals.write`, `approvals.decide`,
  `leave.approve`, `overtime.approve`, `performance.write`). See
  `PrivilegedMfaPolicy.NonPrivilegedPermissions`. Access-mode bundles (ESS, Mobile, Kiosk) are not
  counted. A new permission requires MFA until it is classified, and a test fails until it is.
  With the seeded roles that means: Admin, HR Director, HR Manager, HR Officer, Payroll Manager,
  Payroll Officer, Finance, Finance Approver, Compliance Officer, Supervisor and Recruiter.
  Not: Employee, Manager, HR Assistant, Auditor, Kiosk Operator.
- Unchanged: a tenant can still require MFA for *all* its users (`security_settings.mfa_required`).

## When

Enforcement starts on a date, never on deploy:

| Source | Meaning |
|---|---|
| `platform_config_entries` key `auth.privileged_mfa_enforce_from_utc` | Platform-wide date. Migration `RequirePrivilegedMfa` writes it as *migration time + 14 days*. Applies to operators and to every tenant without its own date. |
| `security_settings.privileged_mfa_enforce_from_utc` | Per-tenant date; wins over the platform date (earlier or later). Null = use the platform date. |

- **Before the date:** sign-in works. Un-enrolled privileged users see a prompt in the app and the
  platform console ("Set up two-step sign-in") that reuses the sign-in page's enrolment step.
- **From the date:** password sign-in returns an enrolment challenge instead of a session
  (`mfaEnrollmentRequired`), and refresh tokens from grace-period sessions stop rotating, so those
  sessions end within one access-token lifetime (30 min).
- No date anywhere (e.g. a database built without migrations) = prompt only, never blocked.

Change the dates (both audited, reason required; the date must be explicit UTC — `Z` or
`+00:00` — and at most 90 days ahead):
- Platform: `PUT /api/platform/security/privileged-mfa-enforcement` (Owner) `{ enforceFromUtc, reason }`.
- Tenant: `PUT /api/platform/tenants/{tenantId}/privileged-mfa-enforcement` (Owner/Admin)
  `{ enforceFromUtc | null, reason }`.

Roll-out check: `GET /api/platform/team` now shows `mfaEnabled` per operator.

## Recovery codes (platform operators)

Enrolling a platform factor returns **10 one-time recovery codes** (20 symbols, 100 bits, shown as XXXX-XXXX-XXXX-XXXX-XXXX; spaces, dashes and case are ignored on entry), shown once (sign-in page,
"Save your recovery codes"). Only SHA-256 hashes are stored (`platform_users.mfa_recovery_code_hashes`).
At the code step of sign-in, "Use a recovery code" (`POST /api/platform/auth/mfa/recovery/verify`)
accepts one instead of a TOTP code; each works once, misses count against the challenge's
five-attempt cap, and uses are audited (`platform.auth.mfa_recovery_code_used` / `_failed`).
`POST /api/platform/auth/mfa/recovery-codes/regenerate` (needs a current TOTP code) replaces them all.
`GET /api/platform/auth/mfa/status` reports `recoveryCodesRemaining`. Tenant users do not have
recovery codes yet; their lost-device path is the platform reset below.

## Break-glass

| Situation | Action |
|---|---|
| Tenant user lost their authenticator | Platform Owner/Admin: `POST /api/platform/users/{userId}/disable-mfa`. The user enrols a new device at next sign-in. |
| Operator lost their authenticator | Sign in with a **recovery code**, then re-enrol. Or another **Owner**: `POST /api/platform/team/{id}/reset-mfa` (never self). |
| Sole Owner lost the authenticator AND the recovery codes | Last resort, under change control, by someone with production database access: clear `mfa_enabled`, `mfa_secret_encrypted`, `mfa_configured_at_utc` and `mfa_recovery_code_hashes` on that `platform_users` row (and bump `updated_at_utc` to end its sessions). The Owner then enrols again at next sign-in. Record it in the incident log. |
| A customer cannot enrol in time | Move that tenant's date later (tenant endpoint above, at most 90 days ahead). |
| Enrolment itself is broken (outage) | Set `Auth__PrivilegedMfa__BreakGlassUntilUtc` (ISO-8601 UTC, e.g. `2026-11-02T18:00:00Z`) on the Render service and redeploy. Un-enrolled privileged users can then sign in without enrolling until that time; at most **7 days** ahead (a later or unparseable value is ignored, with a boot warning); the effective state is logged at boot and every login it lets through logs `[MFA-BREAK-GLASS]`. **It does not bypass the code step for users who are already enrolled** — that is what recovery codes are for. Remove it afterwards. |

Every factor change is audited (`auth.mfa.enabled`, `platform.auth.tenant_mfa_disabled`,
`platform.auth.mfa_reset_by_owner`, `platform.auth.mfa_enabled`, …).

## Sign-in abuse controls

Checked before any password hashing (`LoginAbuseGuard`), on tenant and platform sign-in:

| Control | Default | Config |
|---|---|---|
| Attempts per account (tenant + normalised email) | 10 per 15 min → 429 | `Auth__LoginThrottle__AccountAttempts`, `…AccountWindowMinutes` |
| Failed sign-ins per client IP | 20 per 10 min → 429 | `Auth__LoginThrottle__IpFailures`, `…IpWindowMinutes` |
| Concurrent PBKDF2 (600k) computations | 1, wait up to 4.5 s → 429 | `Auth__PasswordVerification__MaxConcurrency`, `…MaxWaitMs` |

Every 429 carries a jittered `Retry-After` of 2–6 s. State is in-process (one API instance).
The per-IP rate limits in `render.yaml` (`RateLimit__LoginPermitLimit`) are unchanged; once real
client IPs are on (below), 30/min per IP is a reasonable value to set there.

### Real client IP (off by default)

Browsers reach the API through the Vercel proxy, so the API sees Vercel's address and every user
shares one IP bucket. To partition on the real client IP:

1. Generate a secret: `openssl rand -base64 48`.
2. Vercel (frontend project, Production): set `PROXY_CLIENT_IP_SECRET` to it and redeploy. The
   Next.js middleware then forwards `X-KynexOne-Client-IP` and `X-KynexOne-Proxy-Secret` on `/api/*`.
3. Render (API service): set `Proxy__ClientIpSecret` to the same value in the dashboard (not in
   `render.yaml` — it is a secret) and deploy.
4. Check: sign in, then confirm the API's login activity shows your own IP, not Vercel's.

Without a matching secret the headers are ignored and `RemoteIpAddress` is used, as before. To roll
back, remove either variable. Rotate by setting the new value on Render first, then Vercel.
