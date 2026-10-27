# Client Implementation, Acceptance and Support Guide

## Start with the operating outcome

A successful implementation means the customer can complete its agreed workforce processes with accurate records, proper authority and reconciled outcomes. Begin with named legal entities, employee populations, jurisdictions, languages, payroll calendar and external interfaces. Establish measurable outcomes such as completing the agreed payroll cycle without unexplained variance, preserving independent approval and making correct payslips available to eligible employees. Baseline present effort and errors before claiming improvement.

## Implementation stages and exit evidence

| Stage | Deliverable | Exit condition |
|---|---|---|
| Discovery | Signed process, country and interface scope | Every required outcome has an owner and acceptance criterion |
| Server and installation | Server sized and installed with the kit; address, TLS, SMTP, storage and backups decided; licence issued for the installation id | `scripts/status.sh` reports ready with a valid licence; a backup has been taken and restored on a test server |
| Configuration | Organization, policies, roles and approval routes | Resolved settings and access boundaries verified after reload |
| Migration rehearsal | Mapped, validated import and reconciliation | Source and imported counts/totals agree; rejected rows are explained |
| Connected pilot | Real UI/API/database journeys with representative synthetic data | Positive, negative and correction scenarios pass with persisted evidence |
| Country/interface acceptance | Payroll rules and actual provider test records | Domain reviewer and interface owner accept exact supported scope |
| Release readiness | Agreed KynexOne version, licence modules and limits, and a tested restore | No critical unresolved defect; controlled release decision recorded |
| Go-live and handover | Approved production schedule, trained owners and support runbook | First operational cycle reconciled and support responsibility accepted |


## Migration discipline

Identify the authoritative source for employees, companies, grades, salary structures, leave balances, bank details and outstanding loans. Agree field mappings and stable identifiers. Validate duplicates, required dates, currency, organizational placement, employee-to-user linkage and financial opening balances. Rehearse imports on a separate test installation (never the live server), retain rejected-row reasons and reconcile source-to-target counts and financial totals.

Before final migration, agree a change cutoff and incremental update process. Obtain the customer's sign-off on opening balances and unresolved data exceptions. Use supported import or approved migration paths; do not manually edit financial repayment modes or overwrite historical approvals. Protect extracted personnel data and remove temporary copies under the approved retention policy.

## Pilot scenario set

The minimum connected pilot should cover hire/activation, self-service account linkage, company-restricted access, leave/time approval, benefit assignment and reimbursement, loan approval/payout recording/repayment, payroll approval/lock, payslip visibility, GL reconciliation and offboarding. Add every contracted country and interface. A full-coverage register must include all 36 comparison families, even when their implementation exists but current acceptance is missing.

Use distinct named test actors for maker, approver, employee, manager, payroll and finance. Include normal and denied scenarios: cross-company reads/writes, self-approval, duplicated requests, stale proposals, budget exhaustion, missing statutory rows, invalid documents, concurrent payroll lock, partial external failure and retry. Complete Arabic and mobile journeys within their contracted scope. Unsupported advanced modules must be excluded from the signed deployment schedule until accepted, or implemented and tested as separate closure workstreams.

## Acceptance record

For each criterion, record the business expectation, source SHA, environment/builds, enabled gates, actor/scope, scenario data, UI action, API outcome, record identifiers, fresh persisted result, reconciliation and defect disposition. Screenshots support presentation evidence; they do not replace database or provider confirmation. Label mocked UI fixtures, local full-stack proof, deployed acceptance and external-provider tests accurately.

The accountable reviewer classifies the module as accepted, conditional, remediation required or blocked. Conditional acceptance must state an explicit limitation, risk owner and bounded operational scope. A failed critical authorization, pay calculation, duplicate-posting, isolation or settlement criterion prevents unrestricted acceptance. The closure percentage is calculated against committed criteria with evidence; it is not a promise of every feature in the market.

## Training and handover

Train by role and process rather than menu tour. HR should configure policy and handle exceptions; employees should submit requests and interpret status; managers should review within team scope; payroll should diagnose validation and reconcile corrections; finance should distinguish instruction generation from external payment and record/reverse cash evidence. Administrators should grant access, recover accounts, read the licence status and switch maintenance; the IT team should take and restore backups and run upgrades (see the [installation administration chapter](02-administrator-and-user-guide.md#9-installation-administration)).

Handover includes the installed version and licence, the server's `.env` decisions (without secret values), the backup schedule and off-server location, the agreed deployment schedule, role matrix, configuration workbook, acceptance register, migration reconciliation, integration catalogue, operating runbook, recovery evidence, known limitations and named support owners. Give each customer an explanation of planned versus available modules and the change-request process. Do not embed unsupported roadmap claims in contractual scope.

## Support intake and escalation

Support requests should identify the KynexOne version and installation id (`scripts/status.sh`), company, affected process, time, sanitized record/correlation IDs, expected behavior and observed result. State whether payroll, funds, employee access or statutory deadlines are affected. Redact credentials, bank data and unrelated employee information. For urgent payroll or access faults, involve the designated operational owner; for uncertain payment state, reconcile bank evidence before retrying. 
Support channels, operating hours, severity definitions, response commitments and maintenance windows must come from the approved commercial/support agreement. None are independently established by this documentation. Track incidents through diagnosis, correction, deployment and customer verification, retaining the evidence needed to prevent recurrence.

## Client-facing claims

Use demonstrated process strengths in proposals: connected workforce records, explicit approvals, policy history and reconciled financial workflows. Show which candidate and scenarios substantiate each claim. Describe country packs and interfaces at their accepted level. Present learning, engagement, succession, advanced compensation, self-service BI and connector expansion as planned or in delivery until the full accepted outcome exists. 
## Sources

- [Pinned product source](https://github.com/Kode-Kinetics/kynexone/tree/76985c559e9b51c246a4a48ab88e0e14eedc1ae1)
- [Persisted payroll acceptance example](https://github.com/Kode-Kinetics/kynexone/blob/76985c559e9b51c246a4a48ab88e0e14eedc1ae1/docs/acceptance/payroll-journey/index.md)
- [Benefits acceptance example](https://github.com/Kode-Kinetics/kynexone/blob/76985c559e9b51c246a4a48ab88e0e14eedc1ae1/docs/quality/company-benefit-policies-review-2026-10-09.md)
- [Client installation guide](../CLIENT_INSTALLATION.md)
- [Release parity requirements](https://github.com/Kode-Kinetics/kynexone/blob/76985c559e9b51c246a4a48ab88e0e14eedc1ae1/docs/RELEASE_CANDIDATE_AND_PARITY.md)
