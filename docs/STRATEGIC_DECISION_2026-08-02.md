# Zayra (KynexOne) — Strategic Decision of Record (2026-08-02)

_Executive panel (Sales Director + New Product Head + CIO + independent Consultant SME) → CTO owned decision. Code-verified._

All load-bearing facts verified in code. The panel converged on two things (up-market; fix the floor) and split on three (where Benefits sits, whether payroll depth precedes new features, how much hardening is "enough"). Here is the owned decision.

---

# CTO DECISION — Zayra / KynexOne

*Verified in code before ruling: `render.yaml` (storage `local`+`AllowEphemeral=true`, `plan: free`, `autoDeploy:false`, `/health/ready` migration-parity gate, `Storage__Region` unset); `CountryTier.cs` OD-4 tiering (SA/AE Certified, QA/KW/OM/BH FailLoud, else HrOnly — fail-safe in code); `BenefitsController.cs` + migration + tests exist, zero nav/UI; `EnterpriseIdentityController.cs` SSO 501; only `SmtpEmailService` (no SMS/WhatsApp dispatcher); only two ad-hoc `BackgroundService`s (no durable queue).*

**Organizing principle that settles every tie below:** the goal is **ONE referenceable, finance-grade payroll month for a multi-entity group in KSA or UAE.** That single clean pilot is the asset that opens the next ten deals. Sales' demo-feature pull and the CIO's platform gold-plating both get disciplined against it.

---

## 1. POSITIONING (2 sentences)

**Zayra is the finance-grade payroll & compliance system-of-record for multi-entity groups (≈200–5,000 employees, 2–8 legal entities) operating in KSA + UAE — sold sales-led to finance/Group-HR design partners.** We defer the SME/PLG motion entirely (no trial, billing, gateway, or dunning exists, and it would mean price-fighting Bayzat/Qoyod/Palm on their turf), and we make the honest GCC claim the code already earns: *certified payroll in KSA & UAE, full HR/org across all six GCC states, payroll in QA/KW/OM/BH behind published fail-loud guards.*

---

## 2. BUILD SEQUENCE — the ruling: floor-first, then defend the moat, then add features

I am **overruling "finish all employee pods first"** and **respectfully reordering two of the owner's four green-lit items** (WhatsApp and the SSO fix move to gated fast-follow; storage + off-free-tier/DR stay at the top). Rationale per line.

**0 — FIX THE DATA FLOOR (blocking, days, ahead of the remaining pods).**
Durable region-pinned S3/R2 (`Storage__Provider=s3`, remove `AllowEphemeral`) + off free tier (paid dyno, kills SPOF/spin-down) + automated backups with **one tested restore** and documented RTO/RPO + **confirm the leaked Neon/JWT/platform-admin secrets are rotated** + a hosted APM drop-in (Sentry-class) with uptime alerting.
*Why:* this is not a feature — it is the legal license to hold a real employee's Iqama/contract. Today the product deletes uploaded compliance docs on every restart and may be running on credentials disclosed in chat. Pinning the storage/DB region to an approved GCC jurisdiction solves the PDPL residency baseline in the same move. Cheap, mostly config, and every item below is worthless on top of it.

**1 — FINISH Pod2 (duplicate detection) to a clean commit; then HOLD pods 3–5.**
*Why:* never thrash a running pod, and the accept-never-block intake + readiness-gate core is already landed. Bulk-select/IA-dedup are minor polish; **auto-email arrives for free from item 4's dispatcher**, so pods 3–5 wait behind the pilot without loss.

**2 — PAYROLL OPERATIONAL DEPTH (backend critical path).**
Retro/arrears engine (mid-month joiners, backdated increments) + off-cycle/supplementary run type + **monthly EOSB liability accrual to GL**.
*Why:* the pilot IS a real payroll month on the moat. The first month WILL contain a mid-cycle joiner and a backdated raise; if those force manual workarounds in front of the CFO we're converting, "finance-grade" is exposed as demo-grade and the reference dies. **Defend the moat before widening the surface** — this is the single sharpest strategic point the panel raised.

