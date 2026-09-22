# Nitaqat: loading the MHRSD Mutawar curve

**Branch** `feat/nitaqat-mhrsd-grid` · **Date** 2026-09-21 · **Base** `develop`

## The problem, as found

Saudization was non-functional for every customer, and had been since 2026-01-01.
Verified directly against **production Postgres** (read-only) on 2026-09-21:

```
nitaqat_activities                        13   (all platform, TenantId = null)
nitaqat_band_thresholds                   36   (all GENERAL_UNVERIFIED — "illustrative, not an MHRSD activity")
statutory_rules nitaqat.curve.%           12   (all MANUFACTURING, all effective_to = 2026-01-01)
  ... of which in force on 2026-09-21:     0
```

Per-activity threshold counts in production:

```
GENERAL_UNVERIFIED  36
ADMIN_SUPPORT  CONSTRUCTION  EDUCATION  FINANCE  HEALTHCARE  HOSPITALITY
ICT  MANUFACTURING  PROF_SERVICES  RETAIL  TRANSPORT  WHOLESALE          all 0
```

So every real activity refused with `nitaqat_thresholds_not_published`, and Manufacturing —
the one activity that ever worked — had expired nine months earlier. The engine was fine; it
was starved.

## What was loaded

**41 MHRSD economic activities, from published coefficients. Nothing was fitted or derived.**

The brief allowed for deriving `m` and `c` by fitting a logarithm to band floors at sample
sizes. That turned out to be unnecessary: Annex (1) of the 2026 guideline publishes `m` and `c`
**directly**, one gradient per activity+band and one intercept per activity+band+year for 2026,
2027 and 2028. Every number loaded is a cell of that table. **There is no derivation in this
change, and therefore no residuals to report.**

## Sources

| Document | URL | sha256 | Retrieved | Role |
|---|---|---|---|---|
| MHRSD, الدليل الإجرائي – برنامج نطاقات المطور 2026 (Arabic, 16pp) | `hrsd.gov.sa/sites/default/files/2026-03/ntaqat-almtwr.pdf` | `8ecb78d8…27d32e11` | 2026-09-21 | **Primary.** Annex (1), pp. 9–15 — every constant loaded |
| MHRSD Nitaqat Program Procedural Guideline (English edition of Ministerial Decision 182495, 22pp) | `hrsd.gov.sa/sites/default/files/2023-06/E20210523.pdf` | `fe8a0fca…7c908a55` | 2026-09-20 / re-read 2026-09-21 | Method, the abolition wording, the worked example, and an **independent cross-check** annex |

Both were downloaded and text-extracted locally (PyMuPDF). No summary of either was relied on.

### The regime, in the Ministry's words

From the English guideline, verbatim (p.6, item 3; p.8):

> "Removing fixed size bands and moving to a smooth relationship between worker count and required Saudization"

> "Cancel the use of Saudization Rates according to fixed size bands and apply a calculation based on the number of employees in the entity using fixed values for its economic activity"

> `y = m * ln(x) + c` … "'c' is the y-axis intercept of the curve and differs by economic activity and year (Annex. No. 1). **The third-year value will be used in the third year and beyond.**"

`nitaqat_band_thresholds` — the pre-2021 grid — was **not touched**. It keeps its 36
illustrative rows and remains correct for pre-2021-12-01 periods and as a manual Qiwa override.

## Method, and how the transcription was checked

1. **Extract.** Both PDFs → text with PyMuPDF. The Arabic annex extracts band labels and
   numbers cleanly; numerals are LTR and unaffected by RTL reordering.
2. **Transcribe once.** The annex was typed into a single Python table
   (`scratchpad/nitaqat-src/annex2026.py`), never retyped afterwards.
3. **Machine re-parse diff.** An independent parser walked the extracted text, took the four
   numbers following each band marker, and diffed against the hand table:
   **164 of 164 band rows identical, zero mismatches.**
