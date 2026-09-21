# Login rebuild — deferred work (V3)

Everything below was found while rebuilding `/login` on 2026-09-20. It is deferred, not
forgotten. Each item says what it is, where the evidence is, and why it matters — so it can be
picked up without re-deriving any of it.

Nothing here is speculative. Every claim was verified against the code or measured against the
running app on the day.

---

## 1. Pilot blockers — these hurt a customer, not a metric

### 1.1 `/leave` renders zero rows
`e2e/pilot-critical.spec.ts` fails with `/leave rendered 0 data row(s)` on **both** desktop and
Pixel 7, and it gets there *after* a successful login — so this is a data/seed gap, not auth.
Leave is a module a pilot customer opens on day one.

### 1.2 Document storage is ephemeral in production
`render.yaml` ships `Storage__Provider=local` with `Storage__AllowEphemeral=true`, and
`docs/PRODUCT_COMPLETENESS_MAP.md:101` records that uploaded contracts, IDs and offer letters are
**lost on every restart**. That conflicts with KSA/GOSI retention expectations.
Fix is provisioning S3/R2 in a named region and removing `AllowEphemeral` — see
`docs/WAVE_1_OPERATIONAL_DURABILITY_PLAN.md:97`.

---

## 2. Claims that need a decision, not code

These are business decisions. The login page has been written to avoid each of them, but they
remain live elsewhere in the product and in sales material.

### 2.1 Data residency is claimed but not enforced
`Storage__Region` is unset; `PILOT_ACCEPTANCE_SPEC.md:76` marks region-pinning as a **GAP**;
`PRODUCT_COMPLETENESS_MAP.md:66` says "Data residency / PDPL not enforced".
The login page used to read `KSA · Riyadh region`, which is a residency claim. It now reads
`Riyadh time`, which is literally true (both dates are formatted with `timeZone: 'Asia/Riyadh'`)
and promises nothing about where data lives.
**Decide:** either pin the region and say so, or keep every surface silent about it. `/security`
currently declines to name a region, which is the right posture until it is true.

### 2.2 Nitaqat bands are the pre-2021 scheme
The Saudization visual used Red / Yellow / Green / Platinum. Current HRSD bands are Red,
Low Green, Medium Green, High Green, Platinum. A Saudi buyer notices immediately.

### 2.3 WPS files have never been proven accepted
Every exporter carries a VERIFY-the-spec comment. Until a generated file has been accepted by a
live Mudad / MOHRE / QCB portal, the word **"bank-ready"** overclaims. It has been removed from
the login page for that reason.

### 2.4 Two vendor domains at the point of trust
Sales is `sales@kynexone.com`; security and legal are `@kodekinetics.com`. At the moment a buyer
decides whether one company stands behind the product, they see two domains.

### 2.5 Statutory coverage is three countries, not six
`Application/CountryPack/CountryPackRegistry.cs` registers packs for **Saudi Arabia, the UAE and
Qatar only**. Kuwait, Bahrain and Oman resolve to `DefaultPack`: zero statutory deductions, zero
gratuity, an empty WPS file, USD/English/Gregorian.
So **"across the GCC" is false as a coverage claim.** What *is* GCC-wide is the platform itself —
HR, attendance, leave, shifts, approvals, audit, multi-company, multi-currency, Arabic with real
RTL, Hijri. The honest framing is two layers: the platform runs across the Gulf; the statutory
rulebooks are three today.
**No roadmap for the remaining three exists in this repo.** If one exists elsewhere, the login
page's "Statutory pack not yet available" wording should be updated to match it.

---

## 3. Test infrastructure — cheap to fix, high value

### 3.1 The suite defaults to the stale Docker container
`playwright.config.ts:28`:
```ts
baseURL: process.env.PLAYWRIGHT_BASE_URL ?? process.env.E2E_BASE_URL ?? 'http://localhost:5173'
```
`:5173` is the `zayra-frontend` container, which serves a **production build baked into the
image** with no bind mount — i.e. whatever the code looked like when the image was built.
During this work the suite repeatedly reported "flaky" login failures that were actually the
pre-rebrand build being tested. Runs must pass `PLAYWRIGHT_BASE_URL=http://localhost:3111` (or
whatever the dev port is) to test the working tree.
**Suggested fix:** make the pre-flight fail loudly when the served build does not match the
working tree, or change the default.

### 3.2 One platform login poisons ~80 tests
A platform login runs `SET revoked_at_utc = now() ... WHERE r.user_id = ANY(tenantUserIds)`, which
revokes every persona session in `tenants.json` and `platform.json`. A single external login
during a suite run invalidates every spec that reuses `storageState` — 89 failures in one
observed run, ~80 from this single cause.
Evidence: specs that mint their own session passed (`platform-auth` 5/5, `tenant-auth` 12/12);
specs reusing stored state failed (`platform-dashboard` 0/6, `tenant-feature-flags` 0/13).
**Suggested fix:** assert `GET /api/platform/auth/me` → 200 in `e2e/auth.setup.ts` before dependent
projects run, so a revoked session fails one test loudly instead of ninety silently.

### 3.3 A full suite run needs a quiet machine
~83 minutes wall clock at `workers: 1`. No other browser automation or platform-auth calls during
it.

---

## 4. Repo cleanup

- `frontend/src/styles/bench-unit.css` and `frontend/src/components/BenchUnitGround.tsx` —
  the abandoned monochrome "BENCH UNIT" concept. No longer imported anywhere.
- `frontend/app/proto-a`, `proto-b`, `proto-c` and `frontend/src/components/proto/` —
  throwaway visual prototypes used to choose a direction. Their ideas are merged into `/login`.
- `verify-login.mjs` currently lives in `/tmp` and will not survive a reboot. It checks console
  errors, overflow at four viewports, the field contract, accessible names and naming technique,
  tab order, glyph-accurate worst-case contrast sampled from rendered pixels, rAF frame cost,
  reduced-motion, and the no-WebGL fallback. **It belongs in `frontend/scripts/`.**

---

## 5. Things worth knowing before touching `/login` again

- **Contrast must be measured from rendered pixels, not asserted from CSS.** The background is a
  live WebGL field, so a colour token tells you nothing. Worst case across the animation is the
  only number that counts — single-frame sampling reports passes that are wrong four seconds later.
- **`src/styles/index.css` force-overrides** `html:not(.dark) .tenant-login-shell .text-slate-400`
  (and similar) with `!important`. Tailwind colour utilities are therefore unusable on this page;
  it uses CSS custom properties throughout.
- **The cylinder's radius must track its pitch.** `r = 20·f1 / (2·tan(pitch/2))`. Change `PITCH`
  in `LoginMarketing.tsx` without updating `--cyl-r` and the faces stop sitting on the drum.
- **`min-height: 100%` on a padded flex column** adds its padding on top of the full height, and an
  absolutely-positioned `::before` extending past a scroll container's block-end adds to that
  container's scrollable overflow. Both caused phantom scrollbars revealing empty space.
- The `/login` route is a **login first**. The sign-in form must stay fully above the fold at
  1728×963, 1440×900, 1288×717 and 1024×768.
