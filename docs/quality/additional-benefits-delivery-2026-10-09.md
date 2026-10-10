# Individual benefits in addition to grade defaults

An authorized HR user can propose an additional catalogue benefit for one employee while preserving the employee's grade benefits. The proposal creates a pending approval request; only final independent approval creates the entitlement. This extends the local grade-default implementation in `3d78a6de`.

## Product flow

1. During **Add Employee**, choose the company and grade. The Employment and Review steps show the default benefit names, limits, and effective dates. Creation assigns eligible grade benefits automatically, including known future eligibility dates. **Review employee benefits** opens the saved employee package.
2. Open **People → employee → Benefits → Manage benefits**, or **Benefits → Manage benefits**. Assigned benefits and available catalogue benefits appear together. Grade defaults are identified and retained.
3. Check one or more available benefits, set their start/end dates and limits, and enter one confidential **Reason for changes**. Ongoing benefits require a review date. Coverage, treatment, planned costs, and the employee-visible message are under optional controls. **Save changes** sends the selected requests; successful rows are locked and failed rows can be corrected without resubmitting successes.
4. The configured independent approver reviews each proposal in **Approvals**. Multi-step routes apply the entitlement only after the final decision. Pending proposals do not change the package.
5. Use **Edit terms** to amend an additional benefit or **Adjust existing benefit** for an authorized grade exception. Additional amendments require a fresh approval and retain dated history.
6. Uncheck an additional benefit and choose its last covered day to request an end; a date before scheduled coverage starts requests cancellation. Removal requires independent approval and retains the original record and history. Mandatory benefits cannot be removed in this flow.

Administrators can configure the default ordered role route through **Benefits → Additional benefit approvals**. Existing department/grade routes take precedence. The focused editor preserves individual/reporting-line routes and does not attempt to rewrite them. Missing routes fail with actionable guidance. Routes with pending requests cannot be edited.

## Controls and scope

- Tenant/company boundaries, domain permissions, configured routing, requester/beneficiary bars, and earlier-step separation of duties are enforced server-side. Disabled/deleted historical identity links cannot evade the beneficiary bar.
- Proposals seal the original employee/company/grade, plan currency/classification, ordered workflow, terms, and amendment baseline. Approval revalidates the current facts and version. Drift requires withdrawal and resubmission.
- PostgreSQL transaction locks serialize benefit writers. Replays and overlapping pending/current grants cannot create duplicates. The domain audit, approval decision, and entitlement persist atomically.
- Approved planned costs do not create payments, contributions, loans, or payroll deductions. Existing contribution agreements continue across eligible dated amendments; finalized payroll remains protected.
- Review dates flag review without ending coverage. End dates govern expiration. Authorized views flag changes to grade/company or plan terms. Existing company filters continue to hide issuing-company details from new-company-only HR.
- ESS exposes the public reason and approved terms, not internal justification or the approval payload.
- This is the existing Benefits Administration authority. Tenants using the separate `release_a` canonical package authority remain guarded onto that path; its unfinished offer/renewal exception work is not represented as completed here.
- All additional grants require configured independent approval. This does not implement monetary-threshold routing, proxy delegation, or retroactive grants. The employee panel shows the most recent 100 additional requests; the existing Approvals queue remains paginated.

## Schema and release

`20261009132348_AddGovernedAdditionalBenefits` adds three nullable fields to `benefit_enrollments` (`approval_request_id`, `grant_reason`, `review_date`) and a filtered unique tenant/approval index. It reuses `ApprovalRequest` for pending proposals and adds no tables or employee backfill. A PostgreSQL trigger protects immutable proposal evidence and deletion while permitting normal status/routing updates.

Apply the migration before starting this application version. Configure approval routes and eligible role authority before operational use. Rollback refuses to remove the schema after any governed proposal or grant exists; preserve the evidence and use a forward correction. Code, schema, and preview remain local; no production deployment is authorized or performed.

## Verification

