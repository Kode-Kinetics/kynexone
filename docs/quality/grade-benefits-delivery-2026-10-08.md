# Grade benefit defaults and individual exceptions

This change implements the Benefits Administration workflow shown in the request. The work is isolated on `feat/grade-benefit-defaults`; the original checkout and running application are not changed by local verification.

## Operating behavior

1. HR configures a plan's grade rules, company scope, entitlement tier, limit and effective dates. Exact grade and grade level-and-above rules are supported. Company-specific and exact-grade rules take precedence.
2. Selecting a grade while creating an employee previews the applicable benefits. The preview and creation use the same evaluator. A known service or probation waiting period produces a future coverage start; an unknown qualifying date is shown as not yet eligible.
3. Saving the hire assigns eligible defaults in the employee transaction. This applies to direct creation, approved drafts (including recruitment hires), and new imported employees. Import preview rolls everything back. Existing employees and exceptions are not backfilled or rewritten.
4. HR staff can read the assignment. Users with the existing employee-approval authority can change coverage, tier, limits, period, or waive an optional benefit, with a reason. The same authority can grant an out-of-grade plan through an explicit individual exception; company scope and plan dates remain enforced. A person cannot grant their own benefit exception.
5. Exceptions are prospective. Editing at the enrollment's original start date is allowed only before financial records exist. A later effective exception closes the prior period and creates a successor. Active successors carry existing contribution terms forward; waivers stop future contributions. Finalized payroll history is protected.
6. The original grade-rule reference and eligibility snapshot are retained. HR can see the assignment source, current entitlement and exception history. History records the actor, time, reason and before/after values. Employee self-service retains its own-employee boundary. Assigning a benefit does not initiate a loan, payout, contribution or payroll deduction.

A benefit waiting on an unknown probation/confirmation date is not assigned automatically by an undisclosed background process. If the date becomes known after creation, HR completes the employment dates and uses the supported enrollment/exception workflow. Changing a grade after creation does not silently replace an existing package.

## Architecture and controls

- `Infrastructure/Benefits/GradeBenefitDefaults.cs` owns the shared evaluator and batched staging; employee services/controllers invoke it within their existing transaction.
- Direct-create retries reconstruct rolled-back state and check the same tenant's stable employee `PublicId` after an ambiguous commit. Approved drafts resolve grade codes/names, or their designation's grade, before default staging.
- `BenefitsController` guards exception authority, tenant/company boundaries, self-benefit decisions, effective dates and optimistic versions. A common employee lock serializes enrollment, exception, contribution and deduction writes.
- Mandatory benefits cannot be waived, have their monetary limit reduced, or change limit period through an individual exception.
- Audit history uses the existing `AuditLogs` table. No new tables are added.
- `20261009035401_AddGradeBenefitDefaultsAndExceptions` adds only columns to the three existing benefit tables. Existing rows remain manual assignments; old rule labels are filled from grades only within the same tenant. Rollback refuses to erase newly recorded provenance.

## Verification record

Executed 9 October 2026 in the isolated task worktree and synthetic PostgreSQL database. Production is not changed by these checks.

- **Backend: 169 passed, zero failed or skipped.** The combined run covers benefit APIs, shared evaluation, direct creation, approved draft grade code/name/designation fallback, import preview rollback, repeat protection, mandatory limits, stale versions, contribution preservation, tenant/company isolation, seeded-role and real HTTP authorization, and required schema/table-budget/bypass/orphan checks.
- **Failure recovery:** four PostgreSQL cases inject a connection failure before or after commit, with manual or generated employee codes. Each verifies one employee, one default, one creation history, one assignment audit, stable public identity and one code-sequence increment. A separate forced benefit-write failure verifies full rollback.
- **Batch behavior:** 250 synthetic hires across two grades and two companies resolve 41 eligible assignments using at most five benefit-policy reads. Replay creates none. This verifies query behavior for the supported import-sized batch; it is not a production capacity benchmark.
- **Frontend: 14/14 browser checks passed** on the final production build, across desktop and phone. TypeScript, production build and all eight applicable internationalization checks passed (the maintenance-only baseline-repin test is intentionally skipped). Interactions cover grade/date preview, exception authority and submission, mandatory-period protection, localized audit history, scheduled contributions, and plan counts that include scheduled assignments without double-counting successors.
- **Database/runtime:** migration applied successfully to isolated PostgreSQL; `/health/ready` returned HTTP 200 with zero pending migrations. The final EF model-drift check reported no changes since the last migration. No new tables or unrelated migration changes were introduced.
- **Persisted browser scenario:** signed in as `Synthetic HR Admin`, selected Professional grade and saved a hire. Medical Gold started 2026-10-09; Education Allowance was scheduled for 2027-01-09. Both persisted with `GradeDefault` provenance. HR changed medical coverage to Family and the limit from SAR 25,000 to SAR 30,000 with an attributed reason and before/after history. No contribution or deduction was created.
- **Runtime authorization:** HR Officer exception mutation returned 403; another company's HR Manager received 404 for the enrollment. Ordinary out-of-grade enrollment returned 400; the authorized reasoned exception returned 200. The read-only HR view is checked after its entitlement and history finish loading.
- **Responsive evidence:** the live grade preview at 390px has no horizontal overflow; real API responses returned 200 with zero page errors. The preview was cancelled without an employee creation request.

Evidence is in `output/grade-benefits-2026-10-08/` in this worktree: `live-verification.json`, desktop screenshots `01`–`04`, and `05-live-mobile-preview.png` with its companion verification record. The folder date records the task start; the runtime evidence contains execution timestamps. Raw backend results are `/tmp/kynex-grade-benefits-tests/grade-benefits.trx`. Test credentials/tokens are not part of the repository or report.

## Separate release boundary

The opt-in `release_a` grade matrix/package module remains the authority for tenants using that module. This change does not issue duplicate legacy defaults for those tenants or bypass its signed-package controls. Source review found its employee exception/renewal offer path incomplete. The current package link is not presented as an implemented exception editor. That gap is a separate release blocker, documented with market comparisons in [grade-benefits-market-review-2026-10-08.md](grade-benefits-market-review-2026-10-08.md).

## Owner walkthrough

- Create or select a benefit plan and add a grade rule with its company, tier and limits.
- Open People → Add Employee. Select company and grade; inspect the default benefit preview and start dates.
- Save the employee. Open Benefits → Enrolments and confirm the grade defaults.
- Open an enrollment and apply an individual exception with a reason. Verify coverage/limit, source and history.
- Use Enrol employee for a plan outside the grade, select Apply an individual exception, and record the reason.
- Repeat with an HR user lacking employee-approval authority: the exception controls are absent and direct mutation is denied.