4. **Cross-document gradient check.** The 2021/2023 English guideline carries its own Annex
   No. (1) for the same programme. Comparing gradient vectors across the two documents:

   ```
   identical m: 39   changed: 1   new activity: 1
     CHANGED  Energy, Water and their Services:
              2026 m=[1.35, 2.60, 3.00, 3.00]  vs  2021 "Electricity and Water Services" m=[1.68, 1.87, 2.08, 6.00]
     NEW      Higher Education for Health Specialisations
   ```

   **39 of 41 gradient vectors are byte-identical across two languages, two layouts and five
   years.** A misread column, a transposed row or a mis-paired activity could not survive that.
   The one change is explicable: the 2021 "Electricity and Water Services" row was a visible
   copy-paste of the metallic-mining row (`1.68/16, 1.87/19, 2.08/28, 6.00/23` — identical), and
   the 2026 reissue corrects it.

   A further corroboration of the Arabic→English pairing: the 2021 row "Construction & Cleaning"
   splits in 2026 into *construction contracting* (re-baselined) and *cleaning contracting &
   laundries*, and the cleaning row carries the 2021 constants **exactly**
   (`-0.37/12.17, -0.37/14.17, 0.00/17.00, 0.00/22.00`). Likewise the old generic
   "Retail & Wholesale" C-2024 column (`24.25 / 28.72 / 32.91 / 40.91`) reappears in 2026 as the
   *fashion, accessories and miscellaneous retail* row.
5. **Ladder validation.** Every activity × every year column × x ∈ {6, 50, 500, 3 000, 20 000}:
   no band crosses another and every floor stays within 0–100%. This is the same gate
   `NitaqatGridImportService.ImportCurveAsync` applies to customer-supplied curves; seeded
   constants would otherwise bypass it, so it is now asserted over the seeded table too.

## Calibration — the Ministry's own worked example

> "Abdullah for Plastics Company is a fictitious entity in the Manufacturing Sector with 400
> employees and its Saudization rate is (35.00%)."

Reproduced **from the seeded constants**, not from literals:

| Column | Low Green | Medium Green | High Green | Platinum | Band at 35.00% |
|---|---|---|---|---|---|
| C-2023 computed | 22.15 | 30.07 | 34.93 | 40.83 | **High Green** |
| C-2023 published | 22.15 | 30.07 | 34.93 | 40.83 | High Green |
| C-2024 computed | 27.15 | 35.07 | 37.93 | 45.33 | **Low Green** |
| C-2024 published | 27.15 | 35.07 | 37.93 | 45.33 | Low Green |

Exact match on all eight floors and both bands. Pinned as
`NitaqatMhrsdAnnexTests.MinistryWorkedExample_Manufacturing400Workers_IsReproduced`.

The 2026 annex gives Manufacturing the **same gradients** (1.68 / 1.87 / 2.08 / 2.08) as the
Ministry's worked example, which is the strongest single piece of evidence that the annex row
was read correctly.

## Was the Manufacturing 2026-01-01 expiry resolved? Yes.

Those rows were end-dated because a January-2026 reissue existed and had not been read. It has
now been read, and it is the document loaded here. So the expiry is **discharged, not extended**:
the 2023/2024 rows keep their `effective_to = 2026-01-01` untouched (history stays explicable)
and the 2026 annex rows pick up at exactly that instant. No row was mutated.

## The activity table

All 41 rows come from **Annex (1) of the 2026 guideline, retrieved 2026-09-21**. Page numbers
are the PDF's own printed pages.

