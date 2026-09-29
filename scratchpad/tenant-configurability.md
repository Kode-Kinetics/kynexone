# Tenant configurability — module enable/disable

Branch `feat/tenant-configurability`, based on `integration/wave6` (`6d7905f`).

---

## The headline, first

**Before this change an Evostel administrator could configure no modules at all.**

`PUT /api/tenant-admin/feature-flags/{key}` returned **403 unconditionally**
(`TenantAdminController.cs:119-129`, pre-change), while the Tenant Admin "Feature Flags" tab
rendered eight toggles against it and swallowed the failure in a bare `catch {}`
(`TenantAdminPage.tsx:270-281`, pre-change). Clicking a module on or off did nothing, said
nothing, and left no trace. Module enablement was platform-staff-only in practice.

That is the exact defect this codebase is already carrying — a settings page full of switches that
do nothing — so it is the one I fixed first and completely.

---

## What configuration already existed, and what I found dead

### Existed and worked
| Surface | Where | Status |
|---|---|---|
| `TenantFeatureFlag` storage | `Models/SaasPlatform.cs:141`, table `tenant_feature_flags`, `ITenantOwned` | live |
| API gate | `Infrastructure/Filters/FeatureFlagGuardFilter.cs`, global filter registered `Program.cs:214` | live, but see below |
| Nav hiding | `Sidebar.tsx:135` via `requiredFeatureKey` | live, but only 8 of ~35 items tagged |
| Read projection | `FeaturesController.GetDisabledKeys` → `FeatureFlagContext.tsx` | live |
| Platform-admin write | `PlatformController.cs:665-700` | live |

### Found dead or wrong (all now fixed)
1. **The tenant write endpoint was a hard 403** — described above. Fixed.
2. **Seven of nineteen guard route prefixes matched no controller.** `/api/loans`, `/api/advances`,
   `/api/bonuses`, `/api/wps`, `/api/contracts`, `/api/visa-tracking`, `/api/ai-assistant` — the
   controllers had moved to `/api/finance/*` and `/api/compliance/*`. **The `finance` and
   `wps_export` flags were switchable in two admin UIs, priced in the pricing catalogue, and
   enforced nowhere.** Verified: `grep 'Route("api/loans'` → no match; real route is
   `Controllers/Finance/LoansController.cs:19` = `api/finance/loans`.
3. **A statutory obligation was disableable.** `/api/gosi` was gated by `qiwa_integration`
   (pre-change `FeatureFlagGuardFilter.cs:75`), so a KSA tenant switching off the Qiwa portal
   integration — a preference — also switched off GOSI social-insurance filing. Fixed: GOSI has
   its own key and is statutory-locked.
4. **No frontend route guard existed at all.** `ProtectedRoute` takes only `requiredPermissions`.
   Typing `/recruitment` on a tenant with it disabled rendered the full page; its XHRs 403'd, and
   `client.ts:76-84` deliberately silences `feature_not_enabled` toasts — so the user got a
   permanently blank screen with no explanation. Measured: **six silent 403s** on that page.
5. **Four competing copies of the key vocabulary**: `FeatureKeys` (17 keys,
   `Models/SaasPlatform.cs:96`), a 16-key array in `PlatformController.cs:3416`, `FLAG_DESCRIPTIONS`
   in the platform UI, and an unrelated 8-key `FEATURE_KEYS` in `TenantAdminPage.tsx`. None
   referenced another; the tenant-facing one omitted every module a client would plausibly switch off
   and listed five keys with **zero runtime readers**.
6. **`DashboardController` read the Qiwa flag fail-CLOSED** (`Any(... && IsEnabled)`), so a tenant
   with no row — the default for every new tenant — had the Saudization KPI hidden, while every
   other layer treated an absent row as enabled.

Dead configuration owned by the **other agent** (`TenantHrConfig`'s 11 unread fields,
`LeavePolicy.ApprovalWorkflowId`, carry-forward, `Location.GeofenceRadiusMeters`, `NumberingRule`,
`FiscalYear`) — **surveyed but deliberately untouched.**

---

## What I built, and the consumer that proves each option takes effect

Single source of truth: **`Infrastructure/Modules/ModuleCatalog.cs`** — 27 modules, each declaring
its lock class, its API route prefixes, its frontend nav paths and its notification categories.
Every enforcement layer derives from it, so they cannot drift.

