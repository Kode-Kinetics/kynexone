# Product and Availability Guide

KynexOne brings employee administration, time, approvals, payroll and selected financial workforce processes into one operating platform. Its strongest documented proposition is a connected workflow with explicit authority, dated policy evidence and traceable financial outcomes. Customers should evaluate the configured deployment against their own legal entities, policies and interfaces.

## Product scope

| Area | Reviewed position | Customer use boundary |
|---|---|---|
| Organization and employees | Implemented company, branch, department, cost-center, grade and employee records | Validate imports, employee activation and company visibility for the deployment |
| Recruitment and onboarding | Implemented candidates, applications, interviews, assessments, offers and onboarding tasks | Accept the requisition-to-hire journey and its independent approvals |
| Time and leave | Implemented attendance, shifts, leave, overtime and timesheets | Accept local rules, overnight shifts, exceptions and payroll effects |
| Employee and manager self-service | Implemented requests, approvals, payslips and employee-linked views | Prove account linkage and restrictions with real customer roles |
| Payroll | Regular and alternative run types are implemented; a persisted local payroll journey is documented | Current release, country rules, corrections and settlement require deployment-specific acceptance |
| Benefits | Grade defaults, governed individual benefits and receipt reimbursements have bounded persisted evidence | Specialized dependent rules and insurer settlement are outside that evidence |
| Loans | Company policies, approvals, separate payout recording, repayment and correction workflows are documented | External transfers occur outside the platform; prove reconciliation before live use |
| Performance | Reviews, goals and calibration are implemented | Accept the configured review cycle and employee access |
| Reporting and planning | Catalogue reports, saved/scheduled reports and planning foundations exist | Arbitrary self-service BI and advanced planning depth are not established |
| Enterprise identity | Identity settings, metadata and SCIM-related routes exist | SAML/OIDC sign-in is explicitly unimplemented in the reviewed controller |
| Mobile and Arabic | React Native/Expo source and English/Arabic interfaces exist | Public store release, device coverage and complete RTL acceptance are not established |

The six advanced gaps—learning, engagement surveys, succession, advanced merit/pay benchmarking, governed self-service BI and a broad supported connector catalogue—remain completion workstreams. 
## Country and integration scope

The current country-pack registry lists Saudi Arabia, United Arab Emirates and Qatar. UAE labels include mainland, DIFC and ADGM; listing a jurisdiction does not certify the complete legal behavior of its payroll calculations. Kuwait, Oman and Bahrain are not listed packs in the reviewed registry. Contract payroll only for independently accepted country and jurisdiction scope.

The reviewed Saudi GOSI code now obtains new-entrant annuity rates from effective-dated statutory rows. This supersedes the earlier code-level “not modelled” condition. Missing applicable employee or employer rows still prevent a valid rate result. The October 11 delivery report describes a seeded schedule and tests; source change and a reported test run do not establish that the live database contains all applicable rows or that every cohort has passed customer acceptance.

Saudi bank instructions include an ANB Connect two-CSV ZIP format, gated off by default and allowlisted to specific tenant/company combinations. File generation does not transfer salaries or establish WPS/Mudad acceptance. Government connectivity, including Qiwa, needs actual licensed endpoint, transaction and reconciliation evidence before it is sold as an operating connection.

The latest GL release report describes generic CSV and ten named ERP formats. Generic CSV is reported verified within the implementation. Other formats are templates awaiting import into the relevant ERP test instance. 
## Intended customer roles

Employees maintain permitted requests and inspect their own records. Managers review their team within assigned scope. HR configures employment policies and manages personnel processes. Payroll prepares and reviews pay calculations. Finance controls payment recording, loan cash events, accounting and reconciliation. Approvers make independent decisions within the configured route. Administrators grant authority and configure access; platform administrators manage subscriber-level controls. Titles are descriptive: the actual permission grants and company scope determine access.

## What to demonstrate to a buyer

Use a connected demonstration: set up a legal entity and grade; create and activate an employee; submit a leave or benefit request; obtain a separate approval; calculate payroll; review and lock the result; generate payslips and payment instructions; inspect employee self-service; reconcile source records. Explicitly show the distinction between approved, included in payroll, instructions generated and externally paid.

Do not advertise universal competitor parity or market leadership from this scope alone. The defensible story is integrated workforce operations with documented policy and financial controls. Claims about speed, cost reduction, accuracy or usability need measured customer results. Claims about full GCC coverage, certified compliance, independent assurance or turnkey third-party connections need their respective acceptance evidence.

## Sources

- [Country-pack registry](https://github.com/Kode-Kinetics/kynexone/blob/76985c559e9b51c246a4a48ab88e0e14eedc1ae1/backend-dotnet/Zayra.Api/Application/CountryPack/CountryPackRegistry.cs)
- [GOSI cohort schedule](https://github.com/Kode-Kinetics/kynexone/blob/76985c559e9b51c246a4a48ab88e0e14eedc1ae1/backend-dotnet/Zayra.Api/Infrastructure/CountryPack/Ksa/KsaGosiCohortSchedule.cs)
- [Enterprise identity controller](https://github.com/Kode-Kinetics/kynexone/blob/76985c559e9b51c246a4a48ab88e0e14eedc1ae1/backend-dotnet/Zayra.Api/Controllers/EnterpriseIdentityController.cs)
- [Saudi bank export scope](https://github.com/Kode-Kinetics/kynexone/blob/76985c559e9b51c246a4a48ab88e0e14eedc1ae1/docs/saudi-bank-export.md)
- [GL/statutory release report](https://github.com/Kode-Kinetics/kynexone/pull/223)
