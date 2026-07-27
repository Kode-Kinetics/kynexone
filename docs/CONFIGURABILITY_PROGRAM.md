# Zayra Configurability Program — Master Plan

**Status:** Approved program baseline (Program Director synthesis of 6 module audits + SaaS-consultant decisions + compliance & security guardrail registers)
**Date:** 2026-07-27
**Owner directive:** The product must become FULLY CONFIGURABLE — full CRUD (UI+API) on every module's master/config data, per-tenant (and where relevant per-company) scoping, no compile-time business constants where a client would expect a setting, seeded values are only DEFAULTS a client can override.
**Context:** DB is being wiped to schema + reference config + one admin. All schema drop/keep decisions are one-time-free during the wipe window; testing restarts from the clean slate after Wave 1.

Guardrail IDs referenced below: **A1–A12 / B1–B12 / C1–C7 / D1–D10** = Compliance Guardrail Register; **G1–G23** = Security Guardrail Register (both attached to this program; treat as normative).

---

## 1. Executive Summary

Across ~80 audited concepts in six modules, the verdict distribution is roughly: **~15 FULL** (companies, branches, devices, salary structures, payslip designer, holiday calendars, user management, entity scoping, security settings, branding, scorecard templates, advance policy), **~45 PARTIAL**, **~20 NONE**. The codebase's configurability problem is *not* missing data models — it is that the data models are ahead of the runtime. Three systemic failure classes account for almost every finding:

1. **Configuration theater — config that lies.** At least 15 surfaces are stored/editable but enforced nowhere: `CompanyTaxPolicy` (resolver registered in DI, zero consumers — `Program.cs:258`), `ApprovalAuthority` amount limits, both delegation models (delegates still get 403), `TenantHrConfig` approval flags (written by 3 surfaces, read by 0), `LeavePolicy.ApprovalWorkflowId`, tenant EOSB rates (`KsaCalculators.cs:102` `_ = _rules;`), `BonusType.IsIncludedInEosb/Wps`, the entire MasterData catalog (consumed by nothing), `NumberingRule` (decoy Setup tab; zero generators read it), `Employee.*PolicyCode` fields, `GCCComplianceSetting.WeekendDays`, 5 dead `AttendancePolicy` fields, geofence radius, `EnableForcedDistribution`, `DocType`. A client's auditor will treat these as misrepresented controls. **Nothing that lies survives into the clean schema.**

