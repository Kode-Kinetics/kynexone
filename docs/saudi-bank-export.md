# Saudi bank-instruction export

## Scope and supported channel
Implemented format: `anb-connect-csv-v1`, the two-CSV payroll input documented by ANB Connect.
This is not proof of acceptance by ANB's corporate portal (WPY), another bank, or Mudad. No bank API calls, payment authorisation or money movement are performed. The bank-signed statutory return is separate.
The adapter is shared across all tenants. Each company configures its own employer facts once. Additional bank/channel formats are implemented once as versioned shared adapters, not as customer-specific forks.
Source checked September 26, 2026: https://connect.anb.com.sa/apis/api/payroll-payment
Workflow reference: https://www.sab.com/en/sme/services-and-solutions/payment-management-services/faqs-payroll-service/
MHRSD reference (2015 document; not a universal current bank-upload format): https://www.hrsd.gov.sa/sites/default/files/2017-06/WPS%20Wages%20File%20Technical%20Specification.pdf

## User workflow
Payroll → Bank / WPS Files → select a locked payroll run → open its payment batch → Bank instruction file — ANB Connect CSV.
1. Save the employer setup: supported format/channel, MoL establishment ID, ANB 16-digit debit account, employer name/address, narrative and batch type.
2. Complete employee national ID/Iqama and banking information using the normal employee workflow.
3. In Employee bank beneficiary details, enter the BIC and all three address lines. Submit for separate approval; submission is not application of the change. After approval, reload and validate.
4. Enter the unique batch reference and payment date, validate, generate and download.
5. Extract `header.csv` and `body.csv` from the transport ZIP. Hand them to the customer's approved ANB Connect integration/operator under its bank agreement. This export does not implement the submission integration.
No banking login, password, API secret or OTP is needed to generate files. Channel access/authorisation is the bank/customer's separate responsibility.

## Data and protection
The file reads locked payroll slips and frozen payment records, not current salary master values. Basic + housing + other earnings − deductions must equal net; all records, batch and run totals must agree exactly. Overtime/bonus/arrears already included in locked gross flow into other earnings.
Validation rejects unsupported formats, unresolved payroll errors, missing mandatory fields, duplicates, unsuitable IDs/IBANs/BIC syntax, company/currency mismatch, pending effective bank changes, and changed approved bank accounts. No employee is silently dropped. IBAN checksum/BIC syntax checks do not certify account ownership or bank-routing correspondence.
Approved beneficiary JSON uses `schema: saudi-bank-beneficiary-v1` in existing Employee.WpsBankDetails. No branch/company address is invented for an employee. The existing sensitive-change approval applies it.
Settings and export requests require payroll.export; settings changes additionally require payroll.structure_manage. All reads/writes are scoped to tenant and legal entity. Generic system-settings endpoints exclude this reserved category.
Existing SystemSettings, BankTransferFile and payroll audit infrastructure are reused: no new tables, migrations or dependencies.
Generation persists exact CSV bytes and hashes, atomically with an audit record. Repeated identical requests return the same artifact; changed inputs conflict. PostgreSQL tenant-level advisory locking serializes reference generation across companies sharing an account. Payroll-run row locking protects the run snapshot during generation.
Downloads verify file and audit-bound envelope hashes, refuse cancelled/voided or no-longer-locked instructions, and use private/no-store headers. Export does not change payment/WPS status or produce cash/GL entries.
Bank files contain sensitive personal/bank data and inherit database/backup protection; this feature does not introduce application-level encryption of their stored contents.

## Verification and limits
Latest focused backend regression: 125 passed, zero failed/skipped. This includes new export tests, KSA/UAE/WPS regression, payment scope and transaction lint. It is NOT the full HRM suite.
Real PostgreSQL cases cover concurrent same-batch generation, duplicate references across batches, immutable download and approved employee-details → default exporter → download, with no payment-state or GL changes. The schema comes from an isolated fixture, not a production migration/deployment certification.
Two HTTP authorization checks run the real ASP.NET pipeline with an isolated SQLite host: anonymous access is 401; an authenticated user without export permission is 403.
Chrome desktop and phone UI fixtures cover validation, artifact metadata, download, beneficiary-change submission and stale-validation prevention. All API calls are intercepted, so these are UI contract tests, not a complete UI/API/PostgreSQL integrated run. Both have zero page errors and zero page-level horizontal overflow.
Frontend TypeScript validation passes. A StrictMode mount/unmount bug found during the UI exercise was corrected in the beneficiary editor.
No real bank file was submitted, no salary was transferred, and no production deployment was performed. Bank acceptance, selected-channel operational onboarding and deployed-stack verification remain separate gates.
CSV byte choices use UTF-8 without BOM, CRLF and CSV quoting with invariant decimal text. Where the public bank page does not explicitly prescribe encoding/quoting, these are implementation choices, not bank certification claims.

## Reproduce tests
From repository root:
```sh
dotnet test backend-dotnet/Zayra.Api.Tests/Zayra.Api.Tests.csproj --filter 'FullyQualifiedName~SaudiBankExport|FullyQualifiedName~WpsTests|FullyQualifiedName~KsaPackTests|FullyQualifiedName~UaePackTests|FullyQualifiedName~PaymentBatchScopeTests|FullyQualifiedName~ExecutionStrategyLintTests'
cd frontend && npx tsc --noEmit
```
With a local Next dev server running, from repository root:
```sh
E2E_BASE_URL=http://127.0.0.1:5200 node frontend/e2e/saudi-bank-export.ui.mjs
```
Docker is needed for PostgreSQL tests. The UI runner uses installed Playwright and Chrome, generates only synthetic intercepted requests, and writes screenshots/results under artifacts/saudi-bank-export unless SAUDI_EXPORT_EVIDENCE_DIR is specified.
The source remains on the pre-existing dirty worktree; merge/release must preserve unrelated changes and record a clean candidate with matching UI/API versions. Passing these feature checks is not unrestricted application production certification.


## Controlled integration and activation (2026-09-27)

This feature is **OFF by default**. Merging or deploying this code activates no customer.
The operator-owned `SaudiBankExportActivation` configuration requires both `Enabled=true`
and an exact `(TenantId, CompanyId)` entry under `Companies`. Missing/invalid IDs, an empty
list, a missing switch, or a false switch deny access. Wildcards and tenant-wide fallbacks
are not supported. No rollout values are committed in application settings.

The authenticated, company-scoped `GET batches/{batchId}/availability` returns only a boolean.
The UI uses that endpoint before loading the panel; settings/context/validation/generation/
download are independently denied by the backend when OFF. A stale browser, a direct API
call, or editing ordinary tenant settings cannot bypass the operator's switch.

Enable only the intended tenant/legal entity after confirming the actual bank channel and
completing its acceptance. Disabling the switch blocks subsequent downloads as well as new
generation. Restart/reload the API according to its configuration provider when changing
operator environment variables. No funds move as part of activation or file generation.

Example configuration SHAPE (placeholder IDs, not an instruction to activate any tenant):
`SaudiBankExportActivation__Enabled=false`
`SaudiBankExportActivation__Companies__0__TenantId=<tenant-guid>`
`SaudiBankExportActivation__Companies__0__CompanyId=<company-guid>`

The release task integrates only the export feature and its necessary integration fixes.
The source worktree with unrelated changes must remain untouched. Bank acceptance, automatic
transmission, and production rollout remain separate from merge approval.
