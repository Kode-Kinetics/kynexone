# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

KynexOne serves HR administrators, payroll and compliance specialists, managers, implementation teams, employees using self-service, and auditors with read-only responsibilities. The primary user for the People workspace is an authorized HR administrator maintaining an employee record across onboarding, employment, pay, compliance, documents, access, and lifecycle actions.

## Product Purpose

KynexOne is a workforce operations platform that connects employee records, time and leave, payroll, talent, compliance, approvals, reporting, administration, and employee self-service. Success means an authorized user can understand the current record, take the next permitted action, and verify its outcome without losing context or creating conflicting data.

## Positioning

KynexOne treats the employee record as the operational hub that connects organization data, governed changes, readiness, approvals, documents, access, and downstream workforce processes. Configuration, approval, publication, and operational execution remain distinct states.

## Operating Context

The product is used as a signed-in enterprise application with tenant and company scope, role-based permissions, English and Arabic interfaces, desktop and mobile access, and auditable workflows. HR work is non-linear and interruption-prone: users move between employee records, resolve missing readiness items, submit sensitive changes for approval, and return to filtered lists.

## Capabilities and Constraints

- Preserve the existing Next.js frontend, .NET API, PostgreSQL data model, canonical endpoints, permissions, and application shell unless a separately approved change requires otherwise.
- Employee creation remains a guided, review-before-submit flow with save-draft and resume behavior.
- Existing employee maintenance must keep exact user-entered IT-issued work email behavior, explicit Arabic-name suggestions, organization-dependent manager/cost-center behavior, approval-required sensitive changes, and effective-dated salary handling.
- The employee record must not expose actions the signed-in user is not authorized to perform. Sensitive values remain masked or absent without their dedicated permission.
- Tenant, company, role, feature, and employee identity boundaries are server-authoritative. The frontend may explain and guide but must not weaken them.
- No product or legal-compliance claim is inferred from a readiness badge, configured profile, uploaded document, test fixture, or passing local test.

## Brand Commitments

The customer-facing product name is KynexOne. Preserve the established application shell, sapphire/slate visual language, Geist and IBM Plex Sans Arabic typography, English/Arabic support, RTL behavior, and restrained enterprise tone. Internal legacy `Zayra.*` names are not customer-facing and remain unchanged without a migration plan.

## Evidence on Hand

- Existing product, design, and setup contracts: `README.md`, `DESIGN.md`, and `UX-CONTRACT.md`.
- Current employee directory, profile, creation, and editing implementation: `frontend/src/views/EmployeesPage.tsx` and its browser tests.
- Existing API and persistence contracts are exercised by frontend and backend tests. Test evidence is not production acceptance.
- No external usability benchmark or formal client acceptance for the redesigned employee workspace exists yet.

## Product Principles

1. One authoritative employee workspace, not competing representations of the same record.
2. Preserve context: identity, scope, current state, approval consequence, and next action stay visible.
3. Progressive disclosure: show the selected work area rather than one endlessly scrolling record.
4. Governed change: view, edit, approval, and effective date are explicit and auditable.
5. Accessible by default across keyboard, zoom, mobile, Arabic, RTL, and reduced motion.

## Accessibility & Inclusion

Target WCAG 2.2 AA behavior. Support keyboard-only operation, visible focus, logical headings and landmarks, 44px touch targets where practical, 320 CSS px/200% zoom resilience, reduced motion, English and Arabic content, RTL layout, announced asynchronous status, and recoverable validation and error states.
