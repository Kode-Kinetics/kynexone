# Merging `feat/ksa-compliance-truth` into `integration/wave6`

Branch: `integration/wave6-ksa` · base `integration/wave6` @ `6d7905f` · merged `feat/ksa-compliance-truth` @ `3cc32ce`
Merge base: `4117a44` (`integration/wave5`). Worked in `scratchpad/wt-w6int`. Not pushed, not merged.

`integration/wave6` contains, in order: `fix/money-figures`, `feat/demo-reachability`,
`test/demo-verification`, `feat/arabic-rtl`.

---

## 0. The brief said three conflicts. There were five.

| File | Conflicted hunks | Shape |
|---|---|---|
| `Infrastructure/Seed/NitaqatReferenceSeeder.cs` | 1 | semantic — two different regulatory regimes |
| `Infrastructure/Compliance/GosiReadinessReportService.cs` | 2 | semantic — two mechanisms for one ceiling |
| `Zayra.Api.Tests/Security/CrossTenantQueryFilterTests.cs` | 1 | service-construction wiring |
| **`Zayra.Api.Tests/GosiReadinessEndpointTests.cs`** | **10** | service-construction wiring |
| **`Zayra.Api.Tests/PayrollSalaryConsistencyPostgresTests.cs`** | **1** | service-construction wiring |

The two extra files are the same collision as the third one, repeated: both streams changed
`GosiReadinessReportService`'s constructor in the same commit range, so every direct construction
in the test suite collided. Nothing was hidden by them; they are named here only because the brief
predicted three files and a merge that reports five deserves an explanation before anyone assumes
something else moved. `git config rerere.enabled` is unset and there is no `rr-cache`, so this is
the plain three-way merge, not a partially-replayed one.

The brief's line counts (185 / 128 / 91) are much larger than what `git merge` produced here: the
seeder conflict is one hunk of 24 marker-delimited lines, the GOSI conflict two hunks totalling
14, and each test-file hunk is 5. Same files and the same collisions, so the difference is in how
the earlier attempt counted rather than in what conflicted — but it is worth saying out loud,
because a reader comparing a 24-line hunk against a reported 185 might reasonably wonder whether
they are looking at the same merge.

---

## 1. Nitaqat — `NitaqatReferenceSeeder.cs`

### The two intents

**`test/demo-verification` (in wave6).** `GCC_STANDARD`'s `SourceNote` was 509 characters against a
`varchar(500)`. EF does not client-side validate `HasMaxLength`, so the row staged fine and
PostgreSQL threw `22001` inside `SeedAsync`'s single `SaveChangesAsync` — rolling back all 66
Nitaqat reference rows (9 tiers + 8 weight rules + 13 activities + 36 grid cells). `TrySeedAsync`
logged and carried on, so the API booted healthy with an empty catalogue: the Saudization panel
offered an empty activity dropdown while its own refusal told the user to go and pick an activity.
Shortened to 491 characters, and pinned by `NitaqatSeedFitsColumnTests`, which reads the limit off
the EF model rather than hard-coding 500.

**`feat/ksa-compliance-truth`.** The seeder modelled a regime MHRSD abolished on 2021-12-01.
Ministerial Decision 182495 replaced the fixed establishment size bands with a per-activity
logarithmic curve `y = m·ln(x) + c`. The branch reimplemented on the curve, kept the grid for
pre-2021 periods and as a manual override, made every standing report which regime produced it,
and rewrote the weight rules against Ministerial Decision 61706 — including correcting
`GCC_STANDARD` from denominator-only (numerator 0) to counted-as-Saudi (numerator 1) under clause
Twenty-two, superseded at the decision's own in-force date rather than edited in place.

### Resolution

Took `feat/ksa-compliance-truth`'s seeder whole. The wave6 side of this file was only the shortened
string, and the string it shortened no longer exists — the rewrite replaced that note with a
sourced, verified one. Nothing of wave6's was dropped except the text of a note that is gone.

**Then re-applied the wave6 defect class, because the rewrite reintroduced it four times over.**

### Finding: the rewrite ships four over-length `SourceNote` values

The over-length-string defect is a property of any seeder writing that column, not of one string.
Measured against `varchar(500)` (`ZayraDbContext.cs` lines 3019 / 3031 / 3042 / 3058), the
`feat/ksa-compliance-truth` seeder as written contains:

| Value | Length | Over by |
|---|---|---|
| `TierSource` (written to all 9 size tiers) | **869** | 369 |
| `SAUDI_DISABILITY` note | **777** | 277 |
| `EXPAT_PREMIUM_RESIDENCY` note | **584** | 84 |
| `GCC_STANDARD` note | **565** | 65 |

Any one of these rolls back the whole 66-row seed on PostgreSQL, exactly as the 509-character note
did: `22001`, inside one `SaveChangesAsync`, swallowed by `TrySeedAsync`, healthy-looking boot,
empty catalogue, Saudization panel permanently refusing. This does not show up on
`feat/ksa-compliance-truth`'s own suite because `NitaqatSeedFitsColumnTests` is a wave6 test — the
branch has no guard for it, so its reported 2452/0 is green over a latent runtime failure. The
guard is generic (it reflects over the `Weights` array and over every `const string` in the class
and compares against the EF model), so the merge is where this surfaces.

Condensed all four to fit, keeping the substance — the decision reference, the clause number, the
figure, the modelled/not-modelled statement and the `[COUNSEL]` marker. The material dropped from
`TierSource` (the verbatim MHRSD quotation, the source URL, the pre-2021 seven-band list) is not
lost: it is in `NitaqatCurve.cs`, which is source and not a `varchar`. Post-merge lengths:

```
CONST PlatformScope: 161      SAUDI_FULLTIME:          103
CONST TierSource:    498      NONSAUDI_STANDARD:        69
CONST GridSource:    247      SAUDI_PARTTIME:          371
CONST ActivitySource:339      SAUDI_DISABILITY:        498
CONST IllustrativeActivity: 18  SAUDI_HALF_WAGE:       379
                              SAUDI_BELOW_WAGE:        208
                              GCC_STANDARD:            488
                              EXPAT_PREMIUM_RESIDENCY: 471
```

Also carried forward, as a comment at the top of the note block, the reason the limit matters —
the `22001` / whole-transaction-rollback / healthy-boot chain — so the next person adding a note
sees why `NitaqatSeedFitsColumnTests` exists before they hit it.

`NitaqatCurve.cs` and `NitaqatGridImportService.cs` both truncate `SourceNote` to 500 explicitly
before writing, so the curve and the customer-loaded grid are not exposed to this. Checked.

---

## 2. GOSI — `GosiReadinessReportService.cs`

### The two intents

**`fix/money-figures`.** Made the effective-dated rules engine the sole source of the
contributory-wage ceiling. New `KsaGosiWageBounds.ResolveAsync` (ceiling + a deliberately unseeded
floor), called by all four paths; `GosiCalculationService.Calculate` gained a `GosiWageBounds`
parameter and **stopped reading `GosiContributionRule.Min/MaxContributoryWage` entirely**, because
`GosiRuleSeeder` never populates them. Pinned by `GosiTests.Calculate_PlatformDefaultRuleWithNoCap
Columns_StillCapsAtTheStatutoryCeiling` and a ratchet that a rule carrying its own
`MaxContributoryWage` must not change the answer.

**`feat/ksa-compliance-truth`.** Fixed the same user-visible symptom independently: new
`GosiContributoryWageBasis` reading the same rule key, the report capping the wage itself before
calling `Calculate`, plus three new published fields (`ContributoryWageCeiling`,
`ContributoryWageBasis`, `EmployeesAtWageCeiling`) so the surface says *why* a high earner's
contribution is smaller than their salary implies.

Merged naively this is two mechanisms that happen to agree — which is the thing both branch
write-ups argue against in their own words.

### Resolution: one resolver, both surfaces

- `GosiContributoryWageBasis` no longer reads the statutory rule. Every member **delegates** to
  `KsaGosiWageBounds`: `DefaultCoveredWageCeilingSar` is now an alias of
  `KsaGosiWageBounds.DefaultMonthlyCeilingSar` (not a second literal kept in step by a test), and a
  new `BoundsAsync` / the existing `CeilingAsync` both go through `KsaGosiWageBounds.ResolveAsync`.
  It survives as the compliance surfaces' named entry point, not as a second mechanism.
