/**
 * Refuses to let the destructive fixture helpers run against anything but a disposable stack.
 *
 * `provisionLimitedTenantFixture` and `purgeLimitedTenantFixture` both call
 * `DELETE /api/platform/tenants/{id}/purge?confirm=PURGE`, which is a GDPR hard-erase:
 * PlatformController.PurgeTenant sweeps every ITenantOwned entity out of the EF model and
 * ExecuteDeletes the users and the tenant row. It is irreversible.
 *
 * Before this guard existed, the only thing standing between that and a real environment was
 * that the default platform password would fail there — safety by credential accident. An
 * operator with real platform-Owner credentials in their shell and a stale PLAYWRIGHT_BASE_URL
 * would erase a live tenant, and the suite would report it as a green setup step.
 *
 * The rule: a host is disposable if it is loopback, OR if the operator has named it explicitly
 * in E2E_DESTRUCTIVE_HOST_ALLOWLIST. Anything else throws before a single request is sent.
 * Deliberately fail-closed — an unparseable or absent base URL is refused, not waved through.
 */

const LOOPBACK_HOSTS = new Set(['localhost', '127.0.0.1', '::1', '0.0.0.0', 'host.docker.internal']);

/** Hosts that must NEVER be accepted, even if someone adds them to the allowlist by mistake. */
const NEVER_DESTRUCTIVE = [
  'onrender.com',
  'vercel.app',
  'neon.tech',
  'kynexone.com',
];

export function assertDisposableHost(baseUrl: string | undefined, operation: string): void {
  if (!baseUrl || !baseUrl.trim()) {
    throw new Error(
      `[disposable-host] Refusing to ${operation}: no base URL resolved. This helper performs an `
      + 'irreversible tenant purge and will not run against an unknown target.',
    );
  }

  let host: string;
  try {
    host = new URL(baseUrl).hostname.toLowerCase();
  } catch {
    throw new Error(
      `[disposable-host] Refusing to ${operation}: base URL '${baseUrl}' is not parseable.`,
    );
  }

  const banned = NEVER_DESTRUCTIVE.find((suffix) => host === suffix || host.endsWith(`.${suffix}`));
  if (banned) {
    throw new Error(
      `[disposable-host] REFUSING to ${operation} against '${host}'.\n`
      + `That host matches '${banned}', which is a deployed environment. This helper issues\n`
      + 'DELETE /api/platform/tenants/{id}/purge?confirm=PURGE — an irreversible hard-erase of a\n'
      + 'tenant and every row it owns. It is never correct to point the browser suite at a\n'
      + 'deployed environment. Start a disposable stack instead:\n'
      + '  docker compose -p <name> up -d --build\n'
      + 'and set PLAYWRIGHT_BASE_URL to its loopback address.',
    );
  }

  if (LOOPBACK_HOSTS.has(host)) return;

  const allowlist = (process.env.E2E_DESTRUCTIVE_HOST_ALLOWLIST ?? '')
    .split(',')
    .map((entry) => entry.trim().toLowerCase())
    .filter(Boolean);

  if (allowlist.includes(host)) return;

  throw new Error(
    `[disposable-host] REFUSING to ${operation} against '${host}'.\n`
    + 'Only loopback hosts are destructive-safe by default, because this helper hard-erases a\n'
    + 'tenant. If this host really is a throwaway stack, name it explicitly:\n'
    + `  E2E_DESTRUCTIVE_HOST_ALLOWLIST=${host}\n`
    + 'Never set that for an environment holding data you cannot lose.',
  );
}
