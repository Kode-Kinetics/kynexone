# KynexOne Professional Documentation Pack

**Edition:** 11 October 2026 · **Audience:** customers, implementation teams, product owners and enterprise reviewers

It extends the existing company operations manual and process-flow playbook. It is a practical documentation baseline, not a certificate that every feature is deployed, enabled or accepted.

## How to use this pack

| Document | Primary audience | Decision it supports |
|---|---|---|
| 01 Product and availability | Buyer, sponsor, product owner | Which capabilities belong in the contracted deployment |
| 02 Administrator and user guide | HR, payroll, finance, managers, employees | How to carry out controlled workforce processes |
| 03 Architecture and data | IT, architecture, engineering | How services and business records fit together |
| 04 Security and privacy | Security, procurement, privacy | What controls exist and what assurance remains unverified |
| 05 Integrations and API | IT, finance systems, developers | Which integration mechanisms can be used and what needs acceptance |
| 06 Operations and release | Operations, release owner, support | How to promote, monitor and recover a candidate safely |
| 07 Client implementation and support | Sponsor, implementation lead, customer success | How to scope, migrate, pilot and hand over the solution |

## Evidence convention

**Implemented** means current source contains a capability. **Bounded tested** means a referenced acceptance record covers a particular release, environment and scenario. **Gated** means licensing or the organisation's module switch is required before use. **Not established** means completion has not been demonstrated by the reviewed evidence. A successful automated check does not establish external bank acceptance, public mobile distribution or independent certification.

The reviewed source candidate is `76985c559e9b51c246a4a48ab88e0e14eedc1ae1`. KynexOne is installed on each organisation's own server, so what an organisation runs is the release version on that server (`scripts/status.sh`). Health responses identify builds; they do not certify customer workflows or an installation's settings.

## Documentation control

The release owner should publish these documents with a release manifest and approved availability schedule. Product owners maintain workflows; engineering maintains architecture and API contracts; security maintains control statements; operations maintains recovery instructions. Update the affected section when schema, permissions, behavior, integration scope or contractual support changes. Retain the superseded edition with its release identifier.

Each customer should receive a concise deployment schedule listing legal entities, countries, enabled modules, approval routes, interfaces and acceptance exclusions. This schedule has precedence over a generic feature list. Prices, service commitments, hosting regions, recovery targets and certification claims require their own approved commercial or assurance evidence.

## Source baseline

- [Pinned repository](https://github.com/Kode-Kinetics/kynexone/tree/76985c559e9b51c246a4a48ab88e0e14eedc1ae1)
- [GL and statutory release work, PR 223](https://github.com/Kode-Kinetics/kynexone/pull/223): implementation and verification claims are attributed to that delivery report until independently reproduced on the deployment.
- [Client installation guide](../CLIENT_INSTALLATION.md)

