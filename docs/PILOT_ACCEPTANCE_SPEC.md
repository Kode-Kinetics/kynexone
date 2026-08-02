# Pilot Acceptance Specification — "One Unbreakable Payroll Month"

_Definition of Done for the first design-partner pilot. Executive panel (payroll-finance SME + CIO/controls + CFO-buyer) → CTO consolidation. Code-verified. 2026-08-02. Region target: KSA-first, GCC-ready._

Both cross-lens contradictions are now resolved against ground truth. I have what I need. Here is the consolidated specification.

---

# PILOT ACCEPTANCE SPECIFICATION — "One Unbreakable Payroll Month"
## Definition of Done · KynexOne / Zayra · First Design-Partner Pilot
**Owner:** CTO (author) · **Signatories:** Group CFO + Group-HR Sponsor + Zayra Controls Lead
**Design partner profile:** multi-entity group, ~200–5,000 employees, 2–8 legal entities, KSA and/or UAE
**Codebase of record:** `backend-dotnet/Zayra.Api/` at commit `ef9acd5`

### CTO note on how this consolidates the three review lenses
Two status calls were in dispute across the reviews; I resolved both by reading the code directly, and the CFO-facing status below reflects the ground truth, not the optimistic lens:
- **GOSI/statutory reconciliation (criterion 3.5): GAP, not PASS.** The run writes statutory deductions with `Source="Statutory"` and codes `GOSI-ANN-EE / GOSI-SANED-EE / GOSI-ANN-ER` (`PayComponentEngine.cs:160`, `PayrollController.cs:975-978`, `KsaCalculators.cs:58-62`). Both reconciliation endpoints — `contribution-summary` and `variance-report` — filter on `d.Source == "GOSI"` and codes `GOSI_ANNUITIES_EMP / _ER / GOSI_EMPLOYEE` (`GosiController.cs:255, 260-265, 325, 344`). **Neither the Source nor the code format matches.** `contribution-summary` therefore returns `totalGosi = 0` for every real run, and `variance-report` flags every employee with a variance equal to their entire contribution. The reconciliation artifact a CFO would rely on does not reconcile. One review scored this PASS purely because the endpoint and the 0.01 tolerance exist; that is a false positive.
- **EOSB / final settlement (criteria 1.9, 1.10): GAP.** Two divergent engines exist. The authoritative pack calculator `KsaEndOfServiceCalculator` (`KsaCalculators.cs:94+`) computes `serviceYears = fullMonths/12 + remDays/365` and applies the Art. 84 resignation discount. The inline `FinalSettlement` (`PayrollController.cs:~3200-3222`) uses a **different** formula (`basic × 1/3 × years` ≤5yr; `2/3` 5-10yr; `1×` >10yr), `totalYears = Days/365.0`, and **no resignation discount**. Worked example (3 yrs, 10,000 basic, termination): pack ≈ `0.5 × 3 × 10,000 = 15,000`; inline = `(1/3) × 3 × 10,000 = 10,000` (~33% low). The inline path is also calculator-only — it returns JSON + one audit line and posts **no payable, no GL, no payment**.

**Status legend:** **MET** = code satisfies it today (only pilot-month evidence needed) · **PARTIAL** = mechanism exists but a defined hole must close · **GAP** = must be built/proven before sign-off.

---

## 1. ACCEPTANCE STATEMENT (the bar this document certifies against — via evidence, never a label)

> The pilot is **accepted** when a multi-entity CFO can run **one complete payroll month** — supporting documents, approvals, bank output, GL posting, audit trail, and recovery — for their real entities and employees, and every criterion 1.x–7.x below is at **MET**, each backed by its named **evidence artifact for that month** (a file a bank or ministry accepted, a total that reconciles to the register to the halala, a restore drill completed inside the committed RTO, a run that survived a mid-processing restart), with **no spreadsheet used and no manual DB or GL correction made** at any step. No capability is accepted on the word "certified" or on a green screen; each is accepted only on the artifact it produces. Any PARTIAL/GAP row is an open condition and may not be waived without an initialed exception on the sign-off block.

---

## 2. ACCEPTANCE CRITERIA (measurable · evidence-backed · grouped for domain sign-off)

### Group 1 — Payroll correctness (the "no manual workaround" battery)

