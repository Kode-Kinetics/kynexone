# Demo verification — wave 5 surfaces

**Branch:** `test/demo-verification` (from `develop` @ `4117a44`)
**Date:** 2026-09-20
**Scope:** `/hr-letters`, `/timesheets`, `/opening-balances`, report export, ESS "Request a Document", the Nitaqat panel.

---

## 0. Read this first

**The running demo stack cannot serve any of the six surfaces in this report.**

`zayra-api` and `zayra-frontend` were both built **2026-09-18 03:23 UTC**. Every surface I was asked to
verify merged on **2026-09-20, between 13:48 and 15:34 EDT**. The containers are ~2.5 days older than
the code.

```
$ docker image inspect <zayra-api image>      --format '{{.Created}}'  →  2026-09-18T03:23:32Z
$ docker image inspect <zayra-frontend image> --format '{{.Created}}'  →  2026-09-18T03:23:37Z

$ git log -1 --format=%ad --date=iso -- .../HrLettersController.cs        2026-09-20 13:48:39 -0400
$ git log -1 --format=%ad --date=iso -- .../Controllers/Timesheets/       2026-09-20 15:34:49 -0400
$ git log -1 --format=%ad --date=iso -- .../SaudiComplianceController.cs  2026-09-20 13:52:45 -0400
```

Measured against the live stack on :5173 right now:

```
/hr-letters          404
/timesheets          404
/opening-balances    404
/reports             200
```

Those are Next.js route 404s — the pages are not in the deployed frontend bundle at all. The screenshot
is a bare **"404 / Page not found / Go to Dashboard"** (`shots/live_hr-letters.png`). The API side is
equally absent: `/api/hr-letters/types`, `/api/saudi-compliance/nitaqat` and `/api/ess/document-requests/types`
all return 404 on :5117, while `/api/employees` returns 200 (so this is missing code, not broken auth).
The live `/saudi-compliance` page has no "Saudization" tab at all.

The live API also fails its own readiness check:

```
$ curl localhost:5117/health/ready        →  HTTP 503
{"status":"not_ready", ...,
 "pendingMigrations": 69,
 "workers":{"healthy":false,"missingCount":6,   # all 6 background workers "unavailable"
   "workers":[qiwa-sync, notification-delivery, ai-insights,
              report-schedules, compliance-reminders, background-jobs]},
 "redis":{"mode":"fallback_memory","configured":false}}
```

**69 pending migrations and zero running workers.** `report-schedules` being down is why scheduled
report delivery cannot work on this stack; `background-jobs` being down means any queued work never runs.

**Consequence: rebuilding and re-migrating the demo stack is a prerequisite for demoing any of this.**
Nothing else in this report matters until that happens. Everything below was therefore verified against
an isolated stack built from `develop` (see §7), which is the state the demo stack *would* be in after
a rebuild.

---

## 1. Demo-readiness table

