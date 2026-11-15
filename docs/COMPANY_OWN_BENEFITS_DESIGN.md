# A company's own benefits (Setup > Benefits by grade)

Owner decision 2026-11-15. A company admin adds a benefit (contract benefit or monthly cash allowance) that belongs to their company only. Group HR keeps the group-wide add.

## Where the fact lives

No new table and no new column. A benefit's definition is still one `pay_components` row (or one row per effective-dated version of a cash allowance):

| Row | `company_id` | `is_offered` | Meaning |
|---|---|---|---|
| Group benefit | NULL | true | Offered to every company (minus "not offered" switches). Unchanged. |
| "Not offered" switch | the company | false | A company skips a group benefit. Unchanged. |
| A company's own benefit | the company | true | Offered to that company alone. New. |

Switches are only ever written with `is_offered = false`, so a company row that is offered is always a company's own definition. This follows the existing pay component precedent (a company row is that company's) and the database's config write guard (a company-scoped user may write rows of a company they reach, never a tenant-wide row).

## Ownership

- Created with `companyId` in `POST /api/entitlements/components`: the caller must reach that company (403 `company_not_accessible` otherwise, whether or not it exists). Without `companyId` the benefit is the group's and needs access to every company, as before. A `scope` naming any other company is refused (`scope_not_company`).
- Edit, remove, confirm and correct: a company's own benefit is handled by anyone who reaches its company. To anyone else it does not exist (404 `unknown_component`, the same answer as a code that was never created). A group benefit still needs access to every company.
- Widening: group HR sends a `scope` with more companies on `PUT /components/{code}`. The rows move to `company_id = NULL`, every company left out gets a "not offered" switch from the benefit's own start, and from then on it is a group benefit (the company admin gets 403 `group_scope_required`). A cash allowance widens to every company or not at all, its GL driver must post in every company, and it goes back to "pending Finance" unless the person widening holds the Finance permission.

## Names and codes

- Codes stay one identity across the tenant, because payroll lines, grade values and salary lines name a benefit by code. A company's own code ends in `_` plus six characters derived from the company's id (`ALW_CAR_K3P9QZ`). Two companies can each have a "Car" and neither is ever told a code is taken because of the other.
- Names are unique among the benefits the new one sits beside: a company's own against the group's and that company's own (never a sister company's); a group benefit against every one (group HR sees them all).

## Who reads definitions, and what changed

| Reader | Before | Now |
|---|---|---|
| Matrix read (`ReadAsync`) | tenant rows | group rows, plus the viewed company's own. The group grid shows no company's own. Offering mode `Own`. Capability `companyBenefits`. |
| Publish | tenant rows | the target grid's catalogue: a value for another company's own benefit is `unknown_component`. |
| Grade standard (`GradeStandardLinesAsync`, `EntitlementResolver.GradeStandardAsync`) | tenant rows when the definition feature is on | group rows and the company's own; a custom code with no definition this company can see is never offered. |
| `CashAllowanceDefinitions.VersionsAsync` | tenant rows | tenant rows by default (fail closed); `companyId` adds that company's own; `anyCompany` for callers that check access or only name a line already on a salary. |
| Salary staging (`SalaryAllowanceLines.ValidateAsync`, proposals, breakdown writer) | tenant rows | the employee's company decides: a sister company's own allowance is refused like a code that does not exist. |
| Payroll run, back pay, staleness, arrears | tenant rows | the run's company. |
| Pre-run readiness | tenant rows | per employee's company. |
| End of service wage, salary line names | tenant rows | any company (the line is already on the record). |
| Finance confirm/correct | group scope | group scope for a group allowance; access to the owning company for a company's own. Maker is never checker. A company's own allowance is never confirmed by its maker, even one who holds the Finance permission. |
| GL driver choice and validation | tenant drivers, every company | for a company's own allowance: that company's drivers (its own override wins) judged in that company. |
| Committed-period guard | every company's runs | the owning company's runs for a company's own benefit. |
| "Waiting for Finance" notice | Finance with group scope | plus Finance of the owning company. |

The `benefit_definition_v1` flag keeps its meaning. `companyId` is not a definition field, so a company's own benefit can be added with the flag off.
