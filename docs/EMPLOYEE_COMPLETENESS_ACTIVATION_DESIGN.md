# Employee Readiness & Hard Activation Gate — Implementation Spec

**Status:** Implementation-ready. Folds the base design + three SME registers (GCC HR Compliance, Product/UX, Senior Engineering) into one buildable spec, with every material claim re-verified against the code in this branch.

**Owner requirement (the brief):** creation is the easiest thing you can do (name-only); an employee becoming **Active** requires effort; the "required-to-activate" set is **tenant-configurable** with **seeded GCC defaults**; a configured policy may be **stricter, never weaker** than the statutory minimum; **no hard-coded field list**; import is **lenient** (accepts everything with a name, records gaps, never silently lands Active); the list shows a **red/amber/green readiness signal + a fix checklist**; and an under-documented person can be **neither activated nor paid**.

**Compliance disclaimer (retained verbatim in every readiness response):** this encodes prevailing GCC labour/WPS/social-insurance practice as **configurable readiness defaults, not legal certification**. Country rule packs still require licensed-advisor validation before go-live. This matches the codebase's own posture — the disclaimer string at `CompanyGovernanceController.cs:200` and the `CountryTier` certified/fail-loud split (SA/AE certified; QA/KW/OM/BH fail-loud, `Application/CountryPack/CountryTier.cs:24-25`).

### Path key (short forms used throughout)
| Short form | Full path (repo root = `/Users/zackkhan/Downloads/zayra-ai-workforce`) |
|---|---|
| `EmployeeManagementService.cs` | `backend-dotnet/Zayra.Api/Infrastructure/Employees/EmployeeManagementService.cs` |
| `EmployeesController.cs` | `backend-dotnet/Zayra.Api/Controllers/EmployeesController.cs` |
| `CompanyGovernanceController.cs` | `backend-dotnet/Zayra.Api/Controllers/CompanyGovernanceController.cs` |
| `OffboardingController.cs` | `backend-dotnet/Zayra.Api/Controllers/OffboardingController.cs` |
| `SetupSettingsController.cs` | `backend-dotnet/Zayra.Api/Controllers/Admin/SetupSettingsController.cs` |
| `CompanyGovernance.cs` | `backend-dotnet/Zayra.Api/Models/CompanyGovernance.cs` |
| `Employee.cs` | `backend-dotnet/Zayra.Api/Models/Employee.cs` |
| `EmployeePayrollProfile.cs` | `backend-dotnet/Zayra.Api/Models/EmployeePayrollProfile.cs` |
| `SetupAdmin.cs` | `backend-dotnet/Zayra.Api/Models/SetupAdmin.cs` |
| `CompanyTaxPolicyResolver.cs` | `backend-dotnet/Zayra.Api/Infrastructure/Governance/CompanyTaxPolicyResolver.cs` |
| `StatutoryRateGuard.cs` | `backend-dotnet/Zayra.Api/Infrastructure/Payroll/StatutoryRateGuard.cs` |
| `IbanValidator.cs` | `backend-dotnet/Zayra.Api/Infrastructure/Payroll/IbanValidator.cs` |
| `WpsSifValidator.cs` | `backend-dotnet/Zayra.Api/Infrastructure/Payroll/WpsSifValidator.cs` |
| `CountryTier.cs` | `backend-dotnet/Zayra.Api/Application/CountryPack/CountryTier.cs` |
| `EstablishmentOccupancy.cs` | `backend-dotnet/Zayra.Api/Application/Organization/EstablishmentOccupancy.cs` |
| `EstablishmentHttp.cs` | `backend-dotnet/Zayra.Api/Application/Common/EstablishmentHttp.cs` |
| `EnterpriseGroupSeeder.cs` | `backend-dotnet/Zayra.Api/Infrastructure/Seed/EnterpriseGroupSeeder.cs` |
| `TenantProvisioningBundle.cs` | `backend-dotnet/Zayra.Api/Infrastructure/Seed/TenantProvisioningBundle.cs` |
| `EmployeesPage.tsx` | `frontend/src/views/EmployeesPage.tsx` |
| `employees.ts` | `frontend/src/api/employees.ts` |
| `ImportExportToolbar.tsx` | `frontend/src/components/.../ImportExportToolbar.tsx` |

**Severity legend:** **P0** = blocking, must ship in the first PR for the feature to be correct/owner-compliant · **P1** = should-fix in the same milestone · **P2** = enhancement / fast-follow.

---

## 1. Ground truth — what the code does today (verified in this branch)

