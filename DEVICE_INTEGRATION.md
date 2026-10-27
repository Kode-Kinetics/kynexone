# Attendance Device Integration — KynexOne

_How physical attendance devices, cloud platforms, and software punch sources connect to KynexOne, and how to configure them._

## Architecture: raw events → processing → attendance

```
                         ┌─────────────────────────── KynexOne API ───────────────────────────┐
 Device / Cloud / Agent  │  POST /api/attendance/ingest        (X-Device-Key header)            │
 ──── punches ──────────▶│        │                                                             │
                         │        ▼                                                             │
                         │  AttendanceRawEvent (immutable log)  ──▶  Processing engine          │
                         │   EmployeeCode, DeviceId, ts, dir,         (ProcessEmployeeDay):      │
                         │   geo, photo, confidence, payload          dedup → pair IN/OUT →      │
                         │                                            apply AttendancePolicy →   │
                         │                                            attendance_records +       │
                         │                                            daily record + exceptions  │
                         └──────────────────────────────────────────────────────────────────────┘
```
The **raw event store is the immutable source of truth** (audit/SOC). `attendance_records` is the derived, policy-applied result. Processing runs automatically on ingest (`autoProcess`) and can be re-run via `POST /api/attendance/process`.

## The 4 ingestion patterns (cover 100% of devices)

| # | Pattern | How | Use for |
|---|---------|-----|---------|
| A | **Push / webhook** | Device/cloud/agent POSTs to `/api/attendance/ingest` with `X-Device-Key` | Cloud devices, push-firmware biometrics, our agent |
| B | **Pull via local agent (KynexBridge)** | Agent on the branch LAN polls devices via vendor SDK, forwards to pattern A | On-prem biometric devices behind a firewall |
| C | **File / SFTP import** | `POST /api/attendance/events/import` (CSV) | Legacy/file-only devices (old eSSL/ZKTeco, Matrix exports) |
| D | **Software punch** | `/api/attendance/punch/{web,mobile,kiosk}` | Mobile GPS+geofence, kiosk/tablet, web |

## Device coverage matrix

| Device class | Examples | Pattern |
|---|---|---|
| Fingerprint | ZKTeco, eSSL, Suprema, Anviz, Matrix, Realtime | B, or A (push firmware) |
| Face | ZKTeco SpeedFace, Hikvision, Suprema FaceStation, Dahua | A (webhook/ISAPI) or B |
| Palm / Iris | ZKTeco palm, Iris ID | B |
| RFID / card | HID, Matrix, ZKTeco readers | B or C |
| Access control / turnstiles | Hikvision, Suprema BioStar 2, ZKAccess | A (webhook) |
| Cloud attendance | Jibble, Truein, ZKBio CVCloud | A (their webhook → ingest) |
| Mobile / field | drivers, field staff | D (GPS + geofence) |
| Kiosk / tablet | reception iPad | D (PIN/QR/selfie) |
| File-only / legacy | `.dat`/`.csv` exports | C |

## ✅ What's built (verified live: the cloud ingest and the mock/CSV bridge; the ZKTeco bridge connector is NOT verified on hardware)
- **Device registry** — `GET/POST/PUT/DELETE /api/attendance/devices`.
- **Per-device API key** — `POST /api/attendance/devices/{id}/generate-key` (returns plaintext **once**; only a SHA-256 hash is stored).
- **Generic webhook ingest** — `POST /api/attendance/ingest` authenticated by `X-Device-Key` (no user login). Accepts a **batch** of punches, dedups, stores raw events, and **auto-processes** affected days into `attendance_records`. Unmatched employee codes are accepted into the raw log and counted for mapping.
- **CSV import** — `POST /api/attendance/events/import`.
- **Software punch** — web / mobile (GPS) / kiosk.
- **Processing engine** — pairs IN/OUT, computes worked/late/early/overtime/undertime, sets status (Present/Late/Half-day/Absent), flags missing punches.
- **Monitoring** — `GET /api/attendance/devices/{id}/sync-logs`, `/reports/device-sync`, `/reports/missing-punch`, `/events/raw`.

### Example: a device (or the agent) pushing punches
```bash
curl -X POST https://<api>/api/attendance/ingest \
  -H "X-Device-Key: knx_xxxxxxxx..." \
  -H "Content-Type: application/json" \
  -d '{
    "punches": [
      {"employeeCode":"KNX-0001","punchTimestampUtc":"2026-06-08T05:02:00Z","punchDirection":"In","verificationMethod":"Face","confidenceScore":0.98},
      {"employeeCode":"KNX-0001","punchTimestampUtc":"2026-06-08T14:10:00Z","punchDirection":"Out","verificationMethod":"Face"}
    ],
    "autoProcess": true
  }'
# → {"received":2,"accepted":2,"duplicates":0,"unmatched":0,"processed":1,"syncLogId":"..."}
```