| # | Criterion | Measurable pass condition | Evidence artifact required | Status |
|---|---|---|---|---|
| 1.1 | Run lifecycle + atomic processing | Draft→Processed→PendingFinanceReview→Approved→Locked→Batch→WPS→Paid; every illegal transition refused; a mid-`Process` kill leaves **zero** partial payslips, unchanged loan `OutstandingBalance`, unchanged run status; run re-runs to completion | Screen recording of full lifecycle for one entity + before/after DB snapshot across an induced mid-run kill | **MET** (`PayrollController.cs` Process 536, single execution-strategy tx 768-1147; reprocess guard 542) |
| 1.2 | Maker-checker separation of duties | Processor cannot approve (403); Finance approval is a distinct user; two `PayrollApproval` rows with different `DecidedByUserId` | 403 self-approve response + the two-user approval trail | **MET** — *sign-off precondition: confirm the pilot tenant has ≥2 distinct approver identities; a single-admin tenant defeats SoD* (`Approve` 1371) |
| 1.3 | Component pay math deterministic + reconciles | Same inputs → identical gross/deduction/net across reruns; golden-master + engine-equivalence tests green in CI; Σ(slip totals) == run header or `TOTALS_*_MISMATCH` blocks approval | Green `PayComponentGoldenMasterTests` / `PayComponentEngineTests` CI log + the run's totals-validation report | **MET** (`PayComponentEngine.cs`; round-then-sum; validation rules) |
| 1.4 | Net-pay floor + block-on-error gate | Net can never be negative (floored to 0 with `NEGATIVE_NET`/`ZERO_NET` flag); any Error-severity validation result blocks Approve **and** Lock (422) | The floored-net test case + a 422 `validation_errors` blocking list | **MET** (net floor 899; lock gate 1160-1172; approve gate 1356-1367) |
| 1.5 | Mid-month joiner pro-rata (no spreadsheet) | Employee hired on day 12 is paid `(worked days / period days) × package` automatically | Joiner's payslip = pro-rata, produced with no manual edit | **GAP** — `Process` pays the **full** package to every Active employee (`basic = latest assignment ≤ periodEnd`, 806-811); no `JoiningDate`-based proration. Only attendance/LOP reduces pay, and a joiner is not "absent" |
| 1.6 | Backdated increment / retro-arrears | A raise effective in a prior locked month generates an arrears line = Σ(new−old) for the intervening months in the current run | Current run showing the computed arrears line | **GAP** — effective-dated assignments read "latest ≤ periodEnd" (806); new rate applies forward only; retro requires a manual `PayrollAdjustment` |
| 1.7 | Off-cycle / supplementary run | A second payment in the same month (missed joiner, correction, bonus-only) runs alongside the locked regular run without voiding it | An off-cycle run posting its own slips + GL + WPS | **GAP** — one-run-per-(company,month) hard-enforced (409 at 515-519); `PayrollRun` has no run-type/off-cycle field |
| 1.8 | LOP / unpaid absence | Unpaid days deduct at the configured day-rate; GOSI-base treatment of LOP is correct | Slip `LOP_DEDUCTION` = absent-days × day-rate matching policy + compliance sign-off of the GOSI-base rule | **PARTIAL** — computed (826-833) but `basic/30` divisor, 480-min day, and "LOP does not reduce GOSI covered wage" are `[FLAG-COMPLIANCE-KSA]` (823-825, 739-745); needs written compliance sign-off |
| 1.9 | Termination EOSB — single authoritative engine | Same employee → identical EOSB from every endpoint, matching the jurisdiction pack to the halala | One EOSB figure from all surfaces == a reviewer hand-calc | **GAP** — two divergent formulas (pack vs inline `FinalSettlement` 3200-3222); inline ~33% low for KSA and applies no resignation discount; both use `/365.0` leap-year drift |
| 1.10 | Termination final settlement end-to-end | Pro-rata + EOSB + leave encashment − notice becomes a **payable** that posts to GL and flows to bank output | A termination: settlement → GL journal → WPS/off-cycle payment, no spreadsheet | **GAP** — `FinalSettlement` (3171) is a read-only calculator; nothing persisted/posted/paid |
| 1.11 | Statutory rates confirmed current | GOSI/GPSSA rates + ceiling in use equal the current published circular; a stale-rate guard forces review | A compliance-officer rate-confirmation memo archived to the pilot record | **PARTIAL** — structure correct + 18-month staleness guard exists, but every rate and the SAR 45,000 ceiling are marked `VERIFY` in code (`KsaCalculators.cs:26-71`) |