| # | MHRSD activity (Annex (1) name) | Loaded as code | New? | Annex page | Verified? |
|---|---|---|---|---|---|
| 1 | Agriculture & Animal Production, their Services and Equestrian Clubs | `AGRI_ANIMAL_EQUESTRIAN` | new | p.9 | provisional |
| 2 | Hydrocarbons and their Processing | `HYDROCARBONS` | new | p.9 | provisional |
| 3 | Mining of Metallic Minerals and Precious Stones | `MINING_METALLIC` | new | p.9 | provisional |
| 4 | Mining of Non-metallic and Industrial Minerals | `MINING_NONMETALLIC` | new | p.9 | provisional |
| 5 | Building Materials Mining | `MINING_BUILDING_MATERIALS` | new | p.9 | provisional |
| 6 | Energy, Water and their Services | `ENERGY_WATER` | new | p.10 | provisional |
| 7 | Manufacturing | `MANUFACTURING` | existing code | p.10 | **VERIFIED** |
| 8 | Construction and Building Contracting | `CONSTRUCTION` | existing code | p.10 | provisional |
| 9 | Operations & Maintenance | `OPS_MAINTENANCE` | new | p.10 | provisional |
| 10 | Cleaning Contracting and Laundries | `CLEANING_LAUNDRY` | new | p.10 | provisional |
| 11 | General Wholesale and Retail | `RETAIL_GENERAL` + `WHOLESALE` | new | p.10 | provisional |
| 12 | Retail of Perfumes and Watches | `RETAIL_PERFUME_WATCHES` | new | p.11 | provisional |
| 13 | Retail of Fashion, Accessories and Miscellaneous Goods | `RETAIL_FASHION_MISC` | new | p.11 | provisional |
| 14 | Ladies Goods, Sales and Repair of Mobiles | `RETAIL_LADIES_MOBILE` | new | p.11 | provisional |
| 15 | Communication Solutions | `TELECOM_SOLUTIONS` | new | p.11 | provisional |
| 16 | Post Sector | `POST` | new | p.11 | provisional |
| 17 | IT Infrastructure | `IT_INFRASTRUCTURE` | new | p.11 | provisional |
| 18 | Communication Infrastructure | `TELECOM_INFRASTRUCTURE` | new | p.12 | provisional |
| 19 | Operations & Maintenance in Communications | `TELECOM_OPS_MAINTENANCE` | new | p.12 | provisional |
| 20 | Operations & Maintenance in IT | `IT_OPS_MAINTENANCE` | new | p.12 | provisional |
| 21 | IT Solutions | `IT_SOLUTIONS` | new | p.12 | provisional |
| 22 | Land Transportation and Storage | `TRANSPORT_LAND_STORAGE` | new | p.12 | provisional |
| 23 | Air and Sea Transportation | `TRANSPORT_AIR_SEA` | new | p.12 | provisional |
| 24 | Restaurants with Service (excluding Fast Food) | `RESTAURANTS_SERVICE` | new | p.13 | provisional |
| 25 | Fast Food and Ice Cream | `FAST_FOOD_ICECREAM` | new | p.13 | provisional |
| 26 | Coffee and Drinks | `COFFEE_DRINKS` | new | p.13 | provisional |
| 27 | Catering | `CATERING` | new | p.13 | provisional |
| 28 | Employment, Recruitment and Security Services | `SECURITY_RECRUITMENT` | new | p.13 | provisional |
| 29 | Finance | `FINANCE` | existing code | p.13 | provisional |
| 30 | Business Services | `BUSINESS_SERVICES` | new | p.14 | provisional |
| 31 | Social Services | `SOCIAL_SERVICES` | new | p.14 | provisional |
| 32 | Personal Services | `PERSONAL_SERVICES` | new | p.14 | provisional |
| 33 | Higher Education Providers | `HIGHER_EDUCATION` | new | p.14 | provisional |
| 34 | Higher Education for Health Specialisations | `HIGHER_EDUCATION_HEALTH` | new | p.14 | provisional |
| 35 | Girls Schools, Kindergartens, Babysitting | `SCHOOLS_GIRLS_KG` | new | p.14 | provisional |
| 36 | International Schools | `SCHOOLS_INTERNATIONAL` | new | p.15 | provisional |
| 37 | Medical Labs and Health Services | `MEDICAL_LABS_HEALTH` | new | p.15 | provisional |
| 38 | Accommodation, Leisure, Tourism | `ACCOMMODATION_LEISURE_TOURISM` | new | p.15 | provisional |
| 39 | Basic Commodities and Fuel | `BASIC_COMMODITIES_FUEL` | new | p.15 | provisional |
| 40 | Boys Schools, Boys and Girls School Complexes | `SCHOOLS_BOYS_COMPLEX` | new | p.15 | provisional |
| 41 | Combined Entities | `COMBINED_ENTITIES` | new | p.15 | provisional |

`WHOLESALE` and `RETAIL_GENERAL` are two catalogue names for **one** annex row
("البيع بالجملة والتجزئة العامة" — General Wholesale and Retail); the annex publishes no
wholesale-only curve. Their constants are asserted identical by
`SeededCurveConstants_FromThe2026Annex_AgreeWhereverAnnexRowsAreShared`.

## The activities I could NOT source, and why

Every one of these is a **taxonomy** failure, not a data failure. The product's own twelve
coarse activity codes are not MHRSD's taxonomy. Three map one-to-one and were loaded. The other
eight each span several annex rows whose floors differ far too much to pick one.