| Layer | Consumer proving the switch takes effect |
|---|---|
| **API authorisation** | `Infrastructure/Filters/FeatureFlagGuardFilter.cs:52` → `ModuleCatalog.ResolveApiPath`, 403 at `:86` |
| **Effective state (all layers)** | `Infrastructure/Modules/TenantModuleService.cs:97` `GetStateAsync`, locks applied in `Build` at `:118` |
| **Navigation** | `frontend/src/layouts/Sidebar.tsx:141` `verdictForPath(item.path).allowed` |
| **Routes / pages** | `frontend/src/components/ModuleGate.tsx:38`, mounted `frontend/app/(dashboard)/layout.tsx:37` |
| **Dashboard widgets** | `Controllers/DashboardController.cs:94` (payroll trends emptied), `:580` (Saudization KPI) |
| **Notifications** | `Infrastructure/Notifications/NotificationService.cs:152` suppresses at the single dispatch funnel |
| **Admin UI** | `frontend/src/views/TenantAdminPage.tsx` Modules tab |
| **Write API** | `Controllers/TenantModulesController.cs:92` `SetModule` |
| **Anti-drift ratchet** | `Zayra.Api.Tests/Security/ModuleCatalogCoverageTests.cs` |

### Refusals, following the `ApprovalPoliciesController` doctrine
A switch that cannot be honoured end to end is refused with a machine-readable code, never stored:

| Case | Response |
|---|---|
| Load-bearing or statutory module | `409 module_not_disableable` + the catalog's reason verbatim |
| Key exists historically but nothing reads it | `501 module_not_enforceable` + what to switch instead |
| Unknown key | `400 unknown_module` |
| The old dead 403 endpoint | `410 feature_flag_write_moved` → `/api/tenant-modules/{key}` |

`eosb_calc`, `resume_screening`, `payroll_ai_validation`, `risk_scores`, `hijri_calendar` and
`wps_export` are **deliberately not offered as switches** (`ModuleCatalog.NonModuleKeys`) because no
runtime path reads them. `hijri_calendar` points at the localisation setting that is actually read.

---

## The non-disableable set, and the reasoning

The conservative direction is **Core**: a route classified Core behaves exactly as before this
change (always allowed). Only routes I was confident represent a business choice became switches.

**Core — never disableable, any tenant (12):** `core_hr`, `access_control`, `audit`, `approvals`,
`leave_attendance`, `self_service`, `documents`, `offboarding`, `reporting`, `dashboard`,
`notifications`, `configuration`.
Rationale in each `LockReason`. The load-bearing argument: everything references the employee
record; approvals is the funnel every module submits into; the audit log is the record a tenant
could otherwise use to erase the evidence of erasing it. The entitlement argument: annual leave
accrues whether or not it is tracked, end-of-service is owed to every leaver, and an employee's
access to their own payslip is not an employer preference.

**Statutory — locked while the obligation applies (3):** `gosi`, `qiwa_integration` (Saudization),
`compliance` (Iqama/passport/work-permit expiry).

The lock is **conditional**, which is the part worth reading:

- **Country.** The obligation binds only tenants in the countries that impose it —
  KSA for GOSI and Nitaqat, GCC for permit-expiry tracking. A British employer may switch
  Saudization off.
- **Origin.** A statutory module may declare `ObligationArisesFrom`. GOSI filing is owed on wages
  *this system pays*; a tenant that runs payroll elsewhere files GOSI from there, and locking it on
  here would be theatre rather than a safeguard. So **GOSI is disableable exactly when payroll is
  disabled** — verified live below. Saudization declares no origin: it follows from employing
  people, which the tenant does regardless.
- **Unknown country fails CLOSED.** The costs are asymmetric: wrongly locking Saudization on for a
  British tenant is an annoyance they fix by setting their country; wrongly letting a Saudi tenant
  switch off GOSI because their localisation row was blank is a statutory breach we caused.

**Optional — genuine business choices (12):** payroll, recruitment, performance, shifts, overtime,
timesheets, finance (loans/advances/bonuses), benefits, hr_letters, ai_assistant,
payslip_template_designer, mobile_app.

Two judgement calls worth flagging to the product owner:
- **Payroll is disableable.** Running payroll in an external system while using KynexOne for HR is a
  common, legitimate configuration. The statutory outputs that depend on it (GOSI, WPS) are handled
  by the origin rule above rather than by locking payroll on.