2. **Compiled business data shadowing (or replacing) config.** The worst: weekend days hard-coded four different, mutually inconsistent ways (`LeaveService.cs:126` Sat/Sun — *legally wrong leave deductions for the product's own GCC market*; `OvertimeController.cs:357` Fri/Sat; `ShiftsController.cs:196,436` Sat/Sun; attendance has no weekend concept and books 480-min absences on rest days). Payroll pays from fixed columns with a compile-time component set (`PayrollController.cs:824-831`) — a client cannot add an allowance that pays, posts, or prints. Silent statutory fallbacks (`?? 0.09m` GOSI) mean a missing seed pays wrong money undetectably. Loans/advances/bonuses post GL to literal strings bypassing the mapping. Recruitment pipeline is a compile-time array. **Vendor branding ("Zayra AI Workforce", "KynexOne HRMS") is baked into every client's offer and experience letters** (`RecruitmentService.cs:117,122`; `LetterService.cs:258,330,413`). US 22% bonus tax is a compiled switch. Gender-segregated rosters are *invented from shift-name keywords* for tenants who never opened the policy tab (`ShiftsController.cs:427-429`).

3. **Engines without doors.** Real policy engines wired to tables no client can write: `AttendancePolicy` (full resolution engine, zero endpoints, zero UI), lock periods (enforced, unwritable), `EmployeeIdRule` (AI-path only — violates the AI-opt-in rule), approval workflows/policies (full APIs, zero frontend), `LoanPolicy` (consumed in validation, no create endpoint anywhere — enforcement silently never fires).

Two cross-cutting blockers gate everything: **(a) RBAC is cosmetic at the API** — 407 `[Authorize(Roles="…")]` role-name attributes mean custom roles created via the permission matrix are 403'd by most endpoints (G1/G2); **(b) tenant provisioning is broken for the clean slate** — country rules seed only for the bootstrap tenant (`AuthSeeder.cs:101`), MasterData/HR-categories/notification-templates seed only in the demo path, so **every post-wipe tenant is born with empty statutory config and dead features** (C1). 16 of 19 "supported" countries silently fall to a no-op pack computing zero statutory deductions (A5).

The strategy is therefore not "add configurability" — it is: **delete the code that fights the config** (force-resets, shadow routers, literals, fallbacks), **open the doors on engines that already exist** (CRUD+UI), **wire every stored-but-ignored surface or remove it**, **make provisioning seed the defaults**, and **gate all of it behind permission-first auth with the guardrail registers as hard bounds**. Wave 1 (P0) is the price of restarting testing from the wiped DB; Wave 2 (P1) is what makes the product sellable; Wave 3 (P2) is hygiene and differentiators.

---

## 2. THE CRUD MATRIX

Decisions: **FULL** = client full CRUD (UI+API); **ADMIN** = vendor/platform-managed, tenant read-only (or override-only where noted); **FIXED** = deliberately non-configurable code. Scoping: **T** = tenant, **T+C** = tenant default + optional per-company override (the canonical `CompanyTaxPolicy` inheritance pattern — never forced per-company copies), **Plat** = platform-global, **V** = vendor reference data.

### 2.1 Org & HR Core

| Concept | Current state | Decision | Pri | Scope | Guardrails | Effort |
|---|---|---|---|---|---|---|
| Companies (`CompaniesController.cs`) | FULL | FULL (done; retire dup `OrganizationController` surface, role-literals→G1) | P2 | T | Soft-delete only; block deactivate w/ active employees/runs; `ApprovalStatus` platform-governed | S |
| Branches (`BranchesController.cs`) | FULL | FULL (done; fix `America/New_York` TZ default → derive from company country) | P1 | T+C | IANA validation; TZ change on branch w/ history warns, never rewrites | S |
| Departments (`Department.cs:8` — no CompanyId) | PARTIAL | FULL + **add `CompanyId`** | **P0** (schema, pre-wipe) | T+C | G5 server-side company-scope check; delete blocked w/ assignees (reassignment wizard); acyclic hierarchy | M |
| Designations (`DesignationsController.cs`) | FULL | FULL (done; `GradeId` FK = system of record, deprecate free-text `JobGrade/JobLevel`; enforce-or-drop dead `IsSystemDefault`) | P2 | T | Deactivate-not-delete once referenced (C5) | S |
| Grades / salary bands (`Grade.cs`) | PARTIAL | FULL + **add `CompanyId`**; currency from owning company, not `"SAR"` literal | **P0** schema / P1 currency | T+C | Band-check as policy (block/warn/off); pay-scale codes validate vs component registry; narrowing flags, never invalidates (G8) | M |
| Positions (`PositionsController.cs` — API only, no FE client) | PARTIAL | FULL — build Setup UI; no hard delete (correct by design; true delete only never-occupied) | P1 | T+C (is `ICompanyScoped`) | Close-with-incumbent requires transfer; headcount-vs-`ApprovedHeadcount` warning | M |
| Cost centers (`SetupPage.tsx:66-84` — tab dead-coded) | PARTIAL | FULL — **restore `costCenters` to tabs array (one line)** | **P0** | T+C | Recode warns of GL-mapping refs; deactivate once transacted (C5) | S |
| Employee master picklists (gender/marital/contract/… literals, `EmployeesPage.tsx:121-135`) | NONE | FULL via **MasterData wired end-to-end** (BE validation + FE options from API) | **P0** | T | A12/G13: statutory-mapped values keep vendor canonical codes, label-edit + deactivate-only; categories from vendor registry — no client-invented categories | L |
| Country compliance profiles (`EmployeesPage.tsx:148-175` FE-compiled) | NONE | **ADMIN** — vendor country packs served from backend, versioned data releases | P1 | V | A6: tenants toggle countries + *add* documents, never edit statutory ID definitions; C7 data releases | M |
| Employee custom fields | absent | FULL (build: typed per-tenant definitions + JSONB on Employee) | P1 | T | Typed only, cap ~30, no scripted/computed fields, excluded from payroll math v1; in CSV + archive snapshot | M |
| Field requiredness/visibility matrix | absent | FULL (config matrix per standard field) | P1 | T | System-required floor (identity/dates/company) unhidable; statutory requiredness from country pack, exceed-only | M |
| Free-text org overrides in employee edit (`EmployeesController.cs:2160-2171`) | defect | **REMOVE** — resolve against masters, set FKs; denormalized names derived-only | **P0** | — | G13 | S |
| Default password `"ChangeMe123!"` (`EmployeesController.cs:2107`) | defect | **REMOVE** — one-time invitation/reset tokens, forced first-login rotation | **P0** | — | G20 | M |
| Employee statuses / lifecycle constants (`Employee.cs:5-24`) | fixed | **FIXED** (vendor state machine; per-tenant labels later) | — | — | A10 | — |
| Employee code rules (`EmployeeIdRule` — no manual path; decoy `NumberingRule` tab; 2 divergent generators) | NONE effective | FULL — manual settings UI per company, **one generator service**, enforce `AllowManualOverride`, resolve NumberingRule/EmployeeIdRule duplication (OD-8) | **P0** | T+C | A9 forward-only, issued codes immutable, no renumbering ever; sequence per company; manual override behind permission + uniqueness | M |
| Document types (`RequiredDocumentTypes[]` `EmployeeManagementService.cs:18-23` vs unused `DocType` table) | NONE | FULL via `DocType` — finish Update/Deactivate, wire missing-docs report + upload picker; seed current list as defaults | **P0** | T | A6: statutory-mandatory types non-deactivatable while country active; deactivate-not-delete (C5) | M |
| Org-structure import + `TenantHrConfig` governance toggles | FULL / PARTIAL | FULL (done) + manual settings card for toggles; de-brand manual CSV flow from "AI Setup" tab (AI opt-in rule) | P1 | T | G14 import scope checks (org import's `AddScopeRows` is the reference pattern); `RequireImportPreviewBeforeCommit` defaults ON; toggles audited | S |

### 2.2 Time & Attendance

| Concept | Current state | Decision | Pri | Scope | Guardrails | Effort |
|---|---|---|---|---|---|---|
| Attendance policies (`AttendancePolicy` — engine, zero endpoints/UI) | NONE | FULL CRUD (API+UI); enforce the 5 dead fields; kill 480-min (`AttendanceService.cs:867`) and 09:00 (`:802`) literals; **add `CompanyId`** | **P0** | T+C | G8/B10 bounded ranges (grace 0–120, workday 60–960); one non-deletable default policy; deactivate-not-delete; "which policy applies to employee X" preview | L |
| Work-week / weekend (`GCCComplianceSetting` CRUD exists, consumed by nothing; 4 divergent literals) | NONE (behavioral) | FULL T+C via **single `WorkWeekService`** consumed by leave, OT, auto-plan, attendance day-processing (rest-day awareness) | **P0 — first fix** | T+C | B3: valid day-sets only, seeded from country pack, never silent retro recompute | M |
| Attendance devices/ingestion | FULL | FULL (done); drop dead `AttendanceDeviceConnector` pre-wipe; mask `AuthCredentialsJson` in GETs | P2 | T | G15 SSRF egress validation on device URLs; G16 write-only secrets; rate-limit `X-Device-Key` ingest | S |
| Geofences (2 disconnected models, neither enforced) | NONE | FULL — consolidate into Setup `Location` (+ enforcement flags); **drop `AttendanceGeofence`/`AttendanceLocation` pre-wipe**; punch-time enforcement | P1 (drops **P0**) | T | Min radius ~50m; soft/flag mode first, hard-block explicit opt-in with override path | M |
| Lock periods (enforced at `AttendanceService.cs:886`, unwritable) | NONE | FULL, role-gated (`payroll.periods.lock`/`.unlock` distinct keys) | P1 | T+C | D6: unlock = reason + audit; auto-lock on payroll finalization; no future locks | S/M |
| Regularization request types (UI `<option>` literals, `AttendancePage.tsx:558`) | PARTIAL | FULL via MasterData lookup | P1 | T | Seed current 5; deactivate-not-delete | M |
| Regularization approval routing (fixed 1-step "Manager") | PARTIAL | FULL via existing ApprovalWorkflow engine (`ApprovalWorkflowId` link) | P1 | T | Depth cap, cycle check, fallback approver; 1-step manager stays default | M |
| Shift definitions | FULL | FULL (done) + Company/Branch scoping | P1 | T+C | Deactivate-not-delete once assigned | S |
| Roster/auto-plan weekend (`ShiftsController.cs:196,436`) | hard-coded | **FIXED consumer** of WorkWeekService (per-run override kept) | **P0** | — | B3 | S |
| ShiftPolicy inferred gender defaults (`ShiftsController.cs:427-429`) | defect | **REMOVE inference** — empty default; gender rules explicit admin opt-in + audit | **P0** | T | B9 (discrimination-liability; false statutory posture) | S |
| ShiftPolicy (rest) | FULL | FULL (done); per-company rows with Group phase | P2 | T | Typed JSON validated server-side; never raw JSON in UI | S |
| Overtime policies/types/multipliers (create-only; dead targeting cols; unenforced caps) | PARTIAL | FULL lifecycle (PUT/DELETE-deactivate, independent multiplier CRUD, attribute-based deterministic resolution, enforce caps/rounding or drop fields) | **P0** | T+C | B2/G8: multiplier 0.5–5.0, statutory floor per country; effective-dated (payroll audit); never "first active" resolution; G6 maker-checker on multiplier changes | M/L |
| OT statutory path (`StatutoryRule` CRUD) | FULL | FULL (done); fix oldest-active-policy `StandardMonthlyHours` pick → per-employee | P1 | T | Fallbacks flagged in calc output until removed under A2 | S |
| Leave types (PUT exists, UI never calls it) | PARTIAL | FULL — wire Edit UI; category list stays vendor-fixed + "Custom" | P1 | T+C (Wave 2) | Deactivate-not-delete once balances exist | S/M |
| Leave policies + eligibility (`LeavePolicyEligibility` consumed, no CRUD; "Yearly/Prorated" accrual has no engine) | PARTIAL | FULL — **accrual honesty: implement or restrict dropdown to Monthly now**; fold eligibility CRUD into policy form; surface CompanyId/Branch/Workflow/Encashment fields the API already accepts | **P0** (accrual) / P1 | T+C (model ready) | Resolution-preview tool; overlap warning; /30 & /12 divisors from config (B8) | M |
| Leave balances (`Entitled = 30` invention, `LeaveController.cs:106`) | PARTIAL | ADMIN-adjust with audit + policy-driven provisioning job; **delete Entitled=30** | **P0** (literal) / P1 (job) | T | B1; adjustments reason-coded; idempotent provisioning; year-end carry-forward job pre-year-end | M |
| Holiday calendars | FULL | FULL (done); surface CompanyId/BranchId in UI form | P2 | T+C | Country holiday packs as importable templates, never auto-applied | S |
| Blackout dates (GET+POST only; free-text dept) | PARTIAL | FULL — complete PUT/DELETE; Department FK | P2 | T | — | S |
| Comp-off / encashment rates (caller-supplied money) | PARTIAL | FULL rate config — **server-computed** amounts (basis: basic/30, gross/30, ratio, expiry) | P1 | T+C | G8/B11: requester never types money; termination encashment floors at statutory | M |
| `Employee.ShiftPolicyCode/LeavePolicyCode/AttendancePolicyCode` (captured, never consumed) | theater | **Decide pre-wipe: wire as top-specificity override tier or drop columns** (OD-2) | **P0** (decision) | — | If wired: override badge + report | S |
| Ramadan reduced hours (`RamadanReducedHoursPlaceholder`) | placeholder | FULL — date-ranged config consumed by attendance + OT categorization | P1 | T+C | B4 statutory (UAE/KSA); vendor-seeded dates | M |
| OT eligibility ("managers don't get OT") | absent | FULL — grade/band exemption flag on OvertimePolicy targeting | P1 | T | — | S |

### 2.3 Payroll & Finance

| Concept | Current state | Decision | Pri | Scope | Guardrails | Effort |
|---|---|---|---|---|---|---|
| Salary structures | FULL | FULL (done — versioning + in-use guard kept) | — | T+C | — | — |
| **Salary components (THE KEYSTONE)** — engine pays from fixed columns + compile-time `BASIC/HOUSING/…` set (`PayrollController.cs:691-697,824-831`) | PARTIAL (cosmetic) | FULL — component registry drives pay, GL drivers, payslip fields, tax/GOSI/EOSB/WPS bases via behavior flags. **Build first — everything downstream derives from it** | **P0** | T+C | No formula language (typed calc bases: flat, % of base-set, rate×units); seeded components system-protected (relabel yes, delete no); codes immutable once run; deactivate-only; flags effective-dated (C3/G19); B7 GOSI-base validation | L |
| Employee salary assignments (no PUT/DELETE) | PARTIAL | FULL via effective-dated corrections: end-date/deactivate + audited "rescind" (never raw delete) | P1 | T+C | Rescind blocked if fed a locked run | M |
| Payroll calendars / frequency (compile-time monthly; `PayrollCycle` dead) | NONE | FULL — calendar templates with cut-offs; **delete `PayrollCycle` pre-wipe** | P1 (delete **P0**) | T+C | Vendor templates; periods lock once processed; no overlapping periods | L |
| Pay groups (`PayrollGroup` GET+POST, unconsumed) | vestigial | Wire into run scoping **or delete pre-wipe** (OD-3) | **P0** (decision) / P1 (wiring) | T+C | Membership changes don't touch in-flight runs | S |
| GL accounts / mappings | PARTIAL | FULL (accounts done); **route loan/advance/bonus postings through `GlAccountMapping`** — kill literals (`LoansController.cs:179-317`, `AdvancesController.cs:181-269`, `BonusesController.cs:483-484`); per-company mapping per Phase-2 doc | **P0** (literals) / P1 (per-company) | T+C | G6 maker-checker on mapping edits; strict-mode toggle warn→block (OD-13); driver catalog **derived** from component registry, not editable | M |
| Loan types / policies (`LoanPolicy` consumed at `LoansController.cs:132`, zero CRUD endpoints) | PARTIAL/theater | FULL CRUD for both | **P0** | T+C | Policy changes new-loans-only; active-loan types deactivate-only; LN-/ADV- numbering via numbering config (Wave 2) | M |
| Advance policy | FULL | FULL (done — the reference pattern: entity defaults + upsert override) | — | T | — | — |
| Bonuses (US 22%/UK 20% switch `BonusesController.cs:21-26`; dead `IsIncludedInEosb/Wps`) | PARTIAL | FULL (done); **tax via CompanyTaxPolicy/StatutoryRule resolver; wire dead flags via component registry or strip them pre-wipe** | **P0** | T+C | B12; vendor supplemental rates in StatutoryRule, client override under A1 | M |
| EOSB / gratuity (two divergent hard-coded engines; `_ = _rules;` bypass; tenant `EosbYears*Rate` ignored) | effectively NONE | **Statutory formula = ADMIN vendor rule data; base components/day-basis/rounding/enhancement schemes = FULL.** One engine, one formula source | **P0** | T+C (GCC settings per company) | A4: enhancement ≥ statutory (warn+audit); B8 day-basis bounded set; C3 provenance snapshot | L |
| WPS / bank files | PARTIAL | **File format FIXED (versioned vendor spec — correct as-is)**; employer payment profile (IBAN, agent ID, MOL/establishment) = FULL **per company** — kills tenant-singleton split-brain + `EmployerIban: string.Empty` + `"0000000000"` fallback | **P0** | Company | A3; D1/D2; G6 four-eyes on bank identity; IBAN checksum; remove `"USD"`-as-unset sentinel; per-employee payment method | M |
| CompanyTaxPolicy (resolver registered, never consumed — `Program.cs:258`) | theater | FULL (model/CRUD done) — **wire resolver into payroll + bonus tax**; add UI edit/delete (API exists) | **P0** | T+C (the canonical pattern) | Migration: resolver first, SystemSettings magic key as logged deprecation fallback, then remove | M |
| Currencies (compile-time 16-list; AED/SAR/USD scatter) | PARTIAL | **ADMIN reference table** (ISO 4217 seeded, not code, not client CRUD); purge scatter defaults — resolve from Company, fail loud if unset | P2 (table) / P1 (sentinel purge) | Plat/Company | One currency per company; no FX engine until a real client needs it | M |
| Payslip templates | FULL (in fixed catalog) | FULL (done); field catalog **derived** from component registry; statutory locks country-pack-driven, not KSA-always | P1 | T (+C default P2) | A9 immutable snapshots (keep); fonts/locales stay curated vendor list | S/M |
| Statutory / GOSI rules (`StatutoryRule` platform-default + tenant override) | PARTIAL | Model correct — keep. **Kill silent `?? 0.09m` code fallbacks (fail-loud)**; StatutoryRule edit UI + GOSI PUT/UI; platform defaults console (ADMIN) | **P0** (fallbacks) / P1 (UIs) | Plat default + T override (+C for GOSI, Wave 2 — registration is per CR/establishment) | A1/A2/G9; effective-dated; immutable once consumed by posted run; deviation badge + reason (C4) | M |
| One-time payroll inputs (ad-hoc earning/deduction per employee/period) | absent | FULL (build — Workday payroll-input equivalent) | P1 | T+C | Approval-gated; component-typed | M |
| Proration/day-basis + net-pay rounding | scattered literals | FULL — bounded option set per company | P1 | T+C | B8: no free numerics; consistent basis across LOP/leave/encashment | M |

### 2.4 Talent Lifecycle

| Concept | Current state | Decision | Pri | Scope | Guardrails | Effort |
|---|---|---|---|---|---|---|
| Recruitment pipeline stages (compile-time array `Recruitment.cs:6-26` + FE duplicate `RecruitmentPage.tsx:784`) | NONE | FULL — `RecruitmentStage` entity, CRUD API+UI, kanban/advance driven from it, seeded 6-stage default | **P0** | T+C | Fixed `StageType` anchor enum (reporting/conversion keys on type, never names); terminal stages mandatory+unique; no delete with candidates in stage; cap ~15 | L |
| Letter/document templates (vendor brand in client offer letters — `RecruitmentService.cs:117,122`; "KynexOne HRMS" footers `LetterService.cs:258,330,413`) | NONE | FULL — `LetterTemplate` entity (per-tenant + per-company, versioned, merge fields), port the payslip-designer pattern; email channel for candidate comms folds in | **P0** | T+C | **G7 (no sanitizer exists today): allowlist sanitization on save AND render, merge-field whitelist, pure substitution (no expression eval), PDF remote-resource loading disabled, immutable versions (offers pin their version — A9), vendor default always renders (C6)**; D4 bilingual/RTL locale on model from day one | L |
| HR request categories + SLAs (seeds demo-path-only; no admin UI; dead `createCategory` client) | PARTIAL (DOA post-wipe) | FULL — admin UI under Setup; move 5 seeded categories into real tenant provisioning; kill duplicated `48` fallback → tenant setting | **P0** | T | Deactivate-not-delete; SLA bounds 1–2160h | M |
| Offboarding checklist (4 boolean columns, `EmployeeOffboarding.cs:43-46`) | NONE | FULL — template + items entities mirroring onboarding; current 4 become `SystemKey` items (completion gate keeps semantics) | P1 | T+C | System items non-deletable | L |
| Separation types / exit reasons (`OffboardingPage.tsx:9-10` literals) | NONE | FULL lookup masters; **fix `'End of Contract'` vs `EndOfContract` FE/BE mismatch immediately** | P1 (mismatch **P0**) | T | Code+label split; deactivate-only | S |
| Recruitment lookups (sources, interview types ×2 divergent lists, education, employment types) | NONE | FULL via **one generic LookupMaster mechanism** (register types; not 6 bespoke screens) | P1 | T | Type registry vendor-fixed; usage counts before deactivate; both FE forms fed from API | M |
| Requisition/job numbering (`MRQ-{year}-####`, `RecruitmentService.cs:19,38`) | hard-coded | FULL — clone `EmployeeIdRule` pattern | P1 | T+C | A9 forward-only | S |
| Offer policy defaults (probation 3mo, validity 7d, notice 30d literals) | NONE | FULL policy settings per company | P1 | T+C | B5 (UAE ≤6mo, KSA ≤180d) / B6 statutory bounds from country pack; per-offer edit audited | S |
| Assessment templates (Create+Read only; dead `IsActive`; no authoring UI) | PARTIAL | FULL — PUT/DELETE + question mgmt + authoring UI | P1 | T | Version/clone-on-edit once results exist; soft-delete only | M |
| Onboarding checklists (upsert-by-title trap; no update/delete/reorder) | PARTIAL | FULL — stable IDs, full lifecycle, reorder; add assignee-role + due-date offsets while reworking the entity | P1 | T+C | Edits affect future instantiations only; dept FK not free text; categories via LookupMaster | M |
| Rating scales / bands (`RatingLabels` JSON API path exists, no UI; `PerformanceRatingScale` dead model) | NONE | FULL band editor on existing JSON path; **drop dead `PerformanceRatingScale`/`Option` pre-wipe** | P1 (drop **P0**) | T | Contiguous 0–100, 2–7 bands; historical scorecards keep computed labels | M |
| Competency library (full API, no UI) | PARTIAL | FULL — add the missing UI tab (cheapest win in module) | P1 | T | Deactivate when referenced | S |
| Scorecard targeting (free-text dept/desig/grade) + module company scoping | defect | FULL — convert to FKs; optional `CompanyId` on all talent config entities | P1 | T+C | Inheritance by default; G5 | M |
| Workflow status tokens (requisition/offer/PIP/probation) | fixed | **FIXED** machine tokens; per-tenant display-label map later; canonical token list served from one API endpoint | P2 | — | A10 | S |
| Scorecard component set (6 fixed) / interview dimensions (5 fixed) | fixed | **FIXED for now** — weights configurable (0 = disable); revisit on real client demand | P2 | — | Weight-sum=100 validation kept | — |
| PIP templates | absent | FULL (named boilerplate + duration + cadence pre-filling instances) | P2 | T | Statuses stay FIXED | M |
| Calibration forced distribution (`EnableForcedDistribution` decorative) | theater | Build distribution-curve config **or remove the flag** (OD-9; decision itself P1) | P2 | T | Percentages sum 100; advisory before hard-enforce | M |
| Candidate data retention / purge, exit-interview questionnaires, offer approval matrix | absent | FULL (build) — D3 retention is a GCC procurement checklist item | P1 (retention) / P2 (rest) | T | D3 floors and ceilings; purge runs audited | M |

### 2.5 Approvals & Governance

| Concept | Current state | Decision | Pri | Scope | Guardrails | Effort |
|---|---|---|---|---|---|---|
| Approval workflows + steps (API alive, **zero frontend**; EMPLOYEE-CHANGE force-reset `EmployeesController.cs:1682-1726` wipes tenant config) | PARTIAL | FULL — admin UI, deactivate (no hard delete); **delete the force-reset → create-if-missing only** | **P0** | T+C (Wave 2) | G10: version pinning (in-flight completes on start version); save-time validation (≥1 step, resolvable approvers, one final step, cap ~10); maker-checker on payroll/sensitive workflow edits | M |
| Approver-type vocabulary (compile-time switch; unknown → silent nobody) | hard-coded | **FIXED registry, discoverable** — `GET /approver-types`; unknown = save-time error; runtime fail-closed | **P0** | V | G9; configurable tenant fallback approver replaces `"HR Manager"` literal | S |
| Approval policies (full API + CSV, zero UI; lossy CSV) | PARTIAL | FULL — **converge with workflows into one "Approval Rules" admin surface**; fix lossy CSV (drops ApproverRole/Escalation/SpecificEmployee) before anyone imports | **P0** (UI) / P1 (CSV) | T+C | Overlap detection on save; "who approves X for employee Y" effective-policy tester; add amount/duration threshold dimension before UI freezes (data model now) | M |
| HR-approver name heuristic (`ApprovalPolicyService.cs:76-89` `Designation.Contains("HR")`) | defect | **Replace with configurable role binding** (default: HR Manager role). On a wiped DB this resolves to *nobody* | **P0** | T | Validation warns on zero-user resolution | S |
| Hard-coded chain switch (`HrmHierarchyService.cs:258-303` shadows ApprovalPolicy for OT/Attendance/Payroll/…) | NONE | **RETIRE** — convert switch arms to seeded default policies per workflow type; hierarchy service becomes a resolver the policy engine calls | **P0** | — | Depth cap becomes tenant setting w/ clamp | M/L |
| Decide-endpoint authorization (role literals; routed Supervisor gets 403) | defect | **FIXED code, derived from routing**: resolved approver / active delegate / `approvals.override` (logged as override) | **P0** | — | G1/G2 | M |
| SLA windows + defaults (compiled 12/48/24h matrix `ApprovalWorkflowService.cs:404-409`) | PARTIAL→NONE | FULL — per-step + tenant default matrix per workflow type, seeded with today's values | P1 | T+C | Clamp 1–720h; SLA basis (calendar vs working hours) tied to WorkWeekService | M |
| Escalation engine (**none exists** — `EscalationAfterHours` decorative) | absent | Build FIXED engine (BackgroundService), configurable parameters (remind → escalate → opt-in auto-action). If it slips, **hide the field** | P1 | T | G17: auto-approve opt-in per workflow, **hard-excluded for payroll runs/payment batches/sensitive-change**, logged as system actor with SLA config version | M |
| Approval authorities (`AmountLimit`/`CanFinalApprove` never consulted) | theater | FULL (CRUD exists) + **wire enforcement into Loans/Advances/Payroll decide paths — or pull the UI**; add deactivate | P1 | T+C | G12 fail-closed "exceeds authority"; enforcement toggle default ON post-wipe | M |
| Delegations (2 models, neither consulted — delegate 403s) | theater | FULL + **wire into every CanDecide path; unify** (generic `ApprovalDelegation` = source of truth, leave-specific becomes scope) (OD-14); add Update | P1 | T+C | G12: no chaining; maker-checker checks original requester vs acting decider; "X on behalf of Y" recorded; mandatory end date + max duration; auto-delegation from approved leave as differentiator | M |
| Maker-checker core (requester≠decider, processor≠approver) | fixed | **FIXED — deliberately non-configurable, no off-switch ever.** Sell as platform control | — | — | A8 | — |
| Sensitive-field catalog (compile-time HashSet `EmployeesController.cs:30-38`) | NONE | FULL `SensitiveFieldPolicy` over a **vendor field registry**, seeded from today's set (dedup) | P1 | T | G11/A8: floor set (salary, IBAN, national IDs, dates) removable only via audited "reduce controls"; rejection-reason min server-side (currently client-only) | M |
| Notification templates (CRUD+UI done; 1 of ~30 send sites uses them; AR/SMS/WhatsApp dead) | PARTIAL | FULL (done) + **wire all send sites via template codes + seed default bilingual templates at provisioning**; remove SMS/WhatsApp from picker until dispatchers exist | P1 | T+C | G7 encode variables at interpolation (fix `NotificationService.Interpolate`); per-event variable whitelist; vendor fallback on broken template (C6); D4 Arabic rendering wired | L |
| Notification rules (event→recipient/channel) | absent | FULL eventually; minimal slice now: **kill tenant-wide broadcast (`EmployeesController.cs:2221`) + per-user channel prefs** | P2 (engine) / **P1** (broadcast fix) | T | G18: least-privilege recipients; mandatory event class unmute-able | L |
| Audit write path (hash-chained) | fixed | **FIXED** — no category disable, no client writes | — | — | A7; bound the unbounded integrity endpoint (`AuditLogsController.cs:41-46`) | S |
| Audit configuration (retention/export/scope) | NONE | **ADMIN** config — retention ≥ statutory floor, CSV/SIEM export (itself audited), CompanyId filter | P2 | T+C | A7/D9/G19 | S/M |
| Tenant HR approval flags (written by 3 surfaces, read by none) | theater | **RETIRE as runtime config** — Setup Assistant translates answers into generated default ApprovalPolicies, then flags dropped; wire or remove `LeavePolicy.ApprovalWorkflowId` | P1 (hide from wizard **now**) | — | One-way generation, single source of truth | S |
| Per-company scoping (workflows/policies/SLAs/templates) | absent | FULL via nullable `CompanyId` override + effective-config viewer | P1 | T+C | G5; inheritance, never forced copies | M |
| Missing asks: terminated-approver sweep, return-for-correction, multi-approver steps, aging report | absent | Build (sweep + return-for-correction P1; quorum/parallel + reports P2) | P1/P2 | T | Orphaned-approval sweep fits escalation worker | M |

### 2.6 Platform, RBAC & Setup

| Concept | Current state | Decision | Pri | Scope | Guardrails | Effort |
|---|---|---|---|---|---|---|
| Role definitions & permission matrix (407 role-name `[Authorize]`; custom roles 403'd; ~75 inline `HasPermission` prove plumbing) | PARTIAL (cosmetic) | FULL — **`[HasPermission(key)]` policy attribute + sweep all 407 attributes**; seeded-role soft-retire | **P0** | T | G1 key map (one manage + one read key per config domain, registered pre-merge); G2 fail-closed sweep + CI gate on new `[Authorize(Roles=`; G4 authority ceiling, last-admin lockout guard, diff-preview + audit | L |
| Permission key catalog | fixed | **FIXED vendor registry**; read-only API endpoint (kills FE/BE drift); entitlement-based curation of visible keys | P2 | V | G3: add `Permission.Scope` (Tenant/Platform); platform keys rejected in all tenant grant paths | S |
| Overrides / grantors / delegations / authorities | FULL (minor) | FULL — authority delete; validate grantor scope strings; company-aware post-Group | P2 | T | Mandatory expiry on exceptions | S |
| User management / invites / SCIM | FULL | FULL (done); serve AccessModes/status enums from API | P2 | T | Closed enums stay closed | S |
| Entity scoping (company grants, group scope) | FULL | FULL (done) — this IS the company-dimension mechanism | — | T+C | G5 everywhere | — |
| Security settings | FULL | FULL **with platform floors** | P1 | T | G21/A11: min length ≥8, lockout non-disableable, session cap, MFA floor for admin roles — floors vendor-FIXED | S |
| Platform team RBAC | fixed | **FIXED** (internal); generate `/platform/roles` matrix from an endpoint, not the hand-copied array | P2 | Plat | G3 platform consoles only behind `RequirePlatformRole` | S |
| Setup Assistant templates (compiled starter template + **second statutory copy** `SetupAssistantService.cs:292-315`) | PARTIAL | **ADMIN** — versioned platform template store; statutory values resolved via `StatutoryRuleReader` at draft time, never a copy | P1 | Plat | C2 single owner per key; keep exemplary opt-in preview→approve untouched | M |
| MasterData types/values (CRUD done, catalog empty on fresh tenant, consumed by nothing) | FULL CRUD / broken semantics | FULL + **seeded `IsSystemDefined` types & starter values on tenant provisioning**; publish registry of module-expected type codes | **P0** | T | System types non-deletable; referenced values soft-retire (C5); G13 | M |
| Numbering rules (`NumberingRule` vs `EmployeeIdRule` split-brain) | duplicated | FULL — **one system** (OD-8), migrate the other; forward-only | P1 | T+C | A9 | M |
| System settings / fiscal / locations / notification template store | FULL-ish | FULL — add deletes; **send `SubjectAr/BodyAr` or remove the fields** | P1 | T | D4; placeholder validation + test-send | S/M |
| Statutory rules engine (platform default > tenant override, effective-dated) | FULL (tenant) | Keep model. Tenant override FULL (exists); **defaults get ADMIN console** (today: reseed/code only); remove calculator fallbacks under A2 | P1 (console) | Plat + T | A1: override = reason + badge + audit; immutable once consumed; C4 deviations report | M |
| Country payroll rule packs (19-country copy; **not seeded for new tenants** — `AuthSeeder.cs:101` bootstrap-only; no UPDATE; UI lists 10 of 19) | PARTIAL | **Provision on every tenant create (P0)**; add UPDATE; merge into StatutoryRule override model long-term (C2 — "four homes for weekend days") | **P0** (provisioning) / P1 (merge) | T | C1/C7; single-owner rule per config key | M |
| GOSI contribution rules (no PUT, no UI consumers) | PARTIAL | Tenant overrides FULL + supersede helper + **per-company dimension** (GOSI registration is per CR/establishment); defaults ADMIN | P1 | T+C | A1; no edits to rows consumed by posted payroll | S/M |
| GCC compliance settings | FULL-ish | FULL — add delete; fold EOSB/weekend overlaps into C2 consolidation; per-company via T+C | P2 | T+C | — | S |
| Country pack engine (3 executable packs vs 19 seeded countries; silent no-op `DefaultPack`) | structurally NONE | **FIXED code + honest capability surface**: pack-support status API/UI; **payroll hard-block (or loud acknowledged warning) for no-pack countries** | **P0** (surface) | V | A5/G9: "rules reference only — no automated calculation" labeling | S (surface) / L (new packs) |
| Pricing / plans / module catalog (Starter $149 vs $299 conflict; 4 sources) | PARTIAL | **ADMIN** — collapse to DB-backed `PricingConfig` as single source; open plan/module catalog CRUD to platform admins | P1 | Plat | No delete with active subscribers; grandfathering explicit; estimate thresholds → config keys | M |
| Feature flags / entitlements | PARTIAL | **ADMIN** (tenant 403 stays — sound commercial design); validate keys against `FeatureKeys` on write; catalog endpoint; stop storing internals in flags | P2 | T (per-C post-Group) | G23: premium modules default-deny (absent = disabled); server-side enforcement only | S/M |
| Localization / languages (UI offers 2 of 4 shipped locales; US-centric defaults) | PARTIAL | Settings FULL (exists); language catalog **ADMIN**; defaults derived from country at provisioning; no tenant-authored translations (declined) | P1 | T (+C post-Group) | Server-side code validation | M |
| Email / SMTP (tenant path FULL; platform SMTP **dead config**; heuristic masking, `IsEncrypted` unused) | PARTIAL | Tenant FULL (done); platform path **ADMIN + actually wired**; secrets declared-explicitly, write-only, encrypted at rest | P1 (security-flagged) | T (+C sender P2) | G16; FromName from `TenantBranding`, not "KynexOne" literal; no silent drops | M |
| Branding & help text | FULL | FULL (done — textbook override pattern) | — | T | — | — |
| Config export/import & tenant cloning | absent | Build (platform/admin-gated) — also the program's own regression tool for the wipe | P1 | T | G22: secrets excluded, FKs re-mapped, dry-run + checksum, audited both sides | M |
| Data retention / PII purge (PDPL) | absent | FULL bounded both ways | P1 | T | D3: vendor floors (payroll ≥5yr) and ceilings (candidate anonymization); purge audited | M |

---

## 3. Hard-Coded-Data Elimination Register

Legend: **→ENTITY** = replaced by new/existing config entity; **→SEED** = becomes seeded default with override path; **→FIXED** = stays compile-time deliberately (reason given); **→DELETE** = removed outright.

### Org & HR Core
| # | Item | Location | Disposition |
|---|---|---|---|
| O1 | FE dropdown literals (gender, marital, contract, employment type) | `EmployeesPage.tsx:121-135,1077,1141` | →ENTITY: MasterData values, FE fetches options, BE validates (statutory codes vendor-fixed per A12) |
| O2 | `COUNTRY_COMPLIANCE_PROFILES` (SA/AE/QA/KW statutory IDs) | `EmployeesPage.tsx:148-175` | →SEED: vendor country-pack reference data served via API (A6); new country = data release |
| O3 | `RequiredDocumentTypes[]` 10-string array | `EmployeeManagementService.cs:18-23,333` | →ENTITY: `DocType` table (seed current list as defaults) |
| O4 | FE `documentTypesForCountry()` + literals | `EmployeesPage.tsx:235-238` | →ENTITY: `DocType` via API |
| O5 | Free-text dept/designation/grade/CC writes in edit modal | `EmployeesController.cs:2160-2171` | →DELETE: resolve against masters, set FKs (G13) |
| O6 | Default password `"ChangeMe123!"` | `EmployeesController.cs:2107` (+ `AuthSeeder.cs:20` bootstrap) | →DELETE: invitation/reset tokens + forced rotation (G20); bootstrap guard stays with rotation guard |
| O7 | `EmployeeIdRule` defaults as only source; 2nd generator ignoring flags + CompanyId | `EmployeeIdRule.cs:10-17`; `EmployeesController.cs:2067-2090` | →SEED defaults + manual UI; single generator service; delete controller-side generator |
| O8 | Branch TZ `"America/New_York"` | `Branch.cs:16` | →SEED: derive from company country via country→TZ reference map |
| O9 | Grade `Currency = "SAR"` vs Company `"USD"` | `Grade.cs:17` / `Company.cs:25` | →SEED: default from owning company currency; fail loud if unset |
| O10 | Employee status constants | `Employee.cs:5-24` | →FIXED: vendor lifecycle state machine (A10); labels later |
| O11 | Demo seeds (KynexOne company, NYC-HQ, HR dept, ZAY prefix, 30/15/5 balances) | `AuthSeeder.cs:509-624,1061` | →FIXED demo-gated; startup guard asserts `SeedAdmin:SeedDemoData` OFF in prod (G20) |
| O12 | Dead `Designation.IsSystemDefault` flag | `DemoDataSeeder.cs:404` | →DELETE (or enforce deactivate-not-delete; OD, default = drop column) |

### Time & Attendance
| # | Item | Location | Disposition |
|---|---|---|---|
| T1 | Weekend literals ×4 (Sat/Sun, Fri/Sat, Sat/Sun, Sat/Sun) | `LeaveService.cs:126`; `OvertimeController.cs:357`; `ShiftsController.cs:196,436` | →ENTITY: single `WorkWeekService` reading `GCCComplianceSetting`/company override (B3) |
| T2 | Absence = 480 min; fallback shift start 09:00; `DefaultPolicy()` | `AttendanceService.cs:867,802,889` | →ENTITY: `AttendancePolicy` fields (StandardWorkMinutes etc.), enforced |
| T3 | 5 dead AttendancePolicy fields + dead `AttendanceRule` table | `AttendanceModule.cs:132-154` | →ENTITY: enforce fields; `AttendanceRule` drop-or-keep per OD-1 |
| T4 | Inferred gender roster rules | `ShiftsController.cs:418-431` | →DELETE inference; empty default; explicit opt-in + audit (B9) |
| T5 | OT fallback multipliers 2/1.5/1.25; Fri/Sat category; "first active" policy pick | `OvertimeController.cs:344,357,359` | →ENTITY: multiplier CRUD + WorkWeekService + deterministic specificity resolution; fallbacks →DELETE under G9 |
| T6 | `Entitled = 30` balance invention | `LeaveController.cs:106` | →DELETE; policy-driven provisioning job (B1) |
| T7 | Accrual `/12`, unpaid `/30` divisors; "Yearly/Prorated" no-op | `LeaveService.cs:46,65,467` | →ENTITY: divisors from config (B8); accrual methods implemented or dropdown restricted |
| T8 | Regularization type `<option>` list; fixed "Manager" approval | `AttendancePage.tsx:558`; `AttendanceModule.cs:206` | →ENTITY: MasterData lookup + ApprovalWorkflow link |
| T9 | Leave UI category/country/employment-type lists | `LeavePage.tsx:897,1036,1049` | →FIXED categories (statutory semantics) + "Custom"; countries/types served from reference API |
| T10 | Caller-supplied `DaysEarned`/`AmountPerDay` | `CompOffController.cs:72-73`; `EncashmentController.cs:96` | →ENTITY: rate config, server-computed (G8/B11) |
| T11 | Shift cosmetic defaults (#2F6BFF, 60-min break) | `ShiftDefinition.cs:13` | →FIXED entity defaults (overridable in UI — compliant as-is) |
| T12 | `RamadanReducedHoursPlaceholder` | `WorkforceCompensation.cs` | →ENTITY: date-ranged Ramadan config consumed by attendance/OT (B4) |
| T13 | Dead tables: `AttendanceDeviceConnector`, `AttendanceGeofence`, `AttendanceLocation`, `OvertimeRule`, `LeaveAccrualRule` | models/DbContext | →DELETE pre-wipe (AttendanceRule per OD-1) |

### Payroll & Finance
| # | Item | Location | Disposition |
|---|---|---|---|
| P1 | Fixed component set `BASIC/HOUSING/TRANSPORT/OTHER_ALLOWANCES/OVERTIME`; fixed salary columns | `PayrollController.cs:691-697,824-831`; `WorkforceCompensation.cs:281-287` | →ENTITY: component registry with behavior flags (the keystone) |
| P2 | GL literals `"1400 - Employee Loans Receivable"` etc. | `LoansController.cs:179-317`; `AdvancesController.cs:181-269`; `BonusesController.cs:483-484` | →ENTITY: route through `GlAccountMapping` drivers (`DED:LOAN`, `EARN:BONUS` already exist) |
| P3 | Bonus tax switch US 0.22/UK 0.20; fixed region list | `BonusesController.cs:21-26`; `LoansAdvancesBonuses.cs:248` | →ENTITY: CompanyTaxPolicy/StatutoryRule resolver (B12); region enum →DELETE |
| P4 | Dead `IsIncludedInEosb`/`IsIncludedInWps` flags | `LoansAdvancesBonuses.cs` | →ENTITY: enforced via component behavior flags, or columns dropped pre-wipe |
| P5 | KSA EOSB formula hard-coded ×2 (calculator + divergent final-settlement); `_ = _rules;`; jurisdiction "mainland"; 21/30 magic | `KsaCalculators.cs:94-150,102`; `PayrollController.cs:2108-2205,2794-2880` | →SEED: one engine, formula parameters in StatutoryRule; tenant `EosbYears*Rate` honored or removed (A4) |
| P6 | Statutory code fallbacks `?? 0.09m/0.0075m/0.02m/45000m`; `1.5/30/480` | `KsaCalculators.cs:26-71`; `PayrollController.cs:665-678` | →DELETE once provisioning guaranteed: missing rule = fail-loud validation error (A2/G9) |
| P7 | Compile-time monthly pay period; dead `PayrollCycle`; vestigial `PayrollGroup` | `PayrollController.cs:489-490`; `WorkforceCompensation.cs:295-315` | →ENTITY: calendar templates (Wave 2); PayrollCycle →DELETE pre-wipe; PayrollGroup per OD-3 |
| P8 | WPS agent `"0000000000"`, `EmployerIban: string.Empty`, `"USD"`-as-unset sentinel | `PayrollController.cs:1589-1629` | →ENTITY: per-company payment profile (D1); sentinel →DELETE |
| P9 | SIF layout/field widths | `SifFileGenerator.cs` | →FIXED: versioned government file spec (A3) — correct as-is |
| P10 | Currency scatter (AED×6, USD, SAR fallbacks); compile-time 16-currency list | `WorkforceCompensation.cs:244,289,301,539,615`; `IsoReference.cs:66-84`; `PayrollController.cs:1029,2202,2305` | →SEED: ISO reference table (ADMIN); resolve from Company, fail loud; scatter →DELETE |
| P11 | Payslip field catalog mirrors fixed components; KSA GOSI locks always-on | `PayslipTemplateRegistry.cs:22-89` | →ENTITY: derived from component registry; locks country-pack-driven |
| P12 | Income tax via `SystemSettings ("Payroll","IncomeTaxRate")` magic key | `PayrollController.cs:575-579` | →ENTITY: CompanyTaxPolicy resolver; magic key = logged deprecation fallback then removed |
| P13 | GOSI wage basis Basic+Housing constant | `CountryPackContracts.cs:34` | →ENTITY: component `IsGosiCovered` flags with B7 statutory validation |
| P14 | LN-/ADV-/BON- numbering formats | `LoansController.cs:153`; `AdvancesController.cs:160`; `BonusesController.cs:179` | →ENTITY: numbering config (Wave 2); forward-only (A9) |

### Talent Lifecycle
| # | Item | Location | Disposition |
|---|---|---|---|
| L1 | Pipeline stage array (BE + FE duplicate) | `Recruitment.cs:6-26`; `RecruitmentPage.tsx:784` | →ENTITY: `RecruitmentStage` (StageType anchor), seeded 6-stage default |
| L2 | Vendor brand in offer letters ("Zayra AI Workforce", brand div); "KynexOne HRMS" footers; all letter prose; "Monthly (AED)"; "Full-Time, Permanent"; "7 days" | `RecruitmentService.cs:82-154`; `LetterService.cs:258,313,330,413` | →ENTITY: `LetterTemplate` versioned designer (payslip pattern); seeded neutral defaults per letter type |
| L3 | 4-boolean offboarding checklist | `EmployeeOffboarding.cs:43-46` | →ENTITY: offboarding templates; current 4 = SystemKey items |
| L4 | `SEPARATION_TYPES`/`EXIT_REASONS` FE literals (+ `'End of Contract'` mismatch) | `OffboardingPage.tsx:9-10` | →ENTITY: lookup masters; mismatch fixed immediately |
| L5 | Recruitment lookups (sources, 2 divergent interview-type lists, education, priorities) | `RecruitmentPage.tsx:269,275,977,1263,1269,1583` | →ENTITY: generic LookupMaster |
| L6 | `MRQ-/JOB-` numbering | `RecruitmentService.cs:19,38` | →ENTITY: numbering rule (EmployeeIdRule clone) |
| L7 | Probation 3mo / offer validity 7d / notice 30d literals | `Recruitment.cs:190`; `ApplicationsController.cs:330`; `OffboardingPage.tsx:204` | →ENTITY: per-company offer/notice policy (B5/B6 bounds) |
| L8 | Rating band fallbacks (90/75/60/45 + second 60-cutoff) | `PerformanceService.cs:59-66,83` | →SEED: default bands; band editor UI on existing `RatingLabels` path |
| L9 | Onboarding task categories; `ApplicableTo` literals; free-text dept | `RecruitmentPage.tsx:2200,2220`; `RecruitmentExtended.cs:163,178` | →ENTITY: LookupMaster + Department FK |
| L10 | HR request SLA `48` fallback ×2; demo-only category seeds | `HRRequestCenterController.cs:53,167`; `AuthSeeder.cs:1085-1089` | →SEED: categories in real provisioning; fallback → tenant setting |
| L11 | Status tokens (requisition/offer/PIP/probation/check-in) | `Recruitment.cs:49,83,128`; `Performance.cs:377,394,415` | →FIXED machine tokens (A10); canonical list served from one endpoint; label maps P2 |
| L12 | Scorecard 6-component set + weights; 5 interview dimensions | `Performance.cs:41-46`; `PerformanceService.cs:101-109`; `RecruitmentExtended.cs:60-65` | →FIXED for now (weights configurable, 0=disable); revisit on client demand |
| L13 | Dead `PerformanceRatingScale`/`Option` model | `Performance.cs:59-79` | →DELETE pre-wipe |

### Approvals & Governance
| # | Item | Location | Disposition |
|---|---|---|---|
| A1 | EMPLOYEE-CHANGE force-reset of tenant steps | `EmployeesController.cs:1682-1726` | →DELETE: create-if-missing only (G10) |
| A2 | Approver-type switch; unknown → silent nobody | `ApprovalWorkflowService.cs:346-355` | →FIXED discoverable registry + fail-closed (G9) |
| A3 | `"HR Manager"` fallback + skip-ahead special case | `ApprovalWorkflowService.cs:177-190,316-322` | →ENTITY: configurable tenant fallback approver |
| A4 | Chain-per-workflow-type switch (shadow router) | `HrmHierarchyService.cs:258-303` | →SEED: switch arms become seeded default ApprovalPolicies; switch deleted |
| A5 | HR name heuristic (`Contains("HR")`) | `ApprovalPolicyService.cs:76-89` | →ENTITY: HR-approver role binding setting |
| A6 | SLA matrix 12/48/24h; clamp; priority literals | `ApprovalWorkflowService.cs:297-301,404-409` | →SEED: tenant SLA settings seeded with today's values |
| A7 | Sensitive-field HashSet (30+ names, w/ dupes) | `EmployeesController.cs:30-38` | →ENTITY: `SensitiveFieldPolicy` over vendor registry, seeded from set (G11) |
| A8 | Decide-endpoint role literals (divergent between controllers) | `ApprovalRequestsController.cs:53`; `ApprovalWorkflowsController.cs:80` | →DELETE: authorization derived from resolved approver/delegate/override (G1) |
| A9 | ~30 inline English notification strings; `NotifyAsync` template bypass; dead Ar/SMS/WhatsApp | `NotificationService.cs:29-83`; send sites incl. `LeaveRequestsController.cs:193-194` | →ENTITY: template codes per event + seeded bilingual defaults (D4); dead channels hidden until dispatchers exist |
| A10 | Tenant-wide broadcast recipients | `EmployeesController.cs:2221` | →DELETE: least-privilege recipient resolution (G18) |
| A11 | Rejection-reason min length (client-only) | `ApprovalsPage.tsx:89` | →ENTITY: server-side setting |
| A12 | Maker-checker core rules | `ApprovalWorkflowService.cs:209-210`; `PayrollController.cs:1208-1209`; Loans/Advances | →FIXED forever (A8 compliance register) |
| A13 | Audit action strings, list caps, unbounded integrity scan | `AuditLogsController.cs:27,41-46` | →FIXED catalog + bounded/chunked integrity verification |

### Platform, RBAC & Setup
| # | Item | Location | Disposition |
|---|---|---|---|
| S1 | 407 `[Authorize(Roles="…")]` attributes | Controllers/ (repo-wide) | →DELETE via `[HasPermission]` sweep (G1/G2); CI gate blocks new ones |
| S2 | Setup Assistant starter template + second statutory copy + `"SAR"`/`"Riyadh"` literals | `SetupAssistantService.cs:270-378` | →ENTITY: platform template store (ADMIN); statutory via `StatutoryRuleReader` (C2) |
| S3 | Country rule 19-tuple table seeded bootstrap-only | `AuthSeeder.cs:267-320,101` | →SEED: provisioning bundle on every tenant create (C1) |
| S4 | Country pack roster (3 executable) + silent no-op DefaultPack | `CountryPackRegistry.cs:8-24`; `DefaultPack.cs:34-37` | →FIXED code + honest support-status surface + payroll block (A5) |
| S5 | Plan facts ×4 conflicting (Starter $149 vs $299) | `PlatformController.cs:2244-2250`; `DemoDataSeeder.cs:1101`; `SubscriptionTiers.cs:5-12`; `SaasPlatform.cs:123-130` | →SEED: DB-backed `PricingConfig` single source; others become readers |
| S6 | AI token limits per-plan switch; estimate thresholds | `SaasPlatform.cs:285-295`; `PricingController.cs:68-97` | →SEED: `PricingConfig` keys |
| S7 | Feature-key catalog; unvalidated platform PUT; drifting UI lists (8 of 17) | `SaasPlatform.cs:96-115`; `PlatformController.cs:633-670` | →FIXED catalog + write validation + catalog API (G23 default-deny for new modules) |
| S8 | US-centric localization defaults; UI offers 2 of 4 locales | `SaasPlatform.cs:154-169`; `TenantAdminPage.tsx:711-717` | →SEED: defaults from country at provisioning; full locale list; server validation |
| S9 | Platform SMTP dead config; heuristic secret masking; unused `IsEncrypted`; "KynexOne HR" FromName | `SmtpEmailService.cs:50-73`; `PlatformController.cs:3400-3427`; `SetupSettingsController.cs:112` | →ENTITY: wired platform-scope reader; declared secret fields, write-only + encrypted (G16); FromName from TenantBranding |
| S10 | Permission catalog compile-time; no scope column | `AuthSeeder.cs:324-430`; `Permission.cs` | →FIXED registry + `Scope` column (G3) blocking platform keys from tenant grant paths |
| S11 | Platform-admin lockout constants; platform role defs | `SaasPlatform.cs:42-43,72`; `PlatformRoles` | →FIXED (internal, acceptable); UI matrix generated from endpoint |
| S12 | Access-mode/status enums duplicated FE/BE | `AccessControl.cs:5-18`; `UserManagementPage.tsx:43-49` | →FIXED closed enums, served from API |

---

## 4. Implementation Waves

Migration discipline (project rule): **one EF migration per column change**, entity-prop-vs-migration drift audited before merge (the 42703 lesson). All table/column drops MUST land before the DB wipe — the wipe is the one-time free demolition window.

### Wave 1 — P0: unblocks clean-slate testing & onboarding

**Gate:** the DB wipe does not proceed until every Wave-1 schema decision is merged; testing does not restart until Wave-1 behavior items are green.

**W1-A. Schema window (pre-wipe drops/adds) — migrations, one each:**
- Add: `AddCompanyIdToDepartment`, `AddCompanyIdToGrade`, `AddCompanyIdToAttendancePolicy`, `AddCompanyIdToGccComplianceSetting`, `AddScopeToPermission`, `CreateCompanyPaymentProfile` (employer IBAN/agent/MOL — D1), `CreateRecruitmentStage`, `CreateLetterTemplate` + `CreateLetterTemplateVersion`, `CreateSensitiveFieldPolicy`, `CreateEmployeeInvitationToken`, component behavior flags on `SalaryComponent` (one migration per column: `IsGosiCovered`, `IsEosbBase`, `IsWpsIncluded`, `GlDriverKey`, `CalcBasis`, `Sequence`).
- Drop (dead schema): `DropAttendanceDeviceConnector`, `DropAttendanceGeofence`, `DropAttendanceLocation`, `DropOvertimeRule`, `DropLeaveAccrualRule`, `DropPayrollCycle`, `DropPerformanceRatingScale`, `DropPerformanceRatingOption`; conditional on owner decisions: `DropAttendanceRule` (OD-1), `DropPayrollGroup` (OD-3), `DropEmployeeShiftPolicyCode`/`LeavePolicyCode`/`AttendancePolicyCode` (OD-2), `DropNumberingRule` or `DropEmployeeIdRule` survivor merge (OD-8), `DropBonusTypeIsIncludedInEosb`/`Wps` if not wired, `DropDesignationIsSystemDefault`.

**W1-B. Cross-cutting platform (blocks everything else):**
1. `[HasPermission(key)]` attribute via `IAuthorizationPolicyProvider`; G1 permission-key map registered in AuthSeeder; sweep the 407 role-name attributes in slices, fail-closed (G2); CI gate failing builds on new `[Authorize(Roles=` in Controllers/.
2. **Tenant provisioning bundle** (C1): on every tenant create — country rules (`EnsureGlobalCountryRules` wired into `PlatformController` creation path), localization defaults from country, seeded system MasterData types+values, HR request categories, bilingual notification-template defaults, default approval policies, default letter templates, default recruitment stages. Idempotent; the only seeder in prod. Startup guard asserts `SeedAdmin:SeedDemoData` OFF (G20).
3. Country-pack honest surface: support-status API/UI + payroll hard-block/acknowledged-warning for no-pack countries (A5/G9).

**W1-C. Org & HR:** restore CostCenters tab (one line, `SetupPage.tsx:66-84`); MasterData wired end-to-end (BE validation + FE options); DocType CRUD completion + wire missing-docs report and upload picker; EmployeeIdRule manual settings UI + single generator + decoy-tab resolution; kill free-text org overrides (`EmployeesController.cs:2160`); invitation flow replaces `ChangeMe123!`.

**W1-D. Time & Attendance:** `WorkWeekService` (T+C resolution) replacing all four weekend literals + attendance rest-day awareness; AttendancePolicy CRUD (API+UI) + enforcement of dead fields + kill 480/09:00 literals; OT policy/type/multiplier full lifecycle + deterministic resolution (G6 four-eyes on multiplier changes); delete `Entitled = 30`; delete gender-rule inference; accrual honesty (implement Yearly/Prorated or restrict dropdown); Employee.*PolicyCode decision executed.

**W1-E. Payroll & Finance:** component-driven pay engine (keystone — build first; golden-master comparison vs old engine before cutover); wire `CompanyTaxPolicyResolver` into payroll + bonus tax (+ UI edit/delete); route loan/advance/bonus GL through `GlAccountMapping`; single EOSB engine reading StatutoryRule + tenant/company settings (delete both hard-coded formulas); LoanType/LoanPolicy full CRUD; remove silent statutory fallbacks → fail-loud (extend the existing pack guard); per-company WPS payment profile + employer IBAN with G6 maker-checker; purge/wire all dead config (PayrollCycle, PayrollGroup, bonus flags).

**W1-F. Talent:** `LetterTemplate` engine + designer (ports payslip pattern) with **G7 sanitizer stack (none exists today — build allowlist sanitizer + encoding interpolation first)**, seeded neutral defaults — kills vendor branding; `RecruitmentStage` entity + API-fed FE dropdowns; HR category/SLA admin UI + provisioning seeds; `'End of Contract'` mismatch fix.

**W1-G. Approvals:** delete EMPLOYEE-CHANGE force-reset; retire `HrmHierarchyService` shadow switch (arms → seeded default policies); HR-approver role binding replaces name heuristic; unified Approval Rules admin UI (workflows + policies); decide-authorization derived from routing (resolved approver/delegate/`approvals.override`); approver-type discovery endpoint + fail-closed validation.

**W1 test strategy:** keep 747-green baseline; add (1) provisioning smoke test — create tenant → assert country rules, MasterData types, categories, templates, default policies, stages all present; (2) authorization coverage test — every controller action carries a permission attribute (extends existing default-deny CI gate); (3) WorkWeekService unit matrix per country incl. leave-deduction regression (Fri/Sat tenant); (4) payroll fail-loud tests — missing statutory rule / no-pack country blocks the run; (5) component-engine golden master — old fixed-column gross vs new component gross on seeded fixtures; (6) resolver precedence tests (tenant default vs company override) shared across CompanyTaxPolicy/GL/GCC settings; (7) migration drift audit run (entity props vs migrations) before the wipe; (8) sanitizer/injection tests for LetterTemplate (G7) and CSV (G14).

### Wave 2 — P1: what any serious client expects

**Modules & changes:**
- **Org/HR:** Positions setup UI; branch TZ from country; grade currency wiring; server-side country packs (A6); custom fields (`CreateCustomFieldDefinition` + `AddCustomFieldsJsonToEmployee` migrations); field requiredness matrix (`CreateFieldRequirementConfig`); TenantHrConfig manual settings card; import flow de-branded from AI tab.
- **T&A:** lock-period CRUD + period-close exception workbench (D6); geofence consolidation (`AddGeofenceEnforcementToLocation`) + soft enforcement; leave-policy UI completion (Company/Workflow/Encashment fields) + eligibility CRUD in policy form; leave-type edit UI; regularization types→MasterData + workflow hookup; OT attribute resolution columns wired + caps/rounding enforced; balance provisioning + year-end job; encashment/comp-off rate model; `AddCompanyIdToShiftDefinition`, `AddCompanyIdToLeaveType`; Ramadan config (B4); OT exemption flag; per-employee `StandardMonthlyHours`; statutory leave tier engine (D5) before leave-policy UI shape freezes.
- **Payroll:** pay calendars (`CreatePayrollCalendar`, `CreatePayrollPeriod`) + off-cycle runs + pay-group wiring (if kept); per-company GL mapping (`AddCompanyIdToGlAccountMapping` — Phase-2 doc, incl. cost-dimension split scope check); assignment end-date/rescind; StatutoryRule edit UI + GOSI PUT/UI + supersede + `AddCompanyIdToGosiContributionRule`; platform statutory-defaults console (ADMIN); one-time payroll inputs; proration/day-basis + rounding config; document-numbering config for LN-/ADV-/BON-; currency sentinel purge; payslip catalog derivation from components; C3 provenance snapshots universal.
- **Talent:** offboarding templates + masters; LookupMaster mechanism + registrations; requisition numbering; offer/notice policy defaults (B5/B6); assessment CRUD completion + authoring UI; onboarding lifecycle (stable IDs, reorder, assignee-role/due-offsets); rating-band editor; competency UI; FK targeting; `AddCompanyIdTo…` talent config entities; candidate retention/purge (D3); candidate email templates via LetterTemplate channel.
- **Approvals:** SLA settings + escalation BackgroundService (G17 exclusions); authority enforcement wired (or UI pulled); delegation unification + wiring into every CanDecide path (G12) + auto-delegation from leave; SensitiveFieldPolicy live; template wiring across ~30 send sites + AR rendering (D4); kill tenant broadcast; retire HR flags via Setup-Assistant policy generation; `AddCompanyIdToApprovalWorkflow`/`ApprovalPolicy`; workflow version pinning; terminated-approver sweep; return-for-correction status; threshold dimension on ApprovalPolicy.
- **Platform:** security-policy floors (G21); pricing single-source + platform catalog CRUD; platform SMTP wired + secret encryption/masking (G16); localization defaults-from-country + full locale list; numbering consolidation; seeded-role retirement; Setup-Assistant template store (ADMIN) reading StatutoryRuleReader (C2); config export/import + tenant cloning (G22); audit CompanyId filter + bounded integrity.

**W2 test strategy:** escalation-worker time-travel tests; delegation/maker-checker interaction matrix (original requester vs acting decider); authority fail-closed tests on Loans/Advances/Payroll; per-company override resolution tests on every T+C surface (shared resolver test kit — G5); Arabic render snapshot tests; calendar/period lock tests; retro-safety tests (config change never mutates locked runs — G19); import/export round-trip fidelity tests (fixes lossy approvals CSV).

### Wave 3 — P2: hygiene & differentiators

Duplicate `OrganizationController` surface retirement; designation dual-source cleanup; blackout completion + holiday year-templates (tentative Eid dates); device credential DTO audit; per-company ShiftPolicy; status display-label maps; PIP templates; forced-distribution build-or-remove execution; interview kits; exit surveys; notification rules/digest engine + SMS/WhatsApp dispatchers; audit retention/export console; currency reference table + per-company payslip default; multi-currency/FX (only on real demand); per-company entitlements & locale (post-Group); terminology overrides; platform RBAC UI generation; enum-serving endpoints; permission-catalog curation by entitlement; quorum/parallel approvals; conditional steps; approvals aging reports; stage-entry automations.

**W3 test strategy:** regression-only + feature-scoped suites; config-snapshot diffing via the Wave-2 export tool as the standing regression harness.

---

## 5. Open Decisions for the Owner

| # | Decision | Options / recommendation | Deadline |
|---|---|---|---|
| OD-1 | `AttendanceRule` table: keep for occurrence-based lateness rules ("3 lates = half-day" — most common GCC pattern) or drop | Keep **only** if occurrence rules are committed to the roadmap; else drop. Recommendation: keep, schema-frozen, feature in Wave 3 | Pre-wipe |
| OD-2 | `Employee.ShiftPolicyCode/LeavePolicyCode/AttendancePolicyCode`: wire as top-specificity override tier or drop columns | Recommendation: drop now (attribute resolution is built and better); re-add as designed feature if a client asks | Pre-wipe |
| OD-3 | `PayrollGroup`: wire into run scoping or delete with `PayrollCycle` | Recommendation: keep + wire in Wave 2 (staff/worker segregation is a real ask); `PayrollCycle` deletes regardless | Pre-wipe |
| OD-4 | Country scope: which of the 16 rule-only countries get executable packs, and is the no-pack behavior hard-block or acknowledged-warning? | **DECIDED 2026-07-27 (owner delegated to Business Head/CTO): tiered honest-country-pack model.** Tier 1 certified payroll = KSA + UAE (fail-loud correctness in Wave 1). Tier 2 = QA/KW/OM/BH — payroll allowed with fail-loud statutory validation, gaps published. Tier 3 = all other countries — HR/org modules fully available, **payroll hard-blocked** with "country pack not certified — contact vendor"; a new-country customer onboards HR day one and the pack becomes a scoped, funded implementation. Per-tenant acknowledged-warning override retained for reference-only use (vendor-approved, logged). | ~~Wave 1~~ Decided |
| OD-5 | Custom fields: build in Wave 2 as committed (recommended — first-demo ask) or defer to requirements-gated backlog | Recommendation: build; typed+capped design is low-risk | Wave 2 planning |
| OD-6 | Code-uniqueness scope once org entities are company-scoped: unique per tenant vs per company (per-tenant setting?) | Recommendation: per-tenant setting, default unique-per-company (acquisition collisions) | Wave 2 |
| OD-7 | Rehire code policy (reuse vs new — GOSI continuity): setting on `EmployeeIdRule` | Recommendation: yes, Wave 2 | Wave 2 |
| OD-8 | Numbering consolidation direction: `EmployeeIdRule` (used, no UI) vs `NumberingRule` (UI, unused) — which survives | Recommendation: `EmployeeIdRule` model survives and generalizes (entity-typed rules); Numbering tab repointed at it; `NumberingRule` dropped | Pre-wipe |
| OD-9 | Calibration `EnableForcedDistribution`: build distribution-curve config or remove flag | Recommendation: remove flag now (theater in demos), build in Wave 3 if demanded | Wave 1 |
| OD-10 | Grade band enforcement default mode (block / warn / off) for new tenants | Recommendation: warn | Wave 1 |
| OD-11 | GL strict mode default at clean-slate (warn vs block on unmapped drivers) | Recommendation: warn during onboarding, block once first payroll approved | Wave 1 |
| OD-12 | Delegation unification: generic `ApprovalDelegation` absorbs `LeaveDelegation` (recommended) — confirm before decide-path wiring | — | Wave 2 start |
| OD-13 | Effective-dating depth for org config (full Department/Grade versioning vs effective dates on employee assignments only) | Recommendation: assignments-only now; org versioning to explicit roadmap (not silently absent) | Wave 2 |
| OD-14 | D3 retention defaults: statutory floors are vendor-set, but tenant-facing default retention/anonymization windows need a product decision (candidate anonymization after N months — pick N) | Needs compliance advisor input (still an open program input) | Wave 2 |
| OD-15 | Wave-1 scope risk: the component-driven pay engine is the largest single item and sequenced first in payroll. If it threatens the wipe date, does the wipe wait, or does payroll Wave-1 land immediately post-wipe (schema pre-provisioned, engine cutover after)? | Recommendation: schema + registry pre-wipe, engine cutover may trail by one sprint behind a feature gate — but no testing sign-off on payroll until cutover | Now |
| OD-16 | Bonus `IsIncludedInEosb/Wps` columns: wire through component flags (recommended) or drop pre-wipe | Recommendation: drop from BonusType; behavior derives from the bonus component's registry flags | Pre-wipe |

---

*End of program document. The guardrail registers (compliance A/B/C/D, security G1–G23) are normative annexes to this plan; any implementation PR touching a matrix row must cite the row and its guardrail IDs in the PR description.*
