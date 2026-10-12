# Security, Privacy and Enterprise Review Guide

KynexOne has documented authentication, permissions, company scope, audit and workflow controls. Independent assurance, contract-specific privacy obligations and the security configuration of a customer deployment require separate evidence. No SOC 1, SOC 2 or ISO certification is established by this pack.

## Control statements

| Control area | Reviewed evidence | Required deployment proof |
|---|---|---|
| Authentication | JWT/refresh mechanisms and privileged MFA flows in mobile documentation | Actual enforcement, enrollment, recovery, session expiry and revoked-user tests |
| Authorization | Tenant/company filtering, permission checks and domain-specific maker/checker bars | Cross-role and cross-company negative requests, including downloads and direct API calls |
| Record integrity | Immutable benefit witnesses, concurrency protections and preserved loan correction evidence | Database mutation refusal and concurrent transaction tests on the release |
| Audit | General and module audit records are documented | Actor, decision, target and correlation evidence for critical transitions |
| Document privacy | Owned receipt evidence and scoped file access in benefit delivery | Metadata and byte-access tests; retention and deletion behavior |
| Enterprise identity | Settings, metadata and SCIM-related routes | SAML/OIDC authentication remains unavailable in the reviewed adapter boundary |
| ERP credentials | Latest release report describes hashed, expiring scoped keys and MFA administration | Accepted deployed implementation, key scope, expiry, revocation and logging tests |
| Independent assurance | Earlier repository posture is a readiness narrative | Current independent report and scope before any assurance claim |

The historical SECURITY_POSTURE document is dated 8 June. Its exact role/table counts, placeholder-secret warnings and migration recommendations are not current deployment measurements. Validate live configuration privately; do not repeat old assertions as current findings or disclose secret values in evidence.

## Access administration

Grant the minimum permissions and company scope required for a task. Separate the maker from the checker for benefits, loan decisions, cash recording and material financial changes where the workflow enforces that distinction. Avoid a single shared administrator identity for day-to-day operations. Review assignments when employment or company placement changes, and test that terminated/deactivated users cannot retain access through old sessions or integration credentials.

Confirm identity linkage before enabling ESS: an employee must see only their own permitted profile, claims, loans and payslips. A manager's team authority must not imply unrestricted salary-derived loan eligibility access. Group administrators remain confined to their tenant. Privileged APIs must refuse unauthorized calls even when a UI control is hidden.

## Privacy responsibilities

The customer and provider should agree their actual controller/processor roles, purpose of processing, hosting region, subprocessor list, retention periods and cross-border arrangements in approved contracts. This source review does not establish those contractual facts. Maintain a data inventory covering identifiers, employment, bank details, salary, receipts, attendance images/location and any sensitive HR evidence.

Collect receipt, location and image evidence for stated business purposes, with appropriate employee notices and required permissions. Avoid placing confidential approver justification in employee-visible messages. Keep training/demo records synthetic. Redact credentials, personal details, bank identifiers and sensitive attachments from screenshots, support tickets, logs and exported QA evidence.

Define request handling for access, correction, deletion and retention exceptions. Deletion requests may interact with mandatory financial or employment retention; use an approved policy and authorized process. Do not claim automated subject-request orchestration or automatic legal compliance unless the executable flow is accepted.

## Security evidence for procurement

A procurement pack should include an approved architecture diagram, role matrix, data inventory, authentication/recovery results, tenant/company isolation tests, vulnerability/dependency results, penetration-test scope and findings, encryption/key-management facts, backup/restore evidence, incident contacts and contractual service terms. Mark absent independent evidence as unavailable. 

## Sources

- [Historical security posture](https://github.com/Kode-Kinetics/kynexone/blob/76985c559e9b51c246a4a48ab88e0e14eedc1ae1/SECURITY_POSTURE.md)
- [Company-scope design](https://github.com/Kode-Kinetics/kynexone/blob/76985c559e9b51c246a4a48ab88e0e14eedc1ae1/docs/GROUP_COMPANY_ARCHITECTURE.md)
- [Mobile MFA and token handling](https://github.com/Kode-Kinetics/kynexone/blob/76985c559e9b51c246a4a48ab88e0e14eedc1ae1/mobile/README.md)
- [Benefit integrity and access evidence](https://github.com/Kode-Kinetics/kynexone/blob/76985c559e9b51c246a4a48ab88e0e14eedc1ae1/docs/quality/company-benefit-policies-review-2026-10-09.md)
- [ERP-key implementation report](https://github.com/Kode-Kinetics/kynexone/pull/223)