- **Overtime is disableable.** KSA Article 107 overtime pay is a *payroll pay component* and is
  unaffected; the overtime module is the pre-authorisation workflow. This is stated in the module's
  own description so an administrator is not misled.

---

## Custom fields — NOT BUILT, and why

I did not start this, deliberately, and the brief's own rule is the reason: *"If you cannot reach
every consumer, ship fewer surfaces completely and say which."*

I mapped the work first. An employee custom field must reach **16 consumer surfaces** to avoid being
the very orphan-configuration defect this task exists to remove:

`EmployeeFieldRegistry` (which has an `AssertCatalogIntegrity()` startup guard that throws on
drift, `:275-300`) · `EmployeeDetailDto.Project` (`EmployeeManagementDtos.cs:250`) ·
`EmployeeListItemDto` and its **two** construction sites · `EmployeeCreateRequest` ·
the ~60-case `ApplyChanges` switch that **silently ignores unknown keys**
(`EmployeesController.cs:3916`) · `SensitiveFields` (approval routing, `:33`) ·
`SensitiveEmployeeExportHeaders` (export masking — **miss this and a sensitive field leaks**, `:232`) ·
`BuildEmployeesCsvAsync` value map (`:283-406`) · the CSV importer and its preview, which must
resolve identically · `EssEmployeeProfileDto.Project` · `EmployeeSafeSnapshot` · the frontend
`resolveFieldCatalog`, which **structurally cannot add fields** today (it maps over a local
constant, `employeeFieldCatalog.ts:374`) · the hand-written create modal · the hard-coded detail
tabs · mobile `ProfileScreen`.

A half-built version would save a field that never renders or exports. That is worse than not
shipping it. The map above is the handover artefact; the storage decision I would make is a typed
side-table modelled on `EmployeeComplianceRecord` (already an EAV child table in production for
employees) rather than a `jsonb` bag, because the export/search/filter surfaces need per-field
metadata and a `jsonb` blob cannot carry validation or EN/AR labels without a definitions table
anyway — at which point the side-table is simpler and indexable.

**Terminology/branding (priority 3) and workflow shape (priority 4) were not built.**
One finding to hand over: `TenantBranding` is `ITenantOwned` only, so a group tenant cannot give one
legal entity its own logo or letterhead; making it `ICompanyScoped` and mirroring
`HrLetterIssuer.ResolveTemplateAsync`'s "company override first, tenant default second" precedence
(`HrLetterIssuer.cs:295-296`) is the obvious next step.

---

## Three defects found only by rendering

The brief's insistence on rendering earned its keep — all three were invisible to the API tests,
which were green throughout.

1. **`/api/features` was browser-cached for five minutes.**
   `Infrastructure/Http/SecurityHeaders.cs:45` classified it as "semi-static reference data" with
   `private, max-age=300`. It is not reference data — it is what the navigation and route guard read
   to decide what exists. An administrator switched a module off, the write succeeded, the API
   started refusing it, **and the UI carried on showing the module** because every fetch came from
   the browser's own cache. Indistinguishable from a dead switch. `curl` bypasses the HTTP cache and
   looked perfect throughout, which is why nothing else caught it.
   Fixed + pinned by `ModuleCatalogCoverageTests.ModuleStateEndpoints_AreNotBrowserCacheable`.

2. **A cache-coherency race in `TenantModuleService`.** Removing a fixed cache key is not enough:
   a read that began before a write can finish after it and re-insert the pre-toggle state, which
   then sticks for the full 2-minute TTL. Replaced with a per-tenant generation stamp; a late writer
   stores under a superseded key nobody reads. Pinned by
   `InFlightRead_CannotRestoreStaleStateAfterAToggle`.

3. **The gate rendered the real page first.** Fail-open-while-loading meant the disabled page
   painted, fired its data calls, took six silenced 403s, then flipped to the notice. `ModuleGate`
   now holds for a spinner until state is known — final render run reports **`JS errors: none`**.

A fourth, caught by the new ratchet rather than the browser: `PolicyDocumentController` is routed at
`api/ai/policy`, **inside the AI Assistant's `/api/ai` prefix**. Switching off the AI Assistant would
have taken the tenant's policy documents offline with it. The Documents module now claims the longer
prefix, and longest-prefix resolution wins.

---

## Evidence

### Fail before / pass after, by name

