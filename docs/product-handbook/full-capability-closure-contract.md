# KynexOne — Full Capability Closure Review

Prepared from the 10 October 2026 comparison and the attached version 1.1 advanced-capability prompt. This is a coverage/acceptance review, not a current implementation or production certification. Original assessed SHA: `8fb929403fe3632d1b967a60be747c8a09c52008`. Refreshed source evidence at `2026-10-11T04:55:02Z` identifies main `76985c559e9b51c246a4a48ab88e0e14eedc1ae1` (merged PR #223) and live web/API `0e54b4f51e4cea94fd03f934ae92e20ce7cc1d0d`. Updated implementation below is explicitly distinguished from still-unproved deployed acceptance. Refresh these identities before execution.

## Scope and completeness contract

The existing prompt covers six advanced families (F24, F25, F26, F27, F30 and F32). It does not close the other 30 families, nor does it certify previously implemented modules on a current release. All 36 families therefore need a closure ledger. Reuse valid existing implementation and dated evidence; implement confirmed gaps and retest changed behavior plus its regressions.

“100% available” requires a frozen customer/release scope, every committed critical requirement directly passed, required production dependencies proved, current deployed build parity and no open Critical/High findings. A feature can be implemented while device release, statutory acceptance, provider entitlement or independent assurance remains blocked. Unknown behavior is UNVERIFIED, not absent and not complete. No finite feature-family comparison establishes parity with every feature of every competitor.

Each Fnn.n criterion below is critical unless the acceptance charter explicitly identifies a non-critical enhancement and records the user-approved scope. Do not silently downgrade a committed requirement. Native and supported integrated routes are both valid if ownership and actual customer outcomes are clear.

## Common gate required for every family

- Register exact release SHA, deployed web/API builds, environment, requirement/test IDs, seed version, record IDs, tenant/company/roles, time and expected/actual outcomes.
- Prove the complete customer journey in headed browser/mobile as applicable against the real backend, then verify persisted state and relevant audit/downstream outcomes.
- Cover role and tenant denial, sensitive-field protection, invalid transitions, duplicates/concurrency, failure/retry, refresh and recovery appropriate to the family.
- Verify desktop/mobile and Arabic/RTL scope, accessible interactions, useful empty/error/loading states, monitoring and operator recovery.
- Require independent domain, SDET and enterprise/security review for material changes; label limitations when one reviewer combines lenses.
- Deliver working code/configuration, source-linked user/admin guidance, actual API/provider contracts and reproducible evidence. Documentation, test count, logos and static screenshots cannot substitute for completion.
- Gate ACCEPTED, CONDITIONAL ACCEPTANCE (only expressly approved non-critical residuals), REMEDIATION REQUIRED or BLOCKED. Maintain separate local, sandbox and production readiness.

## Family-by-family acceptance criteria

### F01 — HR records and organization

**Baseline:** I — Company, branches, departments, cost centres, grades and employee records implemented.

**Work required:** VALIDATE + close exceptions. Original priority: P1.

- **F01.01:** HR creates a company-scoped employee with mandatory, duplicate and invalid-data checks; approved changes persist after refresh and appear in authorized search.
- **F01.02:** Organizational assignments, position, grade, manager, employment state and effective dates use the existing authorities; transfer/history and termination do not corrupt reporting.
- **F01.03:** Sensitive fields and employee documents are protected by role/field access, tenant boundaries, audit, retention and permitted correction controls.

**Dependencies / external blockers:** Approved master-data/import specification and representative synthetic records.

**Existing evidence/source starting point:** frontend/src/views/EmployeesPage.tsx

### F02 — Multiple companies / entities

**Baseline:** I — Company-level policies and entity scoping exist; complex global hierarchies need validation.

**Work required:** VALIDATE + advanced hierarchy decisions. Original priority: P1.

- **F02.01:** An administrator configures at least two companies with distinct policies and overlapping identifiers; each actor sees only authorized entities.
- **F02.02:** An employee transfer preserves history and explicitly determines salary, benefits, accruals and payroll-effective dates without duplicate employees or double pay.
- **F02.03:** Company consolidation reconciles to scoped source totals; currency conversion, shared services, matrix management and global hierarchy support are individually declared and verified when committed.

**Dependencies / external blockers:** Company hierarchy and transfer policy decisions; customer-specific entities/FX source where applicable.

**Existing evidence/source starting point:** Assessment source IDs: K01

### F03 — Recruitment / ATS

**Baseline:** I — Candidates, applications, interviews, assessments, offers and approval endpoints exist.

**Work required:** VALIDATE. Original priority: P1.

- **F03.01:** Run approved requisition, posting, application, screening, interview/assessment, decision, separate offer approval and accepted hire linked to one employee.
- **F03.02:** Validate duplicate applicants, withdrawn/rejected offers, returning hires, consent/retention, permission boundaries and unauthorized/self approval.
- **F03.03:** Email/job-board/provider operations have explicit supported channels and proof; unconnected publishing is visibly separate from an external submission.

**Dependencies / external blockers:** Controlled messaging sinks and authorized external publishing/assessment access for committed providers.

**Existing evidence/source starting point:** backend-dotnet/Zayra.Api/Controllers/Recruitment/OffersController.cs

### F04 — Onboarding

**Baseline:** I — Templates, checklists and task/status endpoints; employee completion wizard upgraded.

**Work required:** VALIDATE. Original priority: P1.

- **F04.01:** An approved offer triggers one onboarding instance, owned tasks, documents and employee account activation; required completion gates first-day/first-payroll readiness.
- **F04.02:** Test employee, manager, HR and IT task roles, missing/expired documents, rescinded offers, late starters and duplicate callbacks.
- **F04.03:** Completion, outstanding tasks and evidence persist and reconcile to the employee and first payroll; document confidentiality and revocation are enforced.

**Dependencies / external blockers:** Approved onboarding checklist/templates and controlled identity/message sinks.

**Existing evidence/source starting point:** backend-dotnet/Zayra.Api/Controllers/Recruitment/OnboardingController.cs

### F05 — Leave and holiday management

**Baseline:** I — Leave policies, balances, approvals and KSA floor hardening are implemented.

**Work required:** VALIDATE + country-policy closure. Original priority: P0.

- **F05.01:** Independently verify accrual, opening balances, carryover/expiry, encashment, holidays, half-days, overlap and negative-balance rules for each supported country/policy.
- **F05.02:** An employee requests leave; independent manager approves/rejects/cancels; changed balances, roster, attendance and payroll reconcile to golden expectations.
- **F05.03:** Test joining/leaving/proration, effective-date policy changes, backdating, concurrent approvals and reversal; obtain domain sign-off against current primary rules.

**Dependencies / external blockers:** Current official country rules and HR/legal-approved company policies; each country has a separate acceptance record.

**Existing evidence/source starting point:** Assessment source IDs: K01

### F06 — Attendance / location controls

**Baseline:** I/F — Attendance plus selfie and server geofence controls; guarded defaults. Photo is not face recognition.

**Work required:** VALIDATE + device/privacy controls. Original priority: P1.

- **F06.01:** Clock in/out on declared web/mobile/device channels; prove server geofence rules, time zones, offline/late events, missing punch correction and supervised exceptions.
- **F06.02:** Consent, photo retention, authorized viewing and disable/revocation match published policy; selfie capture is not advertised as facial recognition/liveness without implemented proof.
- **F06.03:** Approved attendance and overtime feed payroll once; duplicate/out-of-order events, spoofed client coordinates and cross-tenant records are handled according to a declared threat model.

**Dependencies / external blockers:** Physical devices, declared attendance-provider access and approved privacy/geofence policies.

**Existing evidence/source starting point:** backend-dotnet/Zayra.Api/Controllers/SelfieAttendanceControllers.cs

### F07 — Shifts and rostering

**Baseline:** I — Scheduling/shift functionality exists; optimized staffing and demand forecasting unproven.

**Work required:** VALIDATE + declared advanced roster scope. Original priority: P1.

- **F07.01:** Create and publish shifts; employees receive schedules and authorized managers resolve swaps, absences and exceptions.
- **F07.02:** Golden cases cover overnight shifts, rest days, multiple time zones, overlapping assignments and approved overtime with reconciled payroll effects.
- **F07.03:** Declare whether demand-based staffing, auto-optimization and predictive forecasting are included; if included, test stated constraints/objective and human override with representative demand.

**Dependencies / external blockers:** Country/workforce scheduling policies and representative demand dataset for any promised optimization.

**Existing evidence/source starting point:** Assessment source IDs: K01

### F08 — Project timesheets

**Baseline:** I — Timesheet submission, inbox, approval and attendance variance endpoints inspected.

**Work required:** VALIDATE. Original priority: P1.

- **F08.01:** An employee records time to authorized projects/tasks; a different approver returns, revises and approves the submission.
- **F08.02:** Approved time reconciles to project costing and committed payroll/billing outputs without mixing attendance and billable hours.
- **F08.03:** Test closed projects/periods, duplicate entries, overlaps, corrections, locked exports, rate/effective-date rules and employee/company access.

**Dependencies / external blockers:** Project/rate authority and committed costing/billing destination.

**Existing evidence/source starting point:** backend-dotnet/Zayra.Api/Controllers/Timesheets/TimesheetsController.cs

### F09 — Employee / manager self-service

**Baseline:** I — ESS views, requests, approval restrictions and employee linking have recent upgrades.

**Work required:** VALIDATE. Original priority: P1.

- **F09.01:** New employees activate accounts, recover access and use every committed ESS request/view on actual employee identity; managers operate only their teams.
- **F09.02:** Complete profile/document update, leave, attendance correction, payslip, benefit/loan request and status tracking with linked approvals and persisted results.
- **F09.03:** Test role changes, manager changes, unlinked accounts, termination, MFA/recovery and no employee/self approval or peer-sensitive access.

**Dependencies / external blockers:** Controlled activation/recovery notifications and representative employee/manager accounts.

**Existing evidence/source starting point:** Assessment source IDs: K01

### F10 — Native mobile app

**Baseline:** P — Mobile source, MFA/OTA hardening and App Store app ID exist; public release/device evidence unverified. Refreshed README still lists biometric endpoint, offline punch/history, Hijri and push-certificate work; verify each TODO against current code before treating it as missing.

**Work required:** COMPLETE release + VALIDATE devices. Original priority: P1.

- **F10.01:** Provide installable signed iOS and Android release artifacts and stated distribution status; app identifiers/build configuration alone do not prove public release.
- **F10.02:** Run physical-device employee/manager journeys, login/MFA/recovery, push/deep links, file upload/download and explicit network/offline recovery.
- **F10.03:** Prove secure token storage/logout/revocation, Arabic/RTL/accessibility, supported OS/device range and upgrade/OTA compatibility with backend version.
- **F10.04:** Public-store availability claims require approved store listings and accessible downloads for stated markets; private distribution must be labelled separately.
- **F10.05:** Resolve verified current TODOs for biometric authentication/endpoint, offline punch/history, Hijri dates and push certificates where committed; device/browser biometrics must not be represented as employee facial-recognition attendance.

**Dependencies / external blockers:** Signing/distribution accounts, physical iOS/Android devices, push credentials, store reviews and market availability.

**Existing evidence/source starting point:** mobile/eas.json

### F11 — Arabic / RTL experience

**Baseline:** P — Arabic localization, names and sentence-formatting improvements exist; complete RTL acceptance not established.

**Work required:** COMPLETE localization acceptance. Original priority: P1.

- **F11.01:** Complete supported Arabic web/mobile journeys with RTL navigation, forms, tables, charts, dialogs and keyboard/focus behavior.
- **F11.02:** Validate mixed Arabic/Latin employee names, dates/numerals, phone/account fields, long labels and locale switching without data loss.
- **F11.03:** Arabic payslips, statutory/customer outputs, documents and exports use correct font shaping and declared terminology; missing translations/fallback strings are tracked.
- **F11.04:** HR/domain reviewer and Arabic-capable UX reviewer sign off the committed journey coverage; a locale dictionary alone is insufficient.

**Dependencies / external blockers:** Arabic language/domain review and device/browser/font coverage.

**Existing evidence/source starting point:** Assessment source IDs: K01

### F12 — Regular payroll / payslips

**Baseline:** T — 14-employee persisted journey proves approvals, lock, payslips, file generation and reconciliation on 30 Sep candidate.

**Work required:** REVALIDATE current release. Original priority: P0.

- **F12.01:** Run an independently computed multi-employee payroll from source inputs through separate approval, lock, payslips, bank instruction files and reconciliation on the named current deployed candidate.
- **F12.02:** Golden cases include joining/leaving, unpaid leave, overtime, allowances/deductions, benefits, loans, rounding and effective dates, with ledger/payslip/source reconciliation.
- **F12.03:** Prove immutable approved snapshots, recalculation restrictions, idempotency, unauthorized/self approval denial and explicit separation of approved/exported/paid states.
- **F12.04:** External payment or statutory submission claims require actual accepted external receipts; old local acceptance remains dated historical evidence.
- **F12.05:** Validate PR #223 payroll-to-GL on the deployed candidate: balanced per-company journals, rounding, preview/Lock equivalence, row-lock conflicts, no duplicate Lock/Void journal, valid active mappings/accounts and immutable approved history.
- **F12.06:** Implement committed GL cost-centre dimensions and leave/air-ticket accruals; reconcile employee/payroll provisions, EOSB accrual/settlement/void and maker-checker true-up. A statutory-formula provision is not a complete actuarial IAS 19 valuation.
- **F12.07:** Validate legacy absence contra-expense reclassification with maker-checker, preserved posted history and migration-collision warnings. Resolve unmapped legacy 2104/2105 balances without company through an auditable authorized route; do not silently assign a company.

**Dependencies / external blockers:** Current web/API parity and controlled connected environment; external channel credentials if payment/submission is committed.

**Existing evidence/source starting point:** docs/acceptance/payroll-journey/index.md

### F13 — Off-cycle / corrections / arrears

**Baseline:** I — Regular, off-cycle, supplementary, correction and replacement run types; arrears/proration code exists.

**Work required:** VALIDATE all committed run types. Original priority: P0.

- **F13.01:** Exercise regular, off-cycle, supplementary, correction and replacement/void runs with explicit eligibility and state transitions.
- **F13.02:** Reconcile arrears, retroactive pay, proration, carried YTD and already-consumed benefits/loan deductions to independent golden totals.
- **F13.03:** Test overlapping/concurrent runs, closed/paid periods, rejected/returned approval, cancellation and reversals without duplicate payment or orphaned ledgers.

**Dependencies / external blockers:** Approved payroll correction policy and current deployment/test data.

**Existing evidence/source starting point:** backend-dotnet/Zayra.Api/Controllers/PayrollController.cs

### F14 — Payroll coverage across six GCC

**Baseline:** P — Explicit registry has Saudi Arabia, UAE and Qatar only; not six-GCC payroll.

**Work required:** IMPLEMENT absent packs + country acceptance. Original priority: P2.

- **F14.01:** Keep a country-by-country capability record for Saudi Arabia, UAE, Qatar, Kuwait, Oman and Bahrain, including effective-dated source references, policy scope and exclusions.
- **F14.02:** Implement absent country rules using existing payroll authorities: social insurance, statutory deductions/contributions, leave/EOS and committed payment/reporting formats.
- **F14.03:** Independently verify golden citizen/non-citizen, age/cohort, joining/leaving, arrears and other applicable scenarios against current primary rules; special zones/regimes require separate acceptance.
- **F14.04:** Advertise six-GCC support only after each country’s committed workflow and outputs pass; a registry entry is not statutory payroll certification.

**Dependencies / external blockers:** Current official legislation/specifications; qualified local payroll/legal review; country-specific channel/provider access.

**Existing evidence/source starting point:** backend-dotnet/Zayra.Api/Application/CountryPack/CountryPackRegistry.cs

### F15 — Saudi social insurance cohorts

**Baseline refreshed:** Main PR #223 implements the new-entrant annuity ladder and idempotent seeding; the October 10 “not modelled” statement is superseded for source implementation. Current production verification remains unestablished. PR #223 flags minimum-wage floor sources, in-kind housing treatment and counsel decisions as unresolved.

**Work required:** REVALIDATE new implementation + resolve statutory source/policy gaps. Original priority: P0.

- **F15.01:** Verify the newly implemented Saudi social-insurance new-entrant/effective-date schedule against current authoritative primary rules, idempotent deployed seeding, future changes and bounded eligibility. Do not rebuild the ladder simply because the older comparison recorded a gap.
- **F15.02:** Independently test pre/post-boundary cohorts, nationality/age/service inputs, missing/contradictory data, contribution bases/caps and effective-date transitions.
- **F15.03:** Block payroll for unsupported/unknown critical cohorts rather than silently using an assumed rate; verify calculation, payslip/employer liabilities and audit provenance.
- **F15.04:** Resolve authoritative minimum contributory wage treatment and in-kind housing contribution-base interpretation with qualified counsel/domain sign-off; a warning backed by secondary sources does not establish full statutory coverage.

**Dependencies / external blockers:** Authoritative current GOSI schedule and payroll-domain approval; real eligibility data where production proof is needed.

**Existing evidence/source starting point:** backend-dotnet/Zayra.Api/Infrastructure/CountryPack/Ksa/KsaGosiCohortSchedule.cs

### F16 — Saudi WPS / bank channel outputs

**Baseline:** F/P — ANB Connect two-CSV instruction ZIP exists, allowlisted/off by default; bank/Mudad acceptance unverified.

**Work required:** COMPLETE channel acceptance. Original priority: P0.

- **F16.01:** Name every bank/channel/version and applicable WPS/Mudad route; distinguish the existing ANB instruction ZIP from accepted payment or statutory submission.
- **F16.02:** Generate deterministic immutable approved-payroll instructions, validate format/content and test duplicate prevention, rejection, resubmission and reversal/reconciliation.
- **F16.03:** Obtain bank/channel-specific accepted submission and applicable WPS/Mudad evidence; successful local file generation or internal validation is insufficient.
- **F16.04:** Default-off/allowlisted rollout, operator diagnostics, business ownership and settlement status prevent exported files being shown as paid.

**Dependencies / external blockers:** Bank/channel onboarding, official specs and accepted test/production receipts; applicable Mudad/WPS entitlements.

**Existing evidence/source starting point:** docs/saudi-bank-export.md

### F17 — Government system connections

**Baseline:** F/P — Qiwa adapter has unverified contract and partner-reference gate; no real Qiwa transaction proof.

**Work required:** COMPLETE authorized live provider proof. Original priority: P0.

- **F17.01:** Freeze named government systems, operations and supported channels; verify official contract, partner/tenant entitlement and actual authorized environment.
- **F17.02:** For each operation, execute credential-aware connection, valid transaction, rate limits/pagination, errors, retry/idempotency and persisted reconciliation.
- **F17.03:** Enforce tenant credentials, least privilege, expiry/rotation, safe disconnect, audit and operational support; remove unverifiable success states.
- **F17.04:** Qiwa’s current contract/partner gates remain blocked until licensed provider proof is supplied; other government logos are not evidence of integration.

**Dependencies / external blockers:** Official provider/partner access, contracts and test accounts, production approval where committed.

**Existing evidence/source starting point:** backend-dotnet/Zayra.Api/Infrastructure/Qiwa/QiwaLiveAdapterPolicy.cs

### F18 — Offboarding / end-of-service

**Baseline:** I — Offboarding, access revocation, settlement recording and country calculation paths exist; old error states fixed.

**Work required:** VALIDATE complete exit journey. Original priority: P0.

- **F18.01:** Run resignation/termination through authorized review, final working date, country-specific EOS/final-pay calculation, independent approval and settlement tracking.
- **F18.02:** Reconcile leave encashment, benefits, loans/advances, arrears and deductions to golden final amounts with explicit legal/company scope.
- **F18.03:** Prove access removal in app and committed external systems; handle future-dated/withdrawn exits, return/correction, outstanding assets and external payment failures.
- **F18.04:** Do not equate internal settlement recording with external payment execution.
- **F18.05:** Resolve company LOP-divisor treatment for leave encashment and independently verify final settlement boundaries; PR #223 explicitly reports that encashment does not yet use this company divisor.

**Dependencies / external blockers:** Current country/domain review and authorized identity/payment targets for external execution.

**Existing evidence/source starting point:** frontend/src/views/OffboardingPage.tsx

### F19 — Employee loans / advances

**Baseline:** T/P — Policy by company/type/grade, separate approvals, finance payouts, repayment, reversals and standalone ledger.

**Work required:** VALIDATE real disbursement/recovery. Original priority: P1.

- **F19.01:** Exercise company/type/grade eligibility, affordability/exposure, service/probation gates, independent HR/Finance/director approvals and documented exceptions.
- **F19.02:** Prove standalone and payroll-linked disbursement, repayment schedules, partial/failed payout, rescheduling, reversal and once-only ledger effects.
- **F19.03:** Reconcile real committed payout/recovery routes, payroll deductions and outstanding balance, including termination/final settlement and void/replacement runs.

**Dependencies / external blockers:** Loan policy owner, independent golden schedule and bank/payout proof where real execution is promised.

**Existing evidence/source starting point:** docs/superpowers/standalone-loans-operations.md

### F20 — Grade and individual benefits

**Baseline:** T/P — Auto assignment, HR exceptions, separate proposal approval and immutable history have isolated evidence.

**Work required:** VALIDATE + identified benefit boundaries. Original priority: P0.

- **F20.01:** Validate automatic company/grade assignment, effective dates, renewal/offer changes, individual HR exceptions and separate proposal approval.
- **F20.02:** Immutable policy/entitlement snapshots preserve history and prevent draft/rejected changes creating active benefits.
- **F20.03:** Validate canonical benefit authority across offer, employment, renewal and payroll; no competing salary/benefit state silently overrides approved entitlement.
- **F20.04:** For committed dependent/family/age/count caps and fiscal/academic periods, implement explicit eligibility, balance and revision rules and verify boundary cases.

**Dependencies / external blockers:** Latest release parity, canonical-authority resolution and approved company/dependent benefit policies.

**Existing evidence/source starting point:** docs/quality/company-benefit-policies-review-2026-10-09.md

### F21 — Benefit receipt reimbursements

**Baseline:** T/P — HR and ESS claims with receipts, budget reservation, separate approval and once-only payroll consumption.

**Work required:** COMPLETE partial claim policy + VALIDATE paid cycle. Original priority: P1.

- **F21.01:** HR and ESS submit receipt-backed claims against authorized entitlement; owned attachment integrity, budget reservation, duplicate checks and separate approval are enforced.
- **F21.02:** Implement committed per-child/dependent caps, age/count rules, fiscal/academic-year budgets and rollover rules with deterministic boundary cases.
- **F21.03:** Approved claims enter payroll or committed insurer/provider settlement once; reject/cancel/void/replacement recover balances correctly and preserve traceability.
- **F21.04:** Prove actual paid/settled lifecycle and supplier/insurer integration where promised; receipt approval alone is not settlement.

**Dependencies / external blockers:** Customer benefit rules, provider/insurer contracts and accepted settlement evidence for committed integrations.

**Existing evidence/source starting point:** docs/quality/company-benefit-policies-review-2026-10-09.md

### F22 — Business expenses / travel

**Baseline:** G — Benefit claims exist; dedicated business travel, corporate spend and card reconciliation not established.

**Work required:** IMPLEMENT dedicated workflow or supported integration. Original priority: P2.

- **F22.01:** Declare supported business expenses/travel separately from employee benefit claims, including categories, policies, per diem, mileage and receipts.
- **F22.02:** Complete travel/pre-spend authorization, expense submission, currency/FX/tax treatment, separate approvals, accounting posting and reimbursement reconciliation.
- **F22.03:** Test duplicates, limits, rejection/return, advances, cancelled travel and sensitive attachments; corporate cards/card feeds are independent committed features.
- **F22.04:** If integrated rather than native, prove actual provider operations and support ownership; do not re-label benefit reimbursements as travel management.

**Dependencies / external blockers:** Customer expense/travel rules; approved FX/tax sources and expense/card/accounting provider access where committed.

**Existing evidence/source starting point:** Assessment source IDs: K01

### F23 — Performance / goals / calibration

**Baseline:** I — Reviews, goals, competencies, 360/PIP/probation and score/calibration endpoints implemented.

**Work required:** VALIDATE full performance cycle. Original priority: P1.

- **F23.01:** Run cycle creation, goals/competencies, employee self-review, manager review and independent calibration through final decision and employee acknowledgment/appeal.
- **F23.02:** Verify committed 360, probation and PIP flows, scoring formulas, weights, deadlines and policy changes with golden outcomes.
- **F23.03:** Test conflicting reviewers, transferred managers, ineligible/terminated employees, reopen/revision and restricted private-feedback access.

**Dependencies / external blockers:** Approved review/scoring policy and controlled review participants.

**Existing evidence/source starting point:** backend-dotnet/Zayra.Api/Controllers/Performance/CalibrationController.cs

### F24 — Learning management

**Baseline:** G — No completed LMS/catalogue/enrolment/learning transcript implementation evidence found.

**Work required:** IMPLEMENT advanced module LRN. Original priority: P2.

- **F24.01:** Provide versioned course/path catalogue, prerequisites, eligibility, assignment/deduplication, content delivery, assessments/attempt rules and employee progress.
- **F24.02:** Complete assign, fail/retake, pass, transcript/certificate, expiry/renewal and manager/HR reporting with persisted evidence and unauthorized changes denied.
- **F24.03:** Declare supported content standards/providers and prove launch/token expiry, callback validation, duplicate callbacks, provider failure and completion authority.
- **F24.04:** Learning feeds succession/development and BI through existing employee/position authorities; content launch alone is not verified completion.

**Dependencies / external blockers:** Content licensing/authorized provider and declared standard access; actual playable content and representative assessments.

**Existing evidence/source starting point:** Assessment source IDs: K01

### F25 — Engagement / surveys / eNPS

**Baseline:** G — No dedicated survey, engagement analytics and action-planning cycle evidenced.

**Work required:** IMPLEMENT advanced module ENG. Original priority: P2.

- **F25.01:** Provide survey templates/questions/scales, audiences, scheduling/recurrence, invitations, reminders, submission rules and follow-up actions.
- **F25.02:** Define identified/confidential/anonymous models, response denominators, scoring/eNPS, retention and free-text access without overstating anonymity.
- **F25.03:** Independently reconcile multi-department results/trends; enforce minimum cohorts and suppression across intersecting/complementary filters, dashboards, exports and scheduled delivery.
- **F25.04:** Complete an owned action and verify logs/privileged access match the published privacy model.

**Dependencies / external blockers:** Approved privacy/threat model and confidentiality thresholds, controlled invitation sink and representative cohort data.

**Existing evidence/source starting point:** Assessment source IDs: K01

### F26 — Succession / career development

**Baseline:** G — No completed succession slates, critical-role pipeline and career marketplace evidenced.

**Work required:** IMPLEMENT advanced module SUC. Original priority: P2.

- **F26.01:** Link critical positions to existing organization authority; support nominations, talent pools, competencies, readiness/evidence and controlled review/calibration.
- **F26.02:** Approve and revise succession slates with sensitive role boundaries, historical snapshots, coverage/bench-strength reporting and declared 9-box axes where committed.
- **F26.03:** Link development plans to learning/goals; record progress/readiness changes and test vacant roles, transfer, termination/ineligibility and reviewer conflicts.
- **F26.04:** Career paths/opportunity marketplace are separate explicit subfeatures: implement and verify them if committed; do not claim them from slates alone.

**Dependencies / external blockers:** Approved talent/review policy, sensitive access model and learning/goals integration authority.

**Existing evidence/source starting point:** Assessment source IDs: K01

### F27 — Merit cycles / pay benchmarking

**Baseline:** P — Grades, salary ranges and structures exist; merit budgets, pay-equity and market benchmarking not established.

**Work required:** IMPLEMENT advanced module CMP. Original priority: P2.

- **F27.01:** Create eligible merit cycles, budgeted manager worksheets/scenarios, guidelines/proration, exceptions and separate Finance/HR approval, locking and revision.
- **F27.02:** Independently reconcile monetary awards, currencies/FX dates/pay periods/FTE basis, compa-ratio/range position and declared equity/cohort controls.
- **F27.03:** Provide licensed/customer-authorized benchmark import/feed with geography/job/level, provenance, sample/age/currency metadata and stale/missing/out-of-scope handling.
- **F27.04:** Approved effective-dated changes update existing salary/payroll authority once; rejected/scenario/overlapping changes do not reach payroll and cancellation/reversal is tested.

**Dependencies / external blockers:** Authorized real benchmark data/licensing, approved reward policy, FX source and sensitive-pay access.

**Existing evidence/source starting point:** Assessment source IDs: K01

### F28 — Workforce / headcount planning

**Baseline:** I/P — Establishment, headcount summaries and budgets exist; advanced scenarios/forecasting unproven.

**Work required:** VALIDATE foundation + IMPLEMENT committed forecasting. Original priority: P2.

- **F28.01:** Prove establishment/position/headcount budget controls, approved vacancy/requisition links, employee movement and period-close source reconciliation.
- **F28.02:** Provide versioned scenarios for hiring, attrition, pay changes and organization changes with declared assumptions, time grain and costs; compare scenarios against actuals.
- **F28.03:** Test multi-company/currency/effective-date boundaries and permissions; scenario plans remain separate from approved employee/payroll changes.
- **F28.04:** Advanced demand/skills forecasting or AI projections require separately stated model inputs, error/quality measures and human review when promised.

**Dependencies / external blockers:** Finance-approved planning semantics and representative historical/forecast data.

**Existing evidence/source starting point:** backend-dotnet/Zayra.Api/Controllers/PlanningController.cs

### F29 — Reports / scheduled exports

**Baseline:** I — Report catalogue, saved reports, schedules/history and CSV/XLSX exports; retry/error states fixed.

**Work required:** VALIDATE delivery + reconciliation. Original priority: P1.

- **F29.01:** Authorized users select catalogue reports, save variants, export CSV/XLSX and schedule delivery; actual worker/history and controlled recipients prove execution.
- **F29.02:** Independently reconcile report totals/as-of dates/time zones/currencies to HR/payroll/benefit/loan authorities.
- **F29.03:** Test denied fields, revoked recipient access, failures/retry/cancellation, duplicate delivery, empty/stale data and attachment security.

**Dependencies / external blockers:** Working scheduler/worker and controlled delivery destination.

**Existing evidence/source starting point:** frontend/src/views/ReportsPage.tsx

### F30 — Self-service BI / analytics depth

**Baseline:** P — Reports exist; arbitrary analytical model, pivot builder and external benchmarks not established.

**Work required:** IMPLEMENT advanced module BI. Original priority: P2.

- **F30.01:** Publish a governed semantic catalogue with dataset grain, joins, measures, effective/as-of dates, units/currencies/time zones and freshness.
- **F30.02:** An authorized user builds dimensions/measures/filters/grouping/sort, approved expressions, pivot/crosstab, chart and drill-through; saves, shares, exports and schedules analysis.
- **F30.03:** Reconcile headcount, payroll-cost and training-overdue scenarios independently; prevent fan-out/double counts, currency mixing and incorrect effective dates.
- **F30.04:** Enforce tenant/company/role/field permissions and survey/pay cohort suppression at query, share, export and delivery time; bound/cancel expensive queries with measured volumes.
- **F30.05:** External BI destinations/benchmarks require their own supported contracts/proofs; governed user-defined analysis does not mean unrestricted SQL.

**Dependencies / external blockers:** Stable semantic/source contracts, realistic performance dataset and external BI credentials where committed.

**Existing evidence/source starting point:** Assessment source IDs: K01

### F31 — Approval workflows / configuration

**Baseline:** I/T — Separate approvers, policy configuration and self-approval restrictions; role/isolation CI passes.

**Work required:** VALIDATE + delegation lifecycle. Original priority: P0.

- **F31.01:** Enumerate all committed workflow paths with approver authority, ordered/parallel/escalation rules, effective dates and outcomes; prove configuration controls actual runtime behavior.
- **F31.02:** Test no self approval, requester/beneficiary conflicts, tenant/company boundaries, substituted managers and concurrent/stale approvals.
- **F31.03:** Controlled delegation/emergency access expires/revokes, records rationale and audit; policy edits do not retrospectively alter immutable approved records.
- **F31.04:** Return/reject/cancel/reopen and notification failures preserve consistent source status and downstream once-only effects.

**Dependencies / external blockers:** Explicit workflow/segregation-of-duties policy and controlled notification sink.

**Existing evidence/source starting point:** Assessment source IDs: K01,K02,K03

### F32 — Prebuilt ERP / ecosystem connectors

**Baseline refreshed:** Main PR #223 supplies 11 export formats: generic CSV verified in a bounded scope; ten vendor templates pinned by golden-file tests but not imported into real ERP instances. Scoped ERP API keys for eight allowlisted export endpoints exist off by default. These do not establish ten live ERP connectors or a broad verified marketplace.

**Work required:** IMPLEMENT advanced module CON. Original priority: P1.

- **F32.01:** Freeze named supported provider/channel/version/operation catalogue; minimum advanced milestone spans two identity providers, two ERP/accounting targets including authorized D365 F&O, one attendance, one learning, one BI and one collaboration target.
- **F32.02:** Per entry prove real credentials, scopes, mappings, connection, source/destination transfer or federation, pagination/cursors, limits, idempotency, conflict/delete rules and reconciliation.
- **F32.03:** Provide retry/backoff, failed-item replay, interrupt/resume, credential rotation/revocation/disconnect, monitoring and operator ownership; reject cross-tenant credentials.
- **F32.04:** Label development, unit/mock, sandbox verified and production verified separately; unsupported providers remain explicit gaps and CSV/webhooks do not stand in for promised API operations.
- **F32.05:** Validate PR #223 export/re-export/confirmation locking, row-level confirmed evidence, ERP company/account codes and rejection of conflicting confirmations. Import each committed vendor template into its actual target test instance and independently reconcile posted records before calling that format validated.
- **F32.06:** Verify scoped ERP API keys: MFA-admin creation, one-time secret display and hashed storage, allowed endpoints, expiry/IP rules/rate limits/audit, creator deactivation and cross-tenant denial. Keep export API access separate from bidirectional vendor connector claims.

**Dependencies / external blockers:** Provider licenses, authorized sandbox/production accounts, official API/spec contracts and paid external commitments when needed.

**Existing evidence/source starting point:** Assessment source IDs: K01

### F33 — Working SAML / OIDC federation

**Baseline:** G — EnterpriseIdentity SAML ACS and OIDC authorize return HTTP 501. SCIM foundation does not establish SSO.

**Work required:** IMPLEMENT working federation + VALIDATE lifecycle. Original priority: P0.

- **F33.01:** Implement real SAML and OIDC flows as committed: signed/encrypted assertion rules, issuer/audience/nonce/state/PKCE, safe redirect restrictions and validated metadata/keys.
- **F33.02:** Test actual IdP login, account linking/JIT or stated provisioning, tenant discovery, role/group mapping, logout/session expiry and denied/invalid/replayed assertions.
- **F33.03:** Prove deprovisioning/group removal revokes access and privileged changes are audited; preserve controlled MFA/break-glass recovery and prevent tenant/account takeover.
- **F33.04:** Current HTTP 501 paths must be replaced with working verified behavior; SCIM or configuration screens alone do not satisfy federation.

**Dependencies / external blockers:** At least declared enterprise IdP test tenants/admin configuration and approved federation/provisioning policy.

**Existing evidence/source starting point:** backend-dotnet/Zayra.Api/Controllers/EnterpriseIdentityController.cs

### F34 — AI assistance / automation

**Baseline:** I/P — Kody assistant implementation exists; accuracy, action safety and adopted business outcomes unproven.

**Work required:** VALIDATE useful/safe outcomes. Original priority: P2.

- **F34.01:** Freeze assistant use cases, supported data/actions and success metrics; evaluate representative HR tasks with independently reviewed expected answers.
- **F34.02:** Enforce source/tenant/role/field permissions, sensitive-data minimization, prompt-injection defenses and correct denial/uncertainty behavior.
- **F34.03:** High-impact actions require authorized human approval, idempotent execution and auditable linkage; inaccurate/generated market pay/legal guidance is not authoritative.
- **F34.04:** Report task accuracy, unsafe-action rate, latency/cost and measurable customer outcomes at stated sample/usage scope.

**Dependencies / external blockers:** Authorized model/provider configuration, curated evaluation set and privacy/retention policy.

**Existing evidence/source starting point:** Assessment source IDs: K01

### F35 — RBAC / MFA / audit / isolation

**Baseline:** I/T — Role, MFA, import and anti-self-approval hardening; main role/isolation and security gates pass.

**Work required:** REVALIDATE deployed security matrix. Original priority: P0.

- **F35.01:** Exercise a current deployed role/company/tenant/field matrix for all committed modules including imports, exports, admin and privileged operations.
- **F35.02:** Prove MFA enrollment/login/recovery/reset/revocation and safe emergency access without revealing secrets or allowing employee/self approval.
- **F35.03:** Validate immutable/auditable actor, beneficiary, entity, prior/new state, timestamps and correlation for sensitive reads/changes; test audit access, retention and tamper controls.
- **F35.04:** Run relevant security, session, dependency, authorization and data-integrity checks against the candidate; passing CI alone does not establish independent assurance.

**Dependencies / external blockers:** Current deployed candidate parity, security test accounts and audit/retention decisions.

**Existing evidence/source starting point:** Assessment source IDs: K01,K02

### F36 — Published independent assurance

**Baseline:** G — No current KynexOne SOC 2/ISO certificate or independently evidenced operating assurance verified. CI is not certification.

**Work required:** DELIVER operating evidence + obtain independent assurance. Original priority: P1.

- **F36.01:** Publish a truthful security/procurement pack: architecture/data boundaries, supported residency, subprocessors, retention/deletion, encryption/access controls and actual assurance status.
- **F36.02:** Create and execute backup/restore and disaster-recovery drills with stated measured RPO/RTO, incident/runbook ownership, monitoring and escalation; load/availability claims require measured evidence.
- **F36.03:** Publish agreed support/service commitments, release/maintenance/change policy, vulnerability handling and factual accessibility/availability scope.
- **F36.04:** Obtain independent audit/certification only through the authorized assessor/program; publish scope, dates, entity and limitations of an issued report/certificate, never manufacture one from CI or documentation.

**Dependencies / external blockers:** Security/operations owners, approved service commitments, production restore permissions/evidence and external accredited auditor/certification engagement.

**Existing evidence/source starting point:** Assessment source IDs: K01,K02

## Closure register fields

| Field | Purpose |
|---|---|
| Family / requirement / test IDs | Stable coverage and traceability |
| Customer outcome and supported scope | Prevent implicit unlimited promises |
| Existing behavior and evidence | Reuse known implementation; distinguish dated from current |
| Confirmed gap / defect | Concrete implementation work rather than assumption |
| Criticality / priority / owner | Delivery accountability |
| Dependency and authority | Avoid duplicate employee, salary, payroll or schema ownership |
| Source / design / commit / PR / migration | Reviewable implementation linkage |
| Environment / SHA / web and API builds | Candidate and deployment identity |
| Browser/API/persistence/negative evidence | Direct outcome proof |
| External provider / license / device / statutory status | Real blocker and readiness scope |
| Gate / residual owner / due date | Honest closure decision |
| Published claim and documentation reference | Sales/documentation reflects earned scope |

## Suggested delivery dependency order

1. Refresh current main/PRs/deployments; reconcile all 36 ledger entries against valid existing evidence before opening duplicate work.
2. Accept one active module at a time. First resolve whichever dependency-ready Critical/High operational gap is actually present. Baseline P0 examples are federation, deployed verification of the newly implemented Saudi cohort rules, unresolved statutory interpretations, payroll/channel boundaries, canonical benefits and role isolation; sequence is determined by current findings, not this historical list.
3. Parallel specialists can prepare domain rules, tests, security review and documentation in bounded ownership areas while shared schema/employee/salary authorities remain singly owned.
4. Close mobile/RTL, expense/travel, remaining country packs, advanced planning and the six-module program according to actual customer commitments. Country/provider/device blockers do not stop independent code/docs work.
5. Publish evidence-backed capability/support matrices and client material; independently certify production claims and operational assurance only when corresponding proof exists.

## Refreshed evidence limitations

PR #223 reports 6,894 backend tests, 43 GL browser specs and a live local/API/Postgres QA journey. These are implementer-reported counts and bounded evidence, not independent execution performed in this review. The latest code source and currently live builds differ. Deployment/migration/statutory changes require the concrete read-only production inventory and release controls specified in PR #223; do not automatically deploy or certify actual tenant payroll behavior.

## Documentation implications

Existing repository manuals and acceptance records should be incorporated and updated, not described as nonexistent. The professional pack should include a dated capability/support matrix, user/administrator process guides, country/payroll boundaries, architecture/data ownership, API/integration contracts and named connector catalogue, security/privacy/procurement answers, operations/recovery/support runbooks, release notes, evidence index and implementation/pilot guide. Advanced modules need their own role, privacy, metric and provider details. Security documentation is not an independent audit report.

Client slides can lead with integrated workforce outcomes and evidenced differentiators. Claims such as all six GCC countries, live government/bank integration, public native apps, full enterprise federation, broad ready-made connector marketplace, independent SOC 2/ISO assurance or being ahead of most competitors remain qualified until current proof exists. A competitor capability comparison and a product certification are separate deliverables.
