/**
 * GET /build-info — which release and commit this frontend was built from. Read-only, no data, no auth.
 *
 * The e2e preflight (frontend/e2e/preflight/run.ts) compares it with the API's /health/live commit
 * and with the commit under test, so a browser suite can never go green against a stale image — the
 * prebuilt Docker frontend on :5173 was exactly that. Static: the value is fixed at build time.
 *
 * Commit sources, first non-empty wins: BUILD_COMMIT (CI and the Docker build arg), then GITHUB_SHA. The
 * version is the release's semantic version (the Docker VERSION build arg), the same one the API reports.
 */
export const dynamic = 'force-static';

const commit =
  process.env.BUILD_COMMIT?.trim()
  || process.env.GITHUB_SHA?.trim()
  || 'unknown';

const version = process.env.BUILD_VERSION?.trim() || '0.0.0-dev';

export function GET(): Response {
  return Response.json({ service: 'kynexone-web', version, commit });
}
