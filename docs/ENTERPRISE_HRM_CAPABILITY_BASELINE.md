# Enterprise HRM — Capability Baseline & Current-State Classification

**Program:** Enterprise HRM Product Construction and Production Certification
**Orchestrator:** Lead (this document is the register of record)
**Baseline commit:** `1986999` + working tree (Wave 0 stabilisation applied)
**Evidence environment:** local, .NET 8, Testcontainers Postgres, Docker up
**Test state at baseline:** **1600 passed / 0 failed**, EF model drift **zero**
**Date:** 2026-08-10

---

## 0. How to read this document

The baseline below is derived **independently** of the repository — from what a multi-entity
enterprise HRM must contain — and only then compared against the code. A route, a controller, a
table, a migration or a passing unit test is **never** counted as a capability.

Each capability carries exactly one **state** and exactly one **action**:

| State | Meaning |
|---|---|
| VERIFIED COMPLETE | Lifecycle exists end-to-end and is proven by evidence |
| PRESENT BUT UNPROVEN | Implemented and wired, but no acceptance evidence exists |
| PARTIALLY IMPLEMENTED | A defined hole remains inside an otherwise real capability |
| INCORRECTLY DESIGNED | Exists but violates a constitution rule; needs redesign |
| DISCONNECTED | Backend exists, no UI/nav — or UI exists with no backend |
| MISSING | No implementation of any kind |
| DEMO/HARDCODED | Works only with seeded or hardcoded data |

Actions: PRESERVE · TEST · COMPLETE · REDESIGN · CONNECT · BUILD · CONSOLIDATE · REMOVE · DEFER · CONFIGURE.

---

## 1. WAVE 0 — STABILISATION (executed, evidenced)

Wave 0 was not in the original plan. It was forced by ground truth: the repository could not be
certified at all because **the test project did not compile**, so the 1,269-test suite that the
program's confidence rests on had not run against the in-flight work.

| # | Defect found | Severity | Root cause | Resolution | Evidence |
|---|---|---|---|---|---|
| W0-1 | Test project failed to compile (16 errors) | **P0** | `NotificationService` moved to `IServiceScopeFactory` + `INotificationRecipientResolver`; 8 direct-construction sites in tests never updated | Added one `TestNotifications.For(db)` seam (`TestHelpers/TestNotificationService.cs`); all 8 sites routed through it | Test project builds, 0 errors |
| W0-2 | `GlJournalExport` carried `CompanyId` **without** `ICompanyScoped` | **P0 (isolation)** | New table modelled on `FinanceGlEntry`'s allow-list exemption; that exemption exists only because pre-B1b ledger rows are all NULL — reasoning does not transfer to a new table | ⚠︎ **REVERTED after review** — the fix was wrong. A NULL CompanyId here is not a backfill transient, it is the *meaning* of a group-wide export, so the scoped interface made group exports throw `company_scope_required`. Now a justified allow-list entry; read control is the controller's permission + `ScopeError` | `CompanyScopeBootAssertionTests` green; see closure evidence §9.1 R2 |
| W0-3 | **EF migration drift** — `employee_final_settlements`, `final_settlement_lines`, `payroll_runs.settles_final_settlements` had **no migration** | **P0** | Entities added without `ef migrations add`; identical failure mode to the documented 42703 tenant-wide outage class | Rebuilt snapshot from last migration's Designer model, generated `AddFinalSettlementPersistenceAndExportScope` | `has-pending-model-changes` → *No changes* |
| W0-4 | **31 unjustified `IgnoreQueryFilters()`** calls in new finance/notification code | **P1** | Bypass-justification discipline not applied to POD-D work | ⚠︎ **PARTLY WRONG** — the true POD-D figure is **37**, not 31; there are **two** cross-tenant reads, not one (the delivery drain also lacks a tenant predicate); and on the finance tables what is dropped is the **tenant** filter, not the company one, since those entities have no company filter at all. Annotations stand; the risk model was inverted | `BypassLintTests` green; see closure evidence §9.2 R5–R7 |
| W0-5 | 23 EOSB parity tests failing | **P1** | `/final-settlement` was deliberately hardened (cannot settle a non-leaver; entity must resolve; EOSB must be enabled). POD-A2 tests encoded the older, weaker contract | Fixtures now record the separation and legal entity they always implied; the contract change is documented in-test as a decision of record. **All EOSB figures unchanged** | 34/34 EOSB tests green |
| W0-6 | GL driver count assertion stale (24 vs 30) | **P2** | POD-D added 3 settlement earnings, 1 settlement deduction, 2 control accounts | Updated ledger comment **and added 4 routing goldens** proving settlement expense cannot collapse onto `EARN:OTHER` | `GlPhase2Tests` green |
| W0-7 | Period-close reconcile test stale | **P2** | POD-D4 added a second terminal gate (bank must confirm every record) | Test now proves *both* gates, plus a **new** test that a returned payment blocks close | `SettlementPeriodCloseTests` 18/18 |

