# Jawazat policy workflow

**Owner:** CTO/integration owner; Saudi HR/Compliance, Finance/Audit and integration consultant SME review.

**Decision:** Reuse CompanyComplianceProfile, HRRequest, ApprovalRequest, VisaRecord and ComplianceAuditLog. No new Jawazat tables. First vertical slice is exit/re-entry request, company policy enforcement, HR company-assisted approval, employee-self-service notification, evidence/preflight and explicit provider-unavailable state. Live submission remains blocked until authorised provider documentation/access and sandbox acceptance are available. No guessed endpoints, portal scraping or simulated issued visas.

- [x] Define typed, versioned policy/request payload contracts and nullable fields on existing profile/request records. Keep statutory rules separate from company policy and route-specific requirements.
- [x] Add tests for dates, company/tenant scope, policy resolution/snapshot, employee visibility, approver separation and unknown government facts. Implement dedicated service/controller and shared approval producer integration with generic HR-request mutation guards.
- [x] Add disabled provider contract and capability response. Internal approval must never imply government issuance. Outstanding employee loans must never become a statutory travel block.
- [x] Reuse existing compliance UI navigation for policy editing, employee requests, HR queue/status visibility; surface provider unavailable and rule/evidence uncertainty clearly. Approval workflow configuration uses the existing API; see `docs/superpowers/jawazat-operations.md`.
- [x] Generate a single root-owned additive migration with zero new tables and preserved-history downgrade guards. No customer database migration applied.
- [x] Run backend tests, TypeScript checks, rendered desktop/mobile browser checks, then independent spec and quality review. See `docs/superpowers/2026-10-04-loan-jawazat-verification.md`; live integration remains disabled pending authorised access and certification.

Evidence: `docs/superpowers/jawazat-rules-research-2026-10-04.md`. Do not hardcode historical 2021 timelines or universal fee liability from conflicting route-specific publications. Final exit and Iqama renewal must not pretend to be implemented by the exit/re-entry slice.
