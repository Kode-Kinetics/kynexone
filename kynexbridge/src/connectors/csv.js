// CSV-folder connector — for file-only / legacy devices that export logs.
// Watches an inbox directory, parses each CSV into punches, then archives the file.
import { readdirSync, readFileSync, renameSync, mkdirSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { parseDeviceTimestamp } from '../tz.js';

// Files read by pull() wait here until the poller confirms every punch was forwarded (commit); a failed upload leaves
// them in the inbox, so the next poll reads them again (the server de-duplicates) instead of stranding them in archive/.
const pending = new Map();

export async function pull(device) {
  const cfg = device.csv || {};
  const inbox = cfg.inboxDir || './inbox';
  const archive = cfg.archiveDir || './archive';
  const cols = cfg.columns || { employeeCode: 0, timestamp: 1, direction: 2 };
  if (!existsSync(inbox)) { mkdirSync(inbox, { recursive: true }); return []; }
  if (!existsSync(archive)) mkdirSync(archive, { recursive: true });

  pending.set(device.name, []);
  const files = readdirSync(inbox).filter((f) => f.toLowerCase().endsWith('.csv'));
  const punches = [];
  let skipped = 0;
  for (const file of files) {
    const full = join(inbox, file);
    const lines = readFileSync(full, 'utf8').split(/\r?\n/).filter(Boolean);
    const rows = cfg.hasHeader ? lines.slice(1) : lines;
    for (const line of rows) {
      const parts = line.split(',').map((s) => s.trim());
      const code = parts[cols.employeeCode];
      const rawTs = parts[cols.timestamp];
      if (!code || !rawTs) continue;
      // An explicit offset/Z in the file wins; a bare wall-clock time is read in the DEVICE's zone (device.timeZone),
      // never silently in the bridge host's.
      const iso = parseDeviceTimestamp(rawTs, device.timeZone);
      if (!iso) { skipped++; continue; }
      punches.push({
        employeeCode: code,
        punchTimestampUtc: iso,
        punchDirection: parts[cols.direction] || 'Unknown',
        verificationMethod: 'File import',
      });
    }
    pending.get(device.name).push({ full, to: join(archive, `${Date.now()}_${file}`) });
  }
  if (skipped) console.warn(`[csv:${device.name}] ${skipped} row(s) skipped: unparseable timestamp, or a bare local time with no "timeZone" configured for the device.`);
  return punches;
}

export async function commit(device) {
  for (const f of pending.get(device.name) ?? []) renameSync(f.full, f.to);
  pending.delete(device.name);
}
