# Mandatory MFA for privileged users

Code: `backend-dotnet/Zayra.Api/Infrastructure/Auth/PrivilegedMfaPolicy.cs`.
Tests: `Zayra.Api.Tests/Security/PrivilegedMfaEnforcementTests.cs`, `MfaStatusHttpTests.cs`.

## Who

- **Every platform operator** (all six platform roles).
- **Tenant users holding** Admin, HR Manager, HR Director, Payroll Manager, Payroll Officer,
  Finance or Finance Approver — as a role assignment or as an active company-scoped grant.
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

Change the dates (both audited, reason required):
- Platform: `PUT /api/platform/security/privileged-mfa-enforcement` (Owner) `{ enforceFromUtc, reason }`.
- Tenant: `PUT /api/platform/tenants/{tenantId}/privileged-mfa-enforcement` (Owner/Admin)
  `{ enforceFromUtc | null, reason }`.

Roll-out check: `GET /api/platform/team` now shows `mfaEnabled` per operator.

## Break-glass

| Situation | Action |
|---|---|
| Tenant user lost their authenticator | Platform Owner/Admin: `POST /api/platform/users/{userId}/disable-mfa`. The user enrols a new device at next sign-in. |
| Operator lost their authenticator | Another **Owner**: `POST /api/platform/team/{id}/reset-mfa` (never self). The operator enrols again at next sign-in. |
| A customer cannot enrol in time | Move that tenant's date later (tenant endpoint above). |
| Sole Owner locked out, or an enrolment outage | Set `Auth__PrivilegedMfa__BreakGlassUntilUtc` (ISO-8601 UTC) on the Render service and redeploy. Enforcement is suspended for everyone until then; at most **7 days** ahead (a later value is ignored); every login it lets through logs `[MFA-BREAK-GLASS]`. Remove the variable once the factor is reset. |

Every factor change is audited (`auth.mfa.enabled`, `platform.auth.tenant_mfa_disabled`,
`platform.auth.mfa_reset_by_owner`, `platform.auth.mfa_enabled`, …).