### Group 2 — Bank / WPS output

| # | Criterion | Measurable pass condition | Evidence artifact required | Status |
|---|---|---|---|---|
| 2.1 | File generated only from a locked, validated run + integrity hash | Export requires Approved/Locked run + passes full WPS validator; file carries a stored SHA-256 | Generated SIF + stored `FileHash` + pre-export validator result | **MET** (export gate 1734; `SifFileGenerator.cs:67,92`) |
| 2.2 | Validation blocks bad master data; no silent drops | Missing/invalid IBAN (ISO 13616 mod-97), missing MOL/national ID, ineligibility blocked with an itemised list; policy-drifted-but-active employee requires explicit acknowledgement and is then **included** | Blocked-export response + the drift-acknowledgement flow | **MET** (`WpsSifValidator.cs`; drift ack 1772-1782) |
| 2.3 | File total ties to register/batch to the minor unit | Σ(file net) == run net == payment-batch total, to the halala/fils | Side-by-side totals: file trailer, register, batch | **MET** (batch total 1648) |
| 2.4 | Idempotent export + versioned resubmission | Duplicate generation blocked (409) unless status = Rejected; resubmission carries a number and links the prior file | `wps-export-history` resubmission chain | **MET** (idempotency 1724; `ResubmissionOfWpsFileBatchId` 1845) |
| 2.5 | **File accepted by the bank / Mudad in a real test submission** | The exact generated file (SHA-256 unchanged) is submitted to the pilot's bank / Mudad (KSA) or WPS agent / MOHRE (UAE) and **accepted**; acceptance reference recorded on the batch | Bank/ministry acknowledgement reference stored on `WPSFileBatch` (`Accepted` + reference), matched to the file hash | **GAP** — mechanism to *record* acceptance exists (`UpdateWpsStatus` 1917-1985, ref mandatory) but the layout is self-labeled (`SIF_SA_V1`, "field widths NOT independently confirmed"; `docs/compliance/saudi-track-a-wps-sif.md`: "pending Mudad acceptance test"). No real submission has been done |
| 2.6 | Payment batch = controlled bulk bank output | One batch per locked run; one payment record per included employee with IBAN + amount; duplicate batch rejected (409) | The batch + per-employee records + the dup-batch 409 | **MET** (`CreatePaymentBatch` 1625-1662) |
| 2.7 | Post-payment reconciliation to bank confirmation | Each record's paid/failed status reconciles to the bank/Mudad confirmation | Per-record paid reconciliation against a bank statement | **PARTIAL** — manual status update only; no automated confirmation import |

### Group 3 — GL posting

| # | Criterion | Measurable pass condition | Evidence artifact required | Status |
|---|---|---|---|---|
| 3.1 | Balanced double-entry before lock | Lock refused (422 `gl_unbalanced`) unless `|Σ debits − Σ credits| ≤ 0.01` | Locked-run journal where debits == credits | **MET** (`BuildPayrollGlEntries` 2573; balance gate 1194-1202) |
| 3.2 | Posted once, immutable read | GL posts to `FinanceGlEntries` exactly once (idempotent); locked-run journal reads posted entries, not a live re-projection through later-edited mappings | Journal identical before/after a post-lock Setup mapping edit | **MET** (idempotency 1176-1178; posted-read 1499-1519) |
| 3.3 | Company-first account mapping per entity + currency | Each entity routes to its mapped accounts (company override→tenant default→catalog) in its own currency; unmapped → `9999` flag | Two entities' journals with distinct account codes/currency | **MET** (`LoadGlResolutionContextAsync` 2688) |
| 3.4 | Void writes contra-entries | Voiding a locked run reverses its GL; net effect 0; records never hard-deleted | Void journal netting the original to zero | **MET** (`VoidRun` 1429 via `PayrollVoidService`) |
| 3.5 | **Statutory contributions reconcile to the register to the halala** | Per-employee employer+employee GOSI/GPSSA equals expected and ties to GL liability and the authority register; variance report shows 0 rows > 0.01 | `variance-report` with `withVariance = 0` + tie-out to the GOSI register | **GAP** — reconciliation endpoints filter `Source=="GOSI"` + `GOSI_ANNUITIES_*` while the run writes `Source="Statutory"` + `GOSI-ANN-EE/-ER`; **they never match**, so `contribution-summary` returns 0 and `variance-report` mis-flags every employee (`GosiController.cs:255,260-265,325,344` vs `KsaCalculators.cs:58-62`) |
| 3.6 | GL → customer ERP hand-off, reference-tracked | Balanced journal exports in an importable format and is confirmed imported into the customer GL/ERP; status cannot reach Posted without a reference | Import confirmation + `ErpPostingStatus=Posted` with document reference | **PARTIAL** — status lifecycle + mandatory reference exist (1988-2033) but no automated journal export artifact (CSV/IIF/SAP/Oracle); ERP posting is a manual status |