| Coarse code | Candidate annex rows | Low Green spread (C-2026) | Decision |
|---|---|---|---|
| `RETAIL` "Retail Trade" | General W&R; Perfumes & Watches; Fashion & Misc; Ladies Goods & Mobiles | **23.25 → 82.00** | **Not loaded** |
| `ICT` "Communications & IT" | Telecom Solutions; IT Solutions; IT Infra; Telecom Infra; Telecom O&M; IT O&M | 15.96 → 27.76 | **Not loaded** |
| `HEALTHCARE` "Health & Social Work" | Medical Labs & Health Services; Social Services | 14.82 → 25.74 | **Not loaded** |
| `EDUCATION` | Higher Ed; Higher Ed (Health); Girls Schools/KG; International Schools; Boys Schools | **4.95 → 51.00** | **Not loaded** |
| `HOSPITALITY` "Hotels & Food Service" | Accommodation/Leisure/Tourism; Restaurants; Fast Food; Coffee & Drinks; Catering | 13.47 → 24.60 | **Not loaded** |
| `TRANSPORT` "Transport & Storage" | Land Transportation & Storage; Air and Sea Transportation | 12.09 → 26.57 | **Not loaded** |
| `PROF_SERVICES` "Professional & Technical" | Business Services; Personal Services | 14.07 → 33.78 | **Not loaded** |
| `ADMIN_SUPPORT` "Administrative & Support" | Business Services; Employment/Recruitment/Security; Cleaning & Laundries | **12.17 → 74.50** | **Not loaded** |

The concrete harm avoided: a mobile-phone retailer picking "Retail Trade" and being handed the
*general* retail curve would be told they are **Low Green at 25%** when the Ministry requires
**82%** — Red, and barred from renewing a single work permit. The refusal is the correct answer,
and it is now escapable, because the specific MHRSD activity is in the dropdown next to it.

Pinned as `NitaqatMhrsdAnnexTests.CoarseActivitiesSpanningSeveralAnnexRows_StillRefuse`.

Also **not loaded**: nothing from Qiwa. The annex was sufficient and is the primary source; a
secondary restatement would have added risk without adding coverage.

## Verified vs provisional

**Verified = 1 activity. Provisional = 41.**

"Verified" is given exactly one meaning: MHRSD published a worked example for that activity and
this code reproduces the Ministry's stated answer. That is true of **Manufacturing** and nothing
else. Every other curve is published-but-unreproduced, and says so.

`StatutoryRule` has no `IsVerified` column, and `NitaqatCurve.ResolveAsync` previously hardcoded
`IsVerified: true` — so loading 41 provisional curves would have labelled all of them verified on
screen. The flag is now real data: a `nitaqat.curve.{ACTIVITY}.verified` rule sits beside the
constants, `ResolveAsync` reads it, and **an absent flag means unverified**. The tenant import
path writes it too, so both loaders agree.

Pinned as `EveryActivityButManufacturing_ReportsItsBandAsProvisional` and
`AVerificationFlagThatIsMissing_MeansProvisional`.

## Fail before / pass after

**Before** (`Before_TheAnnexWasLoaded_EveryActivityRefused`) — against the pre-change rule set,
on 2026-09-21:

```
MANUFACTURING  -> ResolveAsync returns null   (2024 constants expired 2026-01-01)
CONSTRUCTION, RETAIL, WHOLESALE, ICT, FINANCE, HEALTHCARE, EDUCATION,
HOSPITALITY, TRANSPORT, PROF_SERVICES, ADMIN_SUPPORT   -> all null
```

**After** (`After_TheAnnexIsLoaded_TheSameActivitiesBand`) — same date, same reader shape:

```
MANUFACTURING, CONSTRUCTION, FINANCE, WHOLESALE  -> four ascending floors each
```

## Worked examples, one per loaded activity (C-2026 column)

Each computed independently of the seeder from the annex cell and `y = m·ln(x) + c`, then
asserted. `NitaqatMhrsdAnnexTests.WorkedExample_PerLoadedActivity_OnTheC2026Column`:

| Activity | x | Saudi % | Low Green | Medium | High | Platinum | Band |
|---|---|---|---|---|---|---|---|
| MANUFACTURING | 400 | 35.00 | 25.15 | 33.07 | 36.43 | 42.33 | Medium Green |
| CONSTRUCTION | 400 | 20.00 | 11.95 | 13.95 | 17.50 | 22.50 | High Green |
| FINANCE | 400 | 60.00 | 65.58 | 72.58 | 77.58 | 80.58 | **Red** |
| WHOLESALE | 400 | 40.00 | 38.05 | 42.52 | 46.41 | 55.93 | Low Green |
| RETAIL_GENERAL | 400 | 40.00 | 38.05 | 42.52 | 46.41 | 55.93 | Low Green |
| RETAIL_LADIES_MOBILE | 400 | 40.00 | 82.00 | 85.00 | 89.00 | 95.04 | **Red** |
| IT_SOLUTIONS | 100 | 45.00 | 36.85 | 43.32 | 53.42 | 62.98 | Medium Green |
| TRANSPORT_LAND_STORAGE | 50 | 15.00 | 16.59 | 20.70 | 23.69 | 34.43 | **Red** |
| ACCOMMODATION_LEISURE_TOURISM | 250 | 50.00 | 37.96 | 44.38 | 50.70 | 56.82 | Medium Green |
| MEDICAL_LABS_HEALTH | 600 | 30.00 | 27.98 | 32.98 | 36.48 | 36.98 | Low Green |
| HIGHER_EDUCATION | 1000 | 80.00 | 34.00 | 48.00 | 78.34 | 84.97 | High Green |
| COMBINED_ENTITIES | 12 | 25.00 | 16.53 | 27.94 | 39.35 | 49.54 | Low Green |

Worked by hand for `MANUFACTURING`: `ln(400) = 5.99146…`; Low Green
`1.68 × 5.99146 + 15.08 = 25.1457 → 25.15`; Medium `1.87 × 5.99146 + 21.87 = 33.0740 → 33.07`;
High `2.08 × 5.99146 + 23.97 = 36.4323 → 36.43`; Platinum `2.08 × 5.99146 + 29.87 = 42.3323 → 42.33`.
At 35.00%: `33.07 ≤ 35.00 < 36.43` → **Medium Green**.

## The Postgres NULL trap — closed for this path

A past defect: Nitaqat platform rows (`TenantId = null`) were invisible in production while
every unit test passed, because the in-memory provider treats `x.TenantId == null` as ordinary
LINQ and PostgreSQL does not (`col = NULL` is never true).

`NitaqatPlatformScopeSqlTests` guards that for the Nitaqat **reference tables**, read through
`ScopedBypass.NullableTenantWide`. **The curve constants do not use that path** — they live in
`statutory_rules` and are read through `StatutoryRuleReader`, a different query with its own
null handling. So this got its own proof, against a real database.

Demonstrated first against **production Postgres** (read-only), on the rows already there:

```sql
-- correct shape: as a tenant, falling back to platform
... AND (tenant_id = '…0001' OR tenant_id IS NULL)     -> 2 rows  (m = 1.68, c = 12.08)
-- the bug shape
... AND (tenant_id = '…0001' OR tenant_id =  NULL)     -> 0 rows
```

Then pinned end-to-end in `NitaqatCurvePlatformScopePostgresTests` (Testcontainers Postgres 16,
the real `StatutoryRuleReader`, production provider options):

- `SeededPlatformCurves_AreVisibleToATenant_OnRealPostgres` — seeds the platform defaults, reads
  as a **non-null tenant**, gets Manufacturing's C-2026 floors 25.15 / 33.07 / 36.43 / 42.33.
- `PlatformScopeRead_AndTenantOverride_BothResolveCorrectly_OnRealPostgres` — platform scope sees
  1.68, the overriding tenant sees its 9.99, an unrelated tenant sees 1.68 and never the override.
- `SeedingTwice_AddsNothing_OnRealPostgres` — the unique index does **not** protect platform rows
  from each other (Postgres treats NULL as distinct), so idempotency is asserted against a real DB.

## What changed in the repo

