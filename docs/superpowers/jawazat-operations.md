# Jawazat internal workflow: deployment prerequisites

Implemented scope: Saudi exit/re-entry requests, HR approval for employer assistance, employee notification recording, reviewed company policies, local preflight evidence, request history and disabled-provider status. This is not a visa-issuance system yet. Cancellation, extension, final exit and Iqama renewal are not supported by this slice.

## Configure before enabling employer assistance

1. Apply the reviewed additive migration in a backed-up staging environment first. No customer database was migrated during development.
2. An Admin/Compliance Officer sets a Saudi company compliance profile and reviews its Jawazat policy. The dedicated policy editor patches only `jawazatPolicyJson`; unrelated readiness fields are retained.
3. An authorised administrator configures an active `JawazatRequest` approval workflow using the existing `/api/approval-workflows` API. Every step must use an HR role. There is no new workflow-configuration UI or automatically manufactured approval route. Apply department/grade routing and default selection deliberately; missing or ambiguous routing prevents employer-assisted creation.
4. Confirm the actual users' employee permissions, company scopes and independent HR approvers. A requester cannot approve their own request. Central approval callers require a matching authenticated, explicitly scoped principal.
5. Verify worker notification and employer assistance separately. Recording a worker notification is not employer consent, a government eligibility decision, or evidence that the employee used the government service.

Example workflow request (illustrative configuration; do not create automatically):

```json
{
  "code": "JAWAZAT-HR",
  "name": "Jawazat employer assistance HR review",
  "entityName": "JawazatRequest",
  "isActive": true,
  "isDefault": true,
  "steps": [{ "stepOrder": 1, "stepName": "HR review", "approverRole": "HR Manager", "approverType": "Role", "isFinalStep": true }]
}
```

## Live integration gate

The shipped provider is disabled. Internal approval and provider-unavailable records must never create an issued visa or a charge. Government facts unavailable locally remain Unknown.

Activation requires the customer's authorised provider contract, supported-operation documentation, secure sandbox credentials, authentication and idempotency specifications, reconciliation/status contracts and tested production onboarding. Public Absher/HRSD information is not an API contract. Do not automate portal logins or invent endpoints.

Before live release, test duplicate submissions, timeouts with unknown outcomes, provider reconciliation, company isolation, redacted audit retention, fee evidence and legally reviewed payer allocation. An outstanding loan is not a statutory travel prohibition. No automatic payroll charge is introduced here.

## Recovery

The new migration refuses downgrade after financial/Jawazat evidence exists; prior loan migrations also protect payment history. Use a reviewed forward correction or a tested backup restore, not a destructive downgrade. Do not infer or backfill ambiguous historical loan journal links automatically.
