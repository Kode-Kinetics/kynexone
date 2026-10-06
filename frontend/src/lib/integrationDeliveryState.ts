/**
 * F09 — one vocabulary for what an outbound integration actually did.
 *
 * A locally healthy worker is not proof that an email reached anyone or that a Qiwa action reached
 * Qiwa. These helpers are the only place the UI turns a server state into words, so no screen can
 * say "Sent" for a message a relay merely accepted, a test sink captured, or nothing ever tried.
 * Every function tolerates the field being absent: an older API that does not send it yet must
 * read as "not known to be live", never as live.
 */

/** Matches the server's QiwaSyncLogStatuses.SimulatedLabel. A readiness check, never a filing. */
export const QIWA_SIMULATED_LABEL = 'Qiwa data check only (nothing sent to Qiwa)';

/** Only shown when the server runs a partner-agreement adapter. Never "Live". */
export const QIWA_PARTNER_LABEL = 'Qiwa partner integration (agreement on file)';

/**
 * The integration mode as a screen shows it. Absent or false is a data check. No Qiwa label anywhere
 * says "Live", "Connected", "Synced" or "Filed with Qiwa": the product has no verified Qiwa API.
 */
export function qiwaModeLabel(isLiveIntegration: boolean | null | undefined): string {
  return isLiveIntegration === true ? QIWA_PARTNER_LABEL : QIWA_SIMULATED_LABEL;
}

/**
 * Connection badge text. Stored statuses are translated; none is shown raw. A stored "Connected" is
 * only believed when the running server says it is a partner integration (isLiveIntegration).
 */
export function qiwaConnectionLabel(status: string, isLiveIntegration?: boolean | null): string {
  switch (status) {
    case 'Simulated': return QIWA_SIMULATED_LABEL;
    case 'Connected': return isLiveIntegration === true ? 'Partner API responding' : QIWA_SIMULATED_LABEL;
    case 'Disconnected': return 'Not set up';
    case 'NotConfigured': return 'Not set up';
    case 'ConfigurationError': return 'Setup incomplete';
    case 'ApiError': return 'Qiwa partner API error';
    default: return 'Qiwa data check';
  }
}

/** Scheduled-report execution status, as the report history shows it. */
export function reportRunStatusLabel(status: string): string {
  switch (status) {
    case 'Success': return 'Accepted by mail server';
    case 'NotConfigured': return 'Not sent: email not set up';
    case 'Captured': return 'Captured by test mode, not sent';
    case 'Failed': return 'Failed';
    default: return status;
  }
}

export type DeliveryTone = 'ok' | 'warn' | 'error' | 'neutral';

export function reportRunStatusTone(status: string): DeliveryTone {
  if (status === 'Success') return 'ok';
  if (status === 'Failed') return 'error';
  if (status === 'NotConfigured' || status === 'Captured') return 'warn';
  return 'neutral';
}

/** The email transport the server reports in /platform/health `components.email.status`. */
export function emailModeLabel(mode: string | null | undefined): string {
  switch (mode) {
    case 'configured': return 'Relay configured';
    case 'permitted_test_delivery': return 'Permitted test delivery only';
    case 'tenant_relays_only': return 'Some workspaces only';
    case 'not_configured': return 'Not set up: nothing is sent';
    case 'capture': return 'Test capture: nothing is sent';
    case undefined:
    case null:
    case '': return 'Not reported';
    default: return mode;
  }
}

export interface DeliveryCounts {
  queued?: number;
  retrying?: number;
  failed?: number;
  deadLetter?: number;
  notConfigured?: number;
  captured?: number;
  reportsDeadLetter?: number;
  qiwaDeadLetter?: number;
}

/**
 * The one-line summary for the outbound-delivery card. Zero problems is stated as a count, never as
 * "all delivered": the ledger can only say what did NOT go wrong.
 */
export function deliveryAttentionSummary(counts: DeliveryCounts | null | undefined): string {
  if (!counts) return 'Delivery counts are not reported by this server.';
  const parts: string[] = [];
  const add = (n: number | undefined, one: string, many: string) => {
    if (n && n > 0) parts.push(`${n} ${n === 1 ? one : many}`);
  };
  add(counts.deadLetter, 'gave up after retries', 'gave up after retries');
  add(counts.failed, 'failed', 'failed');
  add(counts.notConfigured, 'not sent (not set up)', 'not sent (not set up)');
  add(counts.reportsDeadLetter, 'scheduled report gave up', 'scheduled reports gave up');
  add(counts.qiwaDeadLetter, 'Qiwa check needs attention', 'Qiwa checks need attention');
  return parts.length === 0 ? 'No undelivered messages recorded.' : `Needs attention: ${parts.join(', ')}.`;
}