### Group 4 — Documents & retention (PDPL)

| # | Criterion | Measurable pass condition | Evidence artifact required | Status |
|---|---|---|---|---|
| 4.1 | Uploaded supporting documents survive restart | A document uploaded before a redeploy is byte-for-byte retrievable (SHA-256 unchanged) after it | Upload → force restart → re-download; hash identical | **GAP (live)** — prod runs `Storage__Provider=local` + `Storage__AllowEphemeral=true` (`render.yaml`), escape hatch bypasses the fail-fast (`DocumentStorageRegistration.cs:37-46`); uploads lost on restart. *(Generated payslips/WPS regenerate from DB — unaffected.)* |
| 4.2 | Store region-pinned to an approved GCC jurisdiction | Bucket region/endpoint is a named approved jurisdiction (no `auto`), recorded in the runbook | Provisioned bucket region + runbook entry | **GAP** — S3 path defaults `Region=auto`, no enforcement (`StorageOptions.cs:11`) |
| 4.3 | Statutory retention + immutability | Payroll/identity documents retained per KSA/UAE minimums with object-lock/versioning (no silent overwrite/early delete) | Bucket object-lock/versioning + retention-lifecycle config | **GAP** — no retention/object-lock posture (contingent on 4.1) |

### Group 5 — Approvals & audit

| # | Criterion | Measurable pass condition | Evidence artifact required | Status |
|---|---|---|---|---|
| 5.1 | Every sensitive payroll action permission-gated | Process/validate/approve/lock/void/export/delete each require a distinct permission; a user lacking it gets 403 | Permission-to-endpoint matrix + a 403 per action | **MET** (`[HasPermission(...)]` across lifecycle: 1152, 1261, 1430, 1464, 1920) |
| 5.2 | Payslip/comp data employee-scoped | A restricted user sees only slips for their allowed employee set | Scope test on `/runs/{id}/slips` | **MET** (`_scopeService.ResolveAsync` 1238-1241) |
| 5.3 | Tamper-evident, append-only audit chain exists | Central log is a SHA-256 hash chain; rows cannot be UPDATE/DELETE (DB guard); chain verifies clean over the month | `GET /audit-logs/integrity` `IsValid=true` + a demonstrated append-only violation on an edit attempt | **MET** (`AuditService.cs` chain + `VerifyChain`; `ZayraDbContext.cs:130` EnforceAuditLogAppendOnly) |
| 5.4 | **Payroll lifecycle events are ON the immutable chain** | Process/approve/lock/void/WPS/settlement events appear as hash-chained entries and survive the 5.3 integrity check | The integrity report listing the run's lifecycle events as chained entries | **GAP** — payroll events write to `PayrollAuditLog`, a plain **mutable, un-chained** table with no append-only guard (`PayrollAudit` 2770-2784); the tamper-evident chain does not cover the payroll trail |

### Group 6 — Recovery / DR