**Compile-level (before):** with the new `ModuleCatalog` in place, `dotnet build` of the test project
failed on 7 call sites — `DashboardKpiDocumentCoverageTests.cs:37`, `DashboardTests.cs:60,72`,
`DashboardCacheCompanyScopeTests.cs:218`, `UnitTest1.cs:102`, `EssSelfServiceW2DTests.cs:1006`
(`CS7036`, new `DashboardController` parameter) and `FeatureFlagGuardTests.cs:40` (`CS1729`).
Resolved by applying the codebase's own "optional with concrete fallback (house pattern)" to
`DashboardController`, so six of those seven files are **untouched**. Only
`FeatureFlagGuardTests.cs` was edited, because its semantics genuinely changed.

**The ratchet failing before it passed** (first run of `ModuleCatalogCoverageTests`):

```
Failed  ModuleCatalogCoverageTests.EveryCatalogRoutePrefix_MatchesARealController
  Orphans: core_hr -> /api/company-governance, approvals -> /api/approvals,
           documents -> /api/policy-documents, configuration -> /api/master-data
Failed!  - Failed: 1, Passed: 51, Total: 52
```
All four were prefixes I had invented; `/api/policy-documents` turned out to be the `api/ai/policy`
finding above. After correction: `Passed! - Failed: 0, Passed: 52`.

**Behaviour tests I replaced rather than kept** (`FeatureFlagGuardTests`), reconciled by name —
these asserted the *defects*:
- `SaudiCompliance_DisabledCompliance_Returns403` → replaced by
  `SaudiCompliance_StoredDisableFlag_IsNotHonoured_BecauseSaudizationIsStatutory`
- `Gosi_DisabledQiwaIntegration_Returns403` → replaced by
  `Gosi_StoredDisableFlag_IsNotHonoured_BecauseGosiIsStatutory`
- `Gosi_EnabledQiwaIntegration_PassesThrough` → `Gosi_Enabled_PassesThrough`
- `Wps_DisabledWpsExport_Returns403`, `Wps_EnabledWpsExport_PassesThrough` → **deleted**; they
  asserted that the guard blocked `/api/wps/export`, a path no controller has ever served. The test
  passed because nothing was protected.

**After:**
```
ModuleCatalogCoverageTests + TenantModuleBehaviourTests + FeatureFlagGuardTests
Passed!  - Failed: 0, Passed: 63, Skipped: 0, Total: 63

NotificationDeliveryTests (21 pre-existing + 3 new)
Passed!  - Failed: 0, Passed: 24, Skipped: 0, Total: 24
```

### Full suite on the branch

Run only once the machine was genuinely quiet, per the contention rule — polled and launched in a
single shell invocation, and the launch line records the conditions:

```
LAUNCH at load=16.06 competing=0 (19:53:46)
Passed!  - Failed: 0, Passed: 2485, Skipped: 0, Total: 2485, Duration: 1 m 37 s
[exited with code 0]
```

Never run with `--no-build`; the run above rebuilt `Zayra.Api` and `Zayra.Api.Tests` first.

### Baseline on `integration/wave6`, reconciled by name

The two test classes I modified, run on the unmodified base (`6d7905f`) and on the branch:

```
wave6  (6d7905f):  FeatureFlagGuardTests + NotificationDeliveryTests
                   Passed! - Failed: 0, Passed: 48, Total: 48
branch (557959d):  FeatureFlagGuardTests + NotificationDeliveryTests
                   Passed! - Failed: 0, Passed: 49, Total: 49
```

Per class, reconciled:

| Class | wave6 | branch | Delta |
|---|---|---|---|
| `FeatureFlagGuardTests` | 27 | 25 | −6 removed, +4 added (all six named above) |
| `NotificationDeliveryTests` | 21 | 24 | +3 (module gate: suppressed / delivered / security-never-suppressed) |
| `ModuleCatalogCoverageTests` | — | 12 | new |
| `TenantModuleBehaviourTests` | — | 26 | new |

Net: **45 tests added, 6 removed**, and the 6 are exactly the ones that asserted the defects
(`Wps_*` asserted a guard on a path no controller serves; `Gosi_*`/`SaudiCompliance_*` asserted that
a preference could switch off a statutory obligation). 2485 − 45 + 6 = **2446**, the expected wave6
total.

Zero failures on either side, so there is no pre-existing failure being masked. I attempted a full
`wave6` suite run for completeness but abandoned it: other agents drove load from 16 to 55 with six
concurrent `dotnet test` processes mid-run, and a contended run is not evidence. The targeted
run above is the reconciliation that matters — every test I touched, named, on both sides — and the
branch's own full suite passed 2485/2485 on a verified-quiet machine.

