# Group → Company Enterprise Test Data Reference

**Summary.** The enterprise test seeder provisions three realistic Group tenants (Almarai-, Tata-, and Emaar-styled), each with five companies across multiple jurisdictions, a full persona ladder of users (group-scope and company-scoped), and operational data (employees, branches, departments, attendance, leave, one approved payroll run, compliance profiles with deliberate gaps). It is gated behind the `SEED_ENTERPRISE_TEST_DATA=true` environment variable, is idempotent, and is **TEST DATA ONLY** — no real PII; sensitive fields are left empty or synthetic.

> **NEVER enable `SEED_ENTERPRISE_TEST_DATA` in production.** See `docs/GROUP_COMPANY_STRICTMODE_CUTOVER.md` §production env checklist.

---

## 1. Seeder gate and guarantees

- **Gate:** the seeder runs only when the environment variable `SEED_ENTERPRISE_TEST_DATA=true` is set. Absent/false → no-op.
- **Idempotent:** safe to run on every boot; existing tenants/companies/users are matched by slug/code/email and not duplicated.
- **No real PII:** names are fictional, national-id / IBAN / passport style fields are left empty or filled with clearly synthetic values. The seeded data must never be mistaken for (or mixed with) customer data.
- **Universal password:** every seeded user authenticates with `GroupDemo123!x`.

## 2. Tenants and companies

Three Group tenants (`AccountType = Group`):

### ALMARAI_TEST — slug `almarai-test` (KSA-heavy food group)
| Code | Entity |
|---|---|
| `ALM-DAIRY-KSA` | Dairy operations, Saudi Arabia (first company — gets tax policy + payroll run) |
| `ALM-POULTRY-KSA` | Poultry, Saudi Arabia |
| `ALM-BAKERY-KSA` | Bakery, Saudi Arabia |
| `ALM-DIST-KSA` | Distribution, Saudi Arabia |
| `ALM-UAE-TRD` | UAE trading entity (cross-jurisdiction case) |

### TATA_TEST — slug `tata-test` (diversified conglomerate)
| Code | Entity |
|---|---|
| `TATA-TCS-IN` | IT services, India (first company) |
| `TATA-MOTORS-IN` | Automotive, India |
| `TATA-STEEL-IN` | Steel, India |
| `TATA-HOTELS-IN` | Hospitality, India |
| `TATA-JLR-UK` | UK entity (cross-jurisdiction case) |

### EMAAR_TEST — slug `emaar-test` (UAE-heavy real-estate group)
| Code | Entity |
|---|---|
| `EMAAR-PROP-UAE` | Properties, UAE (first company) |
| `EMAAR-MALLS-UAE` | Malls, UAE |
| `EMAAR-HOSP-UAE` | Hospitality, UAE |
| `EMAAR-LEISURE-UAE` | Leisure, UAE |
| `EMAAR-KSA-PROP` | KSA property entity (cross-jurisdiction case) |

## 3. Users per tenant

Password for **all** users: `GroupDemo123!x`. `{slug}` is the tenant slug (`almarai-test`, `tata-test`, `emaar-test`); `{code}` is the company code lower-cased (e.g. `alm-dairy-ksa`).

### Group-scope users (see every company; "All Companies" switcher available)
| Email | Persona |
|---|---|
| `owner@{slug}.local` | Group owner |
| `admin@{slug}.local` | Group admin |
| `hr@{slug}.local` | Group HR lead |
| `finance@{slug}.local` | Group finance lead |
| `compliance@{slug}.local` | Group compliance lead |
| `auditor@{slug}.local` | Read-only group auditor |

### Scoped admin (the key negative-test user)
| Email | Grant |
|---|---|
| `scoped.admin@{slug}.local` | `SelectedCompanies` grants to the **first two companies only** (e.g. ALM-DAIRY-KSA + ALM-POULTRY-KSA). Must NOT see companies 3-5, must NOT get the "All Companies" switcher option, and must be denied on cross-company writes (`company_scope_denied`). |

### Per-company users
| Email pattern | Persona | Which companies |
|---|---|---|
| `admin@{code}.{slug}.local` | Company admin | Every company (5 per tenant) |
| `hr@{code}.{slug}.local` | Company HR | Every company |
| `payroll@{code}.{slug}.local` | Company payroll officer | **First two companies only** |

Example (Almarai): `admin@alm-dairy-ksa.almarai-test.local`, `hr@alm-poultry-ksa.almarai-test.local`, `payroll@alm-dairy-ksa.almarai-test.local`.

## 4. What each tenant seeds

Per company:
- **3 employees** (synthetic identities, sensitive identity/banking fields empty or synthetic),
- **2 branches**, **3 departments**,
- **attendance records** and **leave requests** (mixed statuses),
- a **compliance profile with deliberate missing items** — so readiness dashboards have real gaps to display and fail-closed paths can be exercised.

Per tenant:
- **one approved payroll run for the first company** (payslips included) — gives finance/auditor personas something real, and exercises the company-scoped payroll path,
- a **`CompanyTaxPolicy` on the first company** (company-specific row that beats the tenant default — exercises the resolution precedence in `Models/CompanyGovernance.cs`),
- **per-tenant feature variation** — the three tenants enable different feature-flag combinations so cross-tenant feature isolation is visible in tests.

## 5. Suggested verification scenarios

1. Login `scoped.admin@almarai-test.local` → companies list shows exactly 2; employees list contains only those companies' 6 employees; no "All Companies" option.
2. Same user, attempt to read/write a `ALM-BAKERY-KSA` record by id → 404/denied (`company_scope_denied`).
3. Login `auditor@tata-test.local` → sees all 5 companies read-only, including the approved TCS payroll run.
4. Login `payroll@emaar-malls-uae.emaar-test.local` → payroll surfaces for EMAAR-MALLS-UAE only.
5. Compliance dashboard per company shows the deliberately missing readiness items.
