# Architecture and Data Guide

## Service architecture

The reviewed stack consists of a Next.js/TypeScript web application on Vercel, a .NET 8/EF Core enterprise API on Render, PostgreSQL on Neon and S3-compatible document storage identified in the repository as Backblaze B2. A React Native/Expo mobile client uses the same API. The repository also contains a Node AI service; its presence does not establish the topology or readiness of every deployed AI capability.

| Component | Responsibility | Operating implication |
|---|---|---|
| Web client | Role-aware workforce interaction and presentation | Frontend release must remain compatible with API release and enabled features |
| Enterprise API | Authorization, validation, business state, persistence and audit | Business invariants must be enforced here, including direct-call refusals |
| PostgreSQL | Operational records, policy history, approvals and financial evidence | Migrations, uniqueness and concurrency behavior are release dependencies |
| Document storage | Employee documents and receipt bytes | Authorization must protect both metadata and actual downloads |
| Mobile client | Role-based ESS and approval journeys | Store binaries and OTA runtime compatibility require separate evidence |
| AI capability | Advisory assistance and selected insights | Sensitive actions require normal permissions and authoritative workflows |

Internal `Zayra.*` namespaces and the historical API hostname are retained build identities. They should not be renamed as a branding cleanup without a migration plan. 
## Business and isolation model

A **Tenant** is the subscriber/group boundary. A **Company** is a legal entity and operational/compliance boundary within that tenant. A **Branch** is a work location belonging to a company. Departments represent organization and reporting rather than a separate customer boundary. Grades help define salary and benefit defaults; cost centers support workforce/financial classification. A company's country, jurisdiction and currency determine which rule context its payroll should use.

Tenant account type controls SingleCompany versus Group product behavior. Company quotas are a separate subscription consideration. A SingleCompany tenant cannot create a second active company merely because its commercial limit permits one. Downgrading a Group tenant requires resolving additional active companies.

The documented data-layer design combines tenant filtering with two company-scoping tiers. Shared configuration/templates may use a null company to represent tenant-wide defaults. Operational records with an unassigned company are excluded from ordinary company-scoped users, preventing a missing assignment from becoming shared data. Group-scoped repair access remains within the same tenant. Write-side enforcement resolves a new operational record's company from the employee, a sole active company or a sole permitted scope; ambiguity fails rather than guessing.

These controls are design statements, not a claim of mathematically impossible leakage. Acceptance must include read, write, export, attachment, background-worker and privileged-bypass cases. Jobs without an interactive context require deliberate tenant/company handling. An inter-company transfer must use a defined workflow and preserve original-company financial authority.

## Data authority and lifecycle

| Record family | Authoritative business meaning | Preservation rule |
|---|---|---|
| Employee and placement | Current personnel identity and employment placement | Preserve dated change history; use governed transfer/amendment behavior |
| Policy/version | Rules accepted for the agreement or transaction | Later edits must not rewrite frozen evidence |
| Approval request and decisions | Maker proposal, ordered authority and independent decisions | Preserve terms, decision actor and state history |
| Benefit enrollment/claim | Approved coverage and receipt-backed reimbursement commitment | Separate pending reservation, approval and payroll usage |
| Payroll run/slip | Calculated and approved pay for a defined company/period | Locked evidence is preserved; correction uses a new controlled outcome |
| Payment batch/instruction | Instructions to pay defined beneficiaries | File generation is separate from external settlement |
| Loan/receipt | Disbursed principal, collections and outstanding receivable | Retain original records and compensating reversals |
| GL/ERP export evidence | Posted accounting and external confirmation lineage | Preserve posted history and prevent duplicate confirmation |

Benefits and loans explicitly use snapshot, uniqueness and transaction protections in their delivery evidence. The latest GL report describes shared row locks for payroll transitions and per-journal ERP confirmations. Verify both database constraints and application refusals on the chosen release; API fixture tests alone cannot establish PostgreSQL behavior.

## Migration and release contracts

Versioned EF migrations precede compatible code promotion. The production readiness mechanism compares migration history against a build manifest, including when migration classes are stripped from the image. A build with no known migration set must fail readiness rather than report a misleading zero pending migrations.

Financial features may refuse downgrade once real evidence exists. Keep historical tables and correct forward instead of dropping the evidence. 

## Architecture acceptance

An accepted release should include a pinned source SHA, matching tier identifiers, migration manifest, applied-history check, feature configuration schedule and scoped data-flow inventory. Validate imports, deletions, employee linkage, company transfer, receipt access, concurrent approvals and payroll/GL reconciliation with synthetic but realistic records. Preserve audit and financial history through both forward rollout and the tested recovery path.

## Sources

- [Repository stack](https://github.com/Kode-Kinetics/kynexone/blob/76985c559e9b51c246a4a48ab88e0e14eedc1ae1/README.md)
- [Frontend dependency manifest](https://github.com/Kode-Kinetics/kynexone/blob/76985c559e9b51c246a4a48ab88e0e14eedc1ae1/frontend/package.json)
- [Tenant/company architecture](https://github.com/Kode-Kinetics/kynexone/blob/76985c559e9b51c246a4a48ab88e0e14eedc1ae1/docs/GROUP_COMPANY_ARCHITECTURE.md)
- [Deployment and rollback](https://github.com/Kode-Kinetics/kynexone/blob/76985c559e9b51c246a4a48ab88e0e14eedc1ae1/docs/DEPLOY_ROLLBACK_RUNBOOK.md)