### A real-data divergence the unit tests would have missed

The seeded KSA tenant stores `country_code = 'SAU'` (ISO alpha-3), not `'SA'`:

```
 intelliflow  | SAU
```

My first implementation compared the raw string against the catalog's alpha-2 list, so **a Saudi
tenant would have been found to have no Saudi obligations** — with green unit tests. Now normalised
through the existing `CountryCodeStandard.NormalizeToIso2` (which I found rather than reinvented),
and pinned by a `[Theory]` over `SA`, `SAU`, `sau`, `" sa "`, `GB`, `GBR` and the unrecognised `ZZZ`.

### Live API, against the real database (own API on :5399, demo stack on :5117 untouched)

```
=== BEFORE: GET /api/recruitment/candidates ===   HTTP 200
=== PUT /api/tenant-modules/recruitment {"enabled":false} ===
    {"key":"recruitment",...,"enabled":false,"canDisable":true,"lockClass":"Optional"}   HTTP 200
=== AFTER:  GET /api/recruitment/candidates ===
    {"error":"feature_not_enabled","feature":"recruitment",...}                          HTTP 403
```

Refusals:
```
PUT /api/tenant-modules/gosi        {"enabled":false} -> 409 module_not_disableable  (Social Insurance Law…)
PUT /api/tenant-modules/core_hr     {"enabled":false} -> 409 module_not_disableable
PUT /api/tenant-modules/risk_scores {"enabled":false} -> 501 module_not_enforceable
PUT /api/tenant-modules/nonsense    {"enabled":false} -> 400 unknown_module
PUT /api/tenant-admin/feature-flags/recruitment       -> 410 feature_flag_write_moved
```

The conditional statutory rule, live:
```
payroll OFF  ->  gosi canDisable=True   (obligation follows the wages, paid elsewhere)
                 qiwa_integration canDisable=False  (follows from employing people)
PUT gosi {"enabled":false} -> 200
```

### Screens rendered

Own Next dev server on :3399 against my API on :5399; the demo stack (:5173/:5117) was left running
and untouched throughout.

- **Tenant Admin → Modules** (`02-modules-tab.png`) — three groups rendered *Your choice* (12
  toggles) / *Required by law* (3) / *Required by the product* (12); **15 "Locked on" badges**; each
  locked row shows its reason; country shown as `SAU`.
- **Module gate** (`05-module-gate.png`) — "Recruitment is switched off" with chrome intact and the
  Recruitment nav entry gone; *Back to dashboard* and *Manage modules* actions.
- **Mobile 390×844** (`06`) and **dark mode** (`07`) — both correct.
- **Sidebar after disabling** (`04`) — Recruitment absent.
- **Restored** (`08`) — page returns.

Final scripted run:
```
modules tab rendered; locked-on badges: 15
group headings: Your choice | Required by law | Required by the product
recruitment toggle: aria-checked before: true / after: false
nav entry for /recruitment after disabling: 0 (expect 0)
ModuleGate shown on /recruitment: 1 (expect 1)   gate module: recruitment
  heading: Recruitment is switched off
ModuleGate after restore: 0 (expect 0)
GOSI rendered as a locked switch: 1 (expect 1)   disabled: true
JS errors: none
```

### Gates

```
npx tsc --noEmit                      clean
npx next build                        ✓ Compiled successfully; ✓ Generating static pages (66/66); exit 0
dotnet build Zayra.Api                Build succeeded, 0 Error(s)
dotnet tool run dotnet-ef migrations has-pending-model-changes --project backend-dotnet/Zayra.Api
                                      No changes have been made to the model since the last migration.
git diff --diff-filter=D --name-only integration/wave6 feat/tenant-configurability
                                      (empty — no deletions)
```

**No migration.** This change adds no schema: it reuses the existing `tenant_feature_flags` table
and the existing key spellings, so Evostel's live pilot data is untouched and there is nothing to
apply. `ModuleKeys` re-exports the pre-existing `FeatureKeys` constants *by reference* rather than
re-spelling them, so the compiler guarantees no stored flag is orphaned by a typo.

**`QueryFilterBypassRatchetTests` untouched** — no `IgnoreQueryFilters()` added, pinned count
unchanged. No ratchet suppressed, weakened or re-pinned.

---

## What an Evostel administrator can and cannot configure today