## Admin setup flow
1. **Attendance → Devices → Add device:** name, type, vendor, serial, branch/location, sync method (Push/Pull/File/Cloud), frequency.
2. **Generate device key** → copy the plaintext key (shown once) into the device/cloud/agent config.
3. **Map enrollment IDs → `EmployeeCode`** (punches carry `employeeCode`; unmatched land in the raw log for mapping).
4. Point the device/cloud webhook (or the agent) at `/api/attendance/ingest` with the `X-Device-Key` header. For file-only devices, schedule a CSV drop to `/events/import`.
5. **Monitor** sync-logs, device-sync and missing-punch reports.

## Security
- Per-device API key (SHA-256 hashed at rest); invalid/inactive key → `401`.
- Ingest is scoped to the device's tenant; the key identifies device + tenant (no cross-tenant write).
- Raw payloads retained for audit.

### Ingest hardening (F2)
- **Idempotent.** Each device punch carries a dedup key (`attendance_raw_events.dedup_key`, unique per tenant+device): a resend,
  or two concurrent resends of one batch, stores each punch once and answers `200` with the counts - never `500`. Punches whose
  employee code is not mapped yet (no employee id) de-duplicate too.
- **Bounded.** A request may carry at most `Attendance:DeviceIngest:MaxBatchSize` punches (default 500; larger is refused whole
  with `400 batch_too_large`) and 2 MB of body.
- **Timestamp sanity.** A punch more than `MaxFutureMinutes` (default 10) ahead or `MaxPastDays` (default 45; raise it
  deliberately for a catch-up run) behind server time is rejected on its own: the response lists
  `rejections: [{index, reason}]` (`timestamp_in_future`, `timestamp_too_old`, `timestamp_missing`, `employee_code_required`)
  and the rest of the batch is stored. The bridge writes every rejection to `rejected.jsonl` (mode 0600).
- **Company binding.** A device on a branch belongs to that branch's company (`attendance_devices.company_id`, stamped when the
  device is saved). Its punches resolve employees only inside that company; a code from another company is stored unmatched.
- **Request signing (HMAC v2).** `Attendance:DeviceIngest:SigningMasterKey` (env `Attendance__DeviceIngest__SigningMasterKey`,
  at least 32 bytes - the host refuses to start with a shorter one) enables it. `POST /api/attendance/devices/{id}/signing-secret`
  returns the device's secret ONCE and **turns enforcement on for that device**: from then on the static key alone is refused.
  The bridge sends `X-Device-Timestamp` (unix seconds, +/- `SignatureWindowSeconds`, default 300), `X-Device-Nonce` (unique per
  request) and `X-Device-Signature: v2=<hex HMAC-SHA256(secret, canonical)>` with canonical =
  `"v2\n<METHOD>\n<path>\n<timestamp>\n<nonce>\n<hex SHA-256(raw body)>"`. Method and path are bound, so an ingest body cannot be
  replayed as a heartbeat; a nonce is accepted once per device inside the window (per API instance - with several instances a replay
  to a different instance is bounded by the timestamp window only). Rollout: deploy the API, upgrade the bridge, THEN issue the
  secret. A device without a secret keeps working on the static key until you issue one. A leaked key before that point is
  remedied by `generate-key` rotation. Rotating the secret invalidates the old one at once. v1 signatures are not accepted.
- **Abuse limits.** Per device key `RequestsPerMinute` (120) and per client address `RequestsPerMinutePerIp` (600) across all
  keys; an address with more than `FailedAuthPerMinutePerIp` (30) failed authentications in a minute gets `429` without a key lookup.
- **Heartbeat.** `POST /api/attendance/heartbeat` (same device-key/signature auth) records `last_heartbeat_at_utc`, bridge version
  and bridge-host clock skew on the device. An offline bridge is the device with a stale heartbeat; alerting on `> 15 min` is a
  read of that column (no automatic alert is wired yet).
- IP allow-listing is not implemented (cloud devices roam); the signature and the per-key rate limit are the controls.

## 🔜 Roadmap (not yet built)
1. **KynexBridge local agent.** The agent exists (`kynexbridge/`): mock and CSV connectors work; the **ZKTeco connector is SDK wiring that has not been verified against a physical device** (it fails loudly rather than returning nothing); Hikvision ISAPI and Suprema BioStar 2 adapters are not built. It forwards to `/api/attendance/ingest` with timeouts, retry, chunked resume, device-zone time conversion and a heartbeat - see `kynexbridge/README.md`.
2. **Real `test-connection` / scheduled `sync`** for pull devices (today these write a sync-log placeholder).
3. **Vendor cloud connectors** (Jibble/Truein/ZKBio webhooks → normalized to ingest).
4. **Device-config & employee-enrollment-mapping UI** + unmatched-punch exception queue.
