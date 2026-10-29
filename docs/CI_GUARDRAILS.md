# CI guardrails: what runs where, and the settings only the owner can change

## Lanes
- Pull request: change detection (docs-only PRs skip the heavy jobs), backend unit + ratchets, backend integration (4 shards), frontend typecheck + ESLint ratchet + playwright-config guard + browserless + unit, secret/dependency scans, schema gates, chrome security gate, Browser Pilot (full ~1,040-test lane stays a REQUIRED check here).
- Push to main: the deterministic checks and Browser Pilot's smoke steps (preflight, world bootstrap, employee workspace, setup layout, tenant-pilot) plus the chrome security gate gate `migrate-backend` and `build-image`. The full chromium lane is skipped (it passed on the PR; ruleset forces an up-to-date branch).
- Nightly (`nightly.yml`, 01:17 UTC + manual): 28 route-mocked feature suites in 4 shards. Non-blocking.
- Not run anywhere: `manual` / `manual-stack` entries in `frontend/e2e/config-manifest.json` (owner decision: add a compose-backed nightly or retire).

## Ratchets (counts only go down)
- Backend: `Zayra.Api.Tests/Architecture/architecture-baseline.json` (role-string authorize, unapproved `[HttpDelete]`, 501 endpoints, controllers touching a DbContext). Re-pin after a decrease: `ARCH_REPIN=1 dotnet test --filter ArchitectureRatchetTests`.
- Frontend: `frontend/eslint-baseline.json`. Re-pin after a decrease: `npm run lint:repin`.
- Playwright configs: `node frontend/scripts/check-playwright-configs.mjs`.

## Owner settings (cannot be done from the repo)
1. Branch ruleset: keep the required checks as they are. The names are unchanged ("Backend Tests (security gate)" is now an aggregator job). Skipped jobs on docs-only PRs count as passing.
2. Vercel frontend promotion waits on backend parity: Vercel project, Settings, Git: turn OFF "Auto-assign Custom Production Domains" (or set Production Branch deployments to staged). Then promote only after `deploy-backend` succeeds, either with `vercel promote <deployment-url>` (needs a `VERCEL_TOKEN` repository secret and a final job in ci.yml) or by hand in the dashboard. Until then the frontend still deploys ahead of the backend, as documented in docs/RELEASE_CANDIDATE_AND_PARITY.md.
3. Optional: add the `production` environment as the home of the three production secrets (already a known open item).
