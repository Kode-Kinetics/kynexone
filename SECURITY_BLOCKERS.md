# SECURITY_BLOCKERS.md

Release-blocking and residual security items from the 360° security audit (branch
`fix/system-audit-batch1`). Everything **fixable in code** was fixed in this pass, with tests
(see `backend-dotnet/Zayra.Api.Tests/Security/SecurityAuditBatch2Tests.cs`, `MfaTests.cs`,
`AuthServiceTests.cs`) and CI gates (`.github/workflows/ci.yml`, `codeql.yml`).

This file tracks what remains: items that require a **human action** (secret rotation, infra
config) that code cannot perform, and a small number of items **deliberately deferred** with a
documented compensating control. Each has an owner, risk, impact, and required remediation.

Status legend: 🔴 BLOCKER (must clear before/at go-live) · 🟠 SHOULD-FIX · 🟡 ACCEPTED-RISK (documented)

---

## 🔴 BLOCKER-1 — Rotate the leaked Neon Postgres password
- **Owner:** Platform / DevOps (repo owner)
- **Where:** `docker-compose.override.yml:5` (gitignored, **never committed** — confirmed via `git log --all -S`), present on developer disk.
- **Risk:** The connection string contains a **real** Neon credential for `neondb_owner` (a superuser-class role). Anyone who obtains this file (laptop backup, screen-share, Slack paste) gets full read/write/drop on all tenant HR + payroll PII.
- **Impact:** Total database compromise across every tenant.
- **Required remediation:**
  1. Rotate the password in the Neon console **now**.
  2. Move to a least-privilege Neon role (not the owner) for the app connection; use the pooler role.
  3. Store secrets in Render env vars / a secrets manager — never an on-disk override.
  4. Confirm the value was never pasted into chat/tickets; if it was, treat as disclosed.
- **Note:** This was already flagged for rotation in prior sessions. It is repeated here because it is still live and is the single highest-impact exposure.

## 🔴 BLOCKER-2 — Rotate the JWT signing key and provision it only via env
- **Owner:** Platform / DevOps
- **Where:** `appsettings.json:9` (committed placeholder `CHANGE_ME_…`), `Application/Auth/AuthOptions.cs:17` (same default), and the value is present in `bin/**/appsettings.json` build outputs.
- **Risk:** The committed value is a known string. If any environment ever runs on it, an attacker can forge a valid **platform-admin** JWT offline (tenant + platform tokens share one HS256 key — see ACCEPTED-RISK-1) and take over every tenant.
- **Mitigation already in code:** `Program.cs` now fails startup in **every non-Development** environment if the key is empty/placeholder/`<64 chars`, and if the two audiences collide. So a prod/staging deploy cannot silently boot on the placeholder.
- **Required remediation:**
  1. Generate a ≥64-char random secret; set `Jwt__SigningKey` in the Render dashboard (`render.yaml` already marks it `sync: false`).
  2. Since the placeholder is in git history, treat any past prod use as key-compromise and rotate.
  3. Add `**/bin/` to `.gitignore` so build outputs (which embed appsettings) are never committed.

## 🔴 BLOCKER-3 — Provision platform-admin as a hashed DB user; retire the plaintext env-var bootstrap
- **Owner:** Platform / DevOps
- **Where:** `Controllers/PlatformController.cs` env-var login branch; `PLATFORM_ADMIN_PASSWORD` env var (also baked into `docker-compose.yml:51` for local).
- **Risk:** The env-var bootstrap admin is the highest-privilege account (grants `Owner`). It authenticates against a single static string.
- **Mitigation already in code:** the compare is now **constant-time** (`CryptographicOperations.FixedTimeEquals`); DB platform-admins now have **brute-force lockout** (5 attempts → 15 min). The env path still has **no MFA** and no lockout.
- **Required remediation:**
  1. Create a real `PlatformUser` row (PBKDF2-hashed) and **enroll MFA** on it (the DB path already supports TOTP + lockout).
  2. Once a DB admin exists, unset `PLATFORM_ADMIN_PASSWORD` in prod so the plaintext branch is never reachable.
  3. Never ship a compose file with a usable default admin password against a shared DB.

## 🔴 BLOCKER-4 — Disable Render `autoDeploy` so the CI security gate cannot be bypassed
- **Owner:** Platform / DevOps
- **Where:** `render.yaml:7-9` (`autoDeploy: true`, `branch: main`, `plan: free`).
- **Risk:** Render redeploys on **every push to main** independently of GitHub Actions. The new CI security gates (secret-scan, dependency-scan, CodeQL — see below) are therefore **bypassable**: a push that fails them still ships via Render's own trigger.
- **Impact:** Vulnerable or secret-leaking code auto-deploys despite the gate. SOC2 CC8.1 change-management gap.
- **Required remediation:**
  1. Set `autoDeploy: false` in `render.yaml`.
  2. Deploy **only** via the CI `deploy-backend` job (fires the Render deploy hook) which now `needs: [backend-tests, frontend-typecheck, secret-scan, dependency-scan]`.
  3. Enable branch protection on `main` requiring those checks.
  4. Move production off `plan: free` (cold starts / shared infra are not SOC2-appropriate for payroll PII).

