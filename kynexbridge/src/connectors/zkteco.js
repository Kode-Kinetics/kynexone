// ZKTeco connector (pull) — ZKTeco fingerprint/face devices over TCP/IP (port 4370) via the optional `node-zklib`.
//
// STATUS: SDK wiring, NOT verified against hardware. The mapping below is real, but nobody has yet run it against a
// physical device, so treat the first deployment as a pilot. It FAILS LOUDLY rather than quietly returning nothing:
// a missing SDK, a connection failure or a missing time zone throws, the poller logs an ERROR, the cursor does not
// advance, and the heartbeat tells the server the device is failing (an idle device and a broken one must differ).
//
// Install where the agent runs:  npm install node-zklib
//
// TIME ZONE. A ZKTeco device stores local wall-clock time with no offset, and node-zklib builds a JS Date from those
// fields in the BRIDGE HOST's zone. The wall-clock fields are therefore re-read in the device's own zone
// (device.timeZone, IANA e.g. "Asia/Riyadh", required) — a UTC container next to a Riyadh device no longer shifts
// every punch by three hours. The device clock itself can drift: check it against the server's heartbeat skew.
import { hostLocalDateToUtcIso, assertTimeZone } from '../tz.js';

export async function pull(device, sinceIso, ctx = {}) {
  if (!device.timeZone) throw new Error(`zkteco device "${device.name}" has no "timeZone" (IANA, e.g. "Asia/Riyadh").`);
  assertTimeZone(device.timeZone);
  const cfg = device.zkteco || {};
  if (!cfg.ip) throw new Error(`zkteco device "${device.name}" has no zkteco.ip.`);

  let ZKLib;
  try {
    ZKLib = (await ctx.loadSdk?.() ?? (await import('node-zklib'))).default;
  } catch {
    throw new Error('the zkteco connector needs the node-zklib package on the agent host: run "npm install node-zklib".');
  }

  const zk = new ZKLib(cfg.ip, cfg.port || 4370, cfg.timeout || 10000, 4000);
  const since = sinceIso ? Date.parse(sinceIso) : 0;
  try {
    await zk.createSocket();
    const logs = await zk.getAttendances(); // { data: [{ deviceUserId, recordTime, ... }] }
    return (logs?.data || [])
      .map((r) => ({
        employeeCode: String(r.deviceUserId),           // device enrollment id → EmployeeCode
        punchTimestampUtc: hostLocalDateToUtcIso(new Date(r.recordTime), device.timeZone),
        punchDirection: 'Unknown',                        // ZK devices often don't flag IN/OUT; pairing is done server-side
        verificationMethod: 'Biometric',
      }))
      .filter((p) => Date.parse(p.punchTimestampUtc) > since);
  } finally {
    try { await zk.disconnect(); } catch { /* ignore */ }
  }
}