| # | Criterion | Measurable pass condition | Evidence artifact required | Status |
|---|---|---|---|---|
| 6.1 | **Tested restore drill within stated RTO/RPO** | A point-in-time DB restore into a scratch env completes ≤ committed RTO, data loss ≤ committed RPO; row counts + a re-run 5.3 integrity check pass; restored last-locked run reconciles to pre-drill totals | Dated restore-drill runbook: start/finish timestamps, measured RTO/RPO, post-restore reconciliation | **GAP** — Neon PITR assumed but appears only as an unchecked handover item (`DEPLOYMENT_MULTI_CLIENT.md`); no drill on record; web tier `plan:free` (no platform snapshots) |
| 6.2 | Controlled recovery for a bad month (no spreadsheets/manual GL) | Void a locked/paid run with mandatory reason → GL auto-reversed; a Rejected WPS re-exports; a run needing correction sends back through approvals | Void record + reversed GL + re-generated SIF after Rejected + send-back trail | **MET** (VoidRun 1429; WPS re-export 1724; SendBack 1402) |
| 6.3 | Schema never behind code in prod | `/health/ready` returns 503 while migrations pending; `ef migrations list` shows nothing pending post-deploy | `/health/ready` output + migration list at pilot start | **MET** (`render.yaml` gates traffic on `/health/ready`; boot assertions in `Program.cs`) |
| 6.4 | DataProtection keys survive restart | A secret encrypted before a restart decrypts successfully after it | Encrypt → restart → successful decrypt (no `SafeUnprotect` failure) | **GAP** — `AddDataProtection()` has no `PersistKeysTo` (`Program.cs:320`); on an ephemeral dyno keys rotate on restart, breaking previously-encrypted integration secrets (`QiwaSyncWorker.cs:336-345` swallows it) |

### Group 7 — Durable execution (owner amendment 2 — a hard acceptance criterion)

| # | Criterion | Measurable pass condition | Evidence artifact required | Status |
|---|---|---|---|---|
| 7.1 | Payroll/WPS/GL/import run as **durable jobs** | Long-running ops run out-of-request as tracked jobs that (a) survive a process restart mid-run, (b) retry/resume without double-posting, (c) are observable (queued/running/succeeded/failed + progress) | Kill the worker mid-run → job resumes/retries to completion with no duplicate slips/GL/payment + the job status row across the restart | **GAP** — `Process`, GL-post (`Lock`), `GenerateWps`, import all run **in-request, synchronously**; only `QiwaSyncWorker` + `AiInsightEngine` are hosted services (`Program.cs:329-330`), neither for payroll. `QiwaSyncWorker` is the correct pattern to adopt |
| 7.2 | Scale-safe execution | A 5,000-employee run completes within the request/proxy timeout and cannot be double-executed by two web instances during deploy cutover | Timed 5,000-emp run within limits + a concurrency test showing single execution | **GAP** — synchronous processing fires ~2,500-3,000 sequential rule reads for ~500 employees → times out at scale (`PRODUCTION_HARDENING.md` P0-3); scale-to-zero dyno, no distributed lease |
| 7.3 | Essential payroll bulk ops are first-class + observable | Bulk approve/lock (whole run), bulk bank-output (whole run) complete as single audited operations | Run-level lock flipping all slips Final (one audit entry) + the batch covering all included employees | **MET** (Lock sets all slips Final 1210; `CreatePaymentBatch`; bulk PDF ZIP) |
| 7.4 | Bulk include/exclude employees from a run | Operator deliberately holds/excludes a named set from a run and its WPS file, with a required reason, without deactivating them or editing a spreadsheet | The run + WPS excluding exactly that set, hold reason in audit | **GAP** — `Process` auto-includes all Active employees (604-606); the only exclusions are involuntary (readiness block, WPS-ineligible, drift). No deliberate selector |

---

## 3. BULK OPERATIONS SCOPE (owner amendment 3)

**IN SCOPE — essential payroll bulk operations (must work to sign the pilot):**
- **Bulk approve / lock a run's payslips** — run-level Approve→Lock flips the entire cohort to Final atomically (a run *is* the batch). **[MET — 7.3]**
- **Bulk bank-output actions** — generate / download / status one WPS file for all records in the batch. **[MET — 2.1, 2.6]**
- **Bulk payment / payment-batch actions** — one batch covering every included employee, net total tied to the register. **[MET — 2.6]**
- **Bulk salary import/export + bulk payslip-PDF bundle** — CSV round-trip; ZIP of all payslips. **[MET]**
- **Bulk include / exclude employees from a run** — deliberate hold-out of a named set to an off-cycle. **[GAP — 7.4/1.7; must build]**

**OUT OF SCOPE — cosmetic list bulk-select (explicitly NOT required to sign, will not be built for the pilot):**
- Multi-select checkboxes / "select all" on employee/list grids that do **not** drive a payroll, WPS, GL, or payment state change. Any bulk-select that is purely a UI affordance is deferred.

---

## 4. "NO CERTIFIED WITHOUT EVIDENCE" — claims that currently lack their proof (owner amendment 1)