- `GosiReadinessReportService` resolves the bounds once per report, keeps
  `feat/ksa-compliance-truth`'s base delegation (`GosiContributoryWageBasis.CoveredWage` →
  `SalaryBreakdown.GosiCoveredWage`, so there is one definition of basic+housing), clamps with
  `bounds.Clamp` so the *floor* is honoured too if one is ever configured, counts ceiling-bound
  employees, and hands the bounds to `GosiCalculationService` — which remains the one place a
  contribution line is clamped. Clamping in both places is idempotent; doing it in the report is
  what lets the report say *which* employees the ceiling bound.
- All three of the ksa branch's published report fields are kept.

`RuleKeys.GosiCoveredWageCeilingSar` was already `= KsaGosiWageBounds.CeilingRuleKey` on wave6, so
`GosiContributoryWageBasis.CeilingRuleKey` resolves to the same compile-time constant.

The service names both `BoundsAsync` (for the clamp, which carries the floor) and `CeilingAsync`
(for the published figure) — two calls into one delegating resolver against a per-request memoized
reader, not two sources. The alternative, deriving the published ceiling from `bounds` inline,
would have required editing `StatutoryRateStoreTests.GosiReadiness_CapsAtTheSameCeilingSourceThe
PayslipUses`, which greps for `GosiContributoryWageBasis.CeilingAsync` in the source. I left that
assertion alone.

### Verifying the idempotency claim

`feat/ksa-compliance-truth`'s `ReadinessReport_CapsAtTheSameCeilingThePayslipUses` does prove its
own premise first — it asserts `MaxContributoryWage == null` on every seeded Saudi rule before
asserting the capped figure, with the stated reason "this is the seeder gap the other stream owns;
the fix must not depend on it." That holds under the merge, and for a reason the test did not
anticipate: after `fix/money-figures` those columns are not read at all, so the premise is not
merely still true, it is now structurally true. `GosiCoveredWageCeiling_IsOneNumberFromOnePlace`
observes the pack's own output rather than asserting a literal, so it survives the delegation
unchanged. The order-independence claim is sound.

---

## 3. `CrossTenantQueryFilterTests` / `GosiReadinessEndpointTests` / `PayrollSalaryConsistencyPostgresTests`

Not "both streams added tests" — all twelve hunks are the same one-line collision. wave6 wired
`new GosiReadinessReportService(db, new StatutoryRuleReader(db))`; `feat/ksa-compliance-truth`
wired `TestReconciliation.GosiReadiness(db)`.