| Surface | Verdict | Reason |
|---|---|---|
| **HR Letters** (`/hr-letters`) | **Safe with caveats** | Works end to end once templates are seeded. Ships with **zero templates** — a fresh tenant opens on a dead end. One click of "seed defaults" fixes it. Must be done before the demo. |
| **Salary certificate PDF** | **Safe to demo** | Issues a real 1-page PDF. **Arabic renders correctly as shaped glyphs — the tofu bug is not present.** Reference number, content hash, register entry all correct. Caveat: PDF text layer is not copy/paste-able (D4). |
| **ESS "Request a Document"** | **Safe with caveats** | Full round trip works: employee requests → HR queue → issue → employee downloads. Same dependency: **empty until letter templates are seeded** (the dropdown is built from configured templates). |
| **Timesheets** (`/timesheets`) | **DO NOT DEMO** (until D1 fixed) | The week grid fills in and saves, but **Submit for approval fails with 422 on the demo tenants** — no `Timesheet` approval workflow is seeded. The headline gesture of the feature is a dead button. One row of seed data fixes it; the feature itself is sound. |
| **Nitaqat panel** (Saudi Compliance → Saudization) | **DO NOT DEMO** unprepared — **fixed on this branch** | A seeder crash left the entire Nitaqat reference catalogue empty, so the panel was permanently stuck on "no activity configured" while the dropdown that would fix it had **no options**. Root-caused and fixed (D2). Even fixed, 12 of 13 activities refuse by design (D3) — demo only with `GENERAL_UNVERIFIED`. |
| **Report export (CSV / XLSX)** | **Safe to demo** | The `.xlsx` is a genuine OPC workbook, numbers are typed as numbers, and the leading-zero guard is correctly implemented. |
| **Report "schedule health" column** | **Safe with caveats** | `/api/reports/schedules` returns `[]` — the tab is **empty**, so there is no health to show. Needs a seeded schedule. On the live stack the `report-schedules` worker is also down. |
| **Opening balances** (`/opening-balances`) | **Safe to demo** | Clean 4-step wizard; the 14 CSV templates download correctly from `/api/migrations/template`. Needs a prepared sample file to show an actual import. |

---

## 2. Defects

### D1 — Timesheet submission is impossible on the demo tenants (**demo blocker**)

