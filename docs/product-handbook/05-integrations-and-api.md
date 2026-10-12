# Integrations and API Guide

This guide distinguishes operating workflows from adapter settings, file templates and externally accepted connections. Integration support must identify the provider, protocol/version, supported operations, authentication, entity scope, failure recovery, test evidence and support owner. A logo or export format alone is not a supported connection.

## Integration availability

| Interface | Reviewed mechanism | Current boundary |
|---|---|---|
| Web/mobile to enterprise API | Bearer-authenticated HTTP API | Use the release-specific contract and role/company scope |
| Finance GL | Account, mapping, driver, health and period routes in source | Financial changes require authorized scope and business validation |
| Payroll payment files | Generated instructions and wage-file paths | External transfer and channel acceptance remain separate |
| ANB Connect | Allowlisted two-CSV instruction ZIP | Off by default; bank onboarding/import acceptance not established |
| Qiwa/government | Gated adapter/configuration paths | Licensed real transaction, retry and reconciliation proof not established |
| ERP files | Generic CSV plus ten templates described in the latest release report | Named ERP templates await actual ERP test-instance imports |
| ERP API keys | Scoped export access described in PR 223 | Off by default; release/deployment acceptance required |
| Enterprise SSO | SAML/OIDC metadata and settings | Authentication adapters return 501 in reviewed source |
| SCIM | Token rotation and user list/create routes present | Provider interoperability and complete provisioning/deprovisioning acceptance not established |

The ERP report lists QuickBooks IIF, Oracle EBS GL_INTERFACE, Oracle Fusion FBDI, SAP S/4 GJE, Dynamics 365 F&O, Odoo, NetSuite, Xero, Zoho and QBO alongside generic CSV. These are interface-format descriptions. Do not claim ten live bidirectional ERP connectors or certified ERP partnerships.

## Verified route inventory

Routes below were verified in source or a pinned acceptance record. This is a selected integration guide rather than the full OpenAPI contract. Request fields, required grants and supported states must be read from the matching release schema before writing an integration.

| Method and path | Purpose | Authority / qualification |
|---|---|---|
| GET `/build-info` on web host | Frontend build identity | Anonymous; diagnostic only |
| GET `/health/live` on API host | API build/liveness identity | Anonymous; does not prove database readiness |
| GET `/health/ready` on API host | Migration-aware readiness | Operational readiness, not business certification |
| GET `/api/country-packs/available` | Registered country/jurisdiction choices | Registry declaration is not legal acceptance |
| POST `/api/auth/login` | Tenant-aware sign-in | Mobile guide documents email, password and tenantSlug |
| POST `/api/auth/refresh` | Refresh-token rotation | Token handling must preserve session/revocation behavior |
| GET/PUT `/api/enterprise-identity/settings` | Identity settings | `security.manage` and group entity scope |
| POST `/api/enterprise-identity/scim/token` | Rotate SCIM token | Privileged group-scoped administration |
| GET `/api/enterprise-identity/validate` | Configuration validation | Validation does not establish federation sign-in |
| GET `/api/enterprise-identity/saml/{tenantSlug}/metadata` | SAML metadata | Anonymous metadata surface |
| GET `/api/enterprise-identity/saml/{tenantSlug}/acs` | Adapter boundary | Returns 501; assertion consumption unavailable |
| GET `/api/enterprise-identity/oidc/{tenantSlug}/authorize` | Adapter boundary | Returns 501; federation unavailable |
| GET/POST `/api/enterprise-identity/scim/v2/Users` | SCIM user listing/creation | Controller authenticates a SCIM credential internally |
| GET/POST `/api/finance/gl/accounts` | Account catalogue | Finance read/manage grants checked in controller |
| GET/PUT `/api/finance/gl/mappings` | Mapping reads/updates | Finance read/manage; validate company scope |
| GET/POST `/api/finance/gl/drivers` | Posting drivers | Read or separate driver-management authority |
| GET `/api/finance/gl/control-accounts/health` | Control-account health | Finance read/manage |
| GET `/api/finance/gl/periods` | Accounting periods | Finance read/manage |
| POST `/api/payroll/runs/{id}/lock` | Lock approved payroll | Route observed in the persisted local journey |
| GET `/api/payroll/runs/{id}/payslips` | Run payslips | Role and employee scope must be respected |
| POST `/api/payroll/payment-batches/{id}/wps-file` | File generation | Local evidence proves generation, not channel acceptance |

An anonymous attribute on a SCIM route does not mean unauthenticated access: the controller performs its own SCIM authentication. Likewise, a metadata endpoint being reachable does not mean the corresponding sign-in mechanism works.

## Build an accepted integration

First agree the business event, source-of-truth ownership and direction of transfer. Choose one company/currency scope where the interface requires it. Map employee, employer, account, cost-center and document identifiers explicitly. Obtain authorized nonproduction credentials and confirm the provider's current supported contract. Record authentication and secret rotation without exposing values.

Exercise the normal business transaction against the actual external test instance. Verify the provider's accepted record or response and reconcile it to the KynexOne source. Then exercise duplicate requests, timeouts after commit, invalid credentials, rejected records, partial batches, revocation, rate limits and retry/reconciliation behavior. Persist provider references and distinguish queued, delivered, accepted and reconciled states. Confirm that a repeated operation cannot duplicate salary, loan principal or ERP accounting.

Enable the selected tenant/company only after the support owner accepts evidence and recovery instructions. Keep production enablement separate from merge. A connection that is unavailable must return an understandable boundary or remain disabled; silently returning fake success is unacceptable.

## Client implementation notes

For generated payment files, validate exact format, encoding, beneficiary fields, totals, filename and bank channel in the bank's acceptance environment. Protect downloaded personal/bank data. For ERP templates, validate account mappings and dimensional fields in the actual ERP version; imported rows and totals must reconcile to posted GL evidence. Cost-center dimensions on GL lines were identified as a subsequent workstream in the October 11 report.

For SSO, keep normal tenant sign-in until a real adapter and independent acceptance exist. Do not require an identity provider based only on metadata settings. For mobile, use the supported release environment and test token refresh, MFA, revoked access and reconnect behavior on actual devices. Offline replay and biometric sign-in are not established by the README's placeholders.

## Sources

- [Enterprise identity controller](https://github.com/Kode-Kinetics/kynexone/blob/76985c559e9b51c246a4a48ab88e0e14eedc1ae1/backend-dotnet/Zayra.Api/Controllers/EnterpriseIdentityController.cs)
- [Finance GL controller](https://github.com/Kode-Kinetics/kynexone/blob/76985c559e9b51c246a4a48ab88e0e14eedc1ae1/backend-dotnet/Zayra.Api/Controllers/FinanceGlController.cs)
- [Saudi bank export](https://github.com/Kode-Kinetics/kynexone/blob/76985c559e9b51c246a4a48ab88e0e14eedc1ae1/docs/saudi-bank-export.md)
- [Payroll route evidence](https://github.com/Kode-Kinetics/kynexone/blob/76985c559e9b51c246a4a48ab88e0e14eedc1ae1/docs/acceptance/payroll-journey/index.md)
- [ERP delivery report](https://github.com/Kode-Kinetics/kynexone/pull/223)
