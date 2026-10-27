# Architecture and Data Guide

## Service architecture

KynexOne is installed on the organisation's own server and serves that one organisation. The installation kit ([`deploy/client/`](../../deploy/client/README.md)) runs every component as a container behind one HTTPS address; nothing depends on a hosted service. See the [client installation guide](../CLIENT_INSTALLATION.md).

| Component | Responsibility | Operating implication |
|---|---|---|
| Reverse proxy (Caddy) | The one public address; TLS (internal CA, Let's Encrypt or the organisation's certificate) | Only ports 80 and 443 are published |
| Web client (Next.js) | Role-aware workforce interaction and presentation | Shipped in the same release as the API; the two always carry one version |
| Enterprise API (.NET 8) | Authorization, validation, business state, persistence, audit, licence enforcement | Business invariants must be enforced here, including direct-call refusals |
| PostgreSQL 16 | Operational records, policy history, approvals, financial evidence, data-protection keys, the installation licence | Backed up by `backup.sh`; migrations apply on install and upgrade |
| Redis | Caching and rate limiting | Holds nothing that must survive a restore |
| Document storage | Employee documents and receipt bytes, in a local volume or the organisation's S3-compatible store | Authorization must protect both metadata and actual downloads |
| Mobile client (Expo) | Role-based ESS and approval journeys | Pointed at the organisation's server on first start; HTTPS only |
| KynexBridge | Polls attendance devices on a branch network and posts to the API | Installed on that network, not on the KynexOne server |
| AI capability | Advisory assistance and selected insights | Off unless `AI_PROVIDER` is set and the licence grants an allowance |

Internal `Zayra.*` namespaces are retained build identities. They should not be renamed as a branding cleanup without a migration plan.

## The licence

A signed licence file (ECDSA P-256) states the installation id it belongs to, the organisation, the limits (employees, users, administrators, companies), the modules and optional features, the monthly AI allowance, the support period and the expiry with grace days. The API verifies the signature whenever it loads the licence and reloads it every minute, so an imported licence takes effect without a restart. Limits are enforced at the action that would exceed them; after grace, everything except sign-in and licence administration answers "licence expired". A missing or invalid licence never deletes data.

## Business and isolation model

The **Organisation** is the installation's one top-level boundary; it is created once, by the installer, and there is no second organisation. A **Company** is a legal entity and operational/compliance boundary within it. A **Branch** is a work location belonging to a company. Departments represent organization and reporting rather than a separate customer boundary. Grades help define salary and benefit defaults; cost centers support workforce/financial classification. A company's country, jurisdiction and currency determine which rule context its payroll should use. How many companies may exist is a licence limit.

The data layer still carries an organisation key (`TenantId`) on every row and filters by it, alongside two company-scoping tiers; the schema restructure for a single organisation removes that key. Shared configuration/templates may use a null company to represent organisation-wide defaults. Operational records with an unassigned company are excluded from ordinary company-scoped users, preventing a missing assignment from becoming shared data. Organisation-scoped repair access is limited to organisation administrators. Write-side enforcement resolves a new operational record's company from the employee, a sole active company or a sole permitted scope; ambiguity fails rather than guessing.

These controls are design statements, not a claim of mathematically impossible leakage. Acceptance must include read, write, export, attachment, background-worker and privileged-bypass cases between companies. Jobs without an interactive context require deliberate company handling. An inter-company transfer must use a defined workflow and preserve original-company financial authority.

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
- [Organisation/company architecture](https://github.com/Kode-Kinetics/kynexone/blob/76985c559e9b51c246a4a48ab88e0e14eedc1ae1/docs/GROUP_COMPANY_ARCHITECTURE.md)
- [Client installation guide](../CLIENT_INSTALLATION.md)