| Area | File:line | Current behaviour | Gap vs brief |
|---|---|---|---|
| CSV import status default | `EmployeesController.cs:643` — `Status = string.IsNullOrWhiteSpace(statusVal) ? "Active" : statusVal` | Blank status ⇒ **Active** | **Import silently creates Active, incomplete employees.** Directly violates "Active requires effort." |
| Import create floor | `EmployeesController.cs:449` | Rejects a row only when `FullName` blank; optional-ref problems degrade to `warnings` (`:414/:487/:502`); bad IBAN → warning (`:782`) | Correct floor, but feeds the Active default above. |
| Import writes no score | Import path never calls `CalculateCompleteness` | Imported rows persist `ProfileCompletenessScore = 0` | Badge would be wrong for every imported row. |
| Draft → Active | `EmployeesController.cs:1501` — `Status = "Active"` in `ApproveDraft` (`:1449`); establishment guard only at `:1562` | Approves straight to Active; **no completeness gate** | No readiness gate. |
| Status change / Activate | `EmployeeManagementService.cs:174` `ChangeStatusAsync`; occupancy guard at `:197`; `ActivateAsync:370` delegates to it | Only the establishment/occupancy guard | No completeness gate. |
| **4th Active path (design missed it)** | `OffboardingController.cs:215` — `emp.Status = "Active"` (rescind-resignation `Cancel`) | Writes Active **directly**, bypassing `ChangeStatusAsync` entirely | A hard gate that only wraps `ChangeStatusAsync` leaks here. |
| Create (form) | `EmployeeManagementService.cs:104` — `employee.Status = "Draft"`, score at `:105` | Always Draft (correct); this is the model to generalise | — |
| Completeness math | `EmployeeManagementService.cs:931` — `private static decimal CalculateCompleteness(...)`, fixed ~22-field denominator, **zero statutory fields**; called at `:105/:146`. Second variant for drafts at `EmployeesController.cs:2385`, called at `:1325/:1341`, copied at `ApproveDraft:1536` | Hard-coded, tenant-blind, pure/0-query | Violates "no hard-coded field list"; an employee reads 100% while missing Iqama/GOSI/IBAN. |
| Config policy foundation | `CompanyComplianceProfile` (`CompanyGovernance.cs:58-81`): `ITenantOwned, ICompanyScoped`, `CountryCode` (`:71`), `EffectiveFrom` (`:76`), `Status`, `RequiredFieldsJson` (`:81`, e.g. `[{"field":"IqamaNumber","failClosed":true}]`) | Full CRUD + `GET /readiness` at `CompanyGovernanceController.cs:176`, but **aggregate-only, Active-only** | The policy exists; it is a read-only dashboard with **zero enforcement usages**. |
| **Unknown-key fail-OPEN hole** | `CompanyGovernanceController.cs:351-362` — `GetField` resolves **8** scalars; `_ => "n/a", // unknown field name — counts as present, never breaks readiness` | Any key outside the 8 (docs, expiry, MolId, GOSI-as-doc…) **silently passes for every employee** | A policy requiring `doc:Contract`/`VisaExpiryDate`/`MolId` is a no-op today — fail-open on exactly the statutory items. |
| Policy validator | `CompanyGovernanceController.cs:265-266` — `ValidateAsync` only `JsonDocument.Parse`s; never checks keys against a registry | Unevaluable requirements can be saved | Pairs with the hole above. |
| Precedence engine (reuse) | `CompanyTaxPolicyResolver.cs:25-43` — `IgnoreQueryFilters()` + tenant + `(CompanyId==null || ==companyId)`, `OrderByDescending(CompanyId!=null).ThenByDescending(EffectiveFrom)`, Active + date-covering | The exact composition machinery to reuse | — |
| **Statutory-floor precedent (the right model)** | `StatutoryRateGuard.cs:52-63` `ValidateIncomeTaxRate` blocks a config value that breaches the GCC zero-PIT floor **independently of what the policy resolves to**; `CountryTier.cs:23-25` — floor "kept in code (not seed data) so the tier gate is fail-safe and **cannot be widened by editing a tenant's rows**" | Overlay-guard, not replacement-resolver | This — not the tax **resolver** — is the model for the readiness floor (§3.3). |
| Seeded GCC field literals | `EnterpriseGroupSeeder.cs:39-45` (`KsaFields` = Iqama+GOSI, `UaeFields` = EmiratesId), key `"field"` | **Demo groups only**; not for real tenants | Promote to a code floor + provisioning seed. |
| GCC statutory toggles | `GCCComplianceSetting` (`SetupAdmin.cs:94-111`): `IqamaRequired`, `EmiratesIdRequired`, `WpsEnabled`, `VisaTrackingEnabled`, `IqamaAlertDays`, `VisaAlertDays` | **NOT provisioned by `TenantProvisioningBundle`** — created only on manual admin save (`SetupSettingsController.cs:183`) | A fresh tenant has **no** such row → cannot be the floor source of truth. Also has **no** toggle for QID/CivilId/CPR/GOSI. |
| WPS SIF gate | `WpsSifValidator.cs:90-119` — errors on missing/invalid IBAN + missing `IdNumber` + non-positive salary; **inactive status is a WARNING** (`INACTIVE_EMPLOYEE`); non-Saudi IBAN a warning | Checks only IBAN + IdNumber | Does **not** check Iqama/GOSI/EID/QID/CivilId/CPR/MolId/routing/visa/status. |
| `WpsEligible` dead for export | `EmployeePayrollProfile.cs:16`; set `false` by exit cascade (`EmployeeManagementService.cs:254`), read only by that cascade's own idempotency predicate (`:250`) | **Never read in any payroll-run/SIF export selection query** | The "cannot pay an ex/under-documented employee" guarantee does not exist end-to-end. |
| List UX | `EmployeesPage.tsx:890` — renders `profileCompletenessScore.toFixed(0)%` as plain grey text | No badge, no checklist, no fast-fix | — |
| **Second source of truth (design missed it)** | `EmployeesPage.tsx:148-191` — `COUNTRY_COMPLIANCE_PROFILES` hard-codes per-country `required:true` (SA Iqama, AE Emirates ID, QA QID, KW/OM Civil ID, BH CPR, common Passport) | A client-side "required" list | Shipping a server policy without retiring this = **two divergent definitions of "required"** → violates "no hard-coded field list" on the client. |
| Create-form floor mismatch | `EmployeesPage.tsx:534,1136` require English name **and** gender client-side | Stricter than the server name-only floor and than import | "Save what I have" fights the user. |
| Import feedback ceiling | `ImportExportToolbar.tsx:91,129` — one **5-second auto-dismiss toast, truncated to 2 warnings** | Cannot review the outcome of creating dozens of incomplete records | Under-designed for a deliberately lenient importer. |
| `EmployeeListItemDto` | record at `EmployeesController.cs:2561`; constructed at `EmployeeManagementService.cs:61`, `EmployeesController.cs:97`, `ToListItem:2548` (used by AI endpoints `:2170-2184`). `Id` is `int` | Positional record — compiler catches all sites | Enumerate all 4 when extending. |
| Reusable assets | `Employee.ActivatedAtUtc` exists (`Employee.cs:146`); `ProfileCompletenessScore` (`Employee.cs:141`, decimal); `IbanValidator.IsValid` (`IbanValidator.cs:9`, false on null/empty); `MissingDocumentsAsync` bulk pattern (`EmployeeManagementService.cs:397-406`); `CheckDocumentExpiryAsync` sweep (`:486`); `EstablishmentConflict` extension (`EstablishmentHttp.cs:19`) | — | Build on these; invent nothing parallel. |

**Design thesis (unchanged, now corrected):** do **not** invent a parallel system. Promote `CompanyComplianceProfile.RequiredFieldsJson` from a read-only dashboard into an enforcement-grade, per-employee, jurisdiction-aware **required-to-activate** policy — but resolve it as a **statutory-floor UNION** (§3.3), not the tax-style replacement the base design proposed. Gate every path into `Active` on it, keep creation name-only easy, and store a denormalized badge for the list while computing the itemized checklist live for the gate.

---

## 2. Readiness model — two orthogonal axes

The brief's four words (**draft / incomplete / ready / active**) are **two axes**, not one scale. Stating this explicitly is the single most clarifying decision in the design.

- **Lifecycle Status** (existing vocabulary, the *primary* chip): `Draft → Active → Inactive/Offboarded/Suspended/Terminated/Archived`. Unchanged. `draft` and `active` are Status.
- **Readiness** (the new axis, a *secondary* signal): data-completeness against the resolved policy. `incomplete` and `ready` are Readiness.

The badge must **never** show two words that both look like statuses (Workday shows staffing-status + a To-Do count; BambooHR shows employment-status + a completion ring — never two competing chips).

### 2.1 Internal readiness state (enum, denormalized for the list)
`ReadinessState ∈ { Ready | NeedsAttention | Blocked }`, mapped from a live evaluation:
- `ActivationBlockersCount > 0` ⇒ **Blocked** (has `failClosed` gaps).
- `== 0` and some recommended (`failClosed:false`) missing ⇒ **NeedsAttention**.
- `== 0` and nothing missing ⇒ **Ready**.

Default for a new incomplete record is **Blocked** (fail-safe).

### 2.2 User-facing rendering — progress-framed, 2 gate tones + a real amber (P1)
"Blocked" is system-centric and punitive; **no user-facing surface says "Blocked" about a person.** Keep `Blocked` as the internal enum; render progress-framed. The list badge collapses to **two gate tones plus a semantically distinct amber**:

- **Red** = `Blocked` → **"Incomplete · N"** (N = `ActivationBlockersCount`) — "can't activate yet."
- **Green** = not Blocked and no required doc expiring/expired → **"Ready"**, with a quiet sub-count **"· N optional"** when recommended items are missing (this absorbs `NeedsAttention` — an activatable employee reads as **success**, not amber-alarm).
- **Amber** = reallocated to a **time-based** state the repo already tracks: a **required** statutory doc/ID **expiring or expired** (`CheckDocumentExpiryAsync:486`, `ExpiringDocuments`, `GCCComplianceSetting.VisaAlertDays/IqamaAlertDays`). Amber = "action needed **by a date**"; red = "missing a blocker"; green = "ready." Each colour means exactly one real thing.

**Accessibility (P1):** every tone pairs an **icon + text** (check / alert-triangle / dash), never colour alone (WCAG). Reuse the existing `StatusChip` tone system (`emerald/amber/rose/slate`, `statusTone` at `EmployeesPage.tsx:1302`).

### 2.3 Status × Readiness matrix — define the `Active + Incomplete` cell (P1)
Readiness is orthogonal, so every Status can carry any Readiness. The load-bearing cell:

| | Ready | Incomplete (Blocked) |
|---|---|---|
| **Draft** | "Ready — activate now?" (completion moment, §7) | "Incomplete · N" (onboarding gap) |
| **Active** | normal | **"Compliance gap — N now required"** (drift alert) |

An already-**Active** employee who goes red after a **policy change** is a **compliance-drift alert, not an onboarding gap** — distinct copy, and it must **never auto-deactivate** (the gate fires only on transitions *into* Active, §5). This also feeds the payroll safety rule (§6.4).

---

## 3. The configurable required-to-activate policy

