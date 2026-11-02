# KynexBridge — local attendance device agent

A small, installable agent that runs on a branch LAN, **polls on-prem attendance devices**
(biometric / face / RFID), and **forwards punches to KynexOne** via the device-key webhook
(`POST /api/attendance/ingest`). It's the bridge for devices that sit behind a firewall and
can't be reached from the cloud.

```
Device (LAN) ──pull via SDK──▶ KynexBridge ──HTTPS + X-Device-Key──▶ KynexOne /api/attendance/ingest
```

The cloud side (ingest, dedup, processing → `attendance_records`) is already built. The agent
only needs to **pull** from each device and **forward** — which it does here.

## Requirements
- Node.js ≥ 18 (uses built-in `fetch`; the core has **zero npm dependencies**).
- For ZKTeco devices: `npm install node-zklib` on the agent host (optional dependency).

## Setup
1. In KynexOne: **Attendance → Devices → Add device**, then **Generate key** (copy the `knx_…` key — shown once).
2. `cp config.example.json config.json` and fill in `apiBaseUrl` + each device's `deviceKey` and connector.
3. Run:
   ```bash
   npm start          # poll forever on the configured interval
   npm run once       # single poll (good for cron / testing)
   ```

## Connectors
| Connector | Use | Status |
|-----------|-----|--------|
| `mock` | Generate test punches (verify the pipeline end-to-end) | ✅ working |
| `csv` | Watch an inbox folder for device CSV exports, parse, archive | ✅ working |
| `zkteco` | Pull from ZKTeco devices over TCP/IP (port 4370) via `node-zklib` | 🔌 SDK wiring — **not yet verified against hardware**; fails loudly (missing SDK, no `timeZone`, connection error) instead of returning nothing |

Add new vendors by dropping a module in `src/connectors/` that exports
`async pull(device, sinceIso) → punch[]` and registering it in `src/connectors/index.js`.
Roadmap connectors: `hikvision` (ISAPI), `suprema` (BioStar 2), vendor cloud webhooks.

Each punch returned by a connector:
```js
{ employeeCode, punchTimestampUtc /* ISO */, punchDirection /* In|Out|Unknown */,
  verificationMethod, confidenceScore?, latitude?, longitude?, photoReference? }
```

## Reliability
- **Timeouts and retries.** Every request has a hard timeout (`requestTimeoutMs`, default 20 s) and retries
  408/425/429/5xx and network errors with exponential back-off and jitter (`maxRetries`, default 4), honouring
  `Retry-After`. Retrying is safe: the server de-duplicates.
- **Chunked upload with a resume cursor.** A backlog is sent in chunks of `chunkSize` (default 300, server cap 500);
  the cursor is saved after each chunk, so an interrupted run resumes where it stopped. `maxChunksPerPoll` bounds one
  poll; the rest continues on the next poll. A chunk the server refuses outright is bisected until the bad punch is
  isolated, written to `rejected.jsonl` and skipped, so one poison punch cannot wedge a device.
- **Late punches.** `overlapSeconds` (default 300) re-reads a window before the cursor; punches already sent are
  remembered and not re-sent.
- **Heartbeat.** After each device poll the bridge calls `POST /api/attendance/heartbeat` (version, device zone,
  backlog, last error). An offline bridge is therefore distinguishable from a quiet device.

## Time zones
Devices report local wall-clock time with no offset. Set `timeZone` (IANA, e.g. `Asia/Riyadh`) on every device; it is
**required** for `zkteco`. Wall-clock time is converted to UTC in the *device's* zone, never the bridge host's, so a
UTC Docker container next to a Riyadh device is correct. CSV timestamps with an explicit offset/`Z` use it; bare times
are read in `timeZone` (rows are skipped with a warning when neither exists). Device clock drift is not corrected —
the heartbeat carries the host time so the server can show skew.

## Security
- `apiBaseUrl` must be **https://**. Plain HTTP is refused unless `"allowInsecureHttp": true`
  (or `KYNEX_ALLOW_INSECURE_HTTP=1`) and then warns on every start.
- Device keys: keep `config.json` at mode **600** (the bridge warns when it is group/other readable), or supply
  `KYNEX_DEVICE_KEY_<DEVICE_NAME>` / `KYNEX_DEVICE_SECRET_<DEVICE_NAME>` environment variables (name upper-cased,
  non-alphanumerics → `_`) from your service manager's secret store. The cursor file is written 0600.
  There is no OS-keychain integration; the env-var route is the supported alternative.
- **Request signing (optional).** When a device has a `signingSecret` the bridge sends `X-Device-Timestamp` and
  `X-Device-Signature: v2=HMAC-SHA256(secret, canonical) (method, path, timestamp, nonce and body hash; see signing.js)`. Generate the secret in KynexOne (Attendance → Devices →
  Signing secret). Devices without a secret keep working with the key alone until signing is enforced for the device.

## Tests
`npm test` (Node's built-in runner, no dependencies) covers timeouts/retries, chunking and resume, poison-punch
isolation, time-zone conversion, signing, config validation and the zkteco failure modes.

## How it behaves
- Keeps a **per-device cursor** (`.cursor.json`, saved per chunk) so punches are not re-sent.
- Server-side **dedup** is a second safety net.
- `autoProcess: true` → KynexOne processes the affected days into attendance immediately.
- Unmatched employee codes are accepted into the raw log and reported (`unmatched` count) for mapping.

## Run with Docker
```bash
docker build -t kynexbridge .
docker run --rm -v "$(pwd)/config.json:/app/config.json" kynexbridge --once
```

## Production
- Run as a Windows Service (`nssm`) / systemd unit / Docker container per site.
- Outbound-only HTTPS — no inbound firewall changes.
- Rotate device keys from KynexOne; the agent picks up the new key on config reload/restart.