The original governed-benefit implementation was verified below. The subsequent checklist and removal extension passed **264 backend tests** (including 30 removal cases), **16 persisted API removal checks**, **42 desktop/phone browser checks** against the final production build, localization (8 passed, 1 maintenance-only skip), and a real desktop/mobile journey covering employee creation, two grade defaults, two selected additional benefits, independent approvals, and a later approved end. Both grade rows remained unchanged. No schema changes were introduced by this extension. The extension evidence is in `/tmp/kynex-benefit-checklist-tests/`, `/tmp/kynex-benefit-end-api-evidence.json`, and `/tmp/kynex-benefit-checklist-browser/`.

### Original governed-benefit baseline

- Combined backend suite: **234 passed, 0 failed, 0 skipped**. Includes grade-default/exception regressions, 47 new individual-benefit security cases, approval/routing checks, and schema/bypass/orphan/permission ratchets.
- Independent persisted API scenarios: **27 passed**, covering two-stage approval with three distinct actors, pending/reject/withdraw states, amendment history, company isolation, duplicates/replays, legacy bypass denial, and absence of financial side effects.
- PostgreSQL migration applied; readiness returned no pending migrations. EF model drift check passed. Migration visibility gate found **95 files / 95 discoverable migrations**.
- Actual PostgreSQL witness mutation and deletion attempts were blocked; test transactions rolled back.
- Final production build, TypeScript and localization checks passed (8 localization checks; the maintenance-only repin check skipped).
- **36 desktop/phone browser checks passed against the final production preview**. They cover existing defaults/exceptions, the additional proposal/amendment flow, approval setup, nested modal focus/Escape/reopen behavior, ESS presentation, and authorized/unauthorized employee Benefits URLs.
- The real production-build browser journey used separate requester and approver sessions, persisted a pending proposal, completed approval, then reloaded the employee package. Both original grade rows were byte-for-byte unchanged; the third entitlement was linked to its approval and created no contribution/deduction rows. Browser runtime errors: zero.
- Desktop and phone visual inspection verified the populated Benefits tab, enabled add action, container-aware cards, and no horizontal page overflow. The new tab leads with its benefits; activation/access cards remain on the other employee tabs. Unauthorized tab URLs return to Personal without requesting benefit data.

Local verification evidence (synthetic data only):

- `/tmp/kynex-additional-benefits-tests/additional-benefits.trx`
- `/tmp/kynex-additional-benefits-api-evidence.json`
- `/tmp/kynex-additional-benefits-trigger-evidence.json`
- `/tmp/kynex-additional-benefits-model-check.log`
- `/tmp/kynex-additional-benefits-visibility.log`
- `/tmp/kynex-additional-benefits-browser/`
- `/tmp/kynex-additional-benefits-final-production-fixtures.log`
- `/tmp/kynex-additional-benefits-production-build.log`

The local production preview runs at `http://127.0.0.1:5281`. Synthetic employee `12` demonstrates creation defaults plus two individual benefits, including one approved end date: `/people?employeeId=12&tab=benefits`. The separate API runs at `127.0.0.1:5218` against the dedicated synthetic PostgreSQL database. No live customer data is involved.

## Research basis

The design follows the distinction between eligibility exceptions and enrollment changes documented by [Oracle](https://docs.oracle.com/en/cloud/saas/human-resources/faibf/what-happens-if-i-enable-participation-eligibility-override.html), dated individual overrides in [Dynamics 365 Human Resources](https://learn.microsoft.com/en-us/dynamics365/human-resources/hr-benefits-setup-life-event-types), and employee-level benefit adjustments in [SAP SuccessFactors](https://help.sap.com/docs/successfactors-employee-central/implementing-global-benefits/88040a4846874d4e84fa04d7e00d1484.html?locale=en-US&state=PRODUCTION&version=2605). These sources informed the earlier SME review; the controlled workflow above is KynexOne's product decision, not a claim that each competitor implements it identically.