**Wave 0 verdict: CLOSED** (engineering baseline only). **1616/1616** tests pass with zero skipped; zero
model drift; isolation guards green and negative-tested. Two defects in the Wave 0 work itself (a
`Take`-before-`Where` bug in the new bypass helper, and the W0-2 over-correction) were found by
independent review and are fixed with regression tests.

> **Resolved.** The POD-D work is committed on `wave0/engineering-baseline` (PR #43). See
> `docs/WAVE_0_CLOSURE_EVIDENCE.md` — and note §9 there, where independent review **falsified several
> ratings in this document**. Rows corrected below are marked ⚠︎ REVISED.

---

## 2. CURRENT-STATE INVENTORY (measured, not asserted)

| Dimension | Count |
|---|---|
| Backend C# files | 654 |
| Controllers | 102 |
| Entity models | 64 |
| EF migrations | 54 (after W0-3) |
| Test files / tests | 152 / 1600 |
| Frontend pages | 64 |
| Frontend components | 155 |
| Navigation entries | 24 across 5 groups |
| Background workers | **3** (`QiwaSyncWorker`, `AiInsightEngine`, `NotificationDeliveryWorker`) |

---

## 3. CAPABILITY BASELINE vs REPOSITORY

### Wave 1 — Platform admin, tenancy, entitlements, security, observability, recovery

| Capability | State | Action | Evidence / gap |
|---|---|---|---|
| Platform super-admin, tenant CRUD, bulk tenant ops | VERIFIED COMPLETE | PRESERVE | `PlatformController`, platform route set, prior console audit |
| Subscription gating / entitlement enforcement (backend) | PRESENT BUT UNPROVEN | TEST | `SubscriptionGuardFilter` → 402; no suspend/resume journey evidence |
| Tenant provisioning bundle (zero-state creation) | PRESENT BUT UNPROVEN | TEST | `TenantProvisioningBundle`; never exercised as a certified zero-state journey |
| RBAC — permission-first registry, scope model | VERIFIED COMPLETE | PRESERVE | `[HasPermission]` enforcement, 732-test RBAC pass |
| Tenant isolation (read filter, write guard, audience gate) | VERIFIED COMPLETE | PRESERVE | Fail-closed filter; `BypassLintTests` now clean |
| Company/entity scope enforcement | VERIFIED COMPLETE | PRESERVE | `CompanyScopeBootAssertion` boot guard + W0-2 fix |
| Immutable audit chain (SHA-256, append-only) | VERIFIED COMPLETE | PRESERVE | `AuditService` chain + DB append-only guard |
| SSO federation login (SAML/OIDC) | **INCORRECTLY DESIGNED** | **REDESIGN** | `EnterpriseIdentityController` ACS + authorize return **501**. Presents as shipped; no IdP login exists |
| SCIM v2 provisioning | PRESENT BUT UNPROVEN | TEST | Real create/patch/deactivate; no UI, no acceptance evidence |
| **Observability / APM / tracing** | **MISSING** | **BUILD** | Default `ILogger` only. Constitution #18 (every failure traceable) is unmet |
| **Durable job / queue platform** | **MISSING** | **BUILD** | Only 3 ad-hoc `BackgroundService`. Payroll/WPS/import run **in-request** |
| **Backup / tested restore drill (RTO/RPO)** | **MISSING** | **BUILD** | No drill on record; PITR assumed, never proven |
| **Data residency / PDPL pinning** | **MISSING** | **CONFIGURE** | Storage region `auto`; no per-tenant residency |
| Retention / legal hold / purge | PARTIALLY IMPLEMENTED | COMPLETE | Redaction primitives exist; no DSAR export, no automated retention |

### Wave 2 — Companies, org structure, policies, approvals

| Capability | State | Action | Evidence / gap |
|---|---|---|---|
| Companies / legal entities / branches / locations | VERIFIED COMPLETE | PRESERVE | Full CRUD + governance controllers |
| Departments, designations, grades, cost centres, positions | VERIFIED COMPLETE | PRESERVE | Dedicated controllers, surfaced under `/setup` |
| Establishment matrix (per-level staffing budgets) | VERIFIED COMPLETE | PRESERVE | Hard-block enforcement + tests |
| Calendars / work week | PRESENT BUT UNPROVEN | TEST | `WorkWeekService` exists; no boundary/holiday certification |
| Approval policies + maker-checker | VERIFIED COMPLETE | PRESERVE | Self-approval blocked (403), two-user trail |
| Org structure import | PRESENT BUT UNPROVEN | TEST | Controller exists; no negative/rollback evidence |

### Wave 3 — Roles, privileges, field protection

| Capability | State | Action | Evidence / gap |
|---|---|---|---|
| Role/permission matrix, custom roles | VERIFIED COMPLETE | PRESERVE | Tenant-scoped matrix save |
| API + UI permission gating | VERIFIED COMPLETE | PRESERVE | `[HasPermission]` across lifecycles |
| **Field-level access matrix** | PARTIALLY IMPLEMENTED | COMPLETE | Employee snapshots previously unmasked (Group audit P0); no field-access matrix artifact |
| Export / report / document permission coverage | PARTIALLY IMPLEMENTED | COMPLETE | Constitution #9 requires *all* surfaces; exports partially gated |

### Wave 4 — Employee master and lifecycle

| Capability | State | Action | Evidence / gap |
|---|---|---|---|
| Employee master, country×nationality field engine (6 GCC) | VERIFIED COMPLETE | PRESERVE | `EmployeeFieldRegistry`, `GccReadinessFloor` |
| Completeness-gated activation | VERIFIED COMPLETE | PRESERVE | 422 `employee_not_activatable` + tests |
| Bulk intake / import (accept-never-block) | VERIFIED COMPLETE | PRESERVE | Per-row errors, duplicate detection |
| Documents + storage | **INCORRECTLY DESIGNED (live)** | **REDESIGN** | Prod runs `Storage__Provider=local` + `AllowEphemeral=true` → **uploads lost on restart** |
| Transfers, promotions, probation, confirmation | PRESENT BUT UNPROVEN | TEST | Controllers exist; effective-dating not certified |
| Compensation history / salary-structure versioning | VERIFIED COMPLETE | PRESERVE | Effective-dated assignments |
| User provisioning from employee | PRESENT BUT UNPROVEN | TEST | Auto work-email derivation shipped |

### Wave 5 — Time, attendance, leave

| Capability | State | Action | Evidence / gap |
|---|---|---|---|
| Shifts, rosters, roster planner (AI-advisory) | PRESENT BUT UNPROVEN | TEST | Guardrailed planner; AI opt-in respected |
| Attendance capture, exceptions, corrections | PRESENT BUT UNPROVEN | TEST | Controller + models; no 90-day volume evidence |
| Overtime | PRESENT BUT UNPROVEN | TEST | Dedicated controller + feature key |
| Leave (12 controllers) | PRESENT BUT UNPROVEN | TEST | Deep implementation; open P0s from Leave audit remain |
| **Half-day unit deduction, split shifts** | MISSING | DEFER | Requirements-gated; deferred pending real client need |

### Wave 6 — Payroll, finance, reconciliation *(the moat)*

| Capability | State | Action | Evidence / gap |
|---|---|---|---|
| Run lifecycle + atomic processing | VERIFIED COMPLETE | PRESERVE | Single execution-strategy tx; reprocess guard |
| Maker-checker separation of duties | VERIFIED COMPLETE | PRESERVE | Self-approve 403 |
| Deterministic component pay engine | VERIFIED COMPLETE | PRESERVE | Golden-master + engine-equivalence tests |
| GOSI/statutory reconciliation | VERIFIED COMPLETE | PRESERVE | **POD-A1** unified Source/codes — deducted == report == GL |
| Single authoritative EOSB engine | ⚠︎ REVISED — **PARTIALLY IMPLEMENTED** | **COMPLETE** | Formula is shared, but `ComputeEndOfServiceAsync` hardcodes `jur = "mainland"` and is the only caller, so the registered **DIFC calculator is unreachable**; unknown countries fall through to a silent **0 gratuity** with no fail-closed guard; wage base is basic-only, which the code concedes is not Art. 84 "last wage". Parity tests pass only because every fixture sets `IsActive = true` (D7) |
| Payroll trail on immutable hash chain | VERIFIED COMPLETE | PRESERVE | **POD-A3** |
| Balanced GL, idempotent posting, contra-on-void | VERIFIED COMPLETE | PRESERVE | Lock gate 422 `gl_unbalanced` |
| Settle + remit (control accounts clear) | ⚠︎ REVISED — **PARTIALLY IMPLEMENTED** | COMPLETE | `GlControlAccounts.CheckAsync` omits both new control accounts — `SETTLEMENT_PAYABLE` (2320) and `EOSB_PROVISION` (2310) |
| Off-cycle / supplementary / correction runs + hold-out | VERIFIED COMPLETE | PRESERVE | **POD-B2** |
| Void → replacement recovery | VERIFIED COMPLETE | PRESERVE | **POD-B3** |
| Mid-month proration + retro arrears | VERIFIED COMPLETE | PRESERVE | **POD-C3** |
| Final settlement → payable → GL → payment | PRESENT BUT UNPROVEN | TEST | **POD-C1/D** persisted, entity-resolved, leaver-gated. Needs a real termination journey |
| Monthly EOSB liability provisioning to GL | ⚠︎ REVISED — **MISSING** | **BUILD** | `GlEventTypes.EosbProvisionAccrual` is written by **nothing**; the ledger consumes a stream no code produces |
| GL/ERP journal export (CSV/IIF/Oracle) | PRESENT BUT UNPROVEN | TEST | 3 formatters + frozen-line-set regeneration |
| Bank/WPS confirmation import + reconciliation | PRESENT BUT UNPROVEN | TEST | `BankConfirmationService` + 2 parsers; blocks close on returns |
| **WPS/Mudad real acceptance** | **PRESENT BUT UNPROVEN** | **TEST** | Layout self-labelled `SIF_SA_V1`; **no bank/ministry has ever accepted a file** |
| **Payroll at scale (5,000 emp) / durable execution** | **INCORRECTLY DESIGNED** | **REDESIGN** | Runs in-request; times out at scale; no restart survival |
| **Multi-currency, garnishments** | MISSING | BUILD | Real payroll ops gap |
| Statutory rate currency (GOSI ceiling etc.) | PARTIALLY IMPLEMENTED | CONFIGURE | Every rate marked `VERIFY`; no signed compliance memo |

### Wave 7 — Recruitment → onboarding → conversion

| Capability | State | Action | Evidence / gap |
|---|---|---|---|
| Requisitions, candidates, interviews, scorecards, offers | PRESENT BUT UNPROVEN | TEST | 11 controllers |
| Offer → application state continuity | VERIFIED COMPLETE | PRESERVE | `BusinessLogicContinuityTests` |
| **Exactly-once candidate→employee conversion** | PRESENT BUT UNPROVEN | TEST | Constitution #8 requires proof under retry/concurrency — none exists |
| Onboarding | PRESENT BUT UNPROVEN | TEST | Route exists under recruitment |

### Wave 8 — Benefits, loans, expenses, assets, performance, learning, ER

| Capability | State | Action | Evidence / gap |
|---|---|---|---|
| Loans & advances | VERIFIED COMPLETE | PRESERVE | GL-posting, post-once probes |
| Performance (competency, calibration, PIP, 360) | PRESENT BUT UNPROVEN | TEST | 11 controllers, genuinely deep |
| **Benefits / GCC medical insurance** | ⚠︎ REVISED — **INCORRECTLY DESIGNED** | **REDESIGN** | Backend is real, but the model cannot represent GCC medical: no enrolment↔dependant link, no insurer/policy, no renewal or premium model, eligibility **fails open**, no duplicate-enrolment constraint, payroll link is annotation-only, no exit cascade. Pilot disposition: **feature-flag off**, not "connect" |
| **Expenses & reimbursement** | **MISSING** | **BUILD** | 0 files |
| **Assets / equipment custody** | **MISSING** | **BUILD** | 0 files |
| **Learning / LMS + certification expiry** | **MISSING** | **BUILD** | 0 files |
| **Engagement / surveys / eNPS** | **MISSING** | **BUILD** | 0 files |
| **Succession / 9-box / skills taxonomy** | **MISSING** | **BUILD** | 0 files |
| **Disciplinary / grievance / whistleblower** | **MISSING** | **BUILD** | 0 files |
| **Timesheets / project cost allocation** | **MISSING** | DEFER | 0 files; segment-specific |
| **Contractor / EOR** | **MISSING** | DEFER | 0 files; strategy fork |
| **GCC air-ticket / repatriation entitlement** | **MISSING** | BUILD | 0 files; contractually mandated in Gulf |

### Wave 9 — Termination, settlement, offboarding, rehire

| Capability | State | Action | Evidence / gap |
|---|---|---|---|
| Offboarding lifecycle + exit cascade | PRESENT BUT UNPROVEN | TEST | `OffboardingController`, ex-employee archive |
| Final settlement (see Wave 6) | PRESENT BUT UNPROVEN | TEST | Now leaver-gated and entity-resolved |
| **Access revocation on exit** | ⚠︎ REVISED — **PRESENT BUT UNPROVEN** | TEST | Earlier claim was **wrong**. Real revocation exists and is tested: user deactivated, `AccessMode = NoLogin`, all links disabled, **all refresh tokens revoked**. Narrower defects remain (D4) |
| Historical retention + rehire | ⚠︎ REVISED — **MISSING** | **BUILD** | `RehireEligible` is written and displayed but **read by nothing**; no rehire endpoint; the recruitment→draft→approve path has no duplicate-person probe |

### Wave 10 — Reports, mobile, integrations, AI, certification

| Capability | State | Action | Evidence / gap |
|---|---|---|---|
| Report catalog, saved + scheduled reports | VERIFIED COMPLETE | PRESERVE | Frequency/delivery/format + executions |
| **Report reconciliation to source** | PRESENT BUT UNPROVEN | TEST | Constitution #12; only GOSI tie-out proven |
| Self-serve BI / pivot builder | MISSING | DEFER | No report builder |
| **Mobile app / PWA** | **DISCONNECTED** | **CONNECT** | `MobileController` BFF is real; **no client exists** |
| Notifications — email | ⚠︎ REVISED — **PRESENT BUT UNPROVEN** | **CONFIGURE** | Enqueue-only design is sound, but SMTP is unconfigured in `render.yaml` and sends are silently dropped. **All four channels are unconfigured out of the box** |
| Notifications — SMS / WhatsApp / push | **DISCONNECTED** | **CONNECT** | Dispatchers + worker + ledger built; **all three providers are `Null*` stubs** reporting `not_configured` |
| Public API / webhooks | MISSING | DEFER | Ecosystem play |
| Accounting/ERP + bank connectors | PARTIALLY IMPLEMENTED | COMPLETE | Journal export + confirmation import exist; no live connector |
| AI — advisory, opt-in, governed | VERIFIED COMPLETE | PRESERVE | Opt-in beside manual path honoured |
| **i18n / Arabic RTL** | **MISSING** | **BUILD** | English-only; fails KSA-gov conformance |
| **Accessibility WCAG 2.1 AA** | PARTIALLY IMPLEMENTED | COMPLETE | 58 modals, 4 with dialog semantics |

---

## 4. PRODUCT GAP REGISTER — ranked

### P0 — blocks any real-data pilot

| # | Gap | Wave | Why it blocks |
|---|---|---|---|
| G1 | Ephemeral document storage in prod | 1/4 | Contracts, IDs, offer letters **lost on every restart**; violates KSA/GOSI retention |
| G2 | No durable job execution; payroll runs in-request | 6 | Times out at pilot scale; no restart survival; no double-execution lease |
| G3 | No observability/APM | 1 | Cannot detect or diagnose a prod incident (Constitution #18) |
| G4 | No tested restore drill / stated RTO-RPO | 1 | Auditor/CFO fails this on sight |
| G5 | SSO federation returns 501 | 1 | Reads as shipped; enterprise buyer's IdP cannot log in |
| G6 | Data residency unpinned | 1 | Blocks gov/banking/energy under PDPL |
| G7 | WPS file never accepted by a real bank/Mudad | 6 | The core compliance claim has **zero** external evidence |
| G8 | Access revocation on exit is a checkbox | 9 | Terminated staff may retain live access |

### P1 — required for reliance

| # | Gap | Wave |
|---|---|---|
| G9 | Benefits module dark (backend with no UI) | 8 |
| G10 | Notification SMS/WhatsApp/push providers are stubs | 10 |
| G11 | Exactly-once candidate→employee conversion unproven | 7 |
| G12 | Field-level access matrix incomplete | 3 |
| G13 | Statutory rates lack signed compliance confirmation | 6 |
| G14 | Report-to-source reconciliation unproven | 10 |
| G15 | Mobile BFF has no client | 10 |

### P2 — completeness / market

Expenses · Assets · Learning · Engagement · Succession · Disciplinary · Air-ticket entitlement ·
Kuwait/Oman/Bahrain country packs · multi-currency · garnishments · i18n/RTL · WCAG · public API.

---

## 5. CONSTITUTION COMPLIANCE

| # | Rule | Status |
|---|---|---|
| 1 | Tenant isolation absolute | **PASS** — fail-closed filter; all 31 bypasses now justified and verified |
| 2 | Company scope backend-enforced | **PASS** — boot assertion + W0-2 fix |
| 3 | Controlled state machines | PASS for payroll/WPS/ERP; unproven elsewhere |
| 4 | Effective-dated history | PASS for compensation; unproven for org/transfers |
| 5 | Approval decisions immutable | PASS |
| 6 | Payroll deterministic + reconcilable | **PASS** — A1/A2/A3/B1–B3/C3 closed the divergences |
| 7 | Retries cannot duplicate | PASS in payroll/GL; **unproven** in recruitment conversion |
| 8 | Candidate→employee exactly once | **UNPROVEN** |
| 9 | Permissions cover all surfaces | ⚠︎ **VIOLATED** — 1,034 `[Http*]` actions vs **128** `[HasPermission]` and **371** `[Authorize(Roles=…)]` across 77 of 102 controllers. String-role auth is not permission-first, and tenant custom roles do not satisfy it |
| 10 | Entitlements backend-enforced | PASS (unproven by journey) |
| 11 | Country rules versioned + fail safe | ⚠︎ **VIOLATED** — payroll and WPS block unsupported countries; **EOSB does not**, and silently pays 0 |
| 12 | Reports reconcile | PARTIAL — only GOSI proven |
| 13 | No integration shown live without evidence | **VIOLATED** — WPS is self-labelled; SSO reads as shipped at 501 |
| 14 | No frontend-only or backend-only completeness | **VIOLATED** — Benefits, Mobile, notification channels |
| 15 | No demo/placeholder in prod | PASS — demo seeder disabled |
| 16 | Destructive actions controlled | PARTIAL — audit yes; retention/legal-hold/purge incomplete |
| 17 | AI advisory + human-supervised | **PASS** |
| 18 | Failures traceable | **VIOLATED** — no APM/tracing |

---

## 6. FINAL VERDICTS (this baseline)

| Domain | Verdict | Basis |
|---|---|---|
| Product completeness | **NO-GO** | 12 enterprise capability areas absent; 3 built-but-disconnected |
| Business-logic completeness | **CONDITIONAL GO** | Payroll/GL/EOSB deep and correct; lifecycle breadth unproven |
| Cross-module connectivity | **NO-GO** | Benefits, Mobile, notification channels disconnected |
| Functional correctness | **CONDITIONAL GO** | 1600/1600 green, but coverage ≠ journey evidence |
| **Payroll correctness** | **CONDITIONAL GO** | Strongest area. Blocked only on G7 (no real WPS acceptance) and G2 (scale) |
| Security | **CONDITIONAL GO** | Isolation strong and now lint-clean; G5/G8 open |
| Tenant/company isolation | ⚠︎ REVISED — **CONDITIONAL GO** | Guards are green and negative-tested, but 179 of 209 bypasses are the register's own "debt, not assurance", two user-reachable controllers carry open risk, and both guard suites pass vacuously if path resolution fails (D14) |
| Data integrity | **GO** | Hash-chained audit, idempotent GL, zero drift |
| Performance | **NO-GO** | No scale evidence; in-request payroll |
| Reliability & recovery | **NO-GO** | G2, G3, G4 |
| UX / accessibility / localisation | **NO-GO** | No i18n/RTL; WCAG gaps |
| **Pilot readiness** | **NO-GO** | G1–G8 |
| **Production readiness** | **NO-GO** | As above |

**Bottom line.** The payroll-and-finance core is genuinely enterprise-grade and, after Wave 0, is
provably self-consistent: one EOSB engine, reconciling GOSI, balanced idempotent GL, tamper-evident
trail, recoverable months, and now a settlement that actually reaches the ledger. What stands
between this and a pilot is **not** payroll logic. It is the operational floor beneath it —
durable execution, durable storage, observability, a tested restore, and one real bank acceptance —
plus three modules that were built and never plugged in.

---

## 7. NEXT WAVE (recommended execution order)

1. **Commit the working tree.** 3,750 green, drift-free lines are currently unprotected.
2. **G1 durable storage** — provision region-pinned S3/R2, remove `AllowEphemeral`. Smallest P0, largest data-loss exposure.
3. **G2 durable job execution** — adopt the `QiwaSyncWorker` shape (claim-before-work lease, retry, progress) in front of Process / Lock-GL / GenerateWps / import.
4. **G3 observability** — structured logs + traces + correlation id; this is what makes every later wave debuggable.
5. **G9/G10 CONNECT the dark modules** — Benefits UI and one real notification provider. Cheapest capability gain per line in the repo.
6. **Zero-state certification journey** — tenant → entities → 1,250 employees → 90 days attendance → payroll month → termination → rehire, with evidence captured per criterion.
