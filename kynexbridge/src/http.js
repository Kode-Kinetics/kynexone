// HTTP with a hard per-attempt timeout and bounded retry/back-off.
//
// A hung connection used to stall the whole poll loop for every device (fetch has no default timeout), and a
// transient 5xx/network blip failed the batch with no retry. Everything the bridge sends is idempotent on the
// server (dedup key), so retrying a request whose outcome is unknown is safe.

export class HttpError extends Error {
  constructor(status, body, retryable) {
    super(`HTTP ${status}${body ? ` ${body}` : ''}`);
    this.status = status;
    this.body = body;
    this.retryable = retryable;
  }
}

const sleepReal = (ms) => new Promise((r) => setTimeout(r, ms));

export function isRetryableStatus(status) {
  return status === 408 || status === 425 || status === 429 || status >= 500;
}

// Exponential back-off with full jitter, capped. `random` is injectable for tests.
export function backoffMs(attempt, { baseDelayMs = 500, maxDelayMs = 30_000, random = Math.random } = {}) {
  const ceiling = Math.min(maxDelayMs, baseDelayMs * 2 ** attempt);
  return Math.floor(random() * ceiling);
}

function retryAfterMs(res) {
  const h = res.headers?.get?.('retry-after');
  if (!h) return null;
  const secs = Number(h);
  if (Number.isFinite(secs)) return Math.max(0, secs * 1000);
  const when = Date.parse(h);
  return Number.isNaN(when) ? null : Math.max(0, when - Date.now());
}

// A proxy's Retry-After (86400, say) must not stall the whole poll loop: the wait is capped at maxDelayMs.
export function cappedRetryAfter(res, maxDelayMs) {
  const ms = retryAfterMs(res);
  return ms === null ? null : Math.min(ms, maxDelayMs);
}

/**
 * fetch with timeout + retry. Resolves with the parsed JSON body of the first 2xx response.
 * Throws HttpError (retryable=false) for a non-retryable status, or the last error after `retries` retries.
 */
export async function requestJson(url, init, opts = {}) {
  const {
    timeoutMs = 20_000, retries = 4, baseDelayMs = 500, maxDelayMs = 30_000,
    fetchImpl = globalThis.fetch, sleep = sleepReal, random = Math.random,
  } = opts;
  let lastErr;
  for (let attempt = 0; attempt <= retries; attempt++) {
    let wait = null;
    try {
      // `headers` may be a function so a signed request is re-signed on EVERY attempt (a long retry chain must not age
      // past the server's timestamp window, and a nonce is single-use). Redirects are refused: the device key header
      // must never be forwarded to another host.
      const headers = typeof init.headers === 'function' ? init.headers(attempt) : init.headers;
      const res = await fetchImpl(url, { ...init, headers, redirect: 'error', signal: AbortSignal.timeout(timeoutMs) });
      if (res.ok) {
        try { return await res.json(); }
        catch { throw new Error('successful response was not valid JSON'); }
      }
      const text = await res.text().catch(() => '');
      const retryable = isRetryableStatus(res.status);
      lastErr = new HttpError(res.status, text.slice(0, 500), retryable);
      if (!retryable) throw lastErr;
      wait = cappedRetryAfter(res, maxDelayMs);
    } catch (e) {
      if (e instanceof HttpError && !e.retryable) throw e;
      lastErr = e instanceof HttpError ? e : Object.assign(new Error(
        e?.name === 'TimeoutError' || e?.name === 'AbortError' ? `request timed out after ${timeoutMs}ms` : `network error: ${e?.cause?.code || e?.message}`),
        { retryable: true });
    }
    if (attempt === retries) break;
    await sleep(wait ?? backoffMs(attempt, { baseDelayMs, maxDelayMs, random }));
  }
  throw lastErr;
}
