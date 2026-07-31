# Frontend dependency-vulnerability remediation

Tracked follow-up from the 2026-07-31 deploy-pipeline incident.

## Context
The backend deploy pipeline (`.github/workflows/ci.yml`) had been silently blocked
because the `dependency-scan` job hard-failed the whole pipeline on **frontend**
`npm audit` findings. The frontend deploys independently (Vercel), so its runtime
dependency advisories should not gate backend releases. During the incident we:

- Patched **axios → 1.19.0** (clears ~10 High advisories; frontend typecheck green).
- Made the frontend `npm audit` step **report-only** (`continue-on-error: true`).
  The **backend** `dotnet` vulnerable-package audit remains a hard gate.

## Remaining High advisories (as of 2026-07-31)
All are **Next.js transitive** — `npm audit` cannot fix them in-range (its only
suggestion is the absurd `next@9.3.3` downgrade), so they need a deliberate,
build-verified upgrade:

| Package | Advisory | Fix |
|---|---|---|
| `next` | GHSA-m99w-x7hq-7vfj (DoS via Server Actions), GHSA-89xv-2m56-2m9x (SSRF), cache-confusion GHSAs | Upgrade to the current patched Next 15.x → 16.x |
| `postcss` (via next) | GHSA-r28c-9q8g-f849 (path traversal in source-map loading) | Bundled with the Next upgrade |
| `sharp` (via next) | GHSA-f88m-g3jw-g9cj (libvips CVEs) | Bundled with the Next upgrade / bump `sharp ≥ 0.35.0` |

## Plan
1. On a branch, bump `next` to the latest patched release; run `npm ci`, `npx tsc --noEmit`,
   `npm run build`, and the Playwright e2e smoke.
2. Re-run `npm audit --audit-level=high` → expect 0.
3. Once green, **restore the hard gate**: remove `continue-on-error: true` from the
   `npm audit` step in `ci.yml`.
4. Redeploy the frontend (Vercel) so the patched runtime deps ship.

## Guardrail
Do not leave `npm audit` report-only indefinitely — it is a temporary incident
measure. Re-blocking (step 3) is the definition of done.
