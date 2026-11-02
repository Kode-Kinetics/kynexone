import * as mock from './mock.js';
import * as csv from './csv.js';
import * as zkteco from './zkteco.js';

// Registry of available connectors. Add hikvision.js / suprema.js here as they're built.
const registry = { mock, csv, zkteco };

// Test hook: lets the test suite register a fake device without touching the real registry file.
export function registerConnector(type, impl) { registry[type] = impl; }

export function getConnector(type) {
  const c = registry[type];
  if (!c) throw new Error(`Unknown connector "${type}". Available: ${Object.keys(registry).join(', ')}`);
  return c;
}