---

## 🟠 SHOULD-FIX-1 — Containers run as root; frontend image runs the dev server
- **Owner:** DevOps
- **Where:** all `Dockerfile`s (no `USER`); `frontend/Dockerfile:7` (`CMD npm run dev`); base images are floating tags, not digest-pinned; `kynexbridge/Dockerfile:5` (`npm install … || true`, no lockfile).
- **Risk:** A container breakout/RCE runs as root (max blast radius). A prod frontend image on `next dev` ships source maps, verbose errors, and no prod hardening.
- **Required remediation:**
  1. Add a non-root `USER` to every Dockerfile (aspnet 8 images ship `USER $APP_UID`; node images have `USER node`).
  2. Frontend: multi-stage `next build` + `next start` for any non-local deploy (or confirm prod frontend is Vercel and this image is dev-only).
  3. Pin base images by digest; use `npm ci` with a committed lockfile in `kynexbridge`.
  4. (Optional) add Trivy image scanning to CI.

## 🟠 SHOULD-FIX-2 — Frontend `latest` version pins → non-reproducible builds
- **Owner:** Frontend
- **Where:** `frontend/package.json` — `axios`, `lucide-react`, `recharts`, `@types/node`, `typescript` all pinned to `"latest"`.
- **Risk:** A compromised or breaking upstream release is pulled automatically on the next install; supply-chain exposure (`axios` in particular has a CVE history — the transitive `form-data` High fixed this pass came in via it).
- **Required remediation:** pin to caret ranges (e.g. `^1.7.0`) with the committed `package-lock.json` as the source of truth. CI already runs `npm ci`.

## 🟠 SHOULD-FIX-3 — Wire the frontend MFA-enrollment flow
- **Owner:** Frontend + Backend
- **Context:** The backend now **enforces** tenant-mandated MFA: when `SecuritySetting.MfaRequired` is true and a user hasn't enrolled TOTP, login returns `{ mfaEnrollmentRequired: true }` and **no session** (previously the flag was silently ignored — a false security control). This is safe to ship because `MfaRequired` defaults to **false**, so no current tenant is affected.
- **Required remediation:** the frontend should handle `mfaEnrollmentRequired` by routing the user into the MFA setup flow. Because `/api/auth/mfa/setup` currently requires an authenticated session, a limited enrollment-token flow (or admin-provisioned enrollment) is needed before a tenant enables `MfaRequired` for its users. Track as a feature.

## 🟠 SHOULD-FIX-4 — Add `UseForwardedHeaders` so per-IP rate limiting works behind the proxy
- **Owner:** Backend / DevOps
- **Where:** `Program.cs` rate-limiter partitions use `ctx.Connection.RemoteIpAddress`; no `UseForwardedHeaders` is configured.
- **Risk:** Behind Render/Vercel/Cloudflare, `RemoteIpAddress` may be the proxy IP, collapsing all users into one rate-limit bucket (weakening login/refresh brute-force protection) or, conversely, making the limiter ineffective.
- **Required remediation:** add `app.UseForwardedHeaders(...)` with a trusted-proxy allowlist (known proxy CIDRs) early in the pipeline. Consider a low-cost global limiter plus a tighter one on expensive endpoints (payroll compute, AI advisory).

---

## 🟡 ACCEPTED-RISK-1 — Single HMAC signing key for both tenant and platform tokens
- **Where:** `JwtTokenService.cs`, `PlatformController.cs`, `Program.cs` — both token classes are HS256-signed with `Jwt:SigningKey`; separation is by audience + `is_platform_admin` claim.
- **Why accepted for now:** This is only exploitable **after** the key leaks (BLOCKER-2). The `PlatformAdmin` policy requires the platform **audience** (validated against the signed key) **and** the claim, so within the running app a tenant token cannot reach platform routes — the boundary is real for a non-key-holding attacker.
- **Compensating control:** BLOCKER-2 (rotate + env-only key) removes the leak vector; the audience/claim policy (`Program.cs`) is a genuine in-app boundary; the new default-deny fallback policy fails closed.
- **Hardening (recommended, post-go-live):** issue platform-admin tokens with an **asymmetric** algorithm (RS256/ES256) or a **separate** `Jwt:PlatformSigningKey`, and bind the `PlatformAdmin` policy to a platform-only JWT scheme. Then a leak of the tenant key cannot forge platform tokens.

## 🟡 ACCEPTED-RISK-2 — Stateless access tokens: revocation lag up to token lifetime (30 min)
- **Where:** access tokens carry roles/permissions as claims; logout/lock/role-change revoke the **refresh** token but a live **access** token stays valid until expiry (≤30 min).
- **Why accepted:** This is standard stateless-JWT behavior; 30-min lifetime bounds it; refresh-token revocation stops session continuation.
- **Compensating control:** short access-token lifetime; refresh rotation with replacement chaining; audit trail.
- **Hardening (recommended):** add a `security_stamp` claim + a cached per-user stamp checked per request (bump on logout/lock/delete/password-change/role-change). Redis is already wired for the deny-list.