**Screen:** `/timesheets` → My week → *Submit for approval*
**Severity:** High (blocks the feature's main gesture)
**Status:** Reported, not fixed (it is seed data, and touching the demo seeders risks colliding with other agents)

**Reproduction:** As `employee1@intelliflow.com`, enter hours for the week and press *Submit for approval*.

```
POST /api/ess/timesheets/{id}/submit  →  422
{"code":"no_approval_route",
 "message":"No approval workflow is configured for timesheets. Add one for entity 'Timesheet'
            under Approvals. (No active approval workflow is configured for 'Timesheet' that
            applies to employee 93...)"}
```

**Cause:** `TenantProvisioningBundle.cs:357` seeds a `TIMESHEET-DEFAULT` workflow, but only when a tenant
is *provisioned*. The demo tenants were built by `IntelliFlowDemoSeeder.cs:615` and
`CleanDemoKsaSeeder.cs:548`, which seed **only** `LEAVE-APPROVAL`. Confirmed in the database:

```
 entity_name             | code              | slug
-------------------------+-------------------+-------------
 Timesheet               | TIMESHEET-DEFAULT | zayra        ← bootstrap tenant only
 LeaveRequest            | LEAVE-APPROVAL    | intelliflow
 LeaveRequest            | LEAVE-APPROVAL    | rasalmanar
```

The comment at `TenantProvisioningBundle.cs:355` predicts this exact failure: *"Without this row the
first timesheet a tenant submits 422s with approval_route_not_configured — the module would look shipped
and be unusable."* That is what happened; the guard just was not applied to the pre-existing demo tenants.

**Proof it is only seeding:** I inserted one `Timesheet` workflow for `intelliflow` in an isolated copy
and the whole path then worked first time —

```
POST /submit                        → 200  status=Submitted, approvalRequestId=e5fa853c…
GET  /api/timesheets/inbox (mgr)    → 200  "Timesheet 20 Sep – 26 Sep 2026 — Liu Wei"
POST /api/timesheets/{id}/decision  → 200  status=Approved, "Hours match the roster."
POST .../decision as the submitter  → 403  (self-approval correctly refused)
```

**Fix:** seed an active `ApprovalWorkflow` with `EntityName = "Timesheet"` for each demo tenant, or call
`InstallDefaultApprovalWorkflowsAsync` for existing tenants as a backfill.

---

### D2 — A 9-character string disabled the entire Nitaqat module (**fixed on this branch**)

**Screen:** Saudi Compliance → Saudization
**Severity:** High
**File:** `backend-dotnet/Zayra.Api/Infrastructure/Seed/NitaqatReferenceSeeder.cs:170`
**Status:** **Fixed** + regression test added

**Symptom:** the panel showed `nitaqat_activity_not_configured` and told the user to go and set the
economic activity — but the "Economic activity" dropdown was **empty**, so there was no way to comply.
An unescapable loop; no band could ever be computed.

```
GET /api/saudi-compliance/nitaqat/activities  →  200  []      # zero options
```

**Root cause.** The `GCC_STANDARD` weight rule's `SourceNote` was **509 characters** against
`nitaqat_weight_rules.source_note varchar(500)` (`ZayraDbContext.cs:3042`). EF does not client-side
validate `HasMaxLength`, so this is not caught at staging. `NitaqatReferenceSeeder.SeedAsync` stages
size tiers, weight rules, activities *and* the illustrative grid and commits all four in **one**
`SaveChangesAsync()` (line 69) — so Postgres rejected the batch and rolled back **everything**:

```
Npgsql.PostgresException 22001: value too long for type character varying(500)
fail: Startup[0] Seeder 'NitaqatReferenceSeeder' failed — continuing startup.
```

`Program.cs` wraps each seeder in `TrySeedAsync`, which logs and continues — so the API booted
**healthy** with an empty Nitaqat catalogue and nothing went red anywhere.

**Fix applied:** shortened the note to 491 characters (meaning preserved; the MHRSD "VERIFY" caveat and
the conservative-seeding rationale are intact) and added a comment explaining the blast radius.
Verified by restart:

```
NitaqatReferenceSeeder: seeded 66 platform-default rows.
activities=13  tiers=9  weights=8  thresholds=36
```

and the engine then computes a real band:

```
band Platinum | achieved 50.0% | saudiW 6.0 / totalW 12.0 | raw 6/12 | tier Small B
scenario: expatHiresBeforeDowngrade=6, bandBelow=HighGreen, saudiLeaversBeforeDowngrade=3
```

---

### D3 — 12 of the 13 Nitaqat activities refuse to produce a band (**demo trap, by design**)

**Screen:** Saudi Compliance → Saudization → Setup
**Severity:** Medium for the demo; not a code defect
**Status:** Reported

Only `GENERAL_UNVERIFIED` carries a threshold grid. Picking any real activity — including
"Construction & Contracting", the obvious choice in front of a Saudi client — returns:

```
reason:  nitaqat_thresholds_not_published
message: No Nitaqat band thresholds are published for activity 'Construction & Contracting'
         (CONSTRUCTION) at size tier 'Small B' effective 2026-09-20.
remedy:  Load the MHRSD Nitaqat table row for this activity and size tier, or record the band
         Qiwa reports.
```

This is deliberate and well argued in `NitaqatReferenceSeeder`'s header (inventing ~3,000 unverified
percentages would look authoritative and be wrong). It is still a trap: **the dropdown offers options
the engine will reject.** For the demo, pre-select the illustrative activity and do not click the others,
or load the real MHRSD table.

---

### D4 — Issued PDFs cannot be copied, searched, or read by a screen reader

**Screen:** any issued letter
**Severity:** Medium (bank-facing document)
**Status:** Reported

The PDF **renders correctly** — Arabic is properly shaped and right-to-left, Latin is clean
(`shots/letter-page1.png`). Fonts are embedded exactly as intended:

```
/F5  BAAAAA+NotoSansArabic-Bold      embedded=YES
/F10 EAAAAA+NotoSansArabic-Regular   embedded=YES
replacement/box characters: 0
```

But the **ToUnicode CMap is mismapped**, so the text layer is mojibake. Extracting the visible word
"certify" yields `cerঞfy` (U+099E, Bengali NYA); Arabic extracts with Latin-Extended characters spliced
in (`Ġﺳĥﺮﻛﻪ`). Consequences: a bank clerk cannot copy the reference or search the document, and assistive
technology reads nonsense. Invisible to anyone who only *looks* at the PDF — which is how it shipped.

