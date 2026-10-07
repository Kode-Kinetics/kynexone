# Jawazat: official-source research and proposed implementation boundaries

Status: official-source research supporting the implemented **internal exit/re-entry slice**, not legal certification or a live government integration. User requested company-policy workflow plus live integration and specifically asked for Saudi exit/re-entry rules. Reviewed 2026-10-04. Public official pages differ by service, applicant category, route and publication date; recent crawl dates do not prove a rule's effective date. Provider contracts and applicable legal review are required before production enforcement.

## Verified published rules and software implications

### Employer-assisted issuance

[Absher Business issuance guide](https://www.absher.sa/wps/wcm/connect/business/business%20portal/eservices%20user%20guide/electronic%20services/services_3_en) lists presence in Saudi Arabia, passport validity of at least 90 days, payment of visa fees and worker traffic violations, absence of an existing valid visa or unused-visa cancellation violation, and applicable status/biometric checks.

Implementation proposal: service-specific preflight with Passed/Failed/Unknown and evidence timestamps. Government-only facts cannot be inferred from an employee HR status. Local eligibility or HR approval cannot mark a visa issued.

### Worker self-service is a separate route

[HRSD's March 2021 Labour Reform Initiative guide](https://www.hrsd.gov.sa/sites/default/files/2021-03/13032021.pdf), pages 14–18, describes qualifying professional expatriate workers, an active documented contract, residence validity covering the trip, passport validity of at least 90 days, presence inside the Kingdom, and traffic-violation conditions. It describes employer notification and a worker-operated route in addition to employer issuance. It also contains route-specific waiting periods, fee responsibility and restrictions. Its historical timings/penalties must not be hard-coded as universal current rules without confirmation.

Implementation proposal: distinguish employer-assisted and eligible worker self-service routes; keep employer notification, leave/travel approval and government visa status separate. Do not turn notification into employer consent or assume the employer can cancel every worker-issued visa.

### Cancellation and extension

[Absher Business cancellation guide](https://www.absher.sa/wps/wcm/connect/business/business%20portal/eservices%20user%20guide/electronic%20services/services_6_en) specifies cancellation within 90 days of issue or before the return date on the visa, whichever is earlier, and requires presence in the Kingdom, along with listed status/violation conditions.

[Absher FAQs](https://www.absher.sa/wps/wcm/connect/individuals/contents/individuals/FAQs_EN?uri=nm:oid:moi.absher.footer.about) says cancellation does not refund fees and extension is counted from the visa expiry date. Extension eligibility must be resolved for the actual service/beneficiary route; family/domestic-worker service documentation must not be silently applied to corporate workers.

Implementation proposal: independent issue/cancel/extend actions; retain issue, depart-by, return-by and expiry dates as distinct concepts. Retain fees/evidence after cancellation. Do not equate cancellation with refund, or extension with a new visa.

### Fees require scoped reconciliation, not an arbitrary payer switch

[HRSD's published 2025 Labour Law amendments, Article 40](https://www.hrsd.gov.sa/sites/default/files/2025-03/Amendments%20to%20Labor%20Law%20Articles_0.pdf), pages 10–11, includes exit/re-entry fees among employer-borne costs. The older 2021 worker self-service guide describes worker-borne costs for that particular route. These must be reconciled for current applicability; this research does not resolve a legal conflict by selecting whichever rule is convenient.

Implementation proposal: legal-reviewed route-specific allocation; preserve quoted fee, currency, evidence, fee-rule version and payer basis. No universal employee-payroll deduction and no production fee formula based solely on an old brochure. Fees and provider charges need current provider/service evidence before activation.

## Existing code and proposed reuse

- Existing visa/passport/work-permit records, renewals, compliance reminders/audit and company compliance profiles are available. Saudi dashboard currently focuses on Qiwa/WPS/GOSI; no full Jawazat exit/re-entry/final-exit workflow was found.
- `VisaTrackingController` uses hard-coded reminder thresholds; update does not reconcile reminders. Renewal status accepts arbitrary transitions and is not a completed government transaction.
- Prefer existing `HRRequest` for a typed service request, existing `ApprovalRequest` for routed internal decisions, `VisaRecord` for resulting visa evidence, and a separately validated extension on `CompanyComplianceProfile` for service policy. Preserve `RequiredFieldsJson` for employee readiness; do not hide unrelated fee/service rules in it.
- The implemented internal slice extends existing compliance-profile/HR-request models in migration `20261004191027_AddLoanJournalEvidenceAndJawazatPolicy`; it creates no Jawazat tables. Typed requests enforce frozen company and employee scope, with owner-only historical access after transfer and guarded generic mutation endpoints.
- Track internal request state separately from provider submission/outcome; protect duplicate submission and reconcile ambiguous timeouts before retry. ProviderUnavailable/PendingGovernmentConfirmation must not appear as Issued.
- A loan balance can trigger internal Finance review, not a fabricated government travel prohibition. Final exit/offboarding and debt recovery are related operational tasks but must not overwrite each other's records or legal outcomes.

## Options and recommendation

1. **Recommended:** extend existing company policy, HR request, approval, visa and notification structures with a typed Jawazat workflow and a separate authorised-provider adapter. Minimal schema additions, but every generic request status endpoint must respect the new guarded workflow.
2. Document/renewal enforcement only: smaller change, but does not fulfil the requested live exit/re-entry integration.
3. Standalone Jawazat subsystem: more independent lifecycle freedom, but duplicates existing requests, approvals and policies; not justified by current discovery.

Outstanding prerequisites: approve the route-separated design; identify authorised Muqeem/Elm, Absher Business or other provider access; supply technical API documentation, sandbox and supported operations through secure configuration. Public visa rules do not specify endpoint/authentication/idempotency contracts. No portal-login automation, credential collection in chat, government submission or paid transaction was attempted.