| File | Change |
|---|---|
| `Infrastructure/Seed/StatutoryRuleSeeder.cs` | The 2026 annex: 42 activity curve blocks, `m` + `c`×3 years + a verification flag. Existence check batched into one query (it was one round-trip per rule; ~700 new rules would have meant ~700 sequential queries on every boot). |
| `Infrastructure/Seed/NitaqatReferenceSeeder.cs` | 38 new platform activity rows (Ministry's Arabic names, our codes/English). No new thresholds. |
| `Infrastructure/Compliance/NitaqatCurve.cs` | `VerifiedKey`; `ResolveAsync` reads it instead of hardcoding `IsVerified: true`; absent ⇒ unverified. |
| `Infrastructure/Compliance/NitaqatGridImportService.cs` | The tenant curve importer writes the same flag (9 rows, not 8). |
| `Zayra.Api.Tests/NitaqatMhrsdAnnexTests.cs` | **New.** Fail-before/pass-after, the Ministry example, per-activity worked examples, provisional labelling, catalogue integrity, column fit. |
| `Zayra.Api.Tests/NitaqatCurvePlatformScopePostgresTests.cs` | **New.** Real-Postgres platform-scope proof. |
| `Zayra.Api.Tests/KsaComplianceTruthTests.cs` | See below. |

**No migration.** `dotnet-ef migrations has-pending-model-changes` → *"No changes have been made
to the model since the last migration."* This is data only.

### The test I amended, and why it is not weakened

`SeededCurveConstants_ExpireWhenTheMinistryReissuedTheAnnex` asserted that every seeded curve
constant expires by 2026-01-01 and that Manufacturing is the only activity present. Its purpose
was: *"an un-expiring constant would answer a 2026 question with a 2024 number."*

That purpose is intact and its assertions are **unchanged** — they are now scoped to the rows
they were written about (`EffectiveFrom < 2026-01-01`), which still must stop dead at the
reissue. What changed is that the reissue has now been read, so rows exist on the far side of it.

The guarantees were then **extended**, not traded away — four new tests:

- `EverySeededCurveConstant_CarriesItsSourceAndReadDate` — provenance for **all** vintages, and
  every non-Manufacturing row must literally say `UNVERIFIED`.
- `SeededCurveConstants_FromThe2026Annex_AreCompleteAndContinuous` — no half-loaded activity, and
  intercept windows must **tile with no hole**; a gap would silently stop banding a working
  customer on 1 January with no deploy.
- `SeededCurveConstants_FromThe2026Annex_NeverCrossAndStayPercentages` — the import service's own
  anti-transcription-error gate, applied to seeded data at the same five workforce sizes.
- `SeededCurveConstants_FromThe2026Annex_AgreeWhereverAnnexRowsAreShared`.

`Seeder_ShipsNoThresholdsForRealActivities_SoTheyMustRefuse` — the "fails if anyone invents
numbers" test — **was not touched and still passes**. It governs `nitaqat_band_thresholds`, and
no threshold row was added; all 38 new activities carry `NO BAND THRESHOLDS` on the row itself.

## What still needs a human

Before `IsVerified` can be flipped on any of the 41 provisional activities, a Saudi HR
practitioner or counsel must confirm:

1. **The activity mapping — the highest-risk item.** The numbers are the Ministry's; the
   assignment of an MHRSD annex row to a customer's establishment is a judgement. An
   establishment's true activity is the one on its **Qiwa establishment account**, and only the
   customer can read that. In particular confirm the three one-to-one mappings onto pre-existing
   codes: `MANUFACTURING` ← الصناعات, `CONSTRUCTION` ← مقاولات التشييد والبناء,
   `FINANCE` ← المؤسسات المالية (does "Finance & Insurance" really sit under the single
   *Financial Institutions* row, or are insurers classified elsewhere?).
2. **Whether the 2026 annex is still the current one.** It is dated Feb 2026 in its PDF metadata
   and was live on hrsd.gov.sa on 2026-09-21. MHRSD reissues; confirm no later annex exists.
3. **The English labels.** 33 are the Ministry's own English names from the 2021 annex. Eight are
   my translation of an Arabic row with no English counterpart — flagged `TRANSLATED` in
   `scratchpad/nitaqat-src/annex2026.py`. The Arabic (`NameAr`) is the Ministry's throughout, and
   is what a Saudi user will recognise; the English is a convenience label only.
4. **`x` — what counts toward "total workforce".** Unchanged by this work and still open.
   Decision 61706 clause Twenty-one settles special categories (weight 1 for volume). It does
   **not** settle part-time: clause Eighth halves a part-timer in the *percentage*, and nothing
   obtained says whether they are 1 or 0.5 of the **volume**. The caller passes the weighted
   denominator (0.5). Because `m > 0` for almost every activity, understating `x` understates the
   floor — the **optimistic** direction. This matters before any establishment with a material
   part-time population relies on a band.
5. **Three activities with a gradient that only makes sense above ~6 workers.** Metallic mining,
   non-metallic mining and building-materials mining publish Platinum `m = 6.00` against High
   Green `m ≈ 2.08`. The ladder is sound from 6 workers up (checked) but inverts below ~3. MHRSD's
   own calculator starts at 6; confirm the treatment of establishments smaller than that.
6. **`Energy, Water and their Services`** — the one activity whose gradients changed across the
   reissue, because the 2021 row was an apparent copy-paste. Worth a second pair of eyes on p.10.
7. **Whether an unverified band should be shown at all.** Product decision, not a data one. The
   flag now carries honestly to the surface; someone should confirm the UI wording is strong
   enough for a number that drives work-visa decisions.
