import { createHmac, createHash, randomBytes } from 'node:crypto';

// Request signing (v2). When a device has a signing secret the bridge sends
//   X-Device-Timestamp: <unix seconds>
//   X-Device-Nonce:     <unique per request>
//   X-Device-Signature: v2=<hex HMAC-SHA256(secret, canonical)>
// canonical = "v2\n<METHOD>\n<path>\n<timestamp>\n<nonce>\n<hex SHA-256(rawBody)>"
// Method and path stop a signed body verifying on another endpoint; the nonce (remembered by the server for its window)
// stops a captured request being replayed. Mirrors DeviceSigning.Canonical in the API.
export function canonical(method, path, timestampSec, nonce, body) {
  return `v2\n${method.toUpperCase()}\n${path}\n${timestampSec}\n${nonce}\n${createHash('sha256').update(body).digest('hex')}`;
}

export function signBody(secret, timestampSec, nonce, method, path, body) {
  return 'v2=' + createHmac('sha256', secret).update(canonical(method, path, timestampSec, nonce, body)).digest('hex');
}

export function signedHeaders(secret, { method = 'POST', path, body, nowMs = Date.now(), nonce = randomBytes(16).toString('hex') } = {}) {
  if (!secret) return {};
  const ts = Math.floor(nowMs / 1000);
  return {
    'X-Device-Timestamp': String(ts),
    'X-Device-Nonce': nonce,
    'X-Device-Signature': signBody(secret, ts, nonce, method, path, body),
  };
}