**Note:** the flagged historical bug (Arabic as empty boxes) is **not present**. `DocumentFonts.cs` turns
`QuestPDF.Settings.UseEnvironmentFonts` **off** and embeds Noto Sans Arabic as a resource that fails at
startup if missing. That is the correct fix and it is working.

---

### D5 — `/api/timesheets/reports/attendance-variance` defaults to an empty date range

**Severity:** Low
**File:** `backend-dotnet/Zayra.Api/Controllers/Timesheets/TimesheetsController.cs:134`
**Status:** Reported

Called with no parameters it returns `DateOnly.MinValue` for both bounds and therefore no data:

```
{"from":"0001-01-01","to":"0001-01-01","toleranceMinutes":60,
 "loggedMinutes":0,"attendanceMinutes":0,"items":[]}
```

If the Attendance-variance tab ever loads without explicitly supplying dates, it shows an empty table
that looks like "no variances" rather than "no range selected". Should default to the current period.

---

### D6 — `/api/reports/export` numeric coercion catches leading zeros but not `+` phone numbers

**Severity:** Low
**File:** `backend-dotnet/Zayra.Api/Infrastructure/Reports/ReportWorkbookWriter.cs:120-128`
**Status:** Reported

The flagged risks are handled correctly. Being precise about how each was checked:

- **Proved empirically** — the download is a genuine workbook and numbers are typed as numbers.
  Opened `hr-headcount-20260920-2157.xlsx` with openpyxl: `[('Department','s'), ('Count','s')]`,
  `[('Engineering','s'), (6,'n')]`. The `'n'` is a real numeric cell, so the column sums.
- **Verified by code inspection only** — the leading-zero and IBAN guards. No report in the current
  catalogue emits a leading-zero employee code or an IBAN column, so I could not exercise them through
  a real export. The guard reads
  `if (trimmed.Length > 1 && trimmed[0] == '0' && trimmed[1] != '.') return false;` (so `"0042"` stays
  text) and `if (trimmed.Length > 20) return false;` (so an IBAN stays text). Both look right; neither
  has a test. **Worth a unit test on `ReportWorkbookWriter.ValueCell` before anyone relies on it.**

But `LooksNumeric` admits `+` and `-`, and `decimal.TryParse` with `NumberStyles.Number` allows a leading
sign — so a Saudi mobile in E.164 form, `+966501234567` (13 chars), is coerced to the number
`966501234567` and loses its `+`. The method's own comment names "a phone number" as a case it means to
guard. Low impact today (no current report column emits E.164), but it is a latent correctness bug.

---

### D7 — `e2e/global-setup.ts` hard-codes the API host

**Severity:** Low (test infrastructure)
**File:** `frontend/e2e/global-setup.ts:71`
**Status:** Reported

`PLAYWRIGHT_BASE_URL` redirects the frontend checks but the API readiness probe defaults to
`http://localhost:5117`, so pointing the suite at an isolated stack silently probes the wrong backend.
There *is* an `E2E_API_BASE_URL` override; it is just not mentioned alongside `PLAYWRIGHT_BASE_URL`
anywhere. Worth documenting next to the other env vars.

**This is also how I found the 503 in §0** — the pre-flight did its job and refused to run. That guard
is working exactly as designed.

---

## 3. Which pages are empty, and what they need seeded

Measured on a current-code stack against the `intelliflow` demo tenant.

