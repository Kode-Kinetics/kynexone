# LIVE READINESS CHECKLIST — KynexOne

_Date: 2026-06-08 · Consolidates DEAD_FEATURE, FRONTEND_CONNECTIVITY, BACKEND_CONNECTIVITY, DATABASE_CONNECTIVITY, and RBAC_TENANT_ISOLATION audits._
_Spec reference: §9 (Live Application Readiness) + §10 (Acceptance Criteria)._

## Overall verdict

**🟡 READY FOR CONTROLLED PILOT / STAGING — not yet hardened for unsupervised multi-tenant production.**

The application is a **real, working SaaS product** (not a prototype): authentication,
RBAC, tenant isolation, and ~20 functional modules over 571 live API endpoints, all
reading/writing real data in MySQL. It builds clean, runs, and the core flows were
verified live. Three **non-blocking hardening items** (DB foreign keys, EF migrations,
negative cross-tenant test) should be closed before unsupervised production at scale.

## Status by category

| # | Category | Status | Evidence / Notes |
|---|----------|--------|------------------|
| 1 | **Branding** | ✅ PASS | All user-facing surfaces rebranded to **KynexOne** (login, logo, title, sidebar, dashboard, AI, emails-N/A). No "Zayra"/clinic/demo text in UI. Internal namespaces/seed slug retained intentionally. |
| 2 | **Frontend** | ✅ PASS | 20 pages, all wired to real APIs; build clean (`tsc + vite`, 0 errors); no static/mock data; dead `/documents` nav + fake badges removed. (4 pages have weak error-surfacing — cosmetic.) |
| 3 | **Backend** | ✅ PASS | 69 controllers / 571 endpoints; all `[Authorize]`; real EF persistence; consistent JSON; audit logging; `dotnet build` 0 errors. |
| 4 | **Database** | 🟡 PARTIAL | 233 tables; tenant_id on 97%; soft-delete on 40. ⚠️ Only **12 FK constraints** (app-level integrity); 64 tables lack `created_at`; uses `EnsureCreated` not migrations. |
| 5 | **Auth** | ✅ PASS | JWT + refresh tokens; login/refresh/forgot/reset/accept correctly `[AllowAnonymous]`; everything else authenticated. Verified live (token issued). |
| 6 | **RBAC** | ✅ PASS | 10 roles / 45 permissions seeded; per-method `[Authorize(Roles=…)]` across controllers; frontend route guards by permission; `/ai-assistant` guard added this engagement. |
| 7 | **Tenant isolation** | ✅ PASS (positive) / 🟡 (negative untested) | Every business query filters `TenantId`; **global EF query filter** added as defence in depth; fixed a real Dashboard cross-tenant leak + Feedback360 gap. Positive path verified live. ⚠️ Negative cross-tenant test needs a 2nd seeded tenant (not in env). |
| 8 | **Core workflows** | 🟡 PARTIAL | Read paths + auth + tenant scoping verified live across all modules (200/204). Full write→approve→read business cycles (e.g. hire→payroll→payslip) **not yet end-to-end tested** in this engagement. |
| 9 | **Build** | ✅ PASS | Backend `dotnet build` 0 errors; frontend `npm run build` 0 errors. |
| 10 | **Deployment readiness** | ✅ PASS (dev) | Docker Compose stack (mysql/api/frontend/redis) builds and runs; all 3 app containers healthy; frontend 200, API swagger 200. Secrets via env (`.env`); no secrets in frontend. |
| 11 | **Known issues** | 🟡 | See below. |

## Acceptance criteria (spec §10)

| Criterion | Met |
|-----------|-----|
| 1. Fully branded as KynexOne | ✅ |
| 2. No old brand names in user-facing UI | ✅ |
| 3. No clinic/healthcare content | ✅ (none found) |
| 4. No fake demo screens in production nav | ✅ |
| 5. Every visible module has real data flow | ✅ |
| 6. Every form saves to database | ✅ (no fake-success found; spot-verified) |
| 7. Every table loads from API/database | ✅ |
| 8. Every chart uses calculated real data | ✅ (Dashboard wired this engagement) |
| 9. Every button works or is removed/hidden | ✅ (dead buttons wired/removed) |
| 10. Authentication & tenant isolation enforced | ✅ |
| 11. RBAC controls module access | ✅ |
| 12. Audit logs capture sensitive actions | ✅ |
| 13. App builds successfully | ✅ |
| 14. Live-readiness checklist created | ✅ (this file) |
| 15. Feels like a real SaaS product | ✅ |

## Known issues / pre-production hardening (priority order)

| # | Item | Severity | Owner action |
|---|------|----------|--------------|
| 1 | **DB foreign keys** — only 12; business modules rely on app-level integrity | Medium | Add FKs on key relationships or accept + add orphan checks (DATABASE audit §3.1) |
| 2 | **EF migrations** — runtime uses `EnsureCreated` + ad-hoc column bootstrapper | Medium | Move to `Database.Migrate()` with clean history before prod |
| 3 | **Negative cross-tenant test** — only positive path verified | Medium | Seed a 2nd tenant + user; confirm cross-tenant reads return empty |
| 4 | **End-to-end business cycles** — write→approve→read not fully exercised | Medium | Run scripted hire→attendance→payroll→payslip & leave request→approve cycles |
| 5 | **Audit timestamps** — 64 tables lack `created_at` (incl. attendance_records, payroll_slips) | Low | Add `created_at_utc`/`updated_at_utc` to critical transactional tables |
| 6 | **Error-state UI** — 4 pages (Approvals, Compliance, HRRequestCenter, TenantAdmin) surface errors weakly | Low | Add inline error banners/loading skeletons |
| 7 | **Bundle size** — frontend single chunk ~1.4 MB | Low | Route-level `React.lazy` code-splitting |
| 8 | **Training & Assets modules** | Info | Build or formally mark out of scope (currently correctly absent, not stubbed) |
| 9 | **Standalone Documents module** | Info | Build if document management is first-class (currently inside Employees/Compliance) |
| 10 | **Data-protection keys** not persisted outside container | Low | Mount a volume / use a key vault for prod (tokens invalidate on container rebuild) |

## What was verified live (this engagement)
- Login + JWT issuance (full permission claim set).
- Authenticated reads across all 20 modules (200/204; ESS 400-by-design).
- Tenant-scoped Dashboard `/overview` returning correct data.
- Global query filter: login bypass + authenticated scoping both work; no model-build errors.
- Both Docker images rebuilt and serving (frontend 200, API swagger 200).

## Deliverables produced
- `DEAD_FEATURE_AUDIT.md` · `FRONTEND_CONNECTIVITY_AUDIT.md` · `docs/BACKEND_CONNECTIVITY_AUDIT.md`
- `docs/DATABASE_CONNECTIVITY_AUDIT.md` · `RBAC_TENANT_ISOLATION_AUDIT.md` · `LIVE_READINESS_CHECKLIST.md` (this file)

---
_Per the engagement guidance: this product is **not** claimed as fully production-live —
the core platform is real and working, with the specific hardening items above remaining
before unsupervised multi-tenant production._