## 🟡 ACCEPTED-RISK-3 — JWT (incl. 14-day refresh) stored in browser `localStorage`
- **Where:** `frontend/src/contexts/AuthContext.tsx`, `frontend/src/api/client.ts` (and platform equivalents).
- **Why accepted:** documented trade-off in `SECURITY_POSTURE.md`; there is currently **no XSS sink** in the SPA (verified: no `dangerouslySetInnerHTML`, auto-translation output sanitized), so token theft has no live vector today.
- **Compensating control:** no XSS sinks; bearer-in-header (no ambient credential → classic CSRF N/A).
- **Hardening (recommended):** move the refresh token to an `httpOnly; Secure; SameSite=Strict` cookie + anti-CSRF token; keep the short-lived access token in memory. **Add a Content-Security-Policy** at the frontend layer (`next.config.ts` `headers()` currently sets only caching headers) — a CSP is the primary compensating control for the localStorage model.

## 🟡 ACCEPTED-RISK-4 — Two moderate npm build-tooling vulns (PostCSS via next)
- **Where:** `frontend` — `npm audit` reports 2 **moderate** (postcss CSS-stringify XSS, reachable only via a nonsensical downgrade to `next@9.3.3`).
- **Why accepted:** build-time tooling, not runtime-exploitable in the shipped app; the only "fix" the tool offers is a semver-major **downgrade** that would break the app (which runs `next@15`).
- **Compensating control:** the High `form-data` vuln **was fixed** this pass (`npm audit fix`, non-breaking). The CI gate runs at `--audit-level=high`, so these moderates don't block, but they are tracked here.
- **Hardening:** revisit when the next `next` minor bumps the transitive postcss.

## 🟡 ACCEPTED-RISK-5 — `PdfPig 0.1.9` parses untrusted PDFs
- **Where:** `Zayra.Api.csproj` — resume/document ingestion parses uploaded PDFs.
- **Why accepted:** no known CVE on this version today; upload size is capped (10 MB) and files are stored (not executed).
- **Hardening:** keep the package patched; add PDF-parse size/time limits and consider sandboxing the parse. Uploads now also have an extension/content-type allowlist gap noted for the generic employee-document path (SHOULD-FIX in the report) — worth aligning with the policy/logo upload allowlists.

---

## Fixed in this pass (for cross-reference — NOT blockers)

Code-level issues fixed with tests + a build/deploy gate so they cannot silently return:

| Area | Fix | Test |
|------|-----|------|
| **IDOR — Mobile** | Every `MobileController` endpoint resolves the caller's own `employee_id`; a colleague's payslip/salary/leave is `403` | `SecurityAuditBatch2Tests.Mobile_*` |
| **PII exposure** | `VisaTracking`/`Contracts` reads role-gated to HR (were open to any authenticated user) | `CompliancePiiControllers_AreRoleGatedAtClassLevel` |
| **BOLA cluster** | Scope guards on Loans/Advances/PIP/Probation/CompOff detail + LeaveBalances + 8 P2 detail reads | `Loans_Detail_*`, `Advances_Detail_*`, `Pip_*` |
| **CSV formula injection** | `Csv.Escape` neutralizes `= + - @ \t \r` on all exports | `CsvEscape_*`, `CsvBuild_*` |
| **SSRF** | Attendance device Pull/TCP: private/loopback/metadata blocked, no redirects, no body reflection | `SsrfGuard_*` |
| **MFA brute-force** | Challenge consumed after 5 wrong codes (was replayable for 5 min) | `VerifyChallenge_ConsumesChallengeAfterMaxWrongAttempts` |
| **Tenant-mandated MFA** | `MfaRequired` now enforced at login (was a no-op) | `Login_TenantRequiresMfa_*` |
| **Platform lockout** | DB platform-admin locks after 5 failures; env compare constant-time | `AuthServiceTests` lockout suite |
| **Impersonation audit** | `Impersonate` now writes an attributable audit record; tokens carry `act_sub`/`act_email` | (manual — `AdminAuditLog` write) |
| **Default-deny** | Global `FallbackPolicy` — a forgotten `[Authorize]` now fails closed (401) | public endpoints marked `[AllowAnonymous]` |
| **Rate limiting** | `forgot/reset/accept-invitation` + public pricing writes throttled | `PricingWriteEndpoints_HaveRateLimiting` |
| **Dependency (High)** | `System.IO.Packaging` pinned to patched 8.0.1; frontend `form-data` High fixed | CI `dependency-scan` gate |
| **CI gates** | secret-scan (gitleaks) + dependency-scan + CodeQL SAST, `deploy-backend` depends on them | `.github/workflows/` |