| Page / panel | State | What it needs |
|---|---|---|
| `/hr-letters` → **Templates** | 0 templates; every letter type `isConfigured:false` | `POST /api/hr-letters/templates/seed-defaults` → adds 5 bilingual templates. **One click, and everything else on the page depends on it.** |
| `/hr-letters` → **Issue a Letter** | Dead end: *"No letter templates are configured for this tenant."* | As above. |
| `/hr-letters` → **Issued Register** | 0 issued | Issue 2–3 certificates for recognisable employees so the register is not empty on screen. |
| `/hr-letters` → **Employee Requests** | 0 pending | Raise 1–2 ESS requests and leave them pending, so HR has something to action live. |
| ESS **Request a Document** | Empty dropdown before templates are seeded | Letter templates (same seed). |
| **Nitaqat panel** | Catalogue empty → permanent refusal | Fixed by D2. Then set the establishment's economic activity to `GENERAL_UNVERIFIED`. |
| **Reports → Scheduled Reports** | `[]` — empty tab, no schedule-health column to show | Seed 1–2 report schedules. Also needs the `report-schedules` worker running (down on the live stack). |
| `/timesheets` → **Approvals / All timesheets** | Empty | A `Timesheet` approval workflow (D1), then 2–3 submitted weeks so an approver queue exists. |
| `/opening-balances` | Correctly empty (it is a wizard) | A prepared sample CSV. The 14 templates download fine from `/api/migrations/template`. |

---

## 4. What I fixed vs. what I only reported

**Fixed (2 files):**

- `backend-dotnet/Zayra.Api/Infrastructure/Seed/NitaqatReferenceSeeder.cs` — shortened the
  `GCC_STANDARD` SourceNote from 509 → 491 chars and documented why the limit matters (D2).
  Chosen as a fix rather than a report because it is one string, it unblocks an entire module, and
  `git diff develop feat/ksa-nitaqat` shows no other branch is touching this file.
- `backend-dotnet/Zayra.Api.Tests/NitaqatSeedFitsColumnTests.cs` — **new**, 3 tests.

**Reported only:** D1, D3, D4, D5, D6, D7 — and the stale-stack finding in §0, which is the one that
actually decides whether there is a demo.

I did not touch any frontend source; four other agents are working there. `frontend/e2e/demo-surfaces.spec.ts`
is a new file and collides with nothing.

---

## 5. Specs added

### `backend-dotnet/Zayra.Api.Tests/NitaqatSeedFitsColumnTests.cs` (new, 3 tests)

Asserts every string the Nitaqat seeder writes fits its column, reading the limit **from the EF model**
rather than hard-coding 500, so it survives a column resize. Needs no database.

```
$ dotnet test --filter 'FullyQualifiedName~NitaqatSeedFitsColumnTests'
Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3, Duration: 2 s
```

**Full backend suite reconciled against the `develop` @ `4117a44` baseline of 2411 / 0:**

```
$ dotnet test
Passed!  - Failed: 0, Passed: 2414, Skipped: 0, Total: 2414, Duration: 4 m 51 s
```

2411 → 2414 is **+3, exactly the three tests added here**, by name:
`EveryWeightRuleSourceNote_FitsTheSourceNoteColumn`,
`EverySeededSizeTierAndActivityName_FitsItsColumn`,
`SharedSourceNoteConstants_FitTheNarrowestSourceNoteColumn`.
Nothing regressed and nothing pre-existing was disturbed by the seeder change.

### `frontend/e2e/demo-surfaces.spec.ts` (new, 10 tests)

Against the isolated current-code stack:

```
✓ HR letters › the letter catalogue is configured, not just present (46ms)
✓ HR letters › issuing a salary certificate returns a real PDF with a quotable reference (162ms)
✓ HR letters › the HR letters screen shows its four working areas (4.3s)
✓ an employee request becomes an issued letter the employee can download (223ms)
✓ Timesheets › a week can be entered AND submitted (469ms)
✓ Timesheets › the weekly grid renders seven named days (3.8s)
✓ Nitaqat › the economic-activity catalogue is populated (45ms)
✓ Nitaqat › standing either computes a band or refuses by name — never a bare error (53ms)
✓ the Excel export is a real workbook, not a CSV wearing an xlsx name (28ms)
11 passed (38.5s)        # includes the setup + cleanup projects
```

**These are not vacuous, and I proved it rather than asserting it.** Negative control — I emptied
`nitaqat_activities` and re-ran:

```
✘ Nitaqat › the economic-activity catalogue is populated
  Error: the Nitaqat activity catalogue is EMPTY. The Saudization setup dropdown has no options,
  so no establishment can ever be configured and no band can ever be computed. Check the startup
  log for "Seeder 'NitaqatReferenceSeeder' failed".
  Expected: > 0   Received: 0
```

The timesheet test likewise takes its early-return branch only when the week is *already* submitted; I
deleted the timesheet rows and re-ran to confirm the Draft → save → submit path is the one that executes.

Assertion style, deliberately: PDF **magic bytes** and an embedded-font check rather than a byte length;
`PK` + `xl/workbook.xml` for the workbook rather than a filename; specific day names and tab labels
rather than `innerText().length > 50`. An empty page fails all of them.

**Expect these to go red against the current demo stack** — that is §0, working as intended.

### Gates

- `npx tsc --noEmit` — **clean** (exit 0).
- `npx next build` — **not re-run, and it cannot have changed.** No application source was touched:
  the only frontend file added is `e2e/demo-surfaces.spec.ts`, which is outside the Next build graph.
  Type safety for it is covered by the `tsc` run above.
- `npm run lint` — not runnable in this repo, as stated in the brief.

---

## 6. What a demo would trip on, beyond the defects

- **"Admin" appears as the signatory's job title** on the salary certificate (`issuedByTitle: "Admin"`).
  A bank-facing document signed by "Admin" reads as unfinished. It is the role name, not a job title.
- **The Arabic certificate embeds raw English** for department, job title and purpose — *"في إدارة
  Engineering"*, *"بوظيفة Chief Technology Officer"*, *"لغرض a bank loan application"*. Correct given the
  data is English-only, but it undercuts the bilingual claim in front of an Arabic-speaking client.
- **No Hijri date** on the certificate. For a KSA HR product positioned against ZenHR, a Gregorian-only
  salary certificate is a noticeable gap.
- **`/health/ready` returning 503 while `/health` returns 200** is correct behaviour, but anyone
  spot-checking health before the demo will see the green one and miss the 69 pending migrations.

---

## 7. How this was verified, and what I did not touch

The demo stack is the client environment, so **I did not restart, rebuild, migrate or write to it.**
Its database still has 305 tables and it answered health checks in 12–28 ms throughout.

Instead I stood up an isolated parallel stack:

1. `pg_dump` of `zayra` → new database `zayra_verify` **on the same Postgres instance** (32 MB, 305 tables,
   no errors). `CREATE DATABASE ... TEMPLATE` was not an option — the demo API holds open connections.
2. API built from `develop` and run on **:5217** against `zayra_verify` with `RunMigrationsOnStartup=true`.
   It applied the missing migrations there: **305 → 323 tables.** This is precisely why I cloned — the
   same API pointed at the demo database would have silently migrated the client environment.
3. `next dev` on **:5273** with `NEXT_PUBLIC_API_BASE_URL=http://127.0.0.1:5217` (the rewrite in
   `next.config.ts` makes the frontend's API target a single env var).
4. Redis isolated to database index 7 so the demo stack's keys were untouched.

Browser work used one headless Chromium at a time. The 1-minute load average on this machine is not a
usable gate — it sat between 36 and 420 from other agents' work and the user's simulators — so I gated on
**demo-stack latency** instead, re-measuring `:5117/health` and `:5173/` before and during each run. It
never exceeded 50 ms.

**Evidence** (screenshots and JSON): session scratchpad `shots/` —
`live_hr-letters.png` (the 404 a client would see), `iso_timesheets.png`, `iso_nitaqat.png`,
`iso_hrletters_issued.png`, `letter-page1.png` (the rendered Arabic certificate),
`iso-report.json` / `live-report.json` (per-route console errors, failed API calls, main-region text).

**Cleanup:** `zayra_verify` is disposable — `DROP DATABASE zayra_verify;` on `zayra-postgres` when done.
The :5217 API and :5273 dev server are plain background processes.