Every capability below is worded elsewhere as done/compliant/certified but has **no evidence artifact today**. Until the named artifact exists for the pilot month, the claim is rejected:

1. **"WPS/SIF is Mudad/SAMA-compliant"** — the layout is *self-labeled* (`SIF_SA_V1`, field widths unconfirmed); **no bank/Mudad acceptance reference exists**. → 2.5
2. **"GOSI reconciles to the register to the halala"** — the reconciliation endpoints are wired to the wrong `Source`/codes and return zero/mis-flagged results; **no reconciling report has ever been produced from a real run**. → 3.5
3. **"Payroll is correct / certified"** — EOSB has **two divergent engines** (~33% divergence, one with no resignation discount); there is no single authoritative termination figure, and joiner proration / retro-arrears require manual correction. → 1.5, 1.6, 1.9, 1.10
4. **"Statutory rates are correct"** — every GOSI rate + the ceiling is marked `VERIFY`; LOP/GOSI-base treatment is `[FLAG-COMPLIANCE-KSA]`; **no compliance-officer sign-off is archived**. → 1.8, 1.11
5. **"Immutable payroll audit trail"** — the payroll lifecycle trail is in a **mutable, un-chained** table; only the central (non-payroll) log is on the hash chain. → 5.4
6. **"Documents are durable and PDPL-resident"** — prod storage is **ephemeral local disk**, region `auto`; uploads are lost on restart. → 4.1–4.3
7. **"We have backups / DR"** — Neon PITR is assumed but **no restore drill has been performed**; no stated RTO/RPO. → 6.1
8. **"Resilient processing"** — payroll runs **in-request**, do not survive a restart, and time out at pilot scale. → 7.1, 7.2

---

## 5. GAP BACKLOG → THE PILOT BUILD PLAN (ordered by blocking severity)

Each item states what to build and the evidence that flips it to MET.

**P0 — a CFO cannot sign the month without these:**

1. **Durable payroll job execution (7.1, 7.2, 6.4).** Introduce a persisted job/outbox + worker (adopt the `QiwaSyncWorker` shape: DB-backed status, retry, claim-before-work lease, progress) fronting Process / Lock-GL-post / GenerateWps / import; fix the N+1 rule reads; persist DataProtection keys. *Evidence: a 5,000-emp run survives a mid-run worker restart, completes without re-trigger or timeout, no duplicate slips/GL/payment; encrypted secrets decrypt after restart.*
2. **GOSI/statutory reconciliation (3.5).** Align the `GosiController` `contribution-summary` + `variance-report` filters to the run's actual `Source="Statutory"` and `GOSI-*-EE/-ER` codes (or normalize codes end-to-end); add the GL-liability + authority-register tie-out. *Evidence: `variance-report` shows 0 variances and the total ties to the GOSI register to the halala.*
3. **WPS/Mudad acceptance test (2.5).** Validate `SIF_SA_V1` against the current MHRSD/Mudad spec; run one real sandbox/bank test submission; record the acceptance reference against the batch. *Evidence: bank/Mudad acceptance reference on the batch, matched to the file hash.*
4. **EOSB single authoritative engine + settlement end-to-end (1.9, 1.10).** Delete the inline `FinalSettlement` formula; route both `/eosb/calculate` and `/final-settlement` through the pack calculator (fix `/365` drift, apply resignation discount, exclude unpaid leave); persist the settlement as a payable, post it to GL, and pay it via the off-cycle run. *Evidence: identical EOSB across endpoints matching a hand-calc; a termination flows settlement → GL → WPS with no spreadsheet.*
5. **Mid-month joiner proration + retro-arrears + off-cycle run + run include/exclude (1.5, 1.6, 1.7, 7.4).** Add `JoiningDate`-based proration and a backdated-increment arrears computation; add a run-type field + relax the one-run uniqueness for off-cycle; add a deliberate include/exclude selector with reason + audit. *Evidence: joiner and backdated-raise cases pay correctly with no edit; a named set is held out to an off-cycle.*
6. **Durable, PDPL-resident document storage + retention (4.1, 4.2, 4.3).** Provision S3/R2 in a named approved GCC region; set `Storage__Provider=s3`; **remove `Storage__AllowEphemeral`**; enable object-lock/versioning + a retention lifecycle. *Evidence: an uploaded document survives a redeploy (hash unchanged); bucket region + retention policy recorded in the runbook.*
7. **Payroll trail onto the immutable hash chain (5.4).** Route `PayrollAudit` through `IAuditService` (or make `PayrollAuditLog` append-only + chained). *Evidence: `GET /audit-logs/integrity` includes the run lifecycle events and verifies clean.*
8. **Restore/DR drill (6.1).** Enable Neon PITR (and move the web tier off `plan:free`); author a backup/PITR runbook; perform and sign one restore drill within a stated RTO/RPO; reconcile the restored last-locked run. *Evidence: signed drill record with measured RTO/RPO + post-restore integrity check.*