### 3.1 Reuse `CompanyComplianceProfile`; extend only the JSON payload (P0)
No new top-level entity. The policy already exists (`CompanyGovernance.cs:58-81`) with the precedence-ready fields and `RequiredFieldsJson`. Extend only the **item schema** inside the JSON (backward compatible — today's `{"field","failClosed"}` still parses; accept `"field"` as an alias for `"key"` because all seeded data and `GroupProductFoundationTests` use `"field"`):

```json
[
  { "key": "IqamaNumber",   "category": "identity", "failClosed": true,  "appliesWhen": { "nationalityNot": "SA" } },
  { "key": "IdNumber",      "category": "identity", "failClosed": true,  "appliesWhen": { "nationality": "SA" } },
  { "key": "GosiReference", "category": "identity", "failClosed": true },
  { "key": "BankIban",      "category": "payroll",  "failClosed": true,  "appliesWhen": { "wpsEnabled": true }, "gate": "pay" },
  { "key": "IqamaExpiry",   "category": "identity", "failClosed": true,  "gate": "pay" },
  { "key": "doc:Contract",  "category": "document", "failClosed": true,  "requireVerified": false },
  { "key": "DateOfBirth",   "category": "personal", "failClosed": false }
]
```

- `key` — a member of the **Field Registry** (§3.2). `field` is a back-compat alias.
- `category ∈ { identity | payroll | org | contract | document | personal }` — drives UI grouping; defaults `identity`.
- `failClosed` — **true = blocks (hard gate); false = recommended** (counts toward %, amber/optional, never blocks). This is the statutory-vs-recommended axis.
- `appliesWhen` — optional conditionality evaluated against the employee (`nationality`, `nationalityNot`, `paymentMethod`, `wpsEnabled`, …); absent ⇒ always applies. **Conditionality is a compliance requirement, not a nicety** — requiring an Iqama for a Saudi national, or expat work-permit logic for an Emirati, is *wrong* and would produce false blocks/false passes. Seed every national-vs-expat split with `appliesWhen`.
- `gate` — `"activate"` (default) or `"pay"`. `"pay"` items don't block activation but block payroll inclusion (§6). Lets one policy express both gates (e.g. IBAN blocks pay, not activate).
- `requireVerified` (document items only) — when true, a present document must also be `ApprovalStatus == Verified`.

**Validator hardening (P0):** extend `ValidateAsync` (`CompanyGovernanceController.cs:257-266`) to (a) **reject unknown `key`s** not in the registry, (b) reject unknown `category`/`gate`, (c) cap array length, (d) still `400` on malformed JSON (unchanged contract). This closes the write side of the fail-open hole.

### 3.2 Field Registry — the resolvable catalog (code, not scattered `switch`es) (P0)
Replace the 8-way `GetField` (`CompanyGovernanceController.cs:351-362`, incl. the `_ => "n/a"` fail-open) with `Infrastructure/Employees/EmployeeFieldRegistry.cs`: an immutable dictionary `key → { Label, Category, Getter(EmployeeReadinessSnapshot), FixHint, DefaultGate }`. The registry is **employment-lifecycle vocabulary, not tenant business data**, so it lives in code — **fail-safe**: a tenant cannot invent a field the evaluator can't read. It spans four sources:

- **Employee scalars:** `EnglishName, DateOfBirth, Nationality, Gender, WorkEmail, Phone, DepartmentId, DesignationId, JoiningDate, ContractType, EmploymentType, PassportNumber, PassportExpiryDate, IqamaNumber, IqamaExpiry, GosiReference, EmiratesId, EmiratesIdExpiry, Qid, QidExpiry, CivilId, CivilIdExpiry, IdNumber, VisaNumber, VisaExpiryDate, WorkPermitNumber, MuqeemNumber, QiwaContractNumber, LaborCardNumber` …
- **Payroll profile** (`EmployeePayrollProfile`): `BankIban` (validated **live** via `IbanValidator.IsValid` — a *present-but-invalid* IBAN is a blocker, reusing the check at `EmployeesController.cs:782` and `UpsertPayrollProfile:657`), `SalaryStructure` present, `MolId`, `BankRoutingCode`, `PaymentMethod`.
- **Documents** (`EmployeeDocuments`): `doc:{Type}` keys. "Present" = a non-deleted document of that type exists (and `Verified` when the item sets `requireVerified`). Reuses `GetDocumentsAsync` / `MissingDocumentsAsync` (`:397-406`). **Do NOT reuse the flat `RequiredDocumentTypes` list** (`EmployeeManagementService.cs:19-24` — 10 types for everyone, non-jurisdictional): drive the *required* doc set from the policy `doc:*` items with `appliesWhen`, so a UAE employee is not blocked on an Iqama doc and vice-versa. `RequiredDocumentTypes` stays only as the source for the existing missing-docs *report*.
- **Org linkage:** `DepartmentId`/`DesignationId` present (guards against string-only "Unclassified" rows).
- **Expiry getters (P1):** the `*Expiry` getters return two signals — *missing* and *expired-at-date*. **Missing statutory ID/permit → blocks activation; present-but-expired → blocks payment** at minimum (an expired Iqama/visa/EID/QID/CivilId/CPR at pay date is legally "undocumented"). Pre-expiry within `VisaAlertDays`/`IqamaAlertDays` → amber (§2.2), not a block.

An unknown `key` returns **"not evaluable"**; because the validator (§3.1) now rejects unknown keys on write, an unevaluable required key can never be saved — and if one ever reaches the evaluator, it fails **CLOSED** (block), never open. This reverses the current `"n/a" ⇒ present` behaviour.

### 3.3 Effective-policy resolver — a statutory-floor UNION, NOT replacement (P0, the headline correction)
> **This is where the base design was wrong and must change.** The base design said the resolver is "structurally identical to `CompanyTaxPolicyResolver`" — i.e. company row **replaces** tenant default **replaces** fallback, first-match-wins (`CompanyTaxPolicyResolver.cs:34-42`). Under **replacement**, a client who authors a company profile that simply omits Iqama/GOSI/IBAN gets a **thinner** effective policy — the gate becomes **less strict than the statutory minimum**, letting a KSA entity activate an Iqama-less, GOSI-less expat. That violates the owner's "stricter, never weaker" rule and the law.

The correct precedent is already in the repo, and it is **not** the tax resolver — it is `StatutoryRateGuard` (`StatutoryRateGuard.cs:52-63`), which enforces the GCC zero-PIT floor **independently** of whatever `CompanyTaxPolicy` resolves to, with `CountryTier.cs:23-25` stating the floor is "kept in code … so the tier gate is fail-safe and cannot be widened by editing a tenant's rows."

New `Infrastructure/Employees/EmployeeReadinessPolicyResolver.cs` (`IEmployeeReadinessPolicyResolver`) computes a **UNION**, strictest-wins:

```
EffectiveRequirements(employee) =
      StatutoryReadinessFloor(CountryTier/CountryCode, nationality)   // §3.4 — CODE, non-editable, always present
    ∪ TenantDefaultProfile.Items   (CompanyId == null)               // config: may ADD or upgrade
    ∪ CompanyProfile.Items         (CompanyId == employee.CompanyId) // config: may ADD or upgrade
```

**Merge rule per `key`: strictest wins.** Config may (a) add a new required key, or (b) upgrade a floor item `failClosed:false → true`. Config may **never** remove a floor key nor downgrade `true → false`. A saved profile that contradicts the floor: the **floor value is used** and the contradiction is surfaced (`employee.readiness.floor_contradiction` audit event, keys only), never silently honoured.

- **No acknowledgement escape hatch** for the hard identity / work-authorization / social-insurance / authenticated-contract floor — unlike the tax side's `AcknowledgeNonZeroGccTax`, the owner rule is "never less, full stop." Any ack path is reserved for genuinely-optional-in-law edge items, logged and audited.
- The **config layers** (tenant/company profiles) are still resolved with the exact `CompanyTaxPolicyResolver` composition (`IgnoreQueryFilters()` + tenant + `(CompanyId==null || ==companyId)`, `OrderByDescending(CompanyId!=null).ThenByDescending(EffectiveFrom)`, Active + date-covering). `IgnoreQueryFilters` is **load-bearing**: the gate and the background sweep run as system reads with no user scope (like `CheckDocumentExpiryAsync`'s `new RequestContext(null,"expiry-check",null,tenantId)`) and must see the `CompanyId==null` default row.
- **`GCCComplianceSetting` is NOT the floor source** (P0 correction): it is not provisioned per tenant (`TenantProvisioningBundle` inserts none; only manual `SetupSettingsController.cs:183`) and has no toggle for QID/CivilId/CPR/GOSI, so it cannot express the Tier-2 floors. Where a row **does** exist, treat it as an **additive config layer** (`IqamaRequired ⇒ IqamaNumber failClosed`, `WpsEnabled ⇒ BankIban failClosed@pay`, `VisaTrackingEnabled ⇒ VisaExpiryDate recommended`), never as the guarantee. The guarantee is always the **code floor**.

Returns `ResolvedReadinessPolicy { CountryCode, Nationality, Tier, Items[] (post-union), Sources[] (floor|tenant|company|gcc-setting), Disclaimer }`.

### 3.4 `StatutoryReadinessFloor` — per-jurisdiction code defaults (the SME matrix) (P0)
A code table `Infrastructure/Employees/GccReadinessFloor.cs`, keyed by ISO-2 + `CountryTier` + nationality, promoting the `EnterpriseGroupSeeder.cs:39-45` literals into an always-present floor. **Activate** = "legal to employ in a live seat"; **Pay** = "legal to run through payroll/WPS" (Pay ⊇ Activate). All items `failClosed:true` unless marked *recommended*.

**Universal floor (all six GCC states):**
| Item | Activate | Pay | Note |
|---|---|---|---|
| Legal person name | **CREATE floor** | — | The only bar to *create* (`EmployeesController.cs:449`, `EmployeeManagementService.cs:86`). Never gate anything else on create. |
| Nationality | **Required** | Required | Drives every `appliesWhen`. |
| Employment contract (authenticated), `doc:Contract` | **Required** | Required | Registered/authenticated contract, not a blank type string. |
| Joining/start date | **Required** | Required | Basis for probation, EOSB, first pay period. |
| Basic salary / wage | *recommended* | **Required** | No payslip/WPS record without it. |
| Bank IBAN (ISO-13616 mod-97 valid) | *recommended* | **Required** | The WPS bar; must pass `IbanValidator.IsValid`, not merely be present. `failClosed@pay`; upgrade to `failClosed@activate` when `wpsEnabled`. |
| DOB, work email, department, designation, phone | *recommended* | *recommended* | Amber/optional, never block. |

**Per-state statutory identity + authorization + social-insurance (each `failClosed`, gated by `appliesWhen`):**

| State (Tier) | Nationals | Expats | Social insurance | Contract doc | IBAN |
|---|---|---|---|---|---|
| **KSA `SA`** (T1 certified) | National ID (Hawiya) — `nationality=SA` | **Iqama + valid expiry** — `nationalityNot=SA` | **GOSI reference** (all) | Qiwa authenticated contract | Saudi IBAN @pay |
| **UAE `AE`** (T1 certified) | GPSSA pension — `nationality=AE` | **Emirates ID + expiry** (all residents); **Work permit/Labour card (MOHRE)** + **residence visa + expiry** — `nationalityNot=AE` | GPSSA (nationals) | authenticated contract | MOL Personal ID (`MolId`) @pay, Bank routing code @pay, UAE IBAN @pay |
| **Qatar `QA`** (T2 fail-loud) | — | **QID + expiry**; work/residence permit — `nationalityNot=QA` | — | ADLSA authenticated contract | Qatari IBAN @pay |
| **Kuwait `KW`** (T2) | PIFSS — `nationality=KW` | **Civil ID (Bitaqa) + expiry**; work permit (PAM Art-18) — `nationalityNot=KW` | PIFSS (nationals) | authenticated contract | Kuwaiti IBAN @pay |
| **Oman `OM`** (T2) | PASI — `nationality=OM` | **Civil ID/Resident Card + expiry**; labour clearance/work permit — `nationalityNot=OM` | PASI (nationals) | authenticated contract | Omani IBAN @pay |
| **Bahrain `BH`** (T2) | — | **CPR + expiry**; LMRA work permit — `nationalityNot=BH` | SIO/GOSI-BH social-insurance ref (all) | authenticated contract | Bahraini IBAN @pay |

**Rule of thumb encoded as data:** identity document + work authorization + social-insurance registration + authenticated contract = `failClosed:true`. Everything supporting those or purely HR/analytics (DOB, passport copy, bank letter, emergency contact, photo, education) = `failClosed:false` — **except** where a profession/visa class legally requires an attested degree, which a client raises to blocking via config (the stricter, allowed direction). **Tier-2 packs (QA/KW/OM/BH) must be legally validated before their gate is treated as authoritative** (`CountryTier` fail-loud boundary).

### 3.5 Seeding & provisioning (P1)
- **`TenantProvisioningBundle`** inserts one **tenant-default `CompanyComplianceProfile`** (`CompanyId == null`) per provisioned country as an editable *starting point* mirroring the code floor (insert-if-absent, same idempotency pattern the bundle already uses). Admins edit it in Setup; the seed is never forced (opt-out by editing). **The code floor (§3.4) remains the guarantee** regardless of whether this seed ran — so a fresh/mis-provisioned tenant still gates correctly.
- Keep the code floor **conservative** to bound the test blast radius (§11): tests using the provisioning bundle inherit a known policy; bare-GCC fixtures must be updated to carry the required identity fields (see §11).

---

## 4. Completeness + evaluator — compute once, two consumers

### 4.1 One evaluator; a pure primitive + a throwing wrapper (P0)
New `Infrastructure/Employees/EmployeeReadinessEvaluator.cs`. Two members (the base design's single "returns AND throws" member is impossible and blocks the batch import path):

```csharp
// Pure, never throws — the primitive. Used by import, list recompute, detail panel, gate.
EmployeeReadiness Evaluate(EmployeeReadinessSnapshot emp, ResolvedReadinessPolicy policy);

// Throws EmployeeActivationBlockedException(readiness) when readiness.Blocking.Any(); else returns it.
Task<EmployeeReadiness> EnsureActivatableAsync(EmployeeReadinessSnapshot emp, ResolvedReadinessPolicy policy, RequestContext ctx, CancellationToken ct);
```

`EmployeeReadinessSnapshot` = employee scalars + payroll profile + the set of present `(type, verified, expiry)` documents, **materialized at the call-site** (never DB-loaded-by-id inside the guard — see §5.4). A batch loader `LoadSnapshotsAsync(tenantId, employeeIds)` extends `LoadEmployeeStatutoryFieldsAsync` (`CompanyGovernanceController.cs:313`) — but that method today filters `Status=="Active"` and returns only 8 scalars; **parameterize off the Active filter** (Draft employees are the whole point) and **widen the projection** (payroll IBAN/PaymentMethod/MolId + a per-type document set + expiry dates). Follow the `MissingDocumentsAsync:397-406` shape: all non-terminal employees + all their documents + all payroll profiles in **3 bulk queries, diff in memory, one `SaveChanges`**.

```
EmployeeReadiness {
  State            // Ready | NeedsAttention | Blocked
  Score            // 0–100, policy-weighted
  Blocking[]       // failClosed activate-gate items still missing → itemized {key,label,category,jurisdiction,reason,fixHint}
  PayBlocking[]    // gate:"pay" items still missing/expired
  Recommended[]    // failClosed:false items still missing
  Present[]        // satisfied items (for "12 of 15 complete")
  ExpiringSoon[]   // required IDs/docs within alert window (amber)
}
```

### 4.2 `ProfileCompletenessScore` becomes policy-aware; keep the column & the % (P0)
`CalculateCompleteness` (`EmployeeManagementService.cs:931`) changes from `static`/pure/0-query to delegating to the evaluator: `Score = round(100 * present / (blockingRequired + recommendedRequired))`, with blocking items weighted so **any missing blocker caps the score below the green threshold** (e.g. ≤ 60) — the % and the badge never contradict. This is a **correctness fix, not cosmetic**: today the 22-field denominator contains **zero** statutory fields, so an employee reads 100% while missing Iqama/GOSI/IBAN (the 41/55/82% seen in screenshots are HR/org completeness, not readiness).

Signature ripple (P0, from Eng review): the method goes instance/async/db-backed. **Callers resolve policy + snapshot and pass them in** — keeping the method testable — at `:105`, `:146`, and the draft variant at `EmployeesController.cs:2385` (recompute at the *employee* level in `ApproveDraft`, replacing the stored-score copy at `:1536`, not just editing the service method). The column stays `decimal` (no column-type migration; snapshot already migrated at `ModelSnapshot :7378`).

### 4.3 Denormalized snapshot for the list; live recompute at the gate (P0)
Add to `Employee` (`Employee.cs`): `ReadinessState (string, ≤20, default "Blocked")`, `ActivationBlockersCount (int)`, `ReadinessEvaluatedAtUtc (DateTime?)`. **Display-only.** The gate **always recomputes live** and never trusts the stored snapshot (server-side, fail-closed) — the standard Workday/SuccessFactors split: cached flag for lists, authoritative validation at the transition.

**Freshness — the "already calls `CalculateCompleteness`" premise is false (P0 correction):** only `CreateAsync:105` and `UpdateAsync:146` call it today. `ChangeStatusAsync`, `UploadDocumentAsync` (`:284`), `VerifyDocumentAsync`, and the **entire import path** do **not** (import writes score `0`). Each needs a **new** recompute-and-stamp call. Per-write recompute now costs a few queries (was 0) — acceptable for single-entity writes; the policy-edit fan-out and nightly sweep **must** use the batch loader, never per-employee calls.

**Policy-change fan-out (P1):** on `CompanyComplianceProfilesController` Create/Update/Delete, enqueue a bounded `RecomputeReadinessAsync(tenantId, companyId)` over that company's non-terminal employees (same shape as `CheckDocumentExpiryAsync:486`), plus a nightly tenant sweep. Eventual consistency for the badge; strict consistency at the gate (assert by mutating the stored snapshot and confirming the gate still blocks).

---

## 5. Hard activation gate — enforcement points + structured error

### 5.1 One shared enforcement primitive (P0)
`IEmployeeActivationGuard` (`Infrastructure/Employees/EmployeeActivationGuard.cs`), modeled on `IEstablishmentGuard`. It composes the resolver (§3.3) + evaluator (§4.1). Import calls **`Evaluate`** directly (pure, per-row, never aborts the file); every other path calls **`EnsureActivatableAsync`** (throws on blockers). On success it stamps `Employee.ActivatedAtUtc` (`Employee.cs:146`) and refreshes the readiness snapshot.

### 5.2 The gate condition — corrected to protect mandated reinstatement (P0)
> The base design's condition `request.Status=="Active" && oldStatus!="Active"` **breaks legally-mandated reinstatement** and an existing test proves it: `EstablishmentEnforcementPathTests.SuspendedReinstatement_IsOccupyingToOccupying_NeverBlocked_EvenOverBudget` (`:425`) does `ChangeStatusAsync(Suspended → Active, "Investigation closed")` and asserts success with the comment *"KSA labor law: the system must never refuse to record a mandated reinstatement."* The naive condition would 422 it.

**Gate condition:** fire **only** on
```
request.Status == "Active" && !EstablishmentOccupancy.IsOccupyingStatus(oldStatus)
```
`IsOccupyingStatus` = Active/Offboarded/Suspended (`EstablishmentOccupancy.cs:34`, corroborated at `:47`). So the gate fires only on **first-time activations** from Draft/Invited/Inactive/Terminated/Exited/Archived, and **never** on Suspended→Active or Offboarded→Active reinstatements (deliberately narrower than occupancy). You can suspend/offboard an incomplete record; you cannot make it Active. This condition also auto-exempts the Offboarding.Cancel reinstatement (§5.3) on the same principle.

### 5.3 Enforcement points — all FOUR paths (P0)
There is **no single chokepoint today**: three paths set Active without going through `ChangeStatusAsync`. Prefer one shared `SetActiveAsync` primitive that calls the guard; otherwise accept the call-sites **and add a guard-parity test** (mirroring the establishment occupancy-parity test) asserting every `Status="Active"` employee write invokes the guard.

| Path | File:line | Change |
|---|---|---|
| `ChangeStatusAsync` | `EmployeeManagementService.cs:174`, before the occupancy branch at `:197` | If gate condition (§5.2) → `await EnsureActivatableAsync(...)`. Establishment guard remains after. |
| `ActivateAsync` | `:370` | Delegates to `ChangeStatusAsync` → inherits the gate. |
| `ApproveDraft` | `EmployeesController.cs:1449`; Active at `:1501`; establishment guard `:1562` | Build the `Employee`, then `EnsureActivatableAsync` **before** `EnforceAndExecuteAsync`. Blocked ⇒ leave the draft untouched, return **422**. Draft can still be *saved*; it just can't be *approved-to-Active* while blocked. |
| `Import` | `EmployeesController.cs:292`, blank→Active at `:643` (and the row-status resolution around `:582`) | §7 — call `Evaluate` inline to decide Active-vs-Draft **per row**; blocked rows become Draft + warning. **Never** 422 the whole file. Note there are two blank→Active sites to fix. |
| **`OffboardingController.Cancel`** | `OffboardingController.cs:215` — direct `emp.Status = "Active"` | Route through `SetActiveAsync`/`ChangeStatusAsync` (auto-exempt by §5.2 since old status Offboarded is occupying) **or** add the explicit guard call. Do not leave a raw write. |
| `ChangeStatus` / `Activate` controllers | `EmployeesController.cs:1704 / :1782` | Add `catch (EmployeeActivationBlockedException ex) { return this.NotActivatable(ex); }` **before** the existing `InvalidOperationException` catch (§5.6). |
| **Bulk activate** (new) | — | Calls the guard per employee; returns `{ activated[], blocked[{id, blocking[]}] }`. |

### 5.4 Snapshot must be built at the call-site (P0)
`EnsureActivatableAsync` takes a **materialized `EmployeeReadinessSnapshot`**, never "DB-load by id" — because at `ApproveDraft` the `Employee` is in-memory with `Id == 0` (not inserted until inside the establishment transaction `:1565`), its documents are keyed by **`DraftId`** not `EmployeeId` (`:1568`), and **no `EmployeePayrollProfile` row exists** (IBAN lives on `employee.BankIban`/`:1512`). The call-site assembles the snapshot from the in-memory Employee + explicitly-supplied document set + IBAN string.

### 5.5 Structured error contract — 422 (P0)
`EmployeeActivationBlockedException → 422 Unprocessable Entity` via a new `this.NotActivatable(ex)` extension in `EstablishmentHttp.cs` (same shape as `EstablishmentConflict:19`). 422 (not the establishment 409) because this is a precondition/validation failure, consistent with the existing `InvalidOperationException → UnprocessableEntity` at `:1474/:1701`.

```json
{
  "error": "employee_not_activatable",
  "employeeId": 123,
  "message": "Cannot activate Jane Doe — 3 required details are missing.",
  "policy": { "countryCode": "SA", "tier": "certified", "sources": ["floor","company"] },
  "progress": { "present": 12, "requiredTotal": 15 },
  "blocking": [
    { "key": "IqamaNumber",  "label": "Iqama number", "category": "identity", "reason": "statutory", "jurisdiction": "SA", "fix": { "kind": "field", "target": "iqamaNumber" } },
    { "key": "BankIban",     "label": "IBAN (WPS)",   "category": "payroll",  "reason": "statutory", "fix": { "kind": "field", "target": "payrollProfile.iban" } },
    { "key": "doc:Contract", "label": "Employment contract", "category": "document", "reason": "statutory", "fix": { "kind": "document", "documentType": "Contract" } }
  ],
  "recommended": [ { "key": "DateOfBirth", "label": "Date of birth", "category": "personal" } ],
  "disclaimer": "Configurable readiness policy — not legal certification. Country rule packs require legal validation."
}
```
Reuse the exact disclaimer string at `CompanyGovernanceController.cs:200`. `employeeId` is `int` (matches `Employee.Id` / `EmployeeListItemDto`).

### 5.6 Exception typing & catch ordering (P0)
`EmployeeActivationBlockedException` must be a **standalone exception**, **NOT** a subclass of `InvalidOperationException` — the controllers catch `InvalidOperationException → 400` at `:1701/:1714/:1792`, which would swallow the structured body into a generic 400. Its `catch` block must be placed **before** the `InvalidOperationException` catch at each site.

---

## 6. Pay-side interlock — WPS / GOSI (P0)
**One evaluator, three consumers: activation, payroll-run/SIF selection, GOSI/social-insurance file.** Today the pay guarantee does not exist end-to-end: `WpsSifValidator.cs:90-119` checks only IBAN + `IdNumber` and treats inactive status as a **warning** (`INACTIVE_EMPLOYEE`); `WpsEligible` is written by the exit cascade (`EmployeeManagementService.cs:254`) but **never read by any export/selection query** (`:250` is the cascade's own idempotency predicate).

1. **Activation gate feeds pay-eligibility.** Payroll-run inclusion and SIF export selection **hard-exclude** (error, not warning) any employee whose live readiness has `PayBlocking` items or `State==Blocked` — an actual `WHERE` predicate in the run/SIF selection, not a decorative flag.
2. **Promote `WpsSifValidator`'s status check from warning → error** and make it read readiness, so Draft/Suspended/Blocked cannot export (closes `:116`).
3. **Make `WpsEligible` load-bearing OR replace it with the readiness read** — do not add a second decorative flag. The exclusion must be a real predicate that also honours the exit-cascade `WpsEligible=false`.
4. **GOSI / social-insurance interlock:** GOSI (and PASI/PIFSS/SIO/GPSSA) reference is `failClosed@activate` **and** the monthly contribution file excludes anyone without it. No GOSI ref → not Active → not in payroll → not in the GOSI file. One evaluator, three consumers.
5. **Expiry = undocumented for pay:** a present-but-**expired** Iqama/visa/EID/QID/CivilId/CPR at pay date is a **pay blocker** (activation may amber-warn; payment must block), via the §3.2 expiry getters.
6. **Payroll-run safety for already-paid actives (P0, from UX + duty-of-care):** an already-**Active**, already-paid employee turned `Blocked` by a **policy change** must **warn + require explicit acknowledgement**, never be **silently** dropped from a run (silent exclusion can miss a mandated salary). Never-activated drafts hard-exclude; policy-drifted actives warn-and-acknowledge.

**Net:** Activate gate = jurisdiction-aware statutory completeness; Pay gate = Activate ∪ valid IBAN ∪ SIF identifiers (MolId/routing where required) ∪ non-expired authorization ∪ Active status. Both read one evaluator; neither trusts a stored flag alone.

---

## 7. Import leniency — formalized (P0)
**The only bar to CREATE is minimal identity; everything else missing = created (not skipped) with recorded gaps.**

1. **Creation floor (code, fail-safe, not tenant-editable):** creatable iff non-blank `FullName` + a unique `EmployeeCode` (generated when blank) — already `EmployeesController.cs:449` / `EmployeeManagementService.cs:86`. Formalize as one helper `EmployeeIdentityFloor.IsCreatable(name)` shared by import, form, and drafts. **No statutory/payroll/org field is ever part of the create floor.**
2. **Import never creates Active by accident.** Change status resolution at `EmployeesController.cs:643` (and the row-status site ~`:582`) and `ApproveDraft:1501` from "blank ⇒ Active" to:
   - Compute readiness (`Evaluate`) for the just-built `Employee` against the resolved policy.
   - CSV explicitly `Active` **and** zero blocking gaps → keep `Active`.
   - CSV `Active` but blocking gaps → **downgrade to `Draft`** + `warnings[]`: `Row {n}: {name} imported as Draft — cannot be Active until: Iqama number, IBAN (WPS). Fix in the People list.`
   - CSV blank/omits status → **`Draft`** (never Active).
   - A non-Active, non-terminal status (e.g. `Suspended`) is honoured as-is (no gate needed).
3. **Optional-reference leniency stays** (branch/cost-centre unresolved → warning + import without it; `:487/:502`). The readiness layer never re-introduces row rejection for non-identity data.
4. **Structured response:** add `createdIncomplete: [{ employeeId, employeeCode, name, blockingCount }]` and an `importBatchId` to the import response.

### 7.1 Bulk-import review UX — dry-run + persistent results (P0, replaces the toast)
The 5-second truncated toast (`ImportExportToolbar.tsx:91,129`) is **not** the review surface for an importer that intentionally creates incomplete records. Extend `ImportExportToolbar` into a stepper/results table:
- **Pre-commit dry-run/preview (P0):** upload → server builds the `Employee` rows and returns each row's **projected landing state (Active vs Draft) and why**, **without persisting** → then Confirm. (The server already builds the rows to evaluate readiness.)
- **Persistent results view (P0):** after commit, a view that **stays until dismissed**, separating three tiers — **hard errors** (rows rejected — now only nameless rows) / **warnings** (imported minus a reference) / **created-incomplete** (imported as Draft, needs activation) — with a **downloadable per-row report** (row #, name, outcome, gaps) and a one-click **"View the N incomplete employees"** deep-link to the filtered worklist (§8).
- **Leniency toggle (P2):** pre-import "Create as Draft for review" (default) vs "Activate rows that are already complete," shown with preview counts.
- **Batch provenance (P2):** tag created rows with `ImportBatchId` for audit, worklist filtering, and undo (mitigates the mass-creation concern in the seeder-loop history).

---

## 8. List UX — badge, checklist, fast-fix

### 8.1 DTO + projection (P0)
Extend `EmployeeListItemDto` (record `EmployeesController.cs:2561`) and **all four** construction sites (`EmployeeManagementService.cs:61`, `EmployeesController.cs:97`, `ToListItem:2548`, and the AI-endpoint uses `:2170-2184`) with `readinessState`, `activationBlockersCount` (both denormalized — zero extra queries). Frontend `EmployeeListItem` (`employees.ts:27`) gains the same two fields.

### 8.2 The Profile column becomes a `ReadinessBadge` (P0/P1)
Replace the plain `{score}%` at `EmployeesPage.tsx:890` with the badge per §2.2 (red "Incomplete · N" / green "Ready · N optional" / amber expiry), icon+text, reusing `statusTone` (`:1302`). Add a `readiness` filter param (parallel to the existing `status` filter) and a "Needs info" filter option.

### 8.3 Click-through checklist — progress-first (P1)
New `GET /api/employees/{id}/readiness` → live `EmployeeReadiness`. Detail-drawer **"Activation checklist"** card:
- **Lead with progress** — "**12 of 15 complete — 3 to go**" (from `Present[]`), *then* the list. Deficit-first framing is what makes gates punitive.
- Items grouped by `category`; **blockers before recommended**; within blockers a sensible onboarding order (identity → contract → bank); each item shows the **"why"** (`reason`/`jurisdiction`, e.g. "required by KSA policy").
- Recommended items render with a **distinct icon/tone** from blockers — never as failures.
- The **badge itself is the click target** into the checklist (+ optional hover peek for a 3-item preview).
- The **Activate** button (`EmployeesPage.tsx:1041`) is disabled with tooltip "N required details missing" when `activationBlockersCount>0`; on a 422 it renders the returned `blocking[]` inline **using the same checklist component** (single source of truth = server), never a red error banner.

### 8.4 Fast-fix — just-the-gaps, not scroll-to-field (P0)
Each checklist item carries a `fix` hint. The default fast-fix is a **"Complete required details" mini-form that renders ONLY the missing required fields** (policy-sourced) — the SuccessFactors "Manage Pending Hires" / Workday onboarding pattern. Deep-linking into the existing ~40-field edit modal and scrolling is the **weakest** tier and is where "effortful" tips into "punitive."
- `kind:"field"` → prefill the gap-form field (`target`, e.g. `payrollProfile.iban`); inline-save re-evaluates and updates the badge without leaving the row.
- `kind:"document"` → opens the existing document-upload control (`EmployeesController.cs:1717` POST `/{id}/documents`; drawer control at `EmployeesPage.tsx:989-1001`) pre-set to that `documentType`.
- **Completion moment (P2):** when the last blocker clears, show "All set — activate now?" with the Activate action right there.

### 8.5 One source of truth for "required" (P0)
The frontend **already hard-codes** per-country `required:true` in `COUNTRY_COMPLIANCE_PROFILES` (`EmployeesPage.tsx:148-191`). **Drive the client's required-field rendering from the resolved server policy**; demote that list to **display-labels only** (labels/entity-key mapping), never the "required" source — otherwise "no hard-coded field list" is violated on the client and the two definitions drift.

### 8.6 Create-form floor alignment (P1)
Align the create form to the **name-only** server floor: demote the client-side gender requirement (`EmployeesPage.tsx:534,1136`) to a **readiness** item so "save what I have" never fights the user.

### 8.7 Worklist for scale (P1) + self-service (P2)
- A **"Needs info" worklist** (the `readiness=Blocked` filter) with **inline bulk-fill** (paste N IBANs down a column) and **multi-select bulk-activate** returning per-row blocked results (§5.3). This is what makes the lenient-then-complete loop scale.
- **ESS self-service (P2):** route employee-owned gaps (bank details, personal documents) to the employee via the existing ESS surface with the same `fix` checklist.

---

## 9. Permissions & audit

- **Reads** (`GET /{id}/readiness`, list badges) — same roles as employee read (`Admin, HR Manager, HR Officer, Payroll Officer, Manager, Auditor`); scope-checked via `GetEntityScope`/`_scopeService` as at `EmployeesController.cs:1280`.
- **Policy edits — scope-guard hardening (P0):** `CompanyComplianceProfilesController.Create` (`:206`) and `Update` (`:234`) **lack** the `GetEntityScope().CanAccessCompany(...)` guard the tax controller has (`CompanyTaxPoliciesController.Create :49`). Today it's a read-only dashboard so it's cosmetic; **once this policy gates activation, a company-scoped Compliance Officer could rewrite another company's activation rules — a privilege-escalation vector.** Add the identical guard on both verbs (the `readiness` GET already has it at `:182`). Tenant-default rows (`CompanyId==null`) remain Admin-only.
- **Activation** — unchanged roles (`Admin, HR Manager`), now additionally gated by readiness.
- **Audit (via existing `IAuditService`, no PII values — keys only):** `employee.activation_blocked` (employeeId, policy sources, blocking keys), `employee.readiness_recomputed`, `employee.readiness.floor_contradiction` (keys + source), `compliance_profile.updated`. The gate audits the block **after** any persist (matching the establishment pattern at `EmployeesController.cs:739`). Add `createdIncomplete` counts to the import audit payload.

---

## 10. EF / schema / migration plan (P0)
Minimal — the policy entity, table, controller, and calculator already exist and migrate.

1. **`Employee`** (`Employee.cs`) + `ZayraDbContext` `Entity<Employee>` config: add `ReadinessState (string, maxLen 20, default "Blocked")`, `ActivationBlockersCount (int)`, `ReadinessEvaluatedAtUtc (DateTime?)`. **New migration `AddEmployeeReadinessSnapshot`** (e.g. `20260731_AddEmployeeReadinessSnapshot`) — **shipped in the same PR** (the memory's migration-discipline rule: entity props without a migration → 42703 500s on every tenant). Add index `(TenantId, ReadinessState)` for the "Needs info" filter. **No change to `ProfileCompletenessScore`** (reused; decimal). These 3 columns are absent from `ModelSnapshot` today — one migration genuinely required. `ActivatedAtUtc`, `CompanyComplianceProfile`, `GCCComplianceSetting` are already migrated.
2. **No entity change to `CompanyComplianceProfile`** — only the JSON *schema* in `RequiredFieldsJson` is enriched (data; optional `"schema":2` marker, v1 items still parse).
3. **No change to `GCCComplianceSetting`** (already `CompanyId`-scoped).
4. **Seed rider:** `TenantProvisioningBundle` inserts the tenant-default `CompanyComplianceProfile` (insert-if-absent) — data, no schema.

---

## 11. Test plan (SME test cases)

**Policy resolver — UNION floor** (`EmployeeReadinessPolicyResolverTests`):
- Floor ∪ tenant ∪ company; **strictest-wins** merge per key.
- Config **adds** a key → present in effective policy; config **upgrades** `false→true` → honoured.
- Config **omits** a floor key → floor key **still enforced** (the core "never weaker" case — a KSA company profile without Iqama still blocks an Iqama-less expat).
- Config **downgrades** `true→false` → floor `true` wins + `floor_contradiction` audited.
- `appliesWhen`: Iqama required for non-Saudi, **not** for Saudi; GPSSA for Emirati only; IBAN `@pay` only when `wpsEnabled`.
- Falls back to code floor with **no** `GCCComplianceSetting` and **no** profile rows (fresh tenant) — never returns an empty/no-op policy for a GCC country.
- Tenant/company layer precedence parity with `CompanyTaxPolicyResolver` (newest `EffectiveFrom`; Draft/Archived excluded; `IgnoreQueryFilters` sees `CompanyId==null`).

**Field registry / evaluator** (`EmployeeReadinessEvaluatorTests`):
- Each `key` reads the right source; `doc:Contract` needs a present (and, when `requireVerified`, Verified) document.
- IBAN present-but-invalid = blocker (reuse `IbanValidator` fixtures); IBAN absent + `wpsEnabled` = activate blocker.
- Expiry getters: missing ID → activate block; present-but-expired → pay block; within alert window → amber only.
- **Unknown key rejected at write** (validator) and, if it ever reaches the evaluator, **fails closed** — assert the old `"n/a" ⇒ present` behaviour (`CompanyGovernanceController.cs:361`) is gone.

**Completeness math:** score is policy-weighted; any missing blocker caps score below green; empty policy ⇒ recommended-only reflected.

**Gate** (`EmployeeActivationGuardTests`):
- Blocked employee → 422 with itemized `blocking[]` at **every** path: `ChangeStatusAsync→Active`, `ActivateAsync`, `ApproveDraft`, `ChangeStatus`/`Activate` controllers, import (Active→Draft downgrade), and `OffboardingController.Cancel`.
- Ready employee activates and stamps `ActivatedAtUtc`.
- **Reinstatement not gated:** Suspended→Active and Offboarded→Active succeed (protects `EstablishmentEnforcementPathTests:425`); Draft/Suspended/Offboarded *transitions* are not gated; re-activating an already-Active employee is a no-op gate.
- **Guard-parity test:** assert every `Status="Active"` employee write invokes the guard (greps/asserts the 4 sites).
- `EmployeeActivationBlockedException` is caught **before** `InvalidOperationException` (structured 422, not generic 400).

**Import** (`EmployeeImportReadinessTests`): blank status ⇒ Draft; explicit Active + complete ⇒ Active; explicit Active + missing blocker ⇒ Draft + warning (row still created); identity-only row (name only) ⇒ created as Draft, never skipped; establishment/dup/FK behaviours unchanged; `createdIncomplete[]`/`importBatchId` populated; **dry-run returns projected states without persisting.**

**Pay interlock** (`WpsReadinessGateTests`): Blocked/Draft/Suspended employee excluded from SIF selection (error, not warning); `INACTIVE_EMPLOYEE` is now an error; missing GOSI excluded from GOSI file; expired Iqama/visa blocks pay; **policy-drifted already-Active employee triggers warn+acknowledge, not silent drop.**

**Denormalization & sweep:** create/update/doc-upload/import/status-change refresh `ReadinessState`; policy edit triggers company recompute; nightly sweep re-stamps; **gate ignores a stale snapshot** (mutate stored snapshot → gate still blocks live).

**Scope/permissions** (`GovernanceHardeningTests` extension): scoped Compliance Officer **cannot** Create/Update another company's profile (new guard on `:206/:234`); scoped HR cannot read out-of-scope readiness; audit rows written on block (keys only, no PII).

**Frontend (RTL):** badge renders red/amber/green by `readinessState` + expiry; icon+text present (WCAG); Activate disabled with tooltip when `activationBlockersCount>0`; 422 renders the checklist component; fast-fix gap-form shows only missing fields and deep-links the right field/document type; client "required" is driven by the policy endpoint, not `COUNTRY_COMPLIANCE_PROFILES`.

**Existing tests to update (enumerated, from Eng review):**
- `EstablishmentEnforcementPathTests` — `ApproveDraft` at `:216/:234/:255`; `ChangeStatusAsync→Active` `:402` (verify still the *establishment* throw), `:410` (success), **`:425` (Suspended→Active — passes only with the §5.2 fix).**
- `Security/SensitiveFieldMaskingTests` — `ApproveDraft` `:196/:214/:278/:307` (drafts must satisfy the resolved policy or expect 422).
- `EmployeeModuleTests:40` — `ApproveDraft` + import case.
- `GroupProductFoundationTests:175-186` — directly unit-tests `ComplianceReadinessCalculator.Evaluate`; the `EmployeeStatutoryFields` **positional ctor (8 args)** breaks when the snapshot widens; `:178`'s `"NotARealField"` assertion changes when the unknown-key hole closes.
- Import-driving suites (`HrmHierarchyTests`, `ApprovalPolicyTests`, `PostgresDateTimeIntegrationTests`) — rows importing blank/Active now land Draft.
- Tests that `db.Employees.Add(new Employee{Status="Active"})` **directly** (LeaveApprovalScopeTests, GosiTests, BenefitsCompensationFoundationTests, WorkWeekServiceTests, ComplianceDashboardTests, SyntheticWorkforcePlanningSimulationTests, PayrollOvertimeLopTests, UnitTest1, EmployeeModuleTests direct inserts) **bypass the service → NOT affected.**
- **Test-blast control:** keep the code floor conservative and make the provisioning-seeded tenant-default profile the primary mechanism, so bundle-based fixtures inherit a known policy; budget to update the enumerated fixtures to carry required identity fields. The "747 green" baseline holds only with this.

**Regression:** full suite stays green; `EstablishmentGuardTests` occupancy-parity unaffected (readiness gate is separate from occupancy).

---

## 12. P0 blocking-gaps register (the must-fix checklist)
1. **UNION floor, not replacement** — resolver = code `StatutoryReadinessFloor ∪ tenant ∪ company`, strictest-wins, modeled on `StatutoryRateGuard` (§3.3). Without it the gate can be configured below statutory minimum.
2. **Code floor is the guarantee, not seeded rows** — `GCCComplianceSetting` is not provisioned per tenant and can't express Tier-2 floors; the always-present code floor (§3.4) must gate a settings-untouched tenant.
3. **Close the unknown-key hole at read AND write** — registry resolves every floor key; validator rejects unknown keys; unresolvable required key fails **closed** (§3.1/§3.2). Removes `"n/a" ⇒ present`.
4. **Wire the pay-side exclusion for real** — real `WHERE` predicate on readiness in payroll-run/SIF selection; `INACTIVE_EMPLOYEE` warning → error; honour `WpsEligible`; no second decorative flag (§6).
5. **Gate condition protects reinstatement** — fire on `Status=="Active" && !IsOccupyingStatus(oldStatus)` (§5.2), else break `EstablishmentEnforcementPathTests:425`.
6. **All four Active paths gated**, incl. `OffboardingController.Cancel:215`, with a guard-parity test (§5.3).
7. **Split the primitive** — pure `Evaluate` (import) + throwing `EnsureActivatableAsync` (§4.1); import never aborts the file.
8. **Snapshot built at call-site** — `Id==0`/DraftId/no-payroll-row at `ApproveDraft` (§5.4).
9. **Standalone exception, catch before `InvalidOperationException`** (§5.6).
10. **Compliance-controller scope guard** on Create/Update (`:206/:234`) — privilege-escalation once the policy is load-bearing (§9).
11. **Bulk-import: dry-run preview + persistent results view** — not the 5-second toast (§7.1).
12. **One source of truth** — drive client "required" from the policy; retire `EmployeesPage.tsx:148-191` `required:true` (§8.5).
13. **Just-the-gaps completion form** — not scroll-to-field (§8.4).
14. **Payroll safety for already-paid actives** — warn+acknowledge, never silently exclude (§6.6).
15. **Ship the migration in the same PR** (§10).

---

## 13. Deferred / enhancements (tiered, do not scope-creep P0)
- **P2:** import leniency toggle; `ImportBatchId` provenance/undo; completion "activate now?" moment; ESS self-service completion; reminders/nudges for stale incompletes (reuse `INotificationService` + nightly sweep); progressive disclosure of compliance sections in the edit form; import column-mapping.
- **Future:** onboarding as an **assignable task list** with owners/due-dates/reminders (some tasks are human — background check, equipment — not employee fields); the checklist is the seed of this system — don't preclude it.

---

## 14. How it composes with shipped work
- **Configurability program:** the gate *is* `CompanyComplianceProfile` finally made load-bearing — same entity, same config layers, same disclaimer — but with a **code statutory floor** underneath (via `StatutoryRateGuard`'s overlay model, not the tax **resolver**'s replacement model). No parallel system, no hard-coded field list; GCC defaults are a floor + editable seed.
- **IBAN validation:** the evaluator reuses `IbanValidator.IsValid` so "present but fails mod-97" is a first-class activate/pay blocker, extending entry-time validation (`:782`, `:657`) into the gate and payroll inclusion.
- **Establishment guard:** readiness sits **beside** it, not inside — establishment answers "is there a budgeted seat?", readiness answers "is this person complete enough to sit in it?"; both must pass to go Active, both return structured popup-ready errors, both wrap the same `EnforceAndExecute` transaction shape. The gate fires on a strictly narrower transition set than occupancy.
- **Company scoping:** the resolver and every read are company-scoped through the same `GetEntityScope()`/`ICompanyScoped` machinery; a scoped user only sees/gates employees and edits policies within their companies.
- **Status vocabulary:** unchanged. Readiness is an orthogonal axis; the badge is not a status. (The frontend carries a richer client-side status list — Pre-boarding/Probation/Confirmed — than the backend `EmployeeStatuses`; readiness is deliberately status-agnostic so that mismatch doesn't matter.)

## 15. Disclaimer (retained posture)
WPS SIF field lists, GOSI/PASI/PIFSS/SIO/GPSSA registration rules, and profession/free-zone-specific requirements (DIFC/ADGM/QFC differ from mainland) change by regulator circular. The `CountryTier` split (SA/AE certified; QA/KW/OM/BH fail-loud) is the honest boundary — Tier-2 packs must be legally validated before their gate is authoritative. Retain the disclaimer string (`CompanyGovernanceController.cs:200`) on every readiness response.

---
*This spec supersedes, on every point of conflict, the base design's (a) replacement-style resolver, (b) `GCCComplianceSetting`-as-baseline premise, (c) naive gate condition, (d) three-path enforcement list, (e) single returns-and-throws primitive, and (f) toast-based import feedback — per the SME registers, all verified against the code in this branch.*