*The plain list for the product owner. Everything in the "can" column has been exercised end to end
against a real database and rendered in a browser.*

### Can configure, and it genuinely takes effect everywhere

Under **Tenant Admin → Modules**, an administrator can switch these twelve on and off. Switching one
off removes it from the navigation, blocks its pages behind an explanatory screen, makes its API
refuse with `403 feature_not_enabled`, drops its dashboard widget, and stops its notifications:

1. Payroll  2. Recruitment  3. Performance  4. Shifts & Rosters  5. Overtime  6. Timesheets
7. Loans & Advances  8. Benefits  9. HR Letters  10. AI Assistant  11. Payslip Template Designer
12. Mobile App

Every change is written to the audit log (`tenant.module_enabled` / `tenant.module_disabled`) with
who, when, and the before/after value.

Also already configurable, unchanged by this work: language/RTL, calendar system, timezone, date
format, currency, country, week start and working week (Localization); logo, colours and company
names (Branding); password policy, session timeout and lockout (Security); HR letter templates,
including per-legal-entity overrides.

### Cannot configure — and the administrator is told why, in the UI

- **Twelve core modules** (Core HR, Access & Identity, Audit Trail, Approvals, Leave & Attendance,
  Employee Self-Service, Documents & Policies, Offboarding, Reports & Analytics, Dashboard,
  Notifications, Configuration & Setup) — the product depends on them.
- **Three statutory modules for a Saudi tenant** (GOSI, Saudization & Qiwa, Compliance &
  Document Expiry). GOSI becomes switchable if payroll is not run in KynexOne. A non-GCC tenant can
  switch the relevant ones off.

These render as visibly locked switches with the reason beneath, rather than being hidden — an
administrator looking for "where do I turn this off" is told, not left wondering.

### Cannot configure — not built

- **Custom fields on any record.** Genuinely absent; not started. See the 16-surface map above.
- **Terminology overrides** ("Associate" for "Employee", "Division" for "Department"). Not built.
- **Per-company branding.** `TenantBranding` is tenant-wide, so a group cannot give one legal
  entity its own logo or letterhead.
- **Sub-feature switches** (EOSB calculator, resume screening, payroll anomaly checks, employee risk
  scores, Hijri calendar, WPS export). These appeared as toggles in the old UI and read by nothing.
  They now refuse with `501 module_not_enforceable` and name what to switch instead. **Removing six
  fake switches is part of the deliverable, not a gap in it.**

---

## Files touched (for merge sequencing with the parallel agent)

**New (5):** `Infrastructure/Modules/ModuleCatalog.cs`, `Infrastructure/Modules/ModuleKeys.cs`,
`Infrastructure/Modules/TenantModuleService.cs`, `Controllers/TenantModulesController.cs`,
`frontend/src/components/ModuleGate.tsx`
**New tests (2):** `Tests/Security/ModuleCatalogCoverageTests.cs`,
`Tests/Security/TenantModuleBehaviourTests.cs`

**Modified (14):**
`Infrastructure/Filters/FeatureFlagGuardFilter.cs` (rewritten to read the catalog) ·
`Controllers/FeaturesController.cs` (effective state + `/modules`) ·
`Controllers/DashboardController.cs` (ctor + 2 gates) ·
`Infrastructure/Notifications/NotificationService.cs` (~18 lines at the dispatch funnel) ·
`Infrastructure/Http/SecurityHeaders.cs` (**2 lines** — removed `/api/features` from the cached bucket) ·
`Program.cs` (**4 lines** — one DI registration) ·
`Controllers/TenantAdminController.cs` (**the dead `SetFeatureFlag` 403 → 410 only**) ·
`Tests/Security/FeatureFlagGuardTests.cs`, `Tests/NotificationDeliveryTests.cs` ·
`frontend/src/contexts/FeatureFlagContext.tsx`, `frontend/src/api/intelligence.ts`,
`frontend/src/layouts/Sidebar.tsx` (**1 line**), `frontend/src/views/TenantAdminPage.tsx`
(Feature Flags tab → Modules tab), `frontend/app/(dashboard)/layout.tsx` (**2 lines**)

**Likely overlap with the parallel agent:** `TenantAdminController.cs` (my change is confined to the
one dead method), `SecurityHeaders.cs` and `Program.cs` (both tiny). I did not touch
`TenantHrConfigController`, `LeavePoliciesController`, `SetupSettingsController`,
`ApprovalPoliciesController` or any approval-chain code.