Took the ksa side at all twelve sites. It is one named factory whose doc comment states the
coupling being tested ("the SAME statutory rule reader the payslip path uses"), against twelve
inlined constructions; and it yields the identical SAR 45,000 ceiling that wave6's real
`StatutoryRuleReader` falls back to when the rule is unseeded, so wave6's `expected =
GosiCalculationService.Calculate(..., GosiWageBounds(null, DefaultMonthlyCeilingSar))` comparisons
in the same files still compare like with like. Neither stream's assertion was touched.

---

## 4. The ratchet: `StatutoryRateStoreTests.GosiReadinessAndPreview_PassTheCoveredWageNotBasicAlone`

Reported as: generalising a pinned literal to the rule it encodes, plus an added negative assertion
proved non-vacuous, plus a further ratchet added. **Verified, with one narrow caveat.**

What actually changed:

1. The single needle became a list of accepted forms for `GosiReadinessReportService.cs`
   (the original inline sum, the exact `GosiContributoryWageBasis.CoveredWage(...)` two-line call,
   and a bare `GosiContributoryWageBasis.CoveredWage(`). **This is the one loosening.**
2. **Added** `Assert.DoesNotMatch` for
   `GosiCalculationService.Calculate(<x>Nationality, salary.BasicSalary,` — applied inside the loop,
   so it now covers `GosiController.cs` as well, which previously had only a positive pin.
3. **Added** a whole new test, `GosiReadiness_CapsAtTheSameCeilingSourceThePayslipUses`.

**Non-vacuity of the negative assertion.** No test in the branch proves the regex matches anything,
so I checked it directly against the three relevant source shapes:

```
pre-fix readiness  (emp.Nationality, salary!.BasicSalary, rules, …)      MATCHES
pre-fix controller (employee.Nationality, salary.BasicSalary, rules, …)  MATCHES
post-fix readiness (…, salary!.BasicSalary + salary.HousingAllowance, …) no match
```

It is non-vacuous. The claim is true; it is just asserted rather than demonstrated in-repo.

**Net strength: not weaker, but with a real hole.** Accepted form (3), the bare
`GosiContributoryWageBasis.CoveredWage(`, would accept `CoveredWage(salary!.BasicSalary, 0m)` —
housing dropped — and the negative assertion would not catch that either, because it only matches
a direct `Calculate(nationality, salary.BasicSalary,`. Under the old exact-string pin, dropping
housing failed the lint. Against that, the change adds a negative assertion on two files and an
entire new ceiling-source test, and the old pin had its own weakness (the literal could have sat in
a comment and satisfied it). On balance I judge it stronger overall and **merged it as-is**, and I
kept the ksa branch's exact two-line formatting of the `CoveredWage` call so accepted form (2), the
tight one, is the form that actually matches.

Recommended follow-up (not done here — it is another stream's assertion and nothing required it to
make the merge pass): drop accepted form (3), or require `salary.HousingAllowance` to appear inside
the matched call.

---

## 5. Collisions git did not flag

### 5a. `feat/ksa-compliance-truth` breaks `feat/arabic-rtl`'s ratchet — FIXED

`NitaqatPanel.tsx` gained two new curve-related components, and their markup uses **physical**
direction utilities:

```
src/views/NitaqatPanel.tsx:142  <ol className="list-decimal space-y-1 pl-5 …">
src/views/NitaqatPanel.tsx:203  <div className="surface … border-l-4 border-amber-400 p-4 …">
```

Neither branch's own suite sees this: `rtl-logical-properties.spec.ts` is a wave6 file and these
lines are ksa-only, and git merged the two files cleanly because the streams touched different
regions. Merged as-is it is two ratchet failures — and, behind the ratchet, a real defect: under
`dir=rtl` the amber warning card's accent border lands on the wrong edge and the numbered list
indents away from its own text, in the Arabic Saudization panel.

Converted to the logical equivalents the ratchet's own failure message prescribes: `pl-5` → `ps-5`,
`border-l-4` → `border-s-4`. Nobody's allow-list was touched and the pin stays at 9.

### 5b. `KsaNitaqatTests.GccNationals_AreCountedConservativelyAsDenominatorOnly` is now stale — LEFT

This test asserts GCC nationals sit in the denominator only, and its comment argues that the
conservative default is the safe direction. `feat/ksa-compliance-truth` reversed exactly that
policy in the seeder (MD 61706 clause Twenty-two: GCC nationals are dealt with as Saudis). The
test still **passes**, because it builds its own reference fixture (`KsaNitaqatTests.cs:893` seeds
`GCC_STANDARD … 0m, 1m, 10, false` by hand) rather than running the seeder.

So this is not a failure — it is a test whose name, comment and fixture now document a policy the
product no longer ships, and which will therefore go on passing while asserting the opposite of
the seeded behaviour. I have deliberately **not** touched it: it belongs to the Nitaqat stream, the
fix is a judgement about which regime the fixture should encode, and editing another stream's
assertion to align it with this merge is precisely the move the brief warns against. Flagged for
the `feat/ksa-compliance-truth` author.

### 5c. Checked and clear

- Every `GosiCalculationService.Calculate` call site now passes bounds (compile-enforced; both
  projects build with 0 errors, 0 warnings in `Zayra.Api`).
- `SeedWeightRulesAsync`'s supersede writes through `ScopedBypass.NullableTenantWide`, which is a
  **tracking** query, so `prior.EffectiveTo = …` actually persists; and a supersede is only ever
  reached on a path that also increments `added`, so `SeedAsync`'s `if (added > 0) SaveChangesAsync`
  cannot silently drop it. (This is the "compiles, merges clean, writes nothing" shape; it is fine.)
- `Seeder_ShipsNoThresholdsForRealActivities` requires every real activity's note to contain
  "NO BAND THRESHOLDS" — `ActivitySource` still does after condensing (339 chars).
- `PayrollController` was changed by both streams in different methods (EOSB reason vocabulary vs
  WPS conformance disclosure); independent, no interaction.
- `SaudiComplianceConfig.tsx` was changed by both (RTL logical properties vs a curve-loader link);
  independent regions, and the merged file introduces no physical direction class.
- The MHRSD worked example reproduces from the seeded constants: `ln(400) = 5.99146`, and
  `1.68·ln(400)+12.08 = 22.15`, `1.87·…+18.87 = 30.07`, `2.08·…+22.47 = 34.93`,
  `2.08·…+28.37 = 40.83`. 35.00% sits in High Green for 2023 and, against the C-2024 intercepts
  (27.15 / 35.07 / 37.93 / 45.33), in Low Green for 2024.

---

## 6. Evidence

### Baselines — confirmed here, not quoted

```
integration/wave6      @ 6d7905f   launched 19:46:05 at load 17.69
  Passed!  - Failed: 0, Passed: 2446, Skipped: 0, Total: 2446, Duration: 1 m 54 s

feat/ksa-compliance-truth @ 3cc32ce launched 19:55:33 at load 16.12
  Passed!  - Failed: 0, Passed: 2452, Skipped: 0, Total: 2452, Duration: 1 m 39 s
```

`2452` matches what the branch reported, and `2411 (develop) + 41 = 2452` / `2411 + 35 = 2446`
both hold.

### Merged full suite

```
=== LAUNCH   Sun Sep 20 19:38:55 EDT 2026 — load at start: 13.99 23.46 34.96 ===
    no testhost / vstest.console process at start (rows read, not just counted)
Total tests: 2487
     Passed: 2487
     Failed: 0
    Skipped: 0
=== FINISHED Sun Sep 20 19:40:39 EDT 2026 — load at end:   11.21 20.26 32.41 ===
dotnet test exit=0
```

Never `--no-build`; every run above built first (`Build succeeded. 0 Warning(s) 0 Error(s)` for
`Zayra.Api`).

**A note on the process check.** The first attempt at this never launched: the poll's
`ps ax | grep -E 'testhost|vstest.console'` matched *its own wrapper shell*, whose argv contained
the pattern, and reported 2 phantom test processes while the load bar was already satisfied. I read
the actual rows, saw `31132 /bin/zsh -c … export VERCEL_PLUGIN…` — my own poller — and rewrote the
check into a script file that assembles the pattern at runtime (`PAT="test""host|…"`) so no
process's argv ever contains it, then excludes the poller's own pid/ppid and any shell wrapper.
Rows are printed whenever the count is non-zero, so a future reader can see what blocked a launch.

### Reconciliation by name — every difference, both directions

| | count |
|---|---|
| `integration/wave6` | 2446 |
| `feat/ksa-compliance-truth` | 2452 |
| **merged** | **2487** |

`2446 + 41 = 2487` and `2452 + 35 = 2487`.

**In merged, not in `integration/wave6` — 41, all from the ksa branch:**
40 in `KsaComplianceTruthTests` (32 `[Fact]` + 8 rows of the
`NitaqatCurve_ReproducesTheMinistrysPublishedArithmetic` `[Theory]`), plus
`StatutoryRateStoreTests.GosiReadiness_CapsAtTheSameCeilingSourceThePayslipUses`.

**In merged, not in `feat/ksa-compliance-truth` — 35, all from wave6:**

| class | tests | stream |
|---|---|---|
| `MoneyFigureDefectTests` | 18 | `fix/money-figures` |
| `RequisitionApprovalConvergenceTests` | 9 | `feat/demo-reachability` |
| `NitaqatSeedFitsColumnTests` | 3 | `test/demo-verification` |
| `GosiTests` | 3 | `fix/money-figures` (the ceiling ratchets) |
| `KsaStatutoryLeaveAndHoursTests` | 2 | `fix/money-figures` |

**In `integration/wave6` and not in merged: none.**
**In `feat/ksa-compliance-truth` and not in merged: none.**

Discovery count equals executed count on all three runs and `Skipped: 0` throughout, so no test was
silently dropped by a filter or a build.

### The specific assertions each stream cared about

```
Nitaqat catalogue seeds, and every string fits its column
  Passed NitaqatSeedFitsColumnTests.EveryWeightRuleSourceNote_FitsTheSourceNoteColumn
  Passed NitaqatSeedFitsColumnTests.EverySeededSizeTierAndActivityName_FitsItsColumn
  Passed NitaqatSeedFitsColumnTests.SharedSourceNoteConstants_FitTheNarrowestSourceNoteColumn
  Passed KsaNitaqatTests.Seeder_ShipsNoThresholdsForRealActivities_SoTheyMustRefuse
  Passed KsaNitaqatTests.Seeder_IsIdempotent
  Passed KsaNitaqatTests.Seeder_TierBoundariesHaveNoGapsOrOverlaps

MHRSD's Manufacturing worked example — 400 workers, 35.00%
  Passed KsaComplianceTruthTests.NitaqatCurve_ReproducesTheMinistrysPublishedArithmetic  (×8:
         22.15 / 30.07 / 34.93 / 40.83 for C-2023 and 27.15 / 35.07 / 37.93 / 45.33 for C-2024)
  Passed KsaComplianceTruthTests.NitaqatCurve_LandsTheMinistrysExampleEntityInTheBandTheMinistrySays
  Passed KsaComplianceTruthTests.WorkedExample_ManufacturingCurve_EndToEnd_MatchesTheMinistry
         (High Green in 2023, floor 34.93, next band up Platinum at 40.83)
  Passed KsaComplianceTruthTests.WorkedExample_RealActivity_RefusesBeforeTheGridIsLoaded_AndBandsAfter
  Passed KsaComplianceTruthTests.WhenBandedOffTheObsoleteGrid_TheStandingSaysSo
  Passed KsaComplianceTruthTests.SeededCurveConstants_ExpireWhenTheMinistryReissuedTheAnnex

GOSI: 4,387.50, never 5,850.00
  Passed KsaComplianceTruthTests.ReadinessReport_CapsAtTheSameCeilingThePayslipUses
  Passed KsaComplianceTruthTests.GosiCoveredWageCeiling_IsOneNumberFromOnePlace
  Passed KsaComplianceTruthTests.CoveredWageBase_DelegatesToTheOneSalaryBreakdownDefinition
  Passed GosiTests.Calculate_SixtyThousandWage_EmployeeTotalIsCeilingBased
  Passed GosiTests.Calculate_PlatformDefaultRuleWithNoCapColumns_StillCapsAtTheStatutoryCeiling
  Passed GosiTests.Calculate_WageCap_Applied
  Passed StatutoryRateStoreTests.GosiReadinessAndPreview_PassTheCoveredWageNotBasicAlone   (the ratchet)
  Passed StatutoryRateStoreTests.GosiReadiness_CapsAtTheSameCeilingSourceThePayslipUses
```

### Frontend gates

```
npx tsc --noEmit                         exit 0, no output
npx next build                           succeeded; full route table emitted, /saudi-compliance
                                         builds at 20.8 kB / 147 kB First Load JS
npx playwright test --config=e2e/playwright.browserless.config.ts
  12 passed (2.1s)
  ✓ the lint actually sees the frontend
  ✓ no physical direction utility outside the documented allow-list
  ✓ the pinned exception count may only go down          (still 9)
  ✓ divide-x is paired with rtl:divide-x-reverse
  ✓ off-canvas drawers carry an RTL counterpart for their transform
  ✓ the RTL shell contract is in place
```

### Migration gate

The two-step form, from the repo root, with the project named explicitly (the bare form reports
"No project was found"):

```
$ dotnet tool restore --tool-manifest dotnet-tools.json
Tool 'dotnet-ef' (version '8.0.11') was restored.
Restore was successful.

$ dotnet tool run dotnet-ef migrations has-pending-model-changes \
      --project backend-dotnet/Zayra.Api/Zayra.Api.csproj
Build succeeded.
No changes have been made to the model since the last migration.
```

### Zero deletions, and `mobile/` untouched

```
$ git diff --diff-filter=D --name-only integration/wave6 integration/wave6-ksa
(no output)

$ git rev-parse integration/wave6-ksa:mobile
f1f03528a9f9cf096b722f65dba1c1ff2a9dd565
$ git rev-parse integration/wave6:mobile
f1f03528a9f9cf096b722f65dba1c1ff2a9dd565
```

---

## 7. Deliberately left

- **5b** above: `GccNationals_AreCountedConservativelyAsDenominatorOnly`.
- **§4** above: the bare third needle in the covered-wage lint.
- The Manufacturing curve constants remain end-dated `2026-01-01` and only Manufacturing is seeded.
  Today is 2026-09-20, so an establishment on `MANUFACTURING` is **refused** a band with a pointer
  to the January 2026 annex rather than being answered from a 2024 constant. That is the ksa
  stream's deliberate design and I have not changed it — but it does mean the Saudization screen
  refuses for every real activity on a default installation, and only the self-describing
  `GENERAL_UNVERIFIED` activity computes a band. Anyone demoing the Nitaqat panel needs to know
  that before the demo, not during it.
- `GosiContributionRule.Min/MaxContributoryWage` remain as columns; they are simply no longer read.
  Dropping them is a migration and belongs to whoever owns that table.
