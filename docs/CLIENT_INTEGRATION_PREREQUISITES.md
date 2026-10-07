# Client package — integration prerequisites

## Jawazat: deferred client integration

Scope decision (2026-10-04): authorised government/provider access is not currently available. Live integration is deferred to client onboarding and is not a blocker for delivery of the standalone loan and internal Jawazat policy/approval workflows. It remains a hard gate for live government transactions, not a completed integration.

Until onboarding and acceptance are complete, retain the disabled provider and the visible “Government submission unavailable” status. Internal approval does not issue a visa, verify government eligibility or trigger a fee. No credentials-only activation is supported: engineering must implement and verify the authorised provider adapter.

### Agreed configuration location

The future integration setup belongs in **Saudi Compliance Command Center → Configure → Jawazat**. Reuse the existing Configure screen rather than creating a separate integration-settings module. Its planned responsibilities are client prerequisites/readiness, provider/environment setup, secure credential references, authorised company/establishment mapping, connection verification and integration health. Provider-specific fields and activation controls must follow the supplied API contract and appropriate administrator permissions.

This is the agreed placement for the deferred work, not an existing live Jawazat configuration panel. Employee requests/history remain in the Jawazat workflow, HR decisions in the approvals workflow, and company policy remains in the existing compliance profile with navigation from Configure where appropriate.

### Client/provider inputs

- [ ] Named client business sponsor, HR/compliance owner, IT/security owner and provider support contact.
- [ ] Evidence of authorised service access and approved legal entities/establishments; agreed provider contract and permitted operations.
- [ ] Official provider API documentation, supported environments and operations, authentication requirements and any certificates/network allowlists required by that provider.
- [ ] Sandbox access and approved test identities/data. Supply secrets through an approved secret manager or secure exchange—not in this package, source control or ordinary email.
- [ ] Provider status, error, duplicate/idempotency, timeout and reconciliation specifications; callbacks if supported.
- [ ] Agreed fee/payment arrangements, payer responsibilities, data-sharing/retention requirements and client approval of the company policy and HR routes.

### Delivery-team acceptance before live activation

- [ ] Confirm supported scope against the supplied contract; do not assume portal access grants API access.
- [ ] Implement and review the provider adapter with tenant/company isolation, secure secret handling and redacted audit evidence.
- [ ] Pass sandbox tests for success, rejection, duplicate submission, unknown timeout outcomes, reconciliation and fee evidence; obtain client UAT sign-off.
- [ ] Approve production credentials/access, operational monitoring, support ownership and a disable/recovery procedure; obtain explicit go-live approval.

Track each item in the client's onboarding record with owner, status, evidence reference and target date. Keep secret values out of that record. Final exit, Iqama renewal, cancellation and extension are not delivered by the current exit/re-entry slice; agree any additional scope separately.

Internal-workflow setup and recovery: [Jawazat operations](superpowers/jawazat-operations.md). Other deployment and data-migration gates continue to apply: [deployment playbook](DEPLOYMENT_MULTI_CLIENT.md) and [loan operations](superpowers/standalone-loans-operations.md).
