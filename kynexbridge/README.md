# KynexBridge — local attendance device agent

A small, installable agent that runs on a branch LAN, **polls on-prem attendance devices**
(biometric / face / RFID), and **forwards punches to KynexOne** via the device-key webhook
(`POST /api/attendance/ingest`). It's the bridge for devices that sit behind a firewall and
can't be reached from the KynexOne server.

```
Device (LAN) ──pull via SDK──▶ KynexBridge ──HTTPS + X-Device-Key──▶ KynexOne /api/attendance/ingest
```

The KynexOne side (ingest, dedup, processing → `attendance_records`) is already built. The agent
only needs to **pull** from each device and **forward** — which it does here.

## Requirements
- Node.js ≥ 18 (uses built-in `fetch`; the core has **zero npm dependencies**).
- For ZKTeco devices: `npm install node-zklib` on the agent host (optional dependency).

## Setup
1. In KynexOne: **Attendance → Devices → Add device**, then **Generate key** (copy the `knx_…` key — shown once).
2. `cp config.example.json config.json` and fill in `apiBaseUrl` + each device's `deviceKey` and connector.
   - `apiBaseUrl` is your KynexOne address (`https://<your KynexOne host>`). The exact ingest address
     (`deviceIngestUrl`) is shown in KynexOne under **Attendance → Devices**; `apiBaseUrl` is everything before
     `/api/attendance/ingest`.
   - `caFile` (optional): the path of your organisation's internal CA certificate (PEM), when the KynexOne server's
     certificate was issued by it (for example Caddy's `internal` CA in the client kit). Leave it empty for a
     publicly trusted certificate. `KYNEX_CA_FILE` overrides it.
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
| `zkteco` | Pull from ZKTeco devices over TCP/IP (port 4370) via `node-zklib` | 🔌 SDK wiring stub |

Add new vendors by dropping a module in `src/connectors/` that exports
`async pull(device, sinceIso) → punch[]` and registering it in `src/connectors/index.js`.
Roadmap connectors: `hikvision` (ISAPI), `suprema` (BioStar 2), vendor cloud webhooks.

Each punch returned by a connector:
```js
{ employeeCode, punchTimestampUtc /* ISO */, punchDirection /* In|Out|Unknown */,
  verificationMethod, confidenceScore?, latitude?, longitude?, photoReference? }
```

## How it behaves
- Keeps a **per-device cursor** (`.cursor.json`) so punches are never re-sent.
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