**3 — BENEFITS UI (parallel frontend track — lands in the same window as #2).**
Wire the dark module into nav + build UI on the existing backend (dependents, tiers-by-grade, renewals, insurer census; claims/TPA fast-follow).
*Why:* highest net-additive feature ROI on the board (backend/migration/tests already paid for — frontend only) and it removes the visible UAE-vs-Bayzat hole. It runs on the frontend squad **in parallel** with backend payroll depth, so it costs no critical-path time — that parallelism is how I reconcile Sales (Benefits early) with the moat-defense ranking.

**4 — NOTIFICATION DISPATCHER (WhatsApp/SMS behind the existing `Channel` model).**
*Why:* GCC's default channel and a cheap bolt-on that also **completes the held auto-email pod**. Fast-follow for a white-collar first pilot (responsive web ESS + email covers it); **pull to MVP if the first logo is frontline/deskless.**

---

## 3. EXPLICITLY DEFERRED

- **KW/OM/BH full country packs — DOWNGRADED to demand-gated (code-grounded correction to map M1).** `CountryTier.cs` makes these markets **fail-loud-safe in code** — payroll hard-blocks behind statutory validation rather than mis-paying — so this is *market expansion*, not a safety bug. Fund only on a signed client in that market. ("6-country is marketing" is now overstated; the honest tier claim is a strength to sell, not a hole to hide.)
- **SME/PLG self-serve trial + billing engine + payment gateway — deferred entirely** (the positioning decision).
- **Durable job/queue platform — gated fast-follow, NOT floor.** A few-hundred-employee pilot won't time out; building Hangfire/Quartz now is pre-revenue gold-plating. **Hard gate: required before the first production payroll run beyond ~a few hundred employees** (in-request payroll/WPS/imports will time out at scale). This is the precise line between the CIO's "P0" and the SME's "drop-in, not platform" — the CIO is right that it's mandatory before scale, the SME is right that it's not a pilot blocker.
- **SSO federation adapter (+ MFA enrollment UI) — gated fast-follow.** Design-partner pilot #1 uses local auth; SCIM is already real. Start when the first enterprise procurement names its IdP; land before that security review. (Kill the 501 then, not before.)
- **PWA of ESS/approvals over the existing BFF** — build only if a pilot demands mobile punch; reuse the React app, do not build native.
- **KSA gov breadth (Mudad/Muqeem → Nitaqat what-if simulator)** — when the pipeline tilts KSA.
- **Post-spine / no near-term deal impact:** Expenses, Comp/merit cycles, LMS, Engagement, Succession, EOR/contractor, HSE/disciplinary, air-ticket, asset mgmt, timesheets, SWP; public API/webhooks + ERP/bank connectors; native mobile; multi-region active-active DR; self-serve DSAR surface; full WCAG-AA remediation; self-serve BI/pivot builder; e-sign. All real eventually — **none needed to run one mid-market pilot.**

---

## 4. WHAT THE OWNER MUST PROVISION — and by when (or it blocks)

| Owner action | By when | Blocks if late |
|---|---|---|
| **Residency jurisdiction decision** — which approved GCC/KSA region is our data home (business/legal call only you make) | **This week** | Every item in #0 — it drives the storage + DB region pin |
| **Provision in-region S3/R2** (bucket, keys, region) | **This week** | The entire floor; no safe pilot exists until this is live |
| **Approve paid hosting tier** (Render Starter/Standard) + enable backups | **This week** | Off-free-tier + DR |
| **Confirm/authorize secret rotation** — Neon superuser cred, JWT signing key, platform-admin bootstrap → MFA + unset `PLATFORM_ADMIN_PASSWORD` | **This week** (human action) | Legal/ethical gate to hold real PII |
| **Sign off APM vendor** (Sentry-class drop-in) | **This week** | Blind on the pilot's first incident |
| **Name 2–3 warm, finance-led, multi-entity KSA/UAE design partners** | **Within 2 weeks** | Pilot country/profile is guessed, not driven by a real account — determines whether WhatsApp (#4) and PWA get pulled forward |

Floor is **weeks, not a quarter** (provisioning/config, not R&D). First referenceable paid pilot **within the quarter**; first 3 logos in **1–2 quarters** — only if we hold floor-first sequencing and the honest country claim.

---

## 5. THE SINGLE MOST IMPORTANT THING TO GET RIGHT

**Protect the first reference by making the pilot's payroll month unbreakable — on both ends.** That means (a) a data floor that cannot lose the customer's uploaded documents, and (b) a payroll engine that survives a real month — a mid-month joiner and a backdated raise — with zero manual workarounds. One clean, finance-grade payroll month for a multi-entity group in KSA or UAE is the only asset that unlocks the next ten deals; a Benefits screen, a WhatsApp message, or an SSO login is worth nothing if the platform eats the documents or the moat cracks on the first off-cycle payslip. Everything else in this memo is sequenced to serve that one outcome.