**P1 — required for the CFO to *rely* on the month:**

9. **Statutory rate + LOP compliance sign-off (1.8, 1.11).** A named KSA/UAE compliance reviewer confirms GOSI rates/ceiling and the LOP divisor / GOSI-base treatment against current circulars. *Evidence: signed rate-confirmation memo attached to the pilot record.*
10. **GL/ERP journal export + import confirmation (3.6).** Define an importable journal artifact (CSV/IIF/SAP/Oracle) and confirm a customer GL/ERP import. *Evidence: import confirmation + `Posted` reference.*
11. **Post-payment bank reconciliation (2.7).** Import bank/Mudad confirmation to reconcile per-record paid/failed. *Evidence: per-record reconciliation to a bank statement.*

**P2 — confirm at sign-off (scope decision, not necessarily build):**

12. **Confirm ≥2 distinct approver identities (1.2)** exist in the pilot tenant so maker-checker is real, not nominal.

**What is already signable today (subject to running the named evidence for the pilot month):** Groups **1.1–1.4, 2.1–2.4, 2.6, 3.1–3.4, 5.1–5.3, 6.2–6.3, 7.3** — the lifecycle, atomicity, maker-checker, deterministic pay math, WPS validation/versioning/integrity, balanced-and-idempotent GL, contra-on-void, permission-gating, employee-scoping, the hash-chain primitive, controlled recovery, and run-level bulk approve/lock/bank-output are genuinely built and demonstrable.

---

## 6. SPONSOR SIGN-OFF

> We accept "one unbreakable payroll month" as **done** when every criterion 1.1–7.4 is at **MET**, each backed by its named evidence artifact for the pilot month, and P0 items 1–8 are closed and evidenced. No capability is accepted on the word "certified"; each is accepted on its evidence artifact. Any remaining PARTIAL/GAP row is an open condition and may not be waived without an initialed exception.

| Domain (sign per group) | Blocking open items | Accept? |
|---|---|---|
| 1 — Payroll correctness | 1.5, 1.6, 1.7, 1.9, 1.10 (P0); 1.8, 1.11 (P1) | ☐ |
| 2 — Bank / WPS output | 2.5 (P0); 2.7 (P1) | ☐ |
| 3 — GL posting | 3.5 (P0); 3.6 (P1) | ☐ |
| 4 — Documents & retention | 4.1, 4.2, 4.3 (P0) | ☐ |
| 5 — Approvals & audit | 5.4 (P0) | ☐ |
| 6 — Recovery / DR | 6.1, 6.4 (P0) | ☐ |
| 7 — Durable execution | 7.1, 7.2, 7.4 (P0) | ☐ |

| Role | Name | Signature | Date | Exceptions initialed |
|---|---|---|---|---|
| Group CFO (design partner) | | | | |
| Group-HR Sponsor | | | | |
| Zayra CTO / Controls Lead | | | | |

**CTO bottom line:** the control skeleton is genuinely strong and demonstrable today — real maker-checker with self-approval blocking, atomic all-or-nothing processing, balanced-GL-before-lock with idempotent immutable posting, WPS validation/versioning with integrity hashing, a true SHA-256 append-only audit chain, permission-gated and employee-scoped access, and GL-reversing recovery. The signature is blocked by eight P0 truths a CFO will probe directly: **(1) runs are not durable/observable and time out at scale, (2) GOSI does not actually reconcile because the reconciliation endpoints read the wrong Source/codes, (3) no WPS file has been accepted by a real bank/Mudad, (4) EOSB has two divergent engines and settlement never reaches GL or the bank, (5) joiners/retro/off-cycle still need spreadsheets, (6) uploaded documents are ephemeral and not residency-pinned, (7) the payroll trail itself is not on the immutable chain, and (8) there is no tested restore drill.** Those eight are the entire distance to sign-off.
