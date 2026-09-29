/**
 * GET /build-info — which commit this frontend was built from. Read-only, no data, no auth.
 *
 * The e2e preflight (frontend/e2e/preflight/run.ts) compares it with the API's /health/live commit
 * and with the commit under test, so a browser suite can never go green against a stale image — the
 * prebuilt Docker frontend on :5173 was exactly that. Static: the value is fixed at build time.
 *
 * Sources, first non-empty wins: BUILD_COMMIT (CI and the Docker build arg), Vercel's own
 * VERCEL_GIT_COMMIT_SHA, then GITHUB_SHA. The repository is public, so a commit id discloses nothing.
 */
export const dynamic = 'force-static';

const commit =
  process.env.BUILD_COMMIT?.trim()
  || process.env.VERCEL_GIT_COMMIT_SHA?.trim()
  || process.env.GITHUB_SHA?.trim()
  || 'unknown';

export function GET(): Response {
  return Response.json({ service: 'kynexone-web', commit });
}
